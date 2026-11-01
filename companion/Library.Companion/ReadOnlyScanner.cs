using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion;

// Native worker adapter with no writer, encoder, media upload or catalog access.
public sealed class ReadOnlyScanner(string rootPath, string toolsRoot)
{
    public async Task<ReviewScanResult> Scan(ReviewScanCommand command, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows inspection is required.");
        var remaining = command.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(3)) throw new ArgumentException("Invalid scan deadline.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(remaining);
        var ct = deadline.Token;
        using var root = new Scope(rootPath);
        if (new DriveInfo(Path.GetPathRoot(root.Root)!).DriveFormat != "NTFS") throw new IOException("Sample inspection requires a local NTFS folder.");
        using var tools = new Scope(toolsRoot); using var binary = tools.Read("bin/ffprobe.exe");
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(binary, ct)) != NativeMediaProcessor.FfprobeSha256) throw new IOException("FFprobe differs from the verified project-local build.");
        var limits = new NativeProcessLimits(512L * 1024 * 1024, 2500, 1, TimeSpan.FromSeconds(45));
        async Task<string> Run(FileStream? input, string[] args, int limit)
        {
            using var buffer = new MemoryStream();
            var evidence = await NativeProcess.Run(tools.Resolve("bin/ffprobe.exe"), args, input, buffer, limits, limit, ct);
            if (evidence.ExitCode != 0 || !string.IsNullOrWhiteSpace(evidence.Stderr)) throw new IOException("FFprobe could not inspect a sample. No media was changed.");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        var version = (await Run(null, ["-version"], 65536)).Split('\n')[0].Trim();
        // A sample session is deliberately not a recursive or bulk library scan.
        var entries = Directory.EnumerateFileSystemEntries(root.Root).Take(129).ToArray();
        if (entries.Length > 128) throw new IOException("Sample folder exceeds 128 entries; choose a small dedicated sample folder.");
        var paths = entries.Where(p => new[] { ".mkv", ".mp4", ".m4v", ".webm" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > ReviewScan.MaxFiles) throw new IOException("Read-only sample sessions accept at most eight media files.");
        var files = new List<ReviewScanFile>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested(); var name = Path.GetFileName(path);
            // Deny writes/deletion through every hardlink while held. Reject reparse traversal.
            using var input = root.Read(name); var before = WindowsPublication.Observe(input);
            await Task.Delay(500, ct);
            if (WindowsPublication.Observe(input) != before) throw new IOException("A source is still changing. Wait for its copy to finish and retry.");
            var json = await Run(input, ["-v", "error", "-max_alloc", "67108864", "-threads", "2", "-probesize", "16777216", "-analyzeduration", "10000000", "-protocol_whitelist", "fd", "-format_whitelist", "matroska,webm,mov,mp4,m4a,3gp,3g2,mj2", "-show_format", "-show_streams", "-show_chapters", "-show_data_hash", "sha256", "-of", "json", "fd:"], 4 * 1024 * 1024);
            if (WindowsPublication.Observe(input) != before) throw new IOException("Source identity changed during inspection.");
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            files.Add(new(name, before, parsed.RootElement.Clone()));
        }
        var result = new ReviewScanResult(command.Id, command.SessionId, command.RootId, files.ToArray(), version);
        ReviewScan.Validate(result); return result;
    }
}
