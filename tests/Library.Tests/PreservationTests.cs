using System.Diagnostics;
using System.Security.Cryptography;
using Library.Api;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public sealed class PreservationTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "library-preservation-" + Guid.NewGuid().ToString("N"));
    readonly TestFactory factory;
    readonly Storage storage;
    readonly PreservationService service;
    public PreservationTests()
    {
        foreach (var dir in new[] { "data", "source", "library" }) Directory.CreateDirectory(Path.Combine(home, dir));
        storage = new(new LibraryOptions { DataPath = Path.Combine(home, "data"), Roots = new[] { "source", "library" }.Select(x => new StorageRoot(x, x, Path.Combine(home, x), "", x)).ToList() });
        storage.ValidateConfiguration();
        factory = new(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=" + Path.Combine(home, "data", "catalog.db")).Options);
        using var db = factory.CreateDbContext(); CatalogSchema.Initialize(db); db.Database.ExecuteSqlRaw(PreservationService.SchemaSql);
        service = new(factory, storage);
    }
    async Task<PhysicalCopy> Seed()
    {
        var path = "synthetic.mkv"; var bytes = new byte[1024]; Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(home, "library", path), bytes);
        var observation = storage.Observe("library", path); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var db = factory.CreateDbContext(); var title = new Title { Name = "Synthetic" }; db.Add(title); await db.SaveChangesAsync();
        var edition = new Edition { TitleId = title.Id }; db.Add(edition); await db.SaveChangesAsync();
        var asset = new Asset { EditionId = edition.Id, Bytes = bytes.Length, Sha256 = hash }; db.Add(asset); await db.SaveChangesAsync();
        var copy = new PhysicalCopy { AssetId = asset.Id, RootId = "library", Role = "library", RelativePath = path, Bytes = observation.Bytes, ModifiedTicks = observation.Ticks, FileIdentity = observation.Identity, Sha256 = hash, State = "verified" }; db.Add(copy); await db.SaveChangesAsync(); return copy;
    }
    [Fact]
    public async Task FullAuditDetectsCorruptionWithPreservedSizeAndTimestampAndKeepsHistory()
    {
        var copy = await Seed(); Assert.Equal("content-matched", (await service.AuditAsync(copy.Id)).Outcome);
        var path = Path.Combine(home, "library", copy.RelativePath); var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, new byte[copy.Bytes]); File.SetLastWriteTimeUtc(path, timestamp);
        var result = await service.AuditAsync(copy.Id); Assert.Equal("review-required", result.Outcome); Assert.NotEqual(result.ExpectedHash, result.ActualHash);
        await using var db = factory.CreateDbContext(); Assert.Null((await db.Copies.SingleAsync()).Sha256); Assert.Equal(2, await db.History.CountAsync(x => x.Action == "preservation-audit")); Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task RestartNeverConvertsInterruptedAuditToSuccess()
    {
        var copy = await Seed(); await using (var db = factory.CreateDbContext()) { db.Add(new Operation { Type = "preservation-audit", Status = "auditing" }); await db.SaveChangesAsync(); }
        await service.RecoverInterruptedAsync(CancellationToken.None);
        await using var check = factory.CreateDbContext(); Assert.Equal("interrupted", (await check.Jobs.SingleAsync()).Status); Assert.Single(await check.History.Where(x => x.Action == "preservation-interrupted").ToArrayAsync());
    }
    [Fact]
    public async Task DisabledAndBudgetBoundedSchedulerPreservesCursorAcrossRestart()
    {
        var copy = await Seed(); Assert.Equal(0, await service.RunDueBatchAsync());
        await service.SavePolicyAsync(new(true, 1, 1, 512)); Assert.Equal(0, await service.RunDueBatchAsync());
        var restarted = new PreservationService(factory, storage); var policy = await restarted.PolicyAsync(); Assert.Equal(copy.Id, policy.Cursor); Assert.NotNull(policy.LastRunUtc);
        await using var db = factory.CreateDbContext(); Assert.Equal("verified", (await db.Copies.SingleAsync()).State); Assert.Single(await db.History.Where(x => x.Action == "preservation-budget-skipped").ToArrayAsync());
    }
    sealed class ManualGuard : IQBitCopyGuard
    {
        public Task<CompletionEvidence> CheckAsync(PhysicalCopy copy, int? revision, CancellationToken ct) => Task.FromResult(new CompletionEvidence("manual", "Synthetic review", 0, true, DateTime.UtcNow));
    }
    [Fact]
    public async Task RepairRequiresFreshHealthySourceAndOnlyCreatesDraftAtUnusedPath()
    {
        var copy = await Seed();
        await service.PreviewRepairAsync(new(copy.Id, "library", "replacement.mkv"), new ManualGuard(), CancellationToken.None);
        await using var db = factory.CreateDbContext(); var job = await db.Jobs.SingleAsync(x => x.Type == "copy"); Assert.Equal("draft", job.Status);
        Assert.False(File.Exists(Path.Combine(home, "library", "replacement.mkv")));
        await Assert.ThrowsAsync<IOException>(() => service.PreviewRepairAsync(new(copy.Id, "library", copy.RelativePath), new ManualGuard(), CancellationToken.None));
        await File.WriteAllBytesAsync(Path.Combine(home, "library", copy.RelativePath), new byte[copy.Bytes]);
        await Assert.ThrowsAsync<IOException>(() => service.PreviewRepairAsync(new(copy.Id, "library", "other.mkv"), new ManualGuard(), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(home, "library", "other.mkv"))); Assert.True(File.Exists(Path.Combine(home, "library", copy.RelativePath)));
    }
    [Fact]
    public async Task HistoricalAuditEvidenceCannotBeRewrittenOrDeleted()
    {
        var copy = await Seed(); await service.AuditAsync(copy.Id);
        await using var db = factory.CreateDbContext();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE History SET Detail='rewritten' WHERE Action='preservation-audit'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM History WHERE Action='preservation-audit'"));
    }
    [Fact]
    public async Task MissingExpectedHashNeverCreatesVerifiedEvidence()
    {
        var copy = await Seed(); await using (var db = factory.CreateDbContext()) { await db.Copies.ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256, (string?)null)); }
        Assert.Equal("review-required", (await service.AuditAsync(copy.Id)).Outcome);
    }
    [Fact]
    public async Task RestoreCopiesAllHistoryAndRefusesExistingTargetOrCorruption()
    {
        await Seed();
        await using (var db = factory.CreateDbContext())
        {
            db.Audit("synthetic-preservation-evidence", "Do not rewrite this historical observation"); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var source = Path.Combine(home, "data", "catalog.db");
        var snapshot = Path.Combine(home, "export.db");
        using (var src = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + source)) using (var dst = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + snapshot)) { src.Open(); dst.Open(); src.BackupDatabase(dst); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(snapshot));
        var failedTarget = Path.Combine(home, "interrupted-restore");
        Assert.Equal(0, await RestoreInjectedFailure(snapshot, failedTarget));
        Assert.Contains("restore-incomplete", await File.ReadAllTextAsync(Path.Combine(failedTarget, "restore-report.json")));
        Assert.False(File.Exists(Path.Combine(failedTarget, "catalog.db")));
        var completedCopyFailure = Path.Combine(home, "copied-but-incomplete");
        Assert.Equal(0, await RestoreInjectedFailure(snapshot, completedCopyFailure, "checksum"));
        Assert.Contains("restore-incomplete", await File.ReadAllTextAsync(Path.Combine(completedCopyFailure, "restore-report.json")));
        Assert.True(File.Exists(Path.Combine(completedCopyFailure, "catalog.db")));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(snapshot)));
        var target = Path.Combine(home, "restored"); Assert.Equal(0, await Restore(snapshot, target));
        Assert.Contains("restored-offline", await File.ReadAllTextAsync(Path.Combine(target, "restore-report.json")));
        Assert.False(File.Exists(snapshot + "-wal")); Assert.False(File.Exists(snapshot + "-shm"));
        using (var restored = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(target, "catalog.db")))
        { restored.Open(); using var cmd = restored.CreateCommand(); cmd.CommandText = "SELECT Detail FROM History WHERE Action='synthetic-preservation-evidence'"; Assert.Equal("Do not rewrite this historical observation", cmd.ExecuteScalar());
          cmd.CommandText = "INSERT INTO History (AtUtc,Action,Detail) VALUES ('2026-01-01','preservation-audit','restored trigger check')"; cmd.ExecuteNonQuery();
          cmd.CommandText = "DELETE FROM History WHERE Action='preservation-audit'"; Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery()); }
        Assert.NotEqual(0, await Restore(snapshot, target)); Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(snapshot)));
        await File.WriteAllTextAsync(Path.Combine(home, "corrupt.db"), "not sqlite"); Assert.NotEqual(0, await Restore(Path.Combine(home, "corrupt.db"), Path.Combine(home, "bad"))); Assert.False(Directory.Exists(Path.Combine(home, "bad")));
    }
    static async Task<int> RestoreInjectedFailure(string source, string destination, string phase = "create")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "scripts", "restore-catalog.py"))) root = root.Parent;
        var process = new Process { StartInfo = new("python3") { RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("""
            import importlib.util, os, sys
            from pathlib import Path
            spec = importlib.util.spec_from_file_location('restore', sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            original = os.open
            def fail_catalog(path, flags, mode=0o777):
                if sys.argv[4] == 'create' and Path(path).name == 'catalog.db':
                    raise OSError('Synthetic failure after durable incomplete marker')
                return original(path, flags, mode)
            module.os.open = fail_catalog
            if sys.argv[4] == 'checksum':
                def fail_checksum(*args):
                    raise OSError('Synthetic failure after database copy')
                module.hashlib.file_digest = fail_checksum
            try:
                module.restore(Path(sys.argv[2]), Path(sys.argv[3]))
            except OSError as error:
                assert 'Synthetic failure' in str(error)
            else:
                raise AssertionError('Injected fault did not occur')
            """);
        process.StartInfo.ArgumentList.Add(Path.Combine(root!.FullName, "scripts", "restore-catalog.py")); process.StartInfo.ArgumentList.Add(source); process.StartInfo.ArgumentList.Add(destination); process.StartInfo.ArgumentList.Add(phase);
        process.Start(); var output = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, output); return process.ExitCode;
    }
    static async Task<int> Restore(string source, string destination)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "scripts", "restore-catalog.py"))) root = root.Parent;
        var process = new Process { StartInfo = new("python3") { RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(Path.Combine(root!.FullName, "scripts", "restore-catalog.py")); process.StartInfo.ArgumentList.Add(source); process.StartInfo.ArgumentList.Add(destination);
        process.Start(); await process.WaitForExitAsync(); return process.ExitCode;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(home, true); }
}
