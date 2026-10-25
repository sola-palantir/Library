using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// The managed release supports the Windows control plane only. No worker credentials,
// media paths, native processing or Linux job executor are enabled by this lifecycle.
public static class Deployment
{
    public static string Marker(LibraryOptions options) => Path.Combine(options.DataPath, "deployment-paused");
    public static bool Paused(LibraryOptions options) => File.Exists(Marker(options));
    public static void Pause(LibraryOptions options)
    {
        using var file = new FileStream(Marker(options), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.WriteByte(1); file.Flush(true);
        SyncDirectory(options.DataPath);
    }
    static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return; // API release runtime is Linux.
        var fd = Native.open(path, Native.Directory | Native.NoFollow | Native.CloseOnExec, 0);
        if (fd < 0) throw new IOException("Could not open deployment directory for durability.");
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true);
        if (Native.fsync(fd) != 0) throw new IOException("Deployment directory fsync failed.");
    }
    public static async Task<int> Active(Catalog db) => await db.Jobs.CountAsync(j => j.Status == "running" || j.Status == "auditing") + await db.ProcessingLeases.CountAsync();
    public static async Task<object> Readiness(Catalog db, LibraryOptions options)
    {
        if (CatalogSchema.Version != ReleaseInfo.CatalogSchema) throw new InvalidOperationException("Release/catalog schema mismatch.");
        await db.Database.OpenConnectionAsync();
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA quick_check";
            if ((string?)await command.ExecuteScalarAsync() != "ok") throw new InvalidDataException("Catalog integrity check failed.");
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != CatalogSchema.Version) throw new InvalidDataException("Unexpected catalog schema.");
            command.CommandText = "PRAGMA foreign_key_check";
            if (await command.ExecuteScalarAsync() != null) throw new InvalidDataException("Catalog foreign-key integrity check failed.");
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return new { ready = true, version = ReleaseInfo.Version, protocol = ReleaseInfo.Protocol, deploymentProtocol = ReleaseInfo.DeploymentProtocol,
            catalogSchema = CatalogSchema.Version, mode = ReleaseInfo.Mode, paused = Paused(options), activeJobs = await Active(db),
            production = false, pairing = false, backend = options.ExecutionBackend };
    }
    public static void MapDeployment(this WebApplication app)
    {
        app.MapGet("/api/deployment/readiness", (Catalog db, LibraryOptions o) => Readiness(db, o));
        app.MapPost("/api/deployment/quiesce", async (Catalog db, LibraryOptions o) =>
        {
            if (ExecutionMode.IsLocal(o)) throw new InvalidOperationException("Managed updates require the Windows control plane; Linux jobs need a separate maintenance procedure.");
            Pause(o); return Results.Ok(await Readiness(db, o));
        });
        app.MapPost("/api/deployment/resume", async (Catalog db, LibraryOptions o) =>
        {
            if (ExecutionMode.IsLocal(o) || await Active(db) != 0) throw new InvalidOperationException("Active or unreconciled work prevents resume.");
            await Readiness(db, o);
            // Only our maintenance marker is removed. No catalog, media or journal deletion.
            File.Delete(Marker(o)); return Results.Ok(await Readiness(db, o));
        });
    }

    // Runs in an isolated container with the old volume read-only and a NEW destination.
    // Uses SQLite's backup API, never raw copies of a database/WAL pair.
    public static object Snapshot(string source, string destination, string? expectedHash = null)
    {
        if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("Snapshot target must be empty.");
        if (!File.Exists(Path.Combine(source, "deployment-paused"))) throw new IOException("Source must be quiesced before snapshot.");
        if (Directory.EnumerateFileSystemEntries(source).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new IOException("Linked data refused.");
        var database = Path.Combine(source, "catalog.db");
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(database + suffix)))
            throw new IOException("Source is not a closed standalone catalog; reconcile WAL/journal before snapshot.");
        using var sourceFile = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceFile)).ToLowerInvariant();
        if (expectedHash != null && sourceHash != expectedHash) throw new IOException("Retained backup hash changed; restore refused.");
        // A stopped, exclusively managed source is mounted read-only and has no
        // sidecars. Immutable URI avoids WAL-mode SQLite trying to create SHM on
        // that read-only mount. Never use this against an active database.
        using var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(database).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); input.Open();
        using var check = input.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException("Backup source integrity failed.");
        check.CommandText = "PRAGMA user_version"; var schema = Convert.ToInt32(check.ExecuteScalar());
        if (schema > CatalogSchema.Version || schema < 3) throw new InvalidDataException("Unsupported source schema; no downgrade.");
        check.CommandText = "PRAGMA foreign_key_check";
        if (check.ExecuteScalar() != null) throw new InvalidDataException("Backup source foreign-key integrity failed.");
        check.CommandText = "SELECT (SELECT COUNT(*) FROM Jobs WHERE Status IN ('running','auditing')) + (SELECT COUNT(*) FROM ProcessingLeases)";
        if (Convert.ToInt32(check.ExecuteScalar()) != 0) throw new IOException("Unreconciled work prevents snapshot.");
        var output = Path.Combine(destination, "catalog.db");
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output, Pooling = false }.ToString())) { target.Open(); input.BackupDatabase(target); using var c = target.CreateCommand(); c.CommandText = "PRAGMA integrity_check"; if ((string?)c.ExecuteScalar() != "ok") throw new IOException("Backup integrity failed."); }
        using (var file = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
        Pause(new LibraryOptions { DataPath = destination });
        using var read = File.OpenRead(output);
        sourceFile.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(sourceFile)).ToLowerInvariant() != sourceHash || new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(database + suffix))) throw new IOException("Backup source changed.");
        var result = new { schema, sha256 = Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant(), bytes = read.Length };
        using var receipt = new FileStream(Path.Combine(destination, "snapshot.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(receipt, result); receipt.Flush(true); SyncDirectory(destination); return result;
    }
    public static async Task<int> Command(string[] args)
    {
        try
        {
            object result;
            if (args.Length == 2 && args[1] == "version") result = ReleaseInfo.Describe();
            else if (args.Length is 4 or 5 && args[1] == "snapshot") result = Snapshot(args[2], args[3], args.Length == 5 ? args[4] : null);
            else if (args.Length == 2 && args[1] == "health")
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync("http://127.0.0.1:8080/api/deployment/readiness"); response.EnsureSuccessStatusCode(); return 0;
            }
            else throw new ArgumentException("Unknown deployment command.");
            Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
