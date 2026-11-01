using System.Text.Json;
using Library.Api;
using Library.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Library.Tests;

public class ReadOnlyConnectionTests
{
    sealed class Database : IDbContextFactory<Catalog>, IDisposable
    {
        readonly SqliteConnection connection = new("Data Source=:memory:");
        public Database() { connection.Open(); using var db = CreateDbContext(); db.Database.EnsureCreated(); }
        public Catalog CreateDbContext() => new(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options);
        public Task<Catalog> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
        public void Dispose() => connection.Dispose();
    }
    static ReviewConnectionProfile Profile() => new(Guid.NewGuid().ToString("N"), @"C:\synthetic\samples", "http://127.0.0.1:5095", DerivedMedia.Digest(new string('a',64)), DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(6));
    [Fact] public async Task MissingExpiredAndRevokedAuthorizationCannotBeCreatedOrRenewedByConnect()
    {
        using var db = new Database(); var profile = Profile();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyConnection(new(),db).Connect(profile.Folder,default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadOnlyConnection(new() { ReadOnlyConnection = profile with { ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(-1) } },db).Connect(profile.Folder,default));
        var options = new LibraryOptions { ReadOnlyConnection=profile }; var connection = new ReadOnlyConnection(options,db); await connection.Initialize(default);
        await connection.Disconnect(true,default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Connect(profile.Folder,default));
        var restarted = new ReadOnlyConnection(options,db); await restarted.Initialize(default);
        Assert.True(restarted.Revoked); Assert.False(restarted.Authorize(profile.ApiOrigin,"Bearer "+new string('a',64)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Connect(profile.Folder,default));
    }
    [Fact] public async Task DisconnectCancelsWorkRejectsLateReceiptAndReconnectUsesFreshIdentity()
    {
        using var db = new Database(); var profile = Profile(); var options = new LibraryOptions { ReadOnlyConnection=profile };
        var connection = new ReadOnlyConnection(options,db); using var review = new ReadOnlyReview(options,db,connection);
        await connection.Initialize(default); await connection.Connect(profile.Folder,default);
        var first = connection.Lease!; Assert.True(first.ExpiresUtc-first.IssuedUtc<=TimeSpan.FromHours(1));
        await review.Poll(default,first.SessionId); await review.Queue(profile.Folder,default); var command=(await review.Poll(default,first.SessionId))!;
        await connection.Disconnect(false,default); await review.CancelConnection(default);
        Assert.Null(connection.Lease); Assert.False(review.Connected);
        await connection.Connect(profile.Folder,default); Assert.NotEqual(first.SessionId,connection.Lease!.SessionId); Assert.False(review.Connected);
        await Assert.ThrowsAsync<IOException>(()=>review.Poll(default,first.SessionId));
        var result=new ReviewScanResult(command.Id,command.SessionId,command.RootId,[],"ffprobe version synthetic");
        await Assert.ThrowsAsync<IOException>(()=>review.Accept(result,default));
        using var check=db.CreateDbContext(); Assert.Equal("cancelled",check.Jobs.Single().Status); Assert.Empty(check.Copies);
        Assert.Equal(profile.ExpiresUtc,connection.Profile!.ExpiresUtc);
    }
    [Fact] public async Task RestartDisconnectsButPreservesCompletedReviewsAndProfileExpiry()
    {
        using var db = new Database(); var profile=Profile(); var options=new LibraryOptions { ReadOnlyConnection=profile }; var connection=new ReadOnlyConnection(options,db);
        await connection.Initialize(default); await connection.Connect(profile.Folder,default);
        using var review=new ReadOnlyReview(options,db,connection); await review.Poll(default); await review.Queue(profile.Folder,default); var command=(await review.Poll(default))!;
        await review.Accept(new(command.Id,command.SessionId,command.RootId,[new("invented.mkv",new(123,456,"synthetic-id"),JsonSerializer.SerializeToElement(new { streams=new[] { new { index=0,codec_type="video",codec_name="ffv1" } } }))],"ffprobe version synthetic"),default);
        var restarted=new ReadOnlyConnection(options,db); await restarted.Initialize(default); Assert.Null(restarted.Lease); Assert.False(restarted.Revoked);
        await restarted.Connect(profile.Folder,default); Assert.Equal(profile.ExpiresUtc,restarted.Profile!.ExpiresUtc);
        using var check=db.CreateDbContext(); Assert.Single(check.Integrations); Assert.DoesNotContain(new string('a',64),check.Integrations.Single().Payload);
        using var saved=new ReadOnlyReview(options,db,restarted); await saved.Inspect(check.Copies.Single().Id,default);
    }
    [Fact] public async Task ActiveAuthorizationExpiryStopsLeaseAndRefusesReconnectWithoutRenewal()
    {
        using var db=new Database(); var profile=Profile() with { ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(1) }; var connection=new ReadOnlyConnection(new() { ReadOnlyConnection=profile },db);
        await connection.Initialize(default); await connection.Connect(profile.Folder,default); Assert.NotNull(connection.Lease);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        Assert.Null(connection.Lease); Assert.False(connection.Authorize(profile.ApiOrigin,"Bearer "+new string('a',64)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>connection.Connect(profile.Folder,default));
        Assert.Equal("authorization-expired",JsonSerializer.SerializeToElement(connection.Status(false)).GetProperty("state").GetString());
    }
    [Fact] public async Task ScopeCredentialsAndSessionDurationAreBoundedAndHiddenFromStatus()
    {
        using var db = new Database(); var profile=Profile() with { ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(3) }; var connection=new ReadOnlyConnection(new() { ReadOnlyConnection=profile },db);
        profile.Validate(); await connection.Initialize(default);
        Assert.Throws<ArgumentException>(()=>(profile with { ExpiresUtc=profile.IssuedUtc.AddDays(8) }).Validate());
        Assert.Throws<ArgumentException>(()=>(profile with { Folder=@"C:\synthetic\..\private" }).Validate());
        Assert.Throws<ArgumentException>(()=>(profile with { ApiOrigin="http://0.0.0.0:5095" }).Validate());
        await Assert.ThrowsAsync<ArgumentException>(()=>connection.Connect(@"C:\other",default));
        await connection.Connect(profile.Folder,default); Assert.Equal(profile.ExpiresUtc,connection.Lease!.ExpiresUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>connection.Connect(profile.Folder,default));
        Assert.True(connection.Authorize(profile.ApiOrigin,"Bearer "+new string('a',64)));
        Assert.False(connection.Authorize(profile.ApiOrigin,"Bearer "+new string('b',64)));
        Assert.False(connection.Authorize("http://evil.example","Bearer "+new string('a',64)));
        Assert.DoesNotContain(profile.TokenSha256,JsonSerializer.Serialize(connection.Status(false)));
    }
}
