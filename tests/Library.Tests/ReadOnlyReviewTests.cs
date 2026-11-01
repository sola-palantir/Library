using System.Text.Json;
using Library.Api;
using Library.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public sealed class ReadOnlyReviewTests
{
    static ReviewSessionOptions Grant() => new(Guid.NewGuid().ToString("N"), @"C:\approved\samples", "http://127.0.0.1:5090", DerivedMedia.Digest(new string('a', 64)), DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(9));
    sealed class Database : IDbContextFactory<Catalog>, IDisposable
    {
        readonly SqliteConnection connection = new("Data Source=:memory:");
        public Database() { connection.Open(); using var db = CreateDbContext(); db.Database.EnsureCreated(); }
        public Catalog CreateDbContext() => new(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options);
        public Task<Catalog> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
        public void Dispose() => connection.Dispose();
    }
    static ReviewScanResult Result(ReviewScanCommand command) => new(command.Id, command.SessionId, command.RootId,
        [new("invented.mkv", new(1234, 123456, "native-test-id"), JsonSerializer.SerializeToElement(new { streams = new[] { new { index = 0, codec_type = "video", codec_name = "h264" } } }))], "ffprobe version synthetic");
    [Fact] public void TemporaryGrantBindsHostTokenTimeAndCannotBePermanent()
    {
        var grant = Grant(); grant.Validate();
        Assert.True(grant.Authorize(grant.ApiOrigin, "Bearer " + new string('a',64), DateTimeOffset.UtcNow));
        Assert.False(grant.Authorize("http://evil.example", "Bearer " + new string('a',64), DateTimeOffset.UtcNow));
        Assert.False(grant.Authorize(grant.ApiOrigin, "Bearer " + new string('b',64), DateTimeOffset.UtcNow));
        Assert.False(grant.Authorize(grant.ApiOrigin, "Bearer " + new string('a',64), grant.ExpiresUtc));
        Assert.Throws<ArgumentException>(() => (grant with { ExpiresUtc = grant.IssuedUtc.AddMinutes(11) }).Validate());
        Assert.Throws<ArgumentException>(() => (grant with { Folder = @"C:\approved\..\private" }).Validate());
        Assert.Throws<ArgumentException>(() => (grant with { ApiOrigin = "http://0.0.0.0:5090" }).Validate());
    }
    [Fact] public async Task DisconnectedUnknownFolderAndConcurrentScansAreRefused()
    {
        using var factory = new Database(); var options = new LibraryOptions { ReviewSession = Grant() }; using var service = new ReadOnlyReview(options, factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Queue(options.ReviewSession.Folder, default));
        await service.Poll(default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Queue(@"C:\private", default));
        await service.Queue(options.ReviewSession.Folder, default);
        await Assert.ThrowsAsync<IOException>(() => service.Queue(options.ReviewSession.Folder, default));
    }
    [Fact] public async Task CompletedInventoryRegistersUnverifiedObservationsAndReplayIsIdempotent()
    {
        using var factory = new Database(); var grant = Grant(); using var service = new ReadOnlyReview(new() { ReviewSession = grant }, factory);
        await service.Poll(default); await service.Queue(grant.Folder, default); var command = (await service.Poll(default))!;
        var result = Result(command); await service.Accept(result, default); await service.Accept(result, default);
        using var db = factory.CreateDbContext(); var copy = Assert.Single(db.Copies);
        Assert.Null(copy.Sha256); Assert.Equal("observed", copy.State); Assert.Single(db.Assets); Assert.Empty(db.DerivedAssets);
        var observation = JsonSerializer.SerializeToElement(await service.Inspect(copy.Id, default));
        Assert.True(observation.GetProperty("evidence").GetProperty("cached").GetBoolean());
        Assert.False(observation.GetProperty("evidence").GetProperty("executable").GetBoolean());
        await Assert.ThrowsAsync<IOException>(() => service.Accept(result with { ToolVersion = "ffprobe version conflicting" }, default));
        await service.Poll(default); await service.Queue(grant.Folder, default); var retry = (await service.Poll(default))!;
        await service.Accept(Result(retry) with { Files = [result.Files[0] with { RelativePath = "INVENTED.mkv" }] }, default);
        using var freshDb = factory.CreateDbContext();
        Assert.Equal("INVENTED.mkv", Assert.Single(freshDb.Copies).RelativePath); Assert.Single(freshDb.Assets);
        await service.Inspect(copy.Id, default);
    }
    [Fact] public async Task CancellationAndRetryRejectStaleResultsWithoutPartialRegistration()
    {
        using var factory = new Database(); var grant = Grant(); using var service = new ReadOnlyReview(new() { ReviewSession = grant }, factory);
        await service.Poll(default); var first = await service.Queue(grant.Folder, default); var command = (await service.Poll(default))!;
        await service.Cancel(first.Id, default); Assert.False(await service.KeepRunning(first.Id, default));
        await Assert.ThrowsAsync<IOException>(() => service.Accept(Result(command), default));
        var retry = await service.Retry(first.Id, default); Assert.NotEqual(first.Id, retry.Id);
        var fresh = (await service.Poll(default))!; await service.Accept(Result(fresh), default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Retry(retry.Id, default));
        using var db = factory.CreateDbContext(); Assert.Single(db.Copies); Assert.Equal("cancelled", db.Jobs.Find(first.Id)!.Status);
    }
    [Fact] public async Task ExpiredDeadlineAndApiRestartFailClosed()
    {
        using var factory = new Database(); var grant = Grant(); var options = new LibraryOptions { ReviewSession = grant }; using var service = new ReadOnlyReview(options, factory);
        await service.Poll(default); var first = await service.Queue(grant.Folder, default); var command = (await service.Poll(default))!;
        using (var db = factory.CreateDbContext()) { var row = db.Jobs.Find(first.Id)!; row.Payload = ExecutionContract.Serialize(command with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }); db.SaveChanges(); }
        await Assert.ThrowsAsync<IOException>(() => service.Accept(Result(command), default));
        await service.Poll(default); var second = await service.Queue(grant.Folder, default);
        await service.StartAsync(default); await service.StopAsync(default);
        using var check = factory.CreateDbContext(); Assert.Empty(check.Copies); Assert.Equal("failed", check.Jobs.Find(first.Id)!.Status); Assert.Equal("failed", check.Jobs.Find(second.Id)!.Status);
    }
    [Fact] public async Task InvalidInventoryNeverCommitsAnyRows()
    {
        using var factory = new Database(); var grant = Grant(); using var service = new ReadOnlyReview(new() { ReviewSession = grant }, factory);
        await service.Poll(default); await service.Queue(grant.Folder, default); var command = (await service.Poll(default))!;
        var result = Result(command); var unsafeFile = result.Files[0] with { RelativePath = "../outside.mkv" };
        await Assert.ThrowsAsync<ArgumentException>(() => service.Accept(result with { Files = [result.Files[0], unsafeFile] }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Accept(result with { Files = [null!] }, default));
        await Assert.ThrowsAsync<IOException>(() => service.Accept(result with { RootId = "wrong-root" }, default));
        using var db = factory.CreateDbContext(); Assert.Empty(db.Copies); Assert.Empty(db.Titles);
    }
}
