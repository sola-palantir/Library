using System.Text.Json;
using Library.Api;
using Library.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

// Direct, isolated API-domain qualification. No HTTP import/production receipt route.
public static class QualificationCatalogFlow
{
    public record Evidence(string Name, string SourceName, FileObservation Output, string Sha256, FileObservation Source,
        string SourceSha256, string PlanJson, string ValidationJson);

    public static string Verify(string root)
    {
        string? lineage = null, manifest = null;
        foreach (var name in new[] { "catalog.db", "catalog-restored.db" })
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, name), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check"; if ((string?)command.ExecuteScalar() != "ok") throw new IOException("Persisted catalog integrity failed.");
            command.CommandText = "SELECT COUNT(*) FROM DerivedAssets"; if ((long)command.ExecuteScalar()! != 1) throw new IOException("Persisted lineage count changed.");
            command.CommandText = "SELECT Evidence FROM DerivedAssets"; var currentLineage = (string)command.ExecuteScalar()!;
            command.CommandText = "SELECT ManifestJson FROM BackupPhysicalDiscs"; var currentManifest = (string)command.ExecuteScalar()!;
            lineage ??= currentLineage; manifest ??= currentManifest;
            if (currentLineage != lineage || currentManifest != manifest) throw new IOException("Recovered catalog differs.");
        }
        if (File.ReadAllText(Path.Combine(root, "LIBRARY-MANIFEST.json")) != manifest) throw new IOException("Manifest export differs.");
        return JsonSerializer.Serialize(new { reopenedInNewProcess = true, catalogIntegrity = "ok", restoredIntegrity = "ok", lineageAndManifestMatched = true, mediaReadOrRestored = false, opticalVerified = false });
    }

    static async Task<long> Register(Catalog db, Evidence e, string jobId)
    {
        var encoded = JsonSerializer.Serialize(e);
        var prior = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId);
        if (prior != null)
        {
            if (prior.Type != "offline-qualification" || prior.Payload != encoded) throw new IOException("Conflicting qualification registration.");
            return (await db.DerivedAssets.SingleAsync(d => d.JobId == jobId)).OutputAssetId;
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        var title = new Title { Name = "QUALIFICATION ONLY - supplied source and partial RF20 clip" };
        db.Add(title); await db.SaveChangesAsync();
        var edition = new Edition { TitleId = title.Id, Name = "Partial test clip; not a complete title" };
        db.Add(edition); await db.SaveChangesAsync();
        var source = new Asset { EditionId = edition.Id, FileName = e.SourceName, Bytes = e.Source.Bytes, Sha256 = e.SourceSha256 };
        var output = new Asset { EditionId = edition.Id, FileName = e.Name, Bytes = e.Output.Bytes, Sha256 = e.Sha256 };
        db.Add(source); db.Add(output); await db.SaveChangesAsync();
        var sourceCopy = new PhysicalCopy { AssetId = source.Id, RootId = "qualification-source", RelativePath = source.FileName, Role = "source", State = "online", Bytes = source.Bytes, ModifiedTicks = e.Source.Ticks, FileIdentity = e.Source.Identity, Sha256 = source.Sha256 };
        db.Add(sourceCopy); await db.SaveChangesAsync();
        db.Add(new PhysicalCopy { AssetId = output.Id, RootId = "qualification-stage", RelativePath = e.Name, Role = "staging", State = "sample-validated", Bytes = e.Output.Bytes, ModifiedTicks = e.Output.Ticks, FileIdentity = e.Output.Identity, Sha256 = e.Sha256 });
        db.Add(new DerivedAssetRecord { SourceAssetId = source.Id, SourceCopyId = sourceCopy.Id, OutputAssetId = output.Id, JobId = jobId, Kind = "video-encode-sample", Evidence = encoded });
        db.Add(new ProcessingLease { SourceAssetId = source.Id, JobId = jobId });
        db.Add(new Operation { Id = jobId, Type = "offline-qualification", Status = "qualification-completed", Payload = encoded });
        db.Audit("qualification-only-registration", encoded);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return output.Id;
    }

    public static async Task<string> Run(string root, string checkpointJson)
    {
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new empty isolated catalog directory.");
        var e = JsonSerializer.Deserialize<Evidence>(checkpointJson, ExecutionContract.Json) ?? throw new IOException("Missing checkpoint.");
        ExecutionContract.Hash(e.Sha256); ExecutionContract.Hash(e.SourceSha256); Storage.Components(e.Name); Storage.Components(e.SourceName);
        if (e.Output.Bytes is <= 0 or > 268435456 || e.Source == e.Output) throw new IOException("Distinct bounded sample required.");
        var path = Path.Combine(root, "catalog.db"); using (File.Open(path, FileMode.CreateNew)) { }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); using var db = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options);
        CatalogSchema.Initialize(db);
        var jobId = Guid.NewGuid().ToString("N"); var outputId = await Register(db, e, jobId);
        if (await Register(db, e, jobId) != outputId || await db.DerivedAssets.CountAsync() != 1 || await db.Assets.CountAsync() != 2 || await db.ProcessingLeases.CountAsync() != 1) throw new IOException("Registration was not idempotent.");
        try { await Register(db, e with { Sha256 = new string('f', 64) }, jobId); throw new InvalidOperationException("Changed registration accepted."); } catch (IOException) { }
        var copy = await db.Copies.SingleAsync(c => c.AssetId == outputId);
        var productionRefused = false;
        try { await BackupApi.Resolve(new("Must refuse sample", [copy.Id], 50_000_000_000, 1_000_000_000), db, new Storage(new() { ExecutionBackend = ExecutionMode.Windows }), default); }
        catch (ArgumentException) { productionRefused = true; }
        if (!productionRefused) throw new IOException("Production archive eligibility changed.");
        var request = new BackupRequest("QUALIFICATION ONLY - partial test clip; no physical disc", [new("test-clip", "Partial RF20 qualification clip", [new(copy.Id, e.Name, e.Output.Bytes, e.Sha256, e.Output.Identity, SourceRootId: copy.RootId, SourcePath: e.Name)])], 50_000_000_000, 1_000_000_000);
        var service = new BackupService(db); var preview = service.Preview(request);
        var set = await service.ApproveAsync(request, preview.Sha256, "qualification-plan");
        if ((await service.ApproveAsync(request, preview.Sha256, "qualification-plan")).Id != set.Id) throw new IOException("Archive plan duplicated.");
        var disc = await db.Set<BackupPhysicalDisc>().SingleAsync();
        File.WriteAllText(Path.Combine(root, "LIBRARY-MANIFEST.json"), disc.ManifestJson);
        var backup = Path.Combine(root, "catalog-backup.db"); using (File.Open(backup, FileMode.CreateNew)) { }
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString())) { target.Open(); connection.BackupDatabase(target); }
        var restored = Path.Combine(root, "catalog-restored.db"); using (File.Open(restored, FileMode.CreateNew)) { }
        using var restoredConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restored, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        restoredConnection.Open();
        using (var saved = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString())) { saved.Open(); saved.BackupDatabase(restoredConnection); }
        using var recovered = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(restoredConnection).Options);
        if ((await recovered.DerivedAssets.SingleAsync()).Evidence != JsonSerializer.Serialize(e) || (await recovered.Set<BackupPhysicalDisc>().SingleAsync()).ManifestJson != disc.ManifestJson || await recovered.Copies.CountAsync() != 2 || await recovered.ProcessingLeases.CountAsync() != 1) throw new IOException("Catalog recovery lost lineage or manifest.");
        using (var check = restoredConnection.CreateCommand()) { check.CommandText = "PRAGMA integrity_check"; if ((string?)check.ExecuteScalar() != "ok") throw new IOException("Recovered catalog integrity failed."); }
        var immutable = false; try { await recovered.Database.ExecuteSqlRawAsync("DELETE FROM DerivedAssets"); } catch (SqliteException) { immutable = true; }
        if (!immutable) throw new IOException("Restored lineage protection missing.");
        var result = JsonSerializer.Serialize(new { qualificationOnly = true, productionArchiveRefused = true, assets = 2, copies = 2, derivedRecords = 1, reservations = 1, registrationIdempotent = true, plannedDiscs = preview.Packing.Discs.Length, disc.Label, disc.Sha256, payloadBytes = e.Output.Bytes, backupReopenPassed = true, immutableLineageRetained = true, opticalVerified = false, mediaRestored = false });
        File.WriteAllText(Path.Combine(root, "catalog-evidence.json"), result); return result;
    }
}

public class QualificationCatalogFlowTests
{
    [Fact] public async Task IsolatedCatalogRetainsSampleBoundaryAndRestoresHistory()
    {
        var root = Directory.CreateTempSubdirectory("library-qualification-").FullName;
        var e = new QualificationCatalogFlow.Evidence("invented.mkv", "invented-source.mkv", new(100, 20, "output"), new string('a', 64), new(1000, 10, "source"), new string('b', 64), "{}", "{\"synthetic\":true}");
        var result = await QualificationCatalogFlow.Run(root, JsonSerializer.Serialize(e));
        Assert.Contains("\"productionArchiveRefused\":true", result);
        Assert.Contains("\"backupReopenPassed\":true", result);
        Assert.Contains("\"lineageAndManifestMatched\":true", QualificationCatalogFlow.Verify(root));
        await Assert.ThrowsAsync<IOException>(() => QualificationCatalogFlow.Run(root, JsonSerializer.Serialize(e)));
    }
}
