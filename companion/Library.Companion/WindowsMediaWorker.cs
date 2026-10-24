using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion;

public record WorkerRoot(string Id, string Path, string Role);
public record WindowsWorkerConfiguration(string StateRoot, WorkerRoot[] Roots);

// Trusted in-process adapter, never arbitrary commands supplied by a client.
// The native adapter is qualified with synthetic fixtures. Production activation
// stays unavailable until its separate media/transport gates are satisfied.
public interface IStagedMediaProcessor
{
    string Qualification { get; }
    Task CheckSourceAsync(Stream input, SamplePlan plan, CancellationToken ct);
    Task ProcessAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct);
    Task<WorkerValidation> ValidateAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct);
}
public sealed class UnavailableWindowsProcessor : IStagedMediaProcessor
{
    public string Qualification => "unqualified";
    private static Exception Blocked() => new InvalidOperationException("Native media execution is disabled. Supply the explicitly approved synthetic adapter; production activation is not configured.");
    public Task CheckSourceAsync(Stream input, SamplePlan plan, CancellationToken ct) => throw Blocked();
    public Task ProcessAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct) => throw Blocked();
    public Task<WorkerValidation> ValidateAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct) => throw Blocked();
}

// No database, listener, credentials, media transfer or automatic service installation.
// All state and output publication uses the same native no-overwrite primitive.
public sealed class WindowsMediaWorker : IMediaExecutor
{
    readonly WindowsWorkerConfiguration configuration;
    readonly IStagedMediaProcessor processor;
    readonly Action<string>? checkpoint;
    public WindowsMediaWorker(WindowsWorkerConfiguration configuration, IStagedMediaProcessor? processor = null, Action<string>? checkpoint = null)
    {
        // Copy caller-owned collections before any asynchronous operation.
        this.configuration = configuration with { Roots = configuration.Roots.ToArray() };
        this.processor = processor ?? new UnavailableWindowsProcessor();
        this.checkpoint = checkpoint;
    }
    WorkerRoot Root(string id) => configuration.Roots.SingleOrDefault(r => r.Id == id) ?? throw new ArgumentException("Unknown worker-owned root.");
    void ValidateRoots()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This executor requires native Windows; no Linux fallback.");
        if (configuration.Roots.Length == 0 || configuration.Roots.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Roots.Length || configuration.Roots.Any(r => r.Role is not ("source" or "library" or "staging")))
            throw new ArgumentException("Unique worker root IDs and explicit roles required.");
        var declared = configuration.Roots.Select(r => r.Path).Append(configuration.StateRoot).ToArray();
        if (declared.Any(p => !System.IO.Path.IsPathFullyQualified(p))) throw new ArgumentException("Worker roots must be explicit absolute local paths.");
        var paths = declared.Select(p => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/')).ToArray();
        foreach (var path in paths)
        {
            using var scope = new Scope(path);
            if (new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveFormat != "NTFS") throw new IOException("Only local NTFS worker roots are qualified.");
            if (paths.Count(other => path.Equals(other, StringComparison.OrdinalIgnoreCase) || path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new ArgumentException("Worker state and media roots must be separate, non-overlapping directories.");
        }
    }
    static async Task<string> Hash(FileStream file, CancellationToken ct)
    {
        file.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        file.Position = 0; return hash;
    }
    public async Task<WorkerSource> InspectAsync(string rootId, string relativePath, CancellationToken ct)
    {
        ValidateRoots(); ct.ThrowIfCancellationRequested();
        using var state = new Scope(configuration.StateRoot);
        using var lease = WindowsPublication.Open(state, "media-worker.lock", File.Exists(state.Resolve("media-worker.lock")));
        var root = Root(rootId);
        if (root.Role is not ("source" or "library")) throw new ArgumentException("Inspect a source or library root.");
        using var scope = new Scope(root.Path); using var file = scope.Read(relativePath);
        var before = WindowsPublication.Observe(file); var hash = await Hash(file, ct);
        if (WindowsPublication.Observe(file) != before) throw new IOException("Source changed while inspecting.");
        return new(rootId, relativePath, before, hash);
    }
    public Task<WorkerResult> ExecuteAsync(WorkerCommand command, CancellationToken ct) => Run(command, false, ct);
    public Task<WorkerResult> ReconcileAsync(WorkerCommand command, CancellationToken ct) => Run(command, true, ct);
    async Task<WorkerResult> Run(WorkerCommand inputCommand, bool reconcileOnly, CancellationToken cancellation)
    {
        // Freeze mutable arrays in the plan. Digest binds every selected stream and review.
        var command = JsonSerializer.Deserialize<WorkerCommand>(ExecutionContract.Serialize(inputCommand), ExecutionContract.Json)!;
        ExecutionContract.Validate(command); ValidateRoots();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); var ct = timeout.Token; ct.ThrowIfCancellationRequested();
        var sourceRoot = Root(command.Source.RootId); var targetRoot = Root(command.TargetRootId);
        if (sourceRoot.Role is not ("source" or "library") || targetRoot.Role != "staging") throw new ArgumentException("Samples read source/library and write isolated staging only.");
        using var state = new Scope(configuration.StateRoot);
        // One worker process/job at a time, including journal reconciliation.
        using var lease = WindowsPublication.Open(state, "media-worker.lock", File.Exists(state.Resolve("media-worker.lock")));
        using var source = new Scope(sourceRoot.Path); using var target = new Scope(targetRoot.Path);
        using var original = source.Read(command.Source.RelativePath);
        if (WindowsPublication.Observe(original) != command.Source.Observation || await Hash(original, ct) != command.Source.Sha256)
            throw new IOException("Source changed since review; supplied input is retained.");
        var journal = new WorkerJournal(state);
        var digest = ExecutionContract.Digest(command);
        var intent = journal.Read<WorkerCommand>(command.JobId + ".intent.json");
        if (intent != null && ExecutionContract.Digest(intent) != digest) throw new IOException("Job ID already belongs to another immutable command.");
        foreach (var other in journal.Intents())
            if (other.JobId != command.JobId && (other.Plan.SourceAssetId == command.Plan.SourceAssetId || other.Source.Observation.Identity == command.Source.Observation.Identity))
                throw new IOException("This source already owns an output reservation; another output is refused.");
        var finalName = ExecutionContract.Destination(command); var partialName = finalName + ".partial";
        var finalExists = File.Exists(target.Resolve(finalName)); var partialExists = File.Exists(target.Resolve(partialName));
        var validated = journal.Read<WorkerResult>(command.JobId + ".validated.json");
        var completed = journal.Read<WorkerResult>(command.JobId + ".completed.json");
        if (completed != null || validated != null)
        {
            if (intent == null || completed != null && validated != completed) throw new IOException("Incomplete or inconsistent worker journal; review required.");
            var expected = completed ?? validated!;
            ExecutionContract.Match(command, expected, allowSynthetic: true);
            if (finalExists == partialExists || completed != null && !finalExists) throw new IOException("Missing or ambiguous owned output; reconcile manually without deleting files.");
            using var retained = WindowsPublication.Open(target, finalExists ? finalName : partialName, true);
            if (WindowsPublication.Observe(retained, true) != expected.Output || await Hash(retained, ct) != expected.Sha256)
                throw new IOException("Retained output differs from the validated checkpoint.");
            ct.ThrowIfCancellationRequested();
            if (!finalExists) WindowsPublication.Publish(target, retained, finalName);
            checkpoint?.Invoke("published");
            if (completed == null) journal.Write(command.JobId + ".completed.json", expected);
            return expected;
        }
        if (reconcileOnly) throw new IOException("No validated checkpoint exists. Processing was not restarted.");
        if (finalExists || partialExists) throw new IOException("Existing output or interrupted partial retained; no overwrite or automatic re-encode.");
        // Check/re-probe against shared selection policy before reserving or creating output.
        await processor.CheckSourceAsync(original, command.Plan, ct); original.Position = 0;
        if (processor.Qualification is not ("synthetic" or "media-validated")) throw new IOException("Processor is not qualified.");
        ct.ThrowIfCancellationRequested();
        if (intent == null) journal.Write(command.JobId + ".intent.json", command);
        checkpoint?.Invoke("reserved");
        using (var output = WindowsPublication.Open(target, partialName))
        {
            using var bounded = new BoundedOutput(output, ExecutionContract.MaximumOutputBytes, ct);
            await processor.ProcessAsync(original, bounded, command.Plan, ct);
            ct.ThrowIfCancellationRequested(); output.Flush(true); output.Position = 0; original.Position = 0;
            var validation = await processor.ValidateAsync(original, output, command.Plan, ct);
            if (validation.Kind != processor.Qualification || validation.Detail.Length > 65536) throw new IOException("Unexpected processor validation evidence.");
            var observation = WindowsPublication.Observe(output, true);
            if (observation.Bytes is <= 0 or > ExecutionContract.MaximumOutputBytes) throw new IOException("Output size is outside the bounded sample envelope.");
            if (WindowsPublication.Observe(original) != command.Source.Observation || await Hash(original, ct) != command.Source.Sha256) throw new IOException("Source identity/content changed during processing.");
            var result = new WorkerResult(1, command.JobId, digest, command.Source, command.TargetRootId, finalName, observation, await Hash(output, ct), validation);
            journal.Write(command.JobId + ".validated.json", result); checkpoint?.Invoke("validated");
            ct.ThrowIfCancellationRequested(); WindowsPublication.Publish(target, output, finalName); checkpoint?.Invoke("published");
        }
        // Reopen under an exclusive handle and verify the checkpoint before recording completion.
        var expectedResult = journal.Read<WorkerResult>(command.JobId + ".validated.json")!;
        using (var published = WindowsPublication.Open(target, finalName, true))
            if (WindowsPublication.Observe(published, true) != expectedResult.Output || await Hash(published, ct) != expectedResult.Sha256) throw new IOException("Published output verification failed; files retained.");
        journal.Write(command.JobId + ".completed.json", expectedResult);
        return expectedResult;
    }
}

internal sealed class WorkerJournal(Scope state)
{
    public T? Read<T>(string name) where T : class
    {
        if (Directory.EnumerateFiles(state.Root, "*.journal.partial").Any()) throw new IOException("Interrupted journal write requires review; no output will be changed.");
        if (!File.Exists(state.Resolve(name))) return null;
        using var file = state.Read(name);
        if (file.Length > 131072) throw new IOException("Oversized worker journal record.");
        return JsonSerializer.Deserialize<T>(file, ExecutionContract.Json) ?? throw new IOException("Invalid worker journal record.");
    }
    public IEnumerable<WorkerCommand> Intents()
    {
        foreach (var path in Directory.EnumerateFiles(state.Root, "*.intent.json"))
        {
            var command = Read<WorkerCommand>(System.IO.Path.GetFileName(path))!; ExecutionContract.Validate(command);
            if (System.IO.Path.GetFileName(path) != command.JobId + ".intent.json") throw new IOException("Worker reservation filename differs from command.");
            yield return command;
        }
    }
    public void Write<T>(string name, T record)
    {
        // Deterministic partial: a torn write blocks retries instead of being discarded.
        using var file = WindowsPublication.Open(state, name + ".journal.partial");
        JsonSerializer.Serialize(file, record, ExecutionContract.Json);
        WindowsPublication.Publish(state, file, name);
    }
}

internal sealed class BoundedOutput(Stream inner, long limit, CancellationToken ct) : Stream
{
    long written;
    void Reserve(int count) { ct.ThrowIfCancellationRequested(); if (count > limit - written) throw new IOException("Output byte limit reached."); written += count; }
    public override void Write(byte[] buffer, int offset, int count) { Reserve(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Reserve(buffer.Length); await inner.WriteAsync(buffer, ct); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => inner.Flush();
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
