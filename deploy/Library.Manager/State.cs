using System.Text.Json;
using Library.Companion;

namespace Library.Manager;

public record Installation(string Id, int Port);
public record Release(string Directory, string ManifestSha256, string Version, int Schema, string ImageId, string Volume, string Container);
public record UpdateState(string Phase, Release Candidate, Release? Previous, string? Backup, string? Review = null);
public record Entry(int Sequence, string PreviousHash, UpdateState State);
public record RestorePoint(Release Release, SnapshotReceipt Catalog, string FilesSha256);

// Append-only, flushed records. A torn record fails closed; no last-known-good
// guess can accidentally cross the ResumeIntent barrier after a crash.
public sealed class StateStore : IDisposable
{
    readonly Scope scope;
    readonly FileStream lease = null!;
    public string Home { get; }
    public Installation Installation { get; }
    public StateStore(string home, bool create = false, int port = 5080)
    {
        Home = Path.GetFullPath(home);
        if (create)
        {
            if (Directory.Exists(Home) || File.Exists(Home)) throw new IOException("Choose a new installation directory; existing data is never adopted or overwritten.");
            if (!Directory.Exists(Path.GetDirectoryName(Home))) throw new IOException("Installation parent must exist.");
            using var parent = new Scope(Path.GetDirectoryName(Home)!);
            Directory.CreateDirectory(Home);
        }
        scope = new Scope(Home);
        try
        {
            lease = new FileStream(scope.Resolve("manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (OperatingSystem.IsWindows()) WindowsPublication.Observe(lease, true);
            if (create)
            {
                if (port is < 1024 or > 65535) throw new ArgumentException("Invalid localhost port.");
                WriteNew("installation.json", new Installation(Guid.NewGuid().ToString("N"), port));
                foreach (var d in new[] { "releases", "events", "backups", "worker-state", "configuration" }) Directory.CreateDirectory(Path.Combine(Home, d));
                WriteNew("configuration/settings.json", new { executionBackend = "windows-worker", production = false, pairing = false });
            }
            using var file = scope.Read("installation.json");
            Installation = JsonSerializer.Deserialize<Installation>(file, Bundle.Json) ?? throw new IOException("Missing installation identity.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(Installation.Id, "^[a-f0-9]{32}$") || Installation.Port is < 1024 or > 65535) throw new IOException("Invalid installation identity.");
        }
        catch { lease?.Dispose(); scope.Dispose(); throw; }
    }
    public void WriteNew(string relative, object value)
    {
        if (OperatingSystem.IsWindows())
        {
            using var native = WindowsPublication.Open(scope, relative + ".partial");
            JsonSerializer.Serialize(native, value, Bundle.Json); native.Flush(true);
            WindowsPublication.Publish(scope, native, relative); return;
        }
        using var file = new FileStream(scope.Resolve(relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(file, value, Bundle.Json); file.Flush(true);
    }
    public UpdateState? Read()
    {
        UpdateState? state = null; var previous = ""; var count = 0;
        using var events = new Scope(Path.Combine(Home, "events"));
        foreach (var path in Directory.GetFiles(events.Root).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) != $"{count + 1:D8}.json") throw new IOException("Incomplete update journal; manual reconciliation required.");
            using var file = events.Read(Path.GetFileName(path)); var hash = Bundle.Hash(file); file.Position = 0;
            var entry = JsonSerializer.Deserialize<Entry>(file, Bundle.Json) ?? throw new IOException("Incomplete update journal.");
            if (entry.Sequence != ++count || entry.PreviousHash != previous) throw new IOException("Update journal chain mismatch.");
            ValidateRelease(entry.State.Candidate);
            if (entry.State.Previous != null) ValidateRelease(entry.State.Previous);
            if (entry.State.Phase is not ("Prepared" or "Quiesced" or "LocalBackup" or "BackedUp" or "Verified" or "ResumeIntent" or "Ready" or "Restoring" or "RestoredPendingReview")) throw new IOException("Unknown update journal phase.");
            if (entry.State.Backup != null && !System.Text.RegularExpressions.Regex.IsMatch(entry.State.Backup, "^[a-f0-9]{32}$")) throw new IOException("Invalid backup reference.");
            state = entry.State; previous = hash;
        }
        return state;
    }
    public void ValidateRelease(Release release)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(release.Directory, "^releases/[a-f0-9]{32}$") || !Bundle.Digest(release.ManifestSha256) ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Volume, "^library-" + Installation.Id + "-[a-f0-9]{32}-data$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Container, "^library-" + Installation.Id + "-[a-f0-9]{32}-api$")) throw new IOException("Invalid installation resource reference.");
        var manifest = Bundle.Read(Path.Combine(Home, release.Directory), release.ManifestSha256);
        if (manifest.ImageId != release.ImageId || manifest.CatalogSchema != release.Schema || manifest.Version != release.Version)
            throw new IOException("Journal/restore-point identity differs from its verified release manifest.");
    }
    public void Append(UpdateState state)
    {
        _ = Read(); var files = Directory.GetFiles(Path.Combine(Home, "events")).Order(StringComparer.Ordinal).ToArray();
        WriteNew($"events/{files.Length + 1:D8}.json", new Entry(files.Length + 1, files.Length == 0 ? "" : Bundle.HashFile(files[^1]), state));
    }
    public string BackupLocal()
    {
        var name = Guid.NewGuid().ToString("N"); var directory = Path.Combine(Home, "backups", name); Directory.CreateDirectory(directory);
        var files = new List<Artifact>();
        foreach (var root in new[] { "configuration", "worker-state" })
        {
            CopyTree(Path.Combine(Home, root), Path.Combine(directory, root), root, files);
        }
        WriteNew($"backups/{name}/files.json", files);
        return name;
    }
    public void RestoreLocal(string backupPath, string expected)
    {
        using var source = new Scope(backupPath); using var list = source.Read("files.json");
        if (Bundle.Hash(list) != expected) throw new IOException("Configuration/journal backup list hash mismatch."); list.Position = 0;
        var artifacts = JsonSerializer.Deserialize<Artifact[]>(list, Bundle.Json) ?? throw new IOException("Missing backup list.");
        foreach (var a in artifacts)
        {
            if (!a.Path.StartsWith("configuration/") && !a.Path.StartsWith("worker-state/")) throw new IOException("Unexpected backup path.");
            using var file = source.Read(a.Path);
            if (file.Length != a.Bytes || Bundle.Hash(file) != a.Sha256) throw new IOException("Backup file hash mismatch.");
        }
        // Restored files are staged separately; existing defaults and journals are retained.
        var target = Path.Combine(Home, "restored-state");
        if (Directory.Exists(target)) throw new IOException("Restore state already exists.");
        Directory.CreateDirectory(target); using var destination = new Scope(target);
        foreach (var a in artifacts)
        {
            using var file = source.Read(a.Path); if (Bundle.Hash(file) != a.Sha256) throw new IOException("Backup changed."); file.Position = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, a.Path))!);
            using var output = new FileStream(destination.Resolve(a.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            file.CopyTo(output); output.Flush(true);
        }
    }
    static void CopyTree(string source, string target, string relative, List<Artifact> files)
    {
        using var scope = new Scope(source); Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry); var resolved = scope.Resolve(name);
            if (Directory.Exists(resolved)) { CopyTree(resolved, Path.Combine(target, name), relative + "/" + name, files); continue; }
            using var input = scope.Read(name); if (OperatingSystem.IsWindows()) WindowsPublication.Observe(input, true);
            var hash = Bundle.Hash(input); input.Position = 0;
            using (var output = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
            if (Bundle.HashFile(Path.Combine(target, name)) != hash) throw new IOException("Local backup verification failed.");
            files.Add(new(relative + "/" + name, input.Length, hash));
        }
    }
    public void Dispose() { lease.Dispose(); scope.Dispose(); }
}
