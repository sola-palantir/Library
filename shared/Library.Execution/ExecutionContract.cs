using System.Text.Json;

namespace Library.Execution;

// Control data only: no executable, command-line, URL, secret, absolute path or media bytes.
public record WorkerSource(string RootId, string RelativePath, FileObservation Observation, string Sha256);
public record WorkerCommand(int Version, string JobId, WorkerSource Source, string TargetRootId, SamplePlan Plan);
public record WorkerValidation(string Kind, string Detail);
public record WorkerResult(int Version, string JobId, string CommandSha256, WorkerSource Source,
    string TargetRootId, string RelativePath, FileObservation Output, string Sha256, WorkerValidation Validation);
public interface IMediaExecutor
{
    Task<WorkerSource> InspectAsync(string rootId, string relativePath, CancellationToken ct);
    Task<WorkerResult> ExecuteAsync(WorkerCommand command, CancellationToken ct);
    Task<WorkerResult> ReconcileAsync(WorkerCommand command, CancellationToken ct);
}
public static class ExecutionContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const long MaximumOutputBytes = 268435456;
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static string Digest(WorkerCommand command) => DerivedMedia.Digest(Serialize(command));
    public static string Destination(WorkerCommand command) => $"sample-{command.JobId}.mkv";
    public static void Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !"0123456789abcdef".Contains(c))) throw new ArgumentException("Invalid SHA-256.");
    }
    public static void Validate(WorkerCommand command)
    {
        if (command is null || command.Source is null || command.Plan is null || command.Plan.Request is null || command.Source.Observation is null || command.Plan.Streams is null)
            throw new ArgumentException("Incomplete worker command.");
        if (command.Version != 1 || !Guid.TryParseExact(command.JobId, "N", out _)) throw new ArgumentException("Unsupported worker contract or job ID.");
        if (Serialize(command).Length > 65536) throw new ArgumentException("Oversized worker command.");
        Hash(command.Source.Sha256);
        if (string.IsNullOrWhiteSpace(command.Source.RootId) || string.IsNullOrWhiteSpace(command.TargetRootId) || string.IsNullOrWhiteSpace(command.Source.RelativePath) || command.Plan.SourceAssetId <= 0)
            throw new ArgumentException("Catalog asset and worker-owned paths are required.");
        if (command.Source.Observation.Bytes <= 0 || string.IsNullOrEmpty(command.Source.Observation.Identity) || command.Source.Observation != command.Plan.SourceObservation)
            throw new ArgumentException("Source identity must match the immutable preview.");
        if (command.Plan.Request.TargetRootId != command.TargetRootId || command.Plan.Request.Seconds is < 1 or > 30 || !command.Plan.Request.ConfirmSourceComplete || !command.Plan.Request.ConfirmSdr)
            throw new ArgumentException("Explicit bounded sample review is required.");
        DerivedMedia.ValidateProfile(command.Plan);
    }
    // Call only after an authenticated transport has associated this reply with its worker.
    // Matching hashes are correlation/integrity checks, not authentication.
    public static void Match(WorkerCommand command, WorkerResult result, bool allowSynthetic = false)
    {
        if (result is null || result.Source is null || result.Output is null || result.Validation is null) throw new InvalidDataException("Incomplete worker receipt.");
        Validate(command); Hash(result.Sha256);
        if (result.Version != 1 || result.JobId != command.JobId || result.CommandSha256 != Digest(command) || result.Source != command.Source ||
            result.TargetRootId != command.TargetRootId || result.RelativePath != Destination(command) || result.Output.Bytes is <= 0 or > MaximumOutputBytes || string.IsNullOrEmpty(result.Output.Identity))
            throw new InvalidDataException("Worker receipt does not match the immutable command.");
        if (result.Validation.Kind != "media-validated" && !(allowSynthetic && result.Validation.Kind == "synthetic"))
            throw new InvalidDataException("Synthetic or unqualified output cannot become catalog media evidence.");
    }
}
