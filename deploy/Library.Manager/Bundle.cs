using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Library.Companion;
using Library.Execution;

namespace Library.Manager;

public record Artifact(string Path, long Bytes, string Sha256);
public record Manifest(int Format, string Version, int Protocol, int DeploymentProtocol, int CatalogSchema, string Mode, string ImageId, string ImageFile, bool Signed, Artifact[] Files);

public static class Bundle
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    public static string HashFile(string file) { using var stream = File.OpenRead(file); return Hash(stream); }
    public static bool Digest(string value) => Regex.IsMatch(value, "^[a-f0-9]{64}$");
    public static Manifest Read(string path, string expected)
    {
        if (!Digest(expected)) throw new InvalidDataException("Supply the trusted release manifest SHA-256 (lowercase hex).");
        using var scope = new Scope(path); using var file = scope.Read("release.json");
        if (file.Length > 1024 * 1024 || Hash(file) != expected) throw new InvalidDataException("Release manifest hash mismatch.");
        file.Position = 0; var manifest = JsonSerializer.Deserialize<Manifest>(file, Json) ?? throw new InvalidDataException("Missing manifest.");
        Validate(manifest); return manifest;
    }
    public static void Validate(Manifest m)
    {
        if (m.Format != 1 || m.Protocol != ReleaseInfo.Protocol || m.DeploymentProtocol != ReleaseInfo.DeploymentProtocol || m.CatalogSchema < ReleaseInfo.MinimumDeliverySchema || m.CatalogSchema > ReleaseInfo.CatalogSchema || m.Mode != ReleaseInfo.Mode)
            throw new InvalidDataException("Incompatible release/schema/protocol or enabled production mode.");
        if (!Regex.IsMatch(m.Version, @"^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$") || !Regex.IsMatch(m.ImageId, "^sha256:[a-f0-9]{64}$") || m.Signed)
            throw new InvalidDataException("Invalid release identity. This manager supports explicitly unsigned previews; no signature is being verified.");
        if (m.Files.Length == 0 || m.Files.Length > 4000 || m.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != m.Files.Length)
            throw new InvalidDataException("Invalid or duplicate artifact paths.");
        foreach (var f in m.Files) { Scope.Relative(f.Path); if (!Digest(f.Sha256) || f.Bytes < 1 || f.Path == "release.json") throw new InvalidDataException("Invalid artifact."); }
        if (!m.Files.Any(f => f.Path == m.ImageFile) || !m.Files.Any(f => f.Path == "companion/Library.Companion.exe") || !m.Files.Any(f => f.Path == "manager/Library.Manager.exe")) throw new InvalidDataException("Incomplete delivery bundle.");
        if (m.Files.Any(a => m.Files.Any(b => b.Path.StartsWith(a.Path + "/", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("File/directory collision in bundle.");
    }
    // Copy into a fresh retained release directory. Each Windows source handle denies
    // write/delete while hashed/copied. Failed copies remain unaccepted, never executed.
    public static Manifest Stage(string source, string digest, string target)
    {
        var manifest = Read(source, digest);
        if (Directory.Exists(target)) throw new IOException("Release destination exists.");
        Directory.CreateDirectory(target);
        using var input = new Scope(source); using var output = new Scope(target);
        foreach (var artifact in manifest.Files)
        {
            using var file = input.Read(artifact.Path);
            if (OperatingSystem.IsWindows()) WindowsPublication.Observe(file, requireSingleLink: true);
            if (file.Length != artifact.Bytes || Hash(file) != artifact.Sha256) throw new InvalidDataException($"Corrupt or incomplete artifact: {artifact.Path}");
            file.Position = 0; var full = Path.Combine(target, artifact.Path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var destination = new FileStream(output.Resolve(artifact.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough);
            file.CopyTo(destination); destination.Flush(true);
        }
        using (var file = input.Read("release.json"))
        {
            if (Hash(file) != digest) throw new InvalidDataException("Manifest changed during staging."); file.Position = 0;
            using var destination = new FileStream(output.Resolve("release.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            file.CopyTo(destination); destination.Flush(true);
        }
        Verify(target, digest); return manifest;
    }
    public static Manifest Verify(string path, string digest)
    {
        var manifest = Read(path, digest); using var scope = new Scope(path);
        foreach (var artifact in manifest.Files)
        {
            using var file = scope.Read(artifact.Path);
            if (OperatingSystem.IsWindows()) WindowsPublication.Observe(file, requireSingleLink: true);
            if (file.Length != artifact.Bytes || Hash(file) != artifact.Sha256) throw new InvalidDataException($"Artifact verification failed: {artifact.Path}");
        }
        return manifest;
    }
}
