using System.Text.Json;

namespace Library.Execution;

// Inspection only. No executable, encoder settings, output path or media bytes.
public record ReviewScanCommand(string Id, string SessionId, string RootId, DateTimeOffset DeadlineUtc);
public record ReviewScanFile(string RelativePath, FileObservation Observation, JsonElement Probe);
public record ReviewScanResult(string Id, string SessionId, string RootId, ReviewScanFile[] Files, string ToolVersion);
public static class ReviewScan
{
    public const int MaxFiles = 8;
    public const int MaxResultBytes = 8 * 1024 * 1024;
    public static void Validate(ReviewScanResult result)
    {
        if (!Guid.TryParseExact(result.Id, "N", out _) || !Guid.TryParseExact(result.SessionId, "N", out _) || string.IsNullOrWhiteSpace(result.RootId) || result.RootId.Length > 64)
            throw new ArgumentException("Invalid review scan identity.");
        if (result.Files is null || result.Files.Length > MaxFiles || result.ToolVersion is null || result.ToolVersion.Length > 256 || !result.ToolVersion.StartsWith("ffprobe version "))
            throw new ArgumentException("Invalid bounded review inventory.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files)
        {
            if (file is null) throw new ArgumentException("Missing review inventory entry.");
            var name = file.RelativePath;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => c < 32 || "\\/:<>\"|?*".Contains(c)) || !names.Add(name))
                throw new ArgumentException("Only unique top-level sample filenames are accepted.");
            if (file.Observation is null || file.Observation.Bytes <= 0 || file.Observation.Ticks <= 0 || string.IsNullOrEmpty(file.Observation.Identity) || file.Observation.Identity.Length > 256)
                throw new ArgumentException("Missing native source observation.");
            if (file.Probe.ValueKind != JsonValueKind.Object || file.Probe.TryGetProperty("error", out _) || !file.Probe.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() is < 1 or > 256)
                throw new ArgumentException("Missing bounded FFprobe stream inventory.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(ExecutionContract.Serialize(result)) > MaxResultBytes) throw new ArgumentException("Review inventory exceeds the metadata limit.");
    }
}
