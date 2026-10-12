using System.Security.Cryptography;
using System.Xml;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Library.Tests;

public sealed class IntegrityTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "library-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestFactory factory;
    private readonly Storage storage;
    private readonly JobRunner runner;
    public IntegrityTests()
    {
        foreach (var d in new[] { "source", "library", "staging", "disc", "data" }) Directory.CreateDirectory(Path.Combine(home, d));
        storage = new(new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = new[] { "source", "library", "staging", "disc" }.Select(role => new StorageRoot(role, role, Path.Combine(home, role), "", role)).ToList() });
        storage.ValidateConfiguration();
        factory = new(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(home, "data", "catalog.db")).Options);
        using var db = factory.CreateDbContext(); db.Database.EnsureCreated(); db.Settings.Add(new()); db.SaveChanges();
        runner = new(factory, storage, NullLogger<JobRunner>.Instance);
    }
    private async Task<Operation> Run(string type, object payload, bool cancel = false)
    {
        var job = new Operation { Type = type, Payload = JobRunner.Encode(payload), CancelRequested = cancel };
        await using (var db = factory.CreateDbContext()) { db.Jobs.Add(job); await db.SaveChangesAsync(); }
        await runner.Run(job.Id, CancellationToken.None);
        await using var check = factory.CreateDbContext(); return await check.Jobs.SingleAsync(x => x.Id == job.Id);
    }
    private async Task<PhysicalCopy> Source(string name = "Northlight.2024.mkv")
    {
        await File.WriteAllBytesAsync(Path.Combine(home, "source", name), Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray());
        var scan = await Run("scan", "source"); Assert.Equal("completed", scan.Status);
        await using var db = factory.CreateDbContext(); return await db.Copies.SingleAsync(x => x.RelativePath == name);
    }
    private CopyPlan Plan(PhysicalCopy copy, string path = "Movies/Northlight (2024)/Northlight (2024).mkv") => new(copy.Id, "library", path, copy.Bytes, copy.ModifiedTicks, SourceIdentity: storage.Identity(copy.RootId, copy.RelativePath));
    [Fact]
    public async Task CopyIsIndependentVerifiedAndPreservesSourceAndSidecars()
    {
        var source = await Source(); var plan = Plan(source);
        var sidecar = Path.Combine(home, "source", "Northlight.2024.nfo"); await File.WriteAllTextAsync(sidecar, "<movie><unknown>keep</unknown></movie>");
        var original = await File.ReadAllBytesAsync(Path.Combine(home, "source", source.RelativePath));
        var job = await Run("copy", plan); Assert.True(job.Status == "completed", job.Error);
        var target = Path.Combine(home, "library", plan.TargetPath); Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(home, "source", source.RelativePath)));
        Assert.Contains("unknown", await File.ReadAllTextAsync(sidecar));
        var hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        await using var db = factory.CreateDbContext(); var copy = await db.Copies.SingleAsync(x => x.Role == "library"); Assert.Equal(hash, copy.Sha256); Assert.Equal("verified", copy.State);
        await File.WriteAllTextAsync(target, "independent"); Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(home, "source", source.RelativePath)));
    }
    [Fact]
    public async Task RepeatedScanDoesNotDuplicateAndDoesNotHashUnchangedFiles()
    {
        await Source(); Assert.Equal("completed", (await Run("scan", "source")).Status);
        await using var db = factory.CreateDbContext(); Assert.Equal(1, await db.Titles.CountAsync()); Assert.Equal(1, await db.Copies.CountAsync()); Assert.Null((await db.Copies.SingleAsync()).Sha256);
    }
    [Fact]
    public async Task MissingMediaIsOfflineAndRetainedInCatalog()
    {
        var copy = await Source(); File.Move(Path.Combine(home, "source", copy.RelativePath), Path.Combine(home, "moved.mkv"));
        await Run("scan", "source"); await using var db = factory.CreateDbContext(); Assert.Equal("offline", (await db.Copies.SingleAsync()).State); Assert.Equal(1, await db.Assets.CountAsync());
    }
    [Fact]
    public async Task UnavailableRootDoesNotMarkAllCopiesOffline()
    {
        await Source(); Directory.Move(Path.Combine(home, "source"), Path.Combine(home, "unmounted"));
        Assert.Equal("failed", (await Run("scan", "source")).Status); await using var db = factory.CreateDbContext(); Assert.Equal("online", (await db.Copies.SingleAsync()).State);
    }
    [Fact]
    public async Task StalePreviewFailsWithoutWritingMedia()
    {
        var source = await Source(); var plan = Plan(source); await File.AppendAllTextAsync(Path.Combine(home, "source", source.RelativePath), "changed");
        var job = await Run("copy", plan); Assert.Equal("failed", job.Status); Assert.Contains("changed since preview", job.Error); Assert.Empty(Directory.EnumerateFiles(Path.Combine(home, "library"), "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task CollisionNeverOverwrites()
    {
        var source = await Source(); var plan = Plan(source, "existing.mkv"); await File.WriteAllTextAsync(Path.Combine(home, "library", "existing.mkv"), "precious");
        Assert.Equal("failed", (await Run("copy", plan)).Status); Assert.Equal("precious", await File.ReadAllTextAsync(Path.Combine(home, "library", "existing.mkv")));
    }
    [Fact]
    public async Task CaseInsensitiveCollisionNeverOverwrites()
    {
        var source = await Source(); await File.WriteAllTextAsync(Path.Combine(home, "library", "EXISTING.mkv"), "precious");
        Assert.Equal("failed", (await Run("copy", Plan(source, "existing.mkv"))).Status); Assert.Single(Directory.GetFiles(Path.Combine(home, "library")));
    }
    [Fact]
    public async Task QueuedCancellationLeavesNoDestination()
    {
        var source = await Source(); Assert.Equal("cancelled", (await Run("copy", Plan(source), true)).Status); Assert.Empty(Directory.GetFiles(Path.Combine(home, "library"), "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task RestartAfterPublishReconcilesOnlyMatchingJournalHash()
    {
        var source = await Source(); var plan = Plan(source, "recovered.mkv");
        File.Copy(Path.Combine(home, "source", source.RelativePath), Path.Combine(home, "library", plan.TargetPath));
        using (var file = storage.Read("library", plan.TargetPath)) plan = plan with { Hash = await JobRunner.Hash(file) };
        var job = await Run("copy", plan); Assert.Equal("completed", job.Status);
        await using var db = factory.CreateDbContext(); Assert.Single(await db.Copies.Where(x => x.Role == "library").ToListAsync());
    }
    [Fact]
    public async Task RestartRejectsMismatchedPublishedFile()
    {
        var source = await Source(); await File.WriteAllTextAsync(Path.Combine(home, "library", "bad.mkv"), "other");
        Assert.Equal("failed", (await Run("copy", Plan(source, "bad.mkv") with { Hash = new string('0', 64) })).Status);
    }
    [Fact]
    public async Task SymlinksAndTraversalAreRefused()
    {
        await Source(); Directory.CreateSymbolicLink(Path.Combine(home, "library", "escape"), Path.Combine(home, "source"));
        Assert.ThrowsAny<IOException>(() => storage.Create("library", "escape/evil.mkv"));
        Assert.Throws<ArgumentException>(() => storage.Read("source", "../moved.mkv"));
        File.CreateSymbolicLink(Path.Combine(home, "source", "link.mkv"), Path.Combine(home, "source", "Northlight.2024.mkv"));
        Assert.ThrowsAny<IOException>(() => storage.Read("source", "link.mkv"));
        Assert.Single(storage.EnumerateMedia("source"));
    }
    [Fact]
    public void ReadOnlyRootsAndUnsafeWindowsNamesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => storage.Create("source", "test.mkv"));
        foreach (var path in new[] { "CON.mkv", "A/../b.mkv", "C:\\test.mkv", "/absolute.mkv", "movie. ", "a:b.mkv" }) Assert.Throws<ArgumentException>(() => Storage.Components(path));
    }
    [Fact]
    public void PreviewExistenceCheckDoesNotCreateDirectories()
    {
        Assert.False(storage.Exists("library", "new/folder/file.mkv")); Assert.Empty(Directory.GetDirectories(Path.Combine(home, "library")));
    }
    [Fact]
    public void NfoRejectsExternalEntitiesAndPreservesUnknownFields()
    {
        Assert.Throws<XmlException>(() => Metadata.ReadNfo("<!DOCTYPE movie [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><movie><title>&x;</title></movie>"));
        var text = Metadata.ExportNfo(new Title { Name = "New", Year = 2024 }, new Edition(), "<movie custom='yes'><title>Old</title><unknown attr='x'>keep</unknown></movie>");
        var doc = Metadata.ReadNfo(text); Assert.Equal("keep", doc.Root!.Element("unknown")!.Value); Assert.Equal("yes", doc.Root.Attribute("custom")!.Value); Assert.Equal("New", doc.Root.Element("title")!.Value);
    }
    [Fact]
    public void MetadataRespectsLocksClearLeaveAndRevision()
    {
        var title = new Title { Name = "Original", Genre = "Drama", Year = 2024, LockedFields = "[\"name\"]", Revision = 2 };
        Assert.Throws<ArgumentException>(() => Metadata.Apply(title, new(1, 2, new() { { "name", new("set", "New") } }, [])));
        var edited = Metadata.Apply(title, new(1, 2, new() { { "genre", new("clear", null) }, { "year", new("leave", "1999") } }, ["name"])); Assert.Null(edited.Genre); Assert.Equal(2024, edited.Year); Assert.Equal("Original", edited.Name); Assert.Equal(3, edited.Revision);
        Assert.Throws<ArgumentException>(() => Metadata.Apply(title, new(1, 1, new(), [])));
    }
    [Fact]
    public void DiscPackingUsesDecimalCapacityReserveAndWholeFiles()
    {
        var files = Enumerable.Range(1, 3).Select(i => new ManifestEntry(i, i, $"Series/S01E{i:00}.mkv", 24_000_000_000, new string('a', 64))).ToArray();
        var bins = Packing.Pack(files, 50_000_000_000, 1_000_000_000); Assert.Equal(2, bins.Count); Assert.Equal(2, bins[0].Count); Assert.All(bins, b => Assert.True(b.Sum(x => x.Bytes) <= 49_000_000_000));
        Assert.Throws<ArgumentException>(() => Packing.Pack([files[0] with { Bytes = 49_000_000_001 }], 50_000_000_000, 1_000_000_000));
    }
    [Fact]
    public async Task DiscStageAndReadbackAreSeparateAndVerified()
    {
        var source = await Source(); var copied = await Run("copy", Plan(source, "Northlight.mkv")); Assert.True(copied.Status == "completed", copied.Error);
        Disc disc;
        await using (var db = factory.CreateDbContext())
        {
            var copy = await db.Copies.SingleAsync(x => x.Role == "library");
            disc = new Disc { Label = "LIB-0001", CapacityBytes = 50_000_000_000, ReserveBytes = 1_000_000_000, Manifest = JobRunner.Encode(new[] { new ManifestEntry(copy.AssetId, copy.Id, copy.RelativePath, copy.Bytes, copy.Sha256!) }) }; db.Discs.Add(disc); await db.SaveChangesAsync();
        }
        Assert.Equal("completed", (await Run("disc-stage", new DiscJob(disc.Id, "staging"))).Status);
        await using (var db = factory.CreateDbContext()) Assert.Equal("planned", (await db.Discs.SingleAsync()).Status);
        Assert.Equal("failed", (await Run("disc-verify", new DiscJob(disc.Id, "library"))).Status);
        File.Copy(Path.Combine(home, "staging", disc.Id, "Northlight.mkv"), Path.Combine(home, "disc", "Northlight.mkv"));
        await using (var db = factory.CreateDbContext()) { (await db.Discs.SingleAsync()).Status = "reported-written"; await db.SaveChangesAsync(); }
        Assert.Equal("completed", (await Run("disc-verify", new DiscJob(disc.Id, "disc"))).Status);
        await using (var db = factory.CreateDbContext()) Assert.Equal("read-back-verified", (await db.Discs.SingleAsync()).Status);
    }
    [Fact]
    public void PlexNamingSupportsMovieEditionsAndEpisodes()
    {
        Assert.Equal("Movies/Northlight (2024) {edition-Extended}/Northlight (2024) {edition-Extended}.mkv", Naming.Plex(new() { Name = "Northlight", Year = 2024 }, new() { Name = "Extended" }, new() { FileName = "in.mkv" }));
        Assert.Equal("TV/Signal Coast (2023)/Season 02/Signal Coast (2023) - s02e03.mkv", Naming.Plex(new() { Name = "Signal Coast", Year = 2023, Kind = "tv" }, new() { Season = 2, Episode = 3 }, new() { FileName = "in.mkv" }));
    }
    [Fact]
    public async Task SameLengthReplacementWithRestoredTimestampInvalidatesPreview()
    {
        var source = await Source(); var plan = Plan(source); var path = Path.Combine(home, "source", source.RelativePath);
        var timestamp = File.GetLastWriteTimeUtc(path); var replacement = path + ".new";
        await File.WriteAllBytesAsync(replacement, new byte[source.Bytes]); File.SetLastWriteTimeUtc(replacement, timestamp); File.Move(replacement, path, true);
        var job = await Run("copy", plan); Assert.Equal("failed", job.Status); Assert.Contains("changed since preview", job.Error);
    }
    [Fact]
    public async Task InsufficientSpaceFailsBeforeAllocatingDestination()
    {
        var path = Path.Combine(home, "source", "Large.2024.mkv");
        // Sparse synthetic source takes negligible storage and is never read on this path.
        using (var file = File.Create(path)) file.SetLength(storage.FreeBytes("library") + 1_000_000);
        Assert.Equal("completed", (await Run("scan", "source")).Status);
        await using var db = factory.CreateDbContext(); var source = await db.Copies.SingleAsync();
        var job = await Run("copy", Plan(source)); Assert.Equal("failed", job.Status); Assert.Contains("Insufficient free space", job.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "library"), "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task InterruptedPartialCopyRestartsFromSourceWithoutPublishingPartialData()
    {
        var source = await Source(); var plan = Plan(source, "restart.mkv"); var job = new Operation { Type = "copy", Status = "running", Payload = JobRunner.Encode(plan) };
        await using (var db = factory.CreateDbContext()) { db.Jobs.Add(job); await db.SaveChangesAsync(); }
        await File.WriteAllTextAsync(Path.Combine(home, "library", plan.TargetPath + $".library-{job.Id}.partial"), "interrupted bytes");
        await runner.Run(job.Id, CancellationToken.None);
        await using var verify = factory.CreateDbContext(); Assert.Equal("completed", (await verify.Jobs.SingleAsync(x => x.Id == job.Id)).Status);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(home, "source", source.RelativePath)), await File.ReadAllBytesAsync(Path.Combine(home, "library", plan.TargetPath)));
    }
    [Fact]
    public void TvInferenceSeparatesYearFromTitle()
    {
        var inferred = Naming.Infer("Signal.Coast.2023.S01E02"); Assert.Equal("Signal Coast", inferred.Title.Name); Assert.Equal(2023, inferred.Title.Year); Assert.Equal(1, inferred.Edition.Season); Assert.Equal(2, inferred.Edition.Episode);
    }
    [Fact]
    public async Task WorkerStartupRecoversRunningJournalEntries()
    {
        var source = await Source(); var plan = Plan(source, "startup-recovery.mkv"); var job = new Operation { Type = "copy", Status = "running", Payload = JobRunner.Encode(plan) };
        await using (var db = factory.CreateDbContext()) { db.Jobs.Add(job); await db.SaveChangesAsync(); }
        await runner.StartAsync(CancellationToken.None);
        string status = "running";
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(20);
            await using var db = factory.CreateDbContext(); status = (await db.Jobs.SingleAsync(x => x.Id == job.Id)).Status;
            if (status is "completed" or "failed") break;
        }
        await runner.StopAsync(CancellationToken.None);
        Assert.Equal("completed", status);
        await using var verify = factory.CreateDbContext(); Assert.True(await verify.History.AnyAsync(x => x.Action == "recovered" && x.Detail == job.Id));
    }
    [Fact]
    public async Task CorruptDiscReadbackDoesNotRetainVerifiedStatus()
    {
        var source = await Source(); var plan = Plan(source, "disc-corruption.mkv"); await Run("copy", plan);
        Disc disc;
        await using (var db = factory.CreateDbContext())
        {
            var library = await db.Copies.SingleAsync(x => x.Role == "library");
            disc = new() { Label = "LIB-FAIL", Status = "read-back-verified", Manifest = JobRunner.Encode(new[] { new ManifestEntry(library.AssetId, library.Id, library.RelativePath, library.Bytes, library.Sha256!) }) };
            db.Discs.Add(disc); await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(home, "disc", plan.TargetPath), "corrupt");
        var job = await Run("disc-verify", new DiscJob(disc.Id, "disc")); Assert.Equal("failed", job.Status);
        await using var check = factory.CreateDbContext(); Assert.Equal("reported-written", (await check.Discs.SingleAsync()).Status);
    }
    [Fact]
    public async Task PublishedCopyRecoversWhenOriginalSourceIsOffline()
    {
        var source = await Source(); var plan = Plan(source, "published.mkv");
        File.Copy(Path.Combine(home, "source", source.RelativePath), Path.Combine(home, "library", plan.TargetPath));
        using (var file = storage.Read("library", plan.TargetPath)) plan = plan with { Hash = await JobRunner.Hash(file) };
        File.Move(Path.Combine(home, "source", source.RelativePath), Path.Combine(home, "offline-original.mkv"));
        var job = await Run("copy", plan); Assert.Equal("completed", job.Status);
        await using var db = factory.CreateDbContext(); Assert.Equal("verified", (await db.Copies.SingleAsync(x => x.Role == "library")).State); Assert.Equal("offline", (await db.Copies.SingleAsync(x => x.Role == "source")).State);
    }
    [Fact]
    public async Task ScanInvalidatesVerificationAfterSameSizeSameMtimeReplacement()
    {
        var source = await Source(); var plan = Plan(source, "cached.mkv"); Assert.Equal("completed", (await Run("copy", plan)).Status);
        var path = Path.Combine(home, "library", plan.TargetPath); var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path + ".replacement", new byte[source.Bytes]); File.SetLastWriteTimeUtc(path + ".replacement", timestamp); File.Move(path + ".replacement", path, true);
        Assert.Equal("completed", (await Run("scan", "library")).Status);
        await using var db = factory.CreateDbContext(); var copy = await db.Copies.SingleAsync(x => x.Role == "library");
        Assert.Null(copy.Sha256); Assert.Equal("online", copy.State); Assert.NotNull(copy.FileIdentity);
    }
    [Fact]
    public async Task MissingCatalogDestinationCannotBeReusedByAnotherCopy()
    {
        var first = await Source(); var originalPlan = Plan(first, "reserved.mkv"); Assert.Equal("completed", (await Run("copy", originalPlan)).Status);
        File.Move(Path.Combine(home, "library", "reserved.mkv"), Path.Combine(home, "offline-library.mkv"));
        var second = await Source("Another.2025.mkv"); var retry = await Run("copy", Plan(second, "reserved.mkv"));
        Assert.Equal("failed", retry.Status); Assert.Contains("catalog already reserves", retry.Error); Assert.False(File.Exists(Path.Combine(home, "library", "reserved.mkv")));
        await using var db = factory.CreateDbContext(); Assert.Equal(first.AssetId, (await db.Copies.SingleAsync(x => x.Role == "library")).AssetId);
    }
    [Fact]
    public async Task SchemaUpgradePreservesCatalogAndInvalidatesLegacyVerification()
    {
        var source = await Source(); Assert.Equal("completed", (await Run("copy", Plan(source, "legacy.mkv"))).Status);
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Copies DROP COLUMN FileIdentity;");
        CatalogSchema.Initialize(db); CatalogSchema.Initialize(db);
        Assert.Equal(1, await db.Titles.CountAsync()); Assert.Equal(2, await db.Copies.CountAsync());
        var library = await db.Copies.SingleAsync(x => x.Role == "library"); Assert.Null(library.Sha256); Assert.Null(library.FileIdentity); Assert.Equal("online", library.State);
    }
    [Fact]
    public void OverlappingAndSymlinkRootsAreRejected()
    {
        var options = new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = [new("a", "a", Path.Combine(home, "source"), "", "source"), new("b", "b", Path.Combine(home, "source", "nested"), "", "library")] };
        Assert.Throws<ArgumentException>(() => new Storage(options).ValidateConfiguration());
        Directory.CreateSymbolicLink(Path.Combine(home, "linked-root"), Path.Combine(home, "source"));
        options.Roots = [new("linked", "linked", Path.Combine(home, "linked-root"), "", "source")];
        Assert.ThrowsAny<IOException>(() => new Storage(options).ValidateConfiguration());
        options.Roots = [new("root", "root", "/tmp/..", "", "source")]; Assert.Throws<ArgumentException>(() => new Storage(options).ValidateConfiguration());
    }
    [Fact]
    public async Task SQLiteBackupRestoresCatalogAndOperationHistory()
    {
        var source = await Source(); Assert.Equal("completed", (await Run("copy", Plan(source))).Status);
        var backupPath = Path.Combine(home, "backup.db");
        using (var current = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(home, "data", "catalog.db")))
        using (var target = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + backupPath))
        { current.Open(); target.Open(); current.BackupDatabase(target); using var check = target.CreateCommand(); check.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", check.ExecuteScalar()); }
        var restoredFactory = new TestFactory(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + backupPath).Options);
        await using var restored = restoredFactory.CreateDbContext(); Assert.Equal(2, await restored.Copies.CountAsync()); Assert.Equal(2, await restored.Jobs.CountAsync()); Assert.True(await restored.History.AnyAsync(x => x.Action == "completed"));
        Assert.NotNull((await restored.Copies.SingleAsync(x => x.Role == "library")).FileIdentity);
    }
    [Fact]
    public void OverlappingDeclaredWindowsRootsAreRejected()
    {
        var options = new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = [new("source", "source", Path.Combine(home, "source"), "X:/Downloads", "source"), new("library", "library", Path.Combine(home, "library"), "x:/downloads/organized", "library")] };
        Assert.Throws<ArgumentException>(() => new Storage(options).ValidateConfiguration());
    }
    [Fact] public async Task LegacyPreviewWithoutIdentityCannotStartACopy()
    {
        var source = await Source(); var job = await Run("copy", Plan(source) with { SourceIdentity = null });
        Assert.Equal("failed", job.Status); Assert.Contains("lacks source identity", job.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "library"), "*", SearchOption.AllDirectories));
    }
    public void Dispose() { SqliteClear(); Directory.Delete(home, true); }
    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
public sealed class TestFactory(DbContextOptions<Catalog> options) : IDbContextFactory<Catalog>
{
    public Catalog CreateDbContext() => new(options);
}
