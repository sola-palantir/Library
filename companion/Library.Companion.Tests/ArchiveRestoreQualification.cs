using System.Text.Json;
using Library.Execution;
using Xunit;

namespace Library.Companion.Tests;

// Test harness only: local invented archive bytes, not a production restore API.
public static class ArchiveRestoreQualification
{
    static FrozenManifest Manifest(Scope archive, string digest, string disc, out FileStream pinned)
    {
        Contract.Digest(digest); Contract.Id(disc);
        pinned = archive.Read(Contract.ManifestName);
        try
        {
            if (pinned.Length > 8 * 1024 * 1024) throw new InvalidDataException("Manifest exceeds bound.");
            using var bytes = new MemoryStream(); pinned.CopyTo(bytes);
            if (Contract.Hash(bytes.ToArray()) != digest) throw new InvalidDataException("Archive manifest digest differs.");
            var manifest = JsonSerializer.Deserialize<FrozenManifest>(bytes.ToArray(), Contract.Json) ?? throw new InvalidDataException("Missing manifest.");
            Contract.Validate(manifest);
            if (manifest.DiscId != disc) throw new InvalidDataException("Wrong archive identity.");
            return manifest;
        }
        catch { pinned.Dispose(); throw; }
    }

    static string Plan(string digest, string disc, BackupFile item) => ExecutionContract.Serialize(new { kind = "synthetic-identical-archive-restore", digest, disc, item });

    public static QualificationPublication.Checkpoint Stage(string archiveRoot, string stateRoot, string targetRoot,
        string digest, string disc, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        new CompanionConfig(archiveRoot, stateRoot).Validate(); new CompanionConfig(archiveRoot, targetRoot).Validate();
        using var archive = new Scope(archiveRoot);
        var manifest = Manifest(archive, digest, disc, out var pinned);
        using (pinned)
        {
            var item = manifest.Files.Single(f => f.Path == path);
            if (item.Bytes is < 1 or > 1024 * 1024) throw new InvalidDataException("Only tiny invented fixtures are supported here.");
            using var input = archive.Read(item.Path);
            var before = WindowsPublication.Observe(input);
            if (before.Bytes != item.Bytes) throw new IOException("Archive payload size differs.");
            var bytes = new byte[checked((int)item.Bytes)]; input.ReadExactly(bytes);
            if (Contract.Hash(bytes) != item.Sha256 || WindowsPublication.Observe(input) != before) throw new IOException("Archive payload differs.");
            cancellation.ThrowIfCancellationRequested();
            return QualificationPublication.Stage(stateRoot, targetRoot, item.Path, item.Path, bytes, before,
                item.Sha256, Plan(digest, disc, item), "{\"syntheticArchive\":true,\"originalAvailable\":false}");
        }
    }

