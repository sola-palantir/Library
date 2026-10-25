using System.Security.Cryptography;
using Library.Execution;

namespace Library.Companion.Tests;

// Offline test workflow only. No transport, production processor or catalog writer.
public static class QualificationPublication
{
    public record Checkpoint(string Name, string SourceName, FileObservation Output, string Sha256,
        FileObservation Source, string SourceSha256, string PlanJson, string ValidationJson);

    static string Hash(FileStream file)
    {
        file.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        file.Position = 0;
        return hash;
    }

    static void Separate(params string[] roots)
    {
        var paths = roots.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).ToArray();
        foreach (var path in paths)
            if (paths.Count(other => path.Equals(other, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new IOException("Qualification roots must be separate.");
    }

    public static Checkpoint Stage(string stateRoot, string targetRoot, string name, string sourceName,
        ReadOnlyMemory<byte> bytes, FileObservation source, string sourceSha256,
        string planJson, string validationJson)
    {
        Separate(stateRoot, targetRoot);
        ExecutionContract.Hash(sourceSha256);
        if (bytes.Length is < 1 or > 268435456 || planJson.Length > 65536 || validationJson.Length > 65536)
            throw new IOException("Bounded validated qualification output required.");
        using var state = new Scope(stateRoot); using var target = new Scope(targetRoot);
        using var lease = WindowsPublication.Open(state, "qualification.lock", File.Exists(state.Resolve("qualification.lock")));
        var journal = new WorkerJournal(state);
        if (journal.Read<Checkpoint>("qualification.validated.json") != null || File.Exists(target.Resolve(name)))
            throw new IOException("Qualification output already reserved or published.");
        using var output = WindowsPublication.Open(target, name + ".partial");
        output.Write(bytes.Span); output.Flush(true);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (Hash(output) != expected) throw new IOException("Staged read-back differs.");
        var proof = new Checkpoint(name, sourceName, WindowsPublication.Observe(output, true), expected, source, sourceSha256, planJson, validationJson);
        journal.Write("qualification.validated.json", proof);
        return proof;
    }

    public static Checkpoint Recover(string stateRoot, string targetRoot, string sourceRoot,
        string sourceName, CancellationToken cancellation, Action<string>? checkpoint = null)
    {
        Separate(stateRoot, targetRoot, sourceRoot);
        using var state = new Scope(stateRoot); using var target = new Scope(targetRoot); using var source = new Scope(sourceRoot);
        using var lease = WindowsPublication.Open(state, "qualification.lock", File.Exists(state.Resolve("qualification.lock")));
        var journal = new WorkerJournal(state);
        var proof = journal.Read<Checkpoint>("qualification.validated.json") ?? throw new IOException("No validated checkpoint.");
        if (proof.SourceName != sourceName) throw new IOException("Source path differs from the reviewed checkpoint.");
        var completed = journal.Read<Checkpoint>("qualification.completed.json");
        if (completed != null && completed != proof) throw new IOException("Conflicting completion evidence.");
        cancellation.ThrowIfCancellationRequested();
        using var original = source.Read(sourceName);
        if (WindowsPublication.Observe(original) != proof.Source || Hash(original) != proof.SourceSha256 || WindowsPublication.Observe(original) != proof.Source)
            throw new IOException("Source no longer matches the reviewed input.");
        var final = File.Exists(target.Resolve(proof.Name)); var partial = File.Exists(target.Resolve(proof.Name + ".partial"));
        if (final == partial || completed != null && !final) throw new IOException("Missing or ambiguous output; no overwrite or retry encode.");
        using (var file = WindowsPublication.Open(target, final ? proof.Name : proof.Name + ".partial", true))
        {
            if (WindowsPublication.Observe(file, true) != proof.Output || Hash(file) != proof.Sha256)
                throw new IOException("Output differs from validated checkpoint.");
            cancellation.ThrowIfCancellationRequested();
            if (!final) WindowsPublication.Publish(target, file, proof.Name);
            checkpoint?.Invoke("published");
        }
        using (var reopened = WindowsPublication.Open(target, proof.Name, true))
            if (WindowsPublication.Observe(reopened, true) != proof.Output || Hash(reopened) != proof.Sha256)
                throw new IOException("Published read-back differs.");
        if (completed == null) journal.Write("qualification.completed.json", proof);
        return proof;
    }

    public static string NativeFixture(string root)
    {
        var run = Path.Combine(root, "offline-native-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(run, "state"); var target = Path.Combine(run, "media"); var source = Path.Combine(run, "source");
        foreach (var path in new[] { state, target, source }) Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(source, "invented-source.mkv"), "invented input; not playable"u8.ToArray());
        FileObservation observation; string hash;
        using (var scope = new Scope(source)) using (var file = scope.Read("invented-source.mkv")) { observation = WindowsPublication.Observe(file); hash = Hash(file); }
        var proof = Stage(state, target, "invented-output.mkv", "invented-source.mkv", "invented result; not playable"u8.ToArray(), observation, hash, "{}", "{\"synthetic\":true}");
        try { Recover(state, target, source, proof.SourceName, new CancellationToken(true)); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
        if (!File.Exists(Path.Combine(target, proof.Name + ".partial")) || File.Exists(Path.Combine(target, proof.Name))) throw new IOException("Cancellation lost the partial boundary.");
        try { Recover(state, target, source, proof.SourceName, default, _ => throw new IOException("injected-after-publication")); throw new InvalidOperationException("Checkpoint not reached."); } catch (IOException e) when (e.Message == "injected-after-publication") { }
        if (Recover(state, target, source, proof.SourceName, default) != proof || Recover(state, target, source, proof.SourceName, default) != proof) throw new IOException("Recovery was not idempotent.");
        try { Stage(state, target, proof.Name, proof.SourceName, "second output"u8.ToArray(), observation, hash, "{}", "{}"); throw new InvalidOperationException("Second output accepted."); } catch (IOException) { }
        var collisionState = Path.Combine(run, "collision-state"); var collisionTarget = Path.Combine(run, "collision-media");
        Directory.CreateDirectory(collisionState); Directory.CreateDirectory(collisionTarget);
        var collision = Path.Combine(collisionTarget, "collision.mkv"); File.WriteAllText(collision, "retained collision");
        try { Stage(collisionState, collisionTarget, "collision.mkv", proof.SourceName, "wrong"u8.ToArray(), observation, hash, "{}", "{}"); throw new InvalidOperationException("Collision overwritten."); } catch (IOException) { }
        if (File.ReadAllText(collision) != "retained collision") throw new IOException("Collision changed.");
        return ExecutionContract.Serialize(new { run, proof, cancellationRetained = true, afterPublicationRecovery = true, idempotent = true, secondOutputRefused = true, collisionRetained = true, synthetic = true });
    }
}
