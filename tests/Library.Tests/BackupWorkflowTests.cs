using System.Security.Cryptography;
using System.Text;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Library.Tests;

public sealed class BackupWorkflowTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "backup-workflow-" + Guid.NewGuid().ToString("N"));
    readonly TestFactory factory;
    readonly Storage storage;
    public BackupWorkflowTests()
    {
        foreach (var role in new[] { "library", "source", "staging", "data" }) Directory.CreateDirectory(Path.Combine(home, role));
        storage = new(new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = new[] { "library", "source", "staging" }.Select(x => new StorageRoot(x, x, Path.Combine(home, x), "", x)).ToList() });
        factory = new(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(home, "data", "catalog.db")).Options);
        using var db = factory.CreateDbContext(); CatalogSchema.Initialize(db);
    }
    async Task<(BackupPhysicalDisc Disc, BackupRequest Request)> Plan()
    {
        await File.WriteAllTextAsync(Path.Combine(home, "library", "Movie.mkv"), "synthetic independent movie bytes");
        await File.WriteAllTextAsync(Path.Combine(home, "library", "Movie.nfo"), "<movie><unknown>preserve</unknown></movie>");
        var observed = storage.Observe("library", "Movie.mkv");
        using var input = storage.Read("library", "Movie.mkv"); var hash = await JobRunner.Hash(input);
        await using var db = factory.CreateDbContext();
        var title = new Title { Name = "Movie" }; db.Add(title); await db.SaveChangesAsync();
        var edition = new Edition { TitleId = title.Id }; db.Add(edition); await db.SaveChangesAsync();
        var asset = new Asset { EditionId = edition.Id }; db.Add(asset); await db.SaveChangesAsync();
        var copy = new PhysicalCopy { AssetId = asset.Id, Role = "library", RootId = "library", RelativePath = "Movie.mkv", Bytes = observed.Bytes, ModifiedTicks = observed.Ticks, FileIdentity = observed.Identity, Sha256 = hash, State = "verified" };
        db.Add(copy); await db.SaveChangesAsync();
        var request = await BackupApi.Resolve(new("Synthetic movie backup", [copy.Id], 50_000_000_000, 1_000_000_000, Sidecars: [new(copy.Id, "Movie.nfo")]), db, storage, default);
        var preview = BackupDomain.Preview(request);
        await new BackupService(db).ApproveAsync(request, preview.Sha256, "approve-synthetic");
        return (await db.Set<BackupPhysicalDisc>().SingleAsync(), request);
    }
    async Task<Operation> Stage(BackupPhysicalDisc disc, string? digest = null)
    {
        var job = new Operation { Type = "backup-stage", Payload = JobRunner.Encode(new BackupStageRequest(disc.Id, "staging", digest ?? disc.Sha256)) };
        await using (var db = factory.CreateDbContext()) { db.Add(job); await db.SaveChangesAsync(); }
        await new JobRunner(factory, storage, NullLogger<JobRunner>.Instance).Run(job.Id, default);
        await using var read = factory.CreateDbContext(); return await read.Jobs.SingleAsync(x => x.Id == job.Id);
    }
    [Fact]
    public async Task StageFreezesIndependentMediaAndSelectedSidecarAndManifest()
    {
        var (disc, _) = await Plan(); var staged = await Stage(disc); Assert.True(staged.Status == "completed", staged.Error);
        Assert.Equal(disc.ManifestJson, await File.ReadAllTextAsync(Path.Combine(home, "staging", disc.Id, "LIBRARY-MANIFEST.json")));
        await File.WriteAllTextAsync(Path.Combine(home, "staging", disc.Id, "Movie.nfo"), "changed stage");
        Assert.Contains("preserve", await File.ReadAllTextAsync(Path.Combine(home, "library", "Movie.nfo")));
        Assert.Equal("failed", (await Stage(disc)).Status);
        await using var db = factory.CreateDbContext(); Assert.Equal("failed", (await db.Set<BackupEvidence>().OrderBy(x => x.Id).LastAsync()).State);
    }
    [Fact]
    public async Task StaleSourceOrManifestCannotStage()
    {
        var (disc, request) = await Plan();
        Assert.Equal("failed", (await Stage(disc, new string('0', 64))).Status);
        await File.WriteAllTextAsync(Path.Combine(home, "library", "Movie.mkv"), "replaced source");
        await using var db = factory.CreateDbContext(); await Assert.ThrowsAsync<IOException>(() => BackupApi.ValidateSources(request, db, storage));
        Assert.Equal("failed", (await Stage(disc)).Status);
        Assert.False(File.Exists(Path.Combine(home, "staging", disc.Id, "Movie.mkv")));
    }
    [Fact]
    public async Task SignedEvidenceRequiresPinnedKeyHardwareManifestAndOrderedSequence()
    {
        var (disc, _) = await Plan(); using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using (var db = factory.CreateDbContext()) { db.Integrations.Add(new() { Id = "companion-public-key", Payload = signing.ExportSubjectPublicKeyInfoPem() }); await db.SaveChangesAsync(); }
        SignedReceipt Sign(CompanionEvidence evidence)
        {
            var payload = JobRunner.Encode(evidence);
            return new(payload, Convert.ToBase64String(signing.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }
        var verified = new CompanionEvidence(1, "synthetic-receipt", disc.Id, disc.Sha256, "synthetic-device", "verified", DateTime.UtcNow, new string('a', 64), OpticalRoot: "R:\\", ManifestMatched: true, AllFilesVerified: true, Sequence: 5);
        await using (var db = factory.CreateDbContext())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => BackupApi.ImportReceipt(Sign(verified with { Simulated = true }), db));
            await Assert.ThrowsAsync<ArgumentException>(() => BackupApi.ImportReceipt(Sign(verified with { ManifestSha256 = new string('b', 64) }), db));
            await Assert.ThrowsAsync<ArgumentException>(() => BackupApi.ImportReceipt(Sign(verified with { AllFilesVerified = false }), db));
            await Assert.ThrowsAsync<ArgumentException>(() => BackupApi.ImportReceipt(Sign(verified) with { Signature = Convert.ToBase64String(new byte[64]) }, db));
        }
        var receipts = new[] { Sign(verified), Sign(verified with { State = "failed", Sequence = 6, AllFilesVerified = false }) };
        await Task.WhenAll(receipts.Select(async r => { await using var db = factory.CreateDbContext(); try { await BackupApi.ImportReceipt(r, db); } catch (ArgumentException) { /* An older out-of-order arrival is rejected. */ } }));
        await using var final = factory.CreateDbContext();
        Assert.Equal("failed", (await final.Set<BackupEvidence>().OrderBy(x => x.Id).LastAsync()).State);
        var count = await final.Set<BackupEvidence>().CountAsync(); await BackupApi.ImportReceipt(receipts[1], final); Assert.Equal(count, await final.Set<BackupEvidence>().CountAsync());
        await new BackupService(final).RecordAsync(new(disc.Id, "local-failure", "manual-inspection", "review-required"));
        await Assert.ThrowsAsync<ArgumentException>(() => BackupApi.ImportReceipt(Sign(verified with { Sequence = 7, AtUtc = DateTime.UtcNow.AddHours(-1) }), final));
        Assert.Equal("review-required", (await final.Set<BackupEvidence>().OrderBy(x => x.Id).LastAsync()).State);
    }
    [Fact]
    public async Task UpgradeDowngradesLegacyOpticalClaimsAndPreservesHistory()
    {
        await using var db = factory.CreateDbContext(); db.Discs.Add(new() { Status = "read-back-verified", Label = "LEGACY" }); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version=3;"); CatalogSchema.Initialize(db); db.ChangeTracker.Clear();
        Assert.Equal("content-checked", (await db.Discs.SingleAsync()).Status);
        Assert.True(await db.History.AnyAsync(x => x.Action == "legacy-optical-claim-downgraded"));
    }
    [Fact]
    public async Task RestoredCatalogCannotStartWithoutReviewOrAfterIncompleteRestore()
    {
        var data = Path.Combine(home, "data"); var report = Path.Combine(data, "restore-report.json");
        var complete = "{\"status\":\"restored-offline\"}";
        await File.WriteAllTextAsync(report, complete);
        Assert.Throws<InvalidOperationException>(() => RestoreReview.Validate(data, null));
        RestoreReview.Validate(data, BackupDomain.Digest(complete));
        var incomplete = "{\"status\":\"restore-incomplete\"}";
        await File.WriteAllTextAsync(report, incomplete);
        Assert.Throws<InvalidOperationException>(() => RestoreReview.Validate(data, BackupDomain.Digest(incomplete)));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(home, true); }
}
