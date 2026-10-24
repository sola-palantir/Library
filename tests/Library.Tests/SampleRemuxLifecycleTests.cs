using System.Security.Cryptography;
using System.Text.Json;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Library.Tests;

// Synthetic journal/recovery contracts, never FFmpeg qualification or real media.
public sealed class SampleRemuxLifecycleTests
{
    [Fact]
    public async Task HandoffRequiresApprovedHashAndRehashesTheCurrentPinnedFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(); using var db = f.Factory.CreateDbContext(); var job = db.Jobs.Single();
        await f.Service.Run(job, db, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var grant = new SampleHandoffGrant(job.Id, f.Plan.OutputBytes, f.Plan.Hash!, new string('a', 64), now, now.AddMinutes(10));
        var (copy, descriptor) = await SampleHandoff.Describe(job.Id, db, f.Storage, grant, CancellationToken.None);
        Assert.Equal(grant.OutputSha256, descriptor.Sha256); Assert.NotEqual(descriptor.SourceAssetId, descriptor.OutputAssetId);
        using (var verified = await SampleHandoff.OpenVerified(copy, f.Storage, CancellationToken.None)) Assert.Equal(grant.Bytes, verified.Length);
        await Assert.ThrowsAsync<IOException>(() => SampleHandoff.Describe(job.Id, db, f.Storage, grant with { OutputSha256 = new string('f', 64) }, CancellationToken.None));
        File.WriteAllText(f.Output, "corrupted output");
        await Assert.ThrowsAsync<IOException>(() => SampleHandoff.Describe(job.Id, db, f.Storage, grant, CancellationToken.None));
        var changed = f.Storage.Observe(copy.RootId, copy.RelativePath);
        copy.Bytes = changed.Bytes; copy.ModifiedTicks = changed.Ticks; copy.FileIdentity = changed.Identity;
        await Assert.ThrowsAsync<IOException>(() => SampleHandoff.OpenVerified(copy, f.Storage, CancellationToken.None));
        Assert.Equal("corrupted output", File.ReadAllText(f.Output)); Assert.Equal("invented source", File.ReadAllText(f.Input));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedCheckpointRecoversOnceAndWithdrawalKeepsFileAndReservation(bool encode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(encode: encode); using var db = f.Factory.CreateDbContext();
        var job = db.Jobs.Single();
        await f.Service.Run(job, db, CancellationToken.None);
        await f.Service.Run(job, db, CancellationToken.None);
        Assert.Single(db.DerivedAssets); Assert.Equal(2, db.Assets.Count());
        Assert.Equal(encode ? "video-encode-sample" : "stream-copy-sample", db.DerivedAssets.Single().Kind);
        if (encode)
        {
            var now = DateTimeOffset.UtcNow;
            var grant = new SampleHandoffGrant(job.Id, f.Plan.OutputBytes, f.Plan.Hash!, new string('a', 64), now, now.AddMinutes(10));
            await Assert.ThrowsAsync<IOException>(() => SampleHandoff.Describe(job.Id, db, f.Storage, grant, CancellationToken.None));
        }
        var output = db.Copies.Single(c => c.Role == "staging");
        Assert.NotEqual(f.Source.AssetId, output.AssetId); Assert.Equal("sample-validated", output.State);
        Assert.Null(db.Assets.Single(a => a.Id == f.Source.AssetId).Sha256);
        job.Status = "completed"; await db.SaveChangesAsync();
        await f.Service.Withdraw(job, db, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal("withdrawn", db.Copies.Single(c => c.Role == "staging").State);
        Assert.Single(db.ProcessingLeases); Assert.Single(db.DerivedAssets);
        Assert.Equal("invented output", File.ReadAllText(f.Output));
        Assert.Equal("invented source", File.ReadAllText(f.Input));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Preview(f.Plan.Request, db, CancellationToken.None));
    }
    [Fact]
    public async Task UnboundCollisionAndCorruptionCannotAcquireEvidenceOrEraseFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(); using var db = f.Factory.CreateDbContext(); var job = db.Jobs.Single();
        job.Payload = JobRunner.Encode(f.Plan with { Hash = null, Validation = null });
        await Assert.ThrowsAsync<IOException>(() => f.Service.Run(job, db, CancellationToken.None));
        Assert.Empty(db.DerivedAssets); Assert.True(File.Exists(f.Output));
        job.Payload = JobRunner.Encode(f.Plan); await db.SaveChangesAsync();
        await f.Service.Run(job, db, CancellationToken.None);
        File.WriteAllText(f.Output, "corrupted output retained for review");
        await Assert.ThrowsAsync<IOException>(() => f.Service.Run(job, db, CancellationToken.None));
        db.ChangeTracker.Clear(); var output = db.Copies.Single(c => c.Role == "staging");
        Assert.Equal("review-required", output.State); Assert.Null(output.Sha256);
        Assert.Null(db.Assets.Single(a => a.Id == output.AssetId).Sha256); Assert.Single(db.DerivedAssets);
        Assert.Equal("corrupted output retained for review", File.ReadAllText(f.Output));
        Assert.Equal("invented source", File.ReadAllText(f.Input));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SampleCannotBecomeACompleteLibraryCopyThroughTheExistingWorker(bool encode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(encode: encode); using var db = f.Factory.CreateDbContext();
        await f.Service.Run(db.Jobs.Single(), db, CancellationToken.None);
        var output = db.Copies.Single(c => c.Role == "staging");
        var copy = new Operation { Type = "copy", Payload = JobRunner.Encode(new CopyPlan(output.Id, "library", "not-a-complete-title.mkv", output.Bytes, output.ModifiedTicks, SourceIdentity: output.FileIdentity)) };
        db.Jobs.Add(copy); await db.SaveChangesAsync();
        await new JobRunner(f.Factory, f.Storage, NullLogger<JobRunner>.Instance).Run(copy.Id, CancellationToken.None);
        db.ChangeTracker.Clear(); var failed = db.Jobs.Single(j => j.Id == copy.Id);
        Assert.Equal("failed", failed.Status); Assert.Contains("Samples cannot be promoted", failed.Error);
        Assert.Empty(db.Copies.Where(c => c.Role == "library")); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Home, "library")));
        Assert.True(File.Exists(f.Output));
    }
    [Fact]
    public async Task CancellationDuringProbeTerminatesChildAndWithdrawalReleasesOnlyEmptyReservation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(published: false, slowProbe: true); using var db = f.Factory.CreateDbContext(); var job = db.Jobs.Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = f.Service.Run(job, db, deadline.Token);
        while (!File.Exists(Path.Combine(f.Home, "ready"))) await Task.Delay(10, deadline.Token);
        await using (var poll = f.Factory.CreateDbContext()) await poll.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(File.Exists(f.Output)); Assert.Empty(db.DerivedAssets);
        job.Status = "cancelled"; await db.SaveChangesAsync();
        await f.Service.Withdraw(job, db, CancellationToken.None);
        Assert.Empty(db.ProcessingLeases); Assert.Equal("invented source", File.ReadAllText(f.Input));
    }
    private sealed class ManualGuard : IQBitCopyGuard
    {
        public Task<CompletionEvidence> CheckAsync(PhysicalCopy copy, int? revision, CancellationToken ct) => Task.FromResult(new CompletionEvidence("manual", "Synthetic review", 0, true, DateTime.UtcNow));
    }
    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "library-sample-contract-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Home, "source", "invented.mkv");
        public string Output { get; }
        public TestFactory Factory { get; }
        public Storage Storage { get; }
        public SampleRemuxService Service { get; }
        public PhysicalCopy Source { get; }
        public SamplePlan Plan { get; }
        public Fixture(bool published = true, bool slowProbe = false, bool encode = false)
        {
            foreach (var name in new[] { "source", "staging", "library", "data" }) Directory.CreateDirectory(Path.Combine(Home, name));
            File.WriteAllText(Input, "invented source");
            var probe = JsonSerializer.SerializeToElement(new { format = new { format_name = "matroska,webm" }, streams = new object[] {
                new { index = 0, codec_type = "video", codec_name = "h264", pix_fmt = "yuv420p", width = 320, height = 180, field_order = "progressive", r_frame_rate = "24/1", disposition = new { } },
                new { index = 1, codec_type = "audio", codec_name = "pcm_s16le", disposition = new { } }
            } });
            File.WriteAllText(Path.Combine(Home, "inventory.json"), probe.GetRawText());
            var executable = Path.Combine(Home, "synthetic-probe");
            File.WriteAllText(executable, "#!/bin/sh\nset -eu\n" + (slowProbe ? $"touch '{Home}/ready'\nsleep 30\n" : "") + $"cat '{Home}/inventory.json'\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new LibraryOptions { DataPath = Path.Combine(Home, "data"), FfprobePath = executable, FfmpegPath = executable, EnableSampleRemux = true, EnableSampleEncode = encode,
                Roots = new[] { "source", "staging", "library" }.Select(r => new StorageRoot(r, r, Path.Combine(Home, r), "", r)).ToList() };
            Storage = new(options); Storage.ValidateConfiguration();
            Factory = new(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(Home, "data", "catalog.db")).Options);
            using var db = Factory.CreateDbContext(); CatalogSchema.Initialize(db);
            var title = new Title(); db.Titles.Add(title); db.SaveChanges(); var edition = new Edition { TitleId = title.Id }; db.Editions.Add(edition); db.SaveChanges();
            var source = new Asset { EditionId = edition.Id, FileName = "invented.mkv" }; db.Assets.Add(source); db.SaveChanges();
            var observation = Storage.Observe("source", "invented.mkv");
            Source = new() { AssetId = source.Id, RootId = "source", RelativePath = "invented.mkv", Bytes = observation.Bytes, ModifiedTicks = observation.Ticks, FileIdentity = observation.Identity };
            db.Copies.Add(Source); db.SaveChanges();
            var request = new SampleRequest(Source.Id, "staging", 3, [], [new(1, "eng", "main")], true, true, encode ? DerivedMedia.EncodeMode : "stream-copy");
            var job = new Operation { Type = "sample-remux", Status = "running" };
            Output = Path.Combine(Home, "staging", SampleRemuxService.Destination(job.Id)); Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
            if (published) File.WriteAllText(Output, "invented output");
            Plan = new(request, source.Id, observation, DerivedMedia.Select(probe, request), "synthetic", DerivedMedia.ProbeSignature(probe), published ? DerivedMedia.Digest("invented output") : null, published ? new FileInfo(Output).Length : 0, published ? "synthetic persisted validation checkpoint" : null, Encoding: encode ? DerivedMedia.SdrEncoding : null);
            job.Payload = JobRunner.Encode(Plan); db.Jobs.Add(job); db.ProcessingLeases.Add(new() { SourceAssetId = source.Id, JobId = job.Id }); db.SaveChanges();
            Service = new(Storage, options, new(Storage, options), Factory, new ManualGuard());
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Home, true); }
    }
}
