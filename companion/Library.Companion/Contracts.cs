using System.Security.Cryptography;
using System.Text.Json;

namespace Library.Companion;

public record BackupFile(string Path, long Bytes, string Sha256);
public record FrozenManifest(int SchemaVersion, string DiscId, string Label, string BackupSetId, int ManifestRevision, BackupFile[] Files);
public record CompanionConfig(string StagingRoot, string StateRoot)
{
    public void Validate(bool requireStaging = true)
    {
        string a = System.IO.Path.GetFullPath(StagingRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string b = System.IO.Path.GetFullPath(StateRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staging and state roots must be distinct and nonoverlapping.");
        using var staging = requireStaging ? new Scope(a) : null; using var state = new Scope(b);
    }
}
public record Command(string CommandId, string DiscId, string ManifestSha256, string RecorderId, long ReserveBytes = 1_000_000_000);
public record Media(string RecorderId, int PhysicalType, bool PhysicallyBlank, bool HeuristicallyBlank, long FreeSectors, int SectorBytes, string[] VolumePaths);
public record Evidence(int SchemaVersion, string CommandId, string DiscId, string ManifestSha256, string RecorderId, string State, DateTime AtUtc, string? ImageSha256 = null, long? ImageBytes = null, long? FreeSectors = null, string? Detail = null, bool Simulated = false, string Mode = "hardware", string? OpticalRoot = null, bool ManifestMatched = false, bool AllFilesVerified = false, string? RequestSha256 = null, long Sequence = 0);
public interface IOpticalDevice : IDisposable
{
    bool Simulated { get; }
    Media Inspect(string recorderId);
    IDisposable Acquire(string recorderId);
    void BuildImage(FrozenManifest manifest, string stagingDirectory, byte[] manifestBytes, Stream output);
    void WriteImage(string recorderId, Stream image, Action<string> progress);
    string OpticalRoot(string recorderId, string expectedLabel);
}
public static class Contract
{
    public const string ManifestName = "LIBRARY-MANIFEST.json";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(Stream stream) { var hash = Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position = 0; return hash; }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void Id(string value) { if (value.Length is < 1 or > 80 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Unsafe identifier."); }
    public static void Digest(string value) { if (value.Length != 64 || !value.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid SHA-256."); }
    public static void Validate(FrozenManifest m)
    {
        if (m.SchemaVersion != 1 || m.ManifestRevision < 1 || m.Files.Length == 0 || m.Files.Length > 20000) throw new InvalidDataException("Invalid manifest version or scope.");
        Id(m.DiscId); Id(m.BackupSetId); Id(m.Label);
        if (m.Label.Length > 32) throw new InvalidDataException("Volume label exceeds 32 characters.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files) { Scope.Relative(f.Path); Digest(f.Sha256); if (f.Bytes < 0 || !paths.Add(f.Path) || f.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Duplicate, reserved or invalid file."); }
        if (Library.Execution.ArchivePaths.HasFileDirectoryCollision(paths.Append(ManifestName)))
            throw new InvalidDataException("Manifest files conflict with a required directory.");
    }
    public static void Fit(Media media, long imageBytes, long reserveBytes)
    {
        // IMAPI_MEDIA_TYPE_BDR = 18, including supported single/dual-layer BD-R.
        if (media.PhysicalType != 18 || !media.PhysicallyBlank || !media.HeuristicallyBlank) throw new InvalidOperationException("Only blank write-once BD-R / BD-R DL is supported.");
        // Initial support envelope covers BD-R single/DL; IMAPI type 18 alone also includes BDXL.
        // This is a conservative capacity bound, not a claim to measure physical layer count.
        if (media.FreeSectors > 25_000_000) throw new InvalidOperationException("Media exceeds the initial BD-R single/DL capacity support envelope; BDXL is unsupported.");
        if (media.SectorBytes != 2048 || media.FreeSectors <= 0 || imageBytes <= 0 || imageBytes % 2048 != 0 || reserveBytes < 0) throw new InvalidDataException("Invalid measured capacity or image geometry.");
        if (checked(imageBytes + reserveBytes) > checked(media.FreeSectors * 2048)) throw new InvalidOperationException("Final UDF image plus reserve exceeds measured free sectors.");
    }
}