    public static QualificationPublication.Checkpoint Recover(string archiveRoot, string stateRoot, string targetRoot,
        string digest, string disc, string path, CancellationToken cancellation, Action<string>? checkpoint = null)
    {
        cancellation.ThrowIfCancellationRequested();
        new CompanionConfig(archiveRoot, stateRoot).Validate(); new CompanionConfig(archiveRoot, targetRoot).Validate();
        using var archive = new Scope(archiveRoot);
        var manifest = Manifest(archive, digest, disc, out var pinned);
        using (pinned) using (var state = new Scope(stateRoot))
        {
            var item = manifest.Files.Single(f => f.Path == path);
            var proof = new WorkerJournal(state).Read<QualificationPublication.Checkpoint>("qualification.validated.json") ?? throw new IOException("No checkpoint.");
            if (proof.PlanJson != Plan(digest, disc, item) || proof.Sha256 != item.Sha256 || proof.Output.Bytes != item.Bytes || proof.Name != path)
                throw new IOException("Restore checkpoint differs from trusted manifest.");
            return QualificationPublication.Recover(stateRoot, targetRoot, archiveRoot, path, cancellation, checkpoint);
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    static void Refused(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception) { return; }
        throw new Exception("Expected refusal did not occur.");
    }

    public static string Run(string parent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetFullPath(parent), "archive-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var checks = new List<string>();
        var good = new Fixture(root, "complete"); var proof = good.Stage();
        Refused(() => good.Recover(new CancellationToken(true)));
        Require(File.Exists(good.Partial) && !File.Exists(good.Final), "Cancellation lost validated partial.");
        Refused(() => good.Recover(default, _ => throw new IOException("invented interruption after publication")));
        Require(good.Recover() == proof && good.Recover() == proof, "Recovery duplicated or changed output.");
        Require(File.ReadAllBytes(good.Final).SequenceEqual(good.Payload), "Restored bytes differ.");
        Require(!Directory.Exists(Path.Combine(root, "original")), "Original unexpectedly available.");
        using (var archive = new Scope(good.Archive)) using (var input = archive.Read(good.Name))
        using (var restored = new Scope(good.Target)) using (var output = restored.Read(good.Name))
            Require(WindowsPublication.Observe(input).Identity != WindowsPublication.Observe(output, true).Identity, "Restore is an alias, not an independent file.");
        checks.Add("archive-only restoration: distinct file identity, exact bytes, cancellation, interruption and idempotent recovery");
        foreach (var scenario in new[] { "manifest-changed", "payload-same-size-time", "payload-missing", "wrong-disc", "collision", "pre-cancel" })
        {
            var f = new Fixture(root, scenario);
            if (scenario == "manifest-changed") File.AppendAllText(f.ManifestPath, " ");
            if (scenario == "payload-same-size-time") f.Corrupt(f.Input);
            if (scenario == "payload-missing") File.Move(f.Input, f.Input + ".unavailable"); // Only invented fixture, retained.
            if (scenario == "collision") File.WriteAllText(f.Final, "retained unrelated fixture");
            Refused(() => Stage(f.Archive, f.State, f.Target, f.Digest, scenario == "wrong-disc" ? "wrong-disc" : f.Disc, f.Name,
                new CancellationToken(scenario == "pre-cancel")));
            Require(!File.Exists(f.Partial), "Rejected preflight wrote output.");
            if (scenario == "collision") Require(File.ReadAllText(f.Final) == "retained unrelated fixture", "Collision overwritten.");
            else Require(!File.Exists(f.Final), "Rejected restore published output.");
            checks.Add(scenario + " refused before output");
        }
        foreach (var scenario in new[] { "changed-archive-after-stage", "changed-partial", "changed-manifest-after-stage" })
        {
            var f = new Fixture(root, scenario); f.Stage();
            if (scenario == "changed-archive-after-stage") f.Corrupt(f.Input);
            else if (scenario == "changed-partial") f.Corrupt(f.Partial);
            else File.AppendAllText(f.ManifestPath, " ");
            Refused(() => f.Recover()); Require(File.Exists(f.Partial) && !File.Exists(f.Final), "Changed evidence was published or discarded.");
            checks.Add(scenario + " refused, partial retained");
        }
        Require(File.ReadAllBytes(good.Input).SequenceEqual(good.Payload), "Healthy archive changed.");
        var report = ExecutionContract.Serialize(new { root, checks, synthetic = true, originalAvailable = false, sameDevice = true,
            independentHardware = false, optical = false, realMediaReadOrCopied = false, filesDeleted = 0, restoredSha256 = proof.Sha256 });
        File.WriteAllText(Path.Combine(root, "result.json"), report); return report;
    }

    sealed class Fixture
    {
        public string Archive, State, Target, Input, ManifestPath, Digest;
        public string Name = "invented-payload.bin", Disc = "invented-disc";
        public byte[] Payload = Enumerable.Range(0, 1048576).Select(i => (byte)((i * 17 + 3) % 251)).ToArray();
        public string Final => Path.Combine(Target, Name); public string Partial => Final + ".partial";
        public Fixture(string root, string name)
        {
            var home = Path.Combine(root, name); Archive = Path.Combine(home, "archive"); State = Path.Combine(home, "state"); Target = Path.Combine(home, "restored");
            foreach (var path in new[] { Archive, State, Target }) Directory.CreateDirectory(path);
            Input = Path.Combine(Archive, Name); File.WriteAllBytes(Input, Payload);
            var manifest = new FrozenManifest(1, Disc, "QUALIFICATION-ONLY", "invented-set", 1, [new(Name, Payload.Length, Contract.Hash(Payload))]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Contract.Json); ManifestPath = Path.Combine(Archive, Contract.ManifestName); File.WriteAllBytes(ManifestPath, bytes); Digest = Contract.Hash(bytes);
        }
        public QualificationPublication.Checkpoint Stage() => ArchiveRestoreQualification.Stage(Archive, State, Target, Digest, Disc, Name, default);
        public QualificationPublication.Checkpoint Recover(CancellationToken ct = default, Action<string>? at = null) => ArchiveRestoreQualification.Recover(Archive, State, Target, Digest, Disc, Name, ct, at);
        public void Corrupt(string path) { var time = File.GetLastWriteTimeUtc(path); using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) { file.WriteByte(0xff); file.Flush(true); } File.SetLastWriteTimeUtc(path, time); }
    }
}

public class ArchiveManifestIntegrityTests
{
    [Theory]
    [InlineData("movie.mkv", "movie.mkv/child")]
    [InlineData("Folder/file.mkv", "folder")]
    [InlineData("LIBRARY-MANIFEST.json/child", "movie.mkv")]
    public void FileDirectoryAndManifestCollisionsAreRefused(string first, string second)
    {
        var manifest = new FrozenManifest(1, "disc", "LABEL", "set", 1,
            [new(first, 1, new string('a', 64)), new(second, 1, new string('b', 64))]);
        Assert.Throws<InvalidDataException>(() => Contract.Validate(manifest));
    }
    [Fact] public void SiblingFilesInTheSameDirectoryAreAllowed() => Contract.Validate(new(1, "disc", "LABEL", "set", 1,
        [new("folder/a.bin", 1, new string('a', 64)), new("folder/b.bin", 1, new string('b', 64))]));
}
