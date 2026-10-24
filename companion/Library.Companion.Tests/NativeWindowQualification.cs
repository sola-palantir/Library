using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion.Tests;
public static class NativeWindowQualification
{
    public static string Run(string parent, string toolsRoot)
    {
        var home = Path.Combine(parent, "window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        using var tools = new Scope(toolsRoot);
        using var binary = tools.Read("bin/ffmpeg.exe");
        if (Convert.ToHexStringLower(SHA256.HashData(binary)) != NativeMediaProcessor.FfmpegSha256) throw new IOException("Binary changed.");
        var source = Path.Combine(home, "invented.mkv");
        using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var generate = NativeProcess.Run(tools.Resolve("bin/ffmpeg.exe"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=24:duration=4", "-c:v", "libx264", "-g", "24", "-keyint_min", "24", "-sc_threshold", "0", "-bf", "0", "-threads", "2", "-color_range", "tv", "-f", "matroska", "pipe:1"], null, output, NativeProcessLimits.Sample, 1024 * 1024, default).GetAwaiter().GetResult();
            if (generate.ExitCode != 0 || generate.Stderr.Length != 0) throw new IOException(generate.Stderr); output.Flush(true);
        }
        using var scope = new Scope(home); using var input = scope.Read("invented.mkv");
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(input));
        using var processor = new NativeMediaProcessor(toolsRoot, [sourceHash]);
        var checks = new List<object>();
        foreach (var live in new[] { false, true })
        {
            using var output = new MemoryStream();
            var args = new List<string> { "-nostdin", "-v", "error", "-protocol_whitelist", "fd", "-ss", "1.5", "-i", "fd:", "-t", "1", "-map", "0:v:0", "-c", "copy", "-f", "matroska" };
            if (live) args.AddRange(["-live", "1"]); args.AddRange(["-protocol_whitelist", "pipe", "pipe:1"]);
            var run = NativeProcess.Run(tools.Resolve("bin/ffmpeg.exe"), args.ToArray(), input, output, NativeProcessLimits.Sample, 1024 * 1024, default).GetAwaiter().GetResult();
            if (run.ExitCode != 0 || run.Stderr.Length != 0) throw new IOException(run.Stderr);
            using var json = new MemoryStream();
            var probe = NativeProcess.RunBufferedInput(tools.Resolve("bin/ffprobe.exe"), ["-v", "error", "-protocol_whitelist", "pipe", "-show_format", "-show_packets", "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json", "pipe:0"], output.ToArray(), json, NativeProcessLimits.Sample, 1024 * 1024, default).GetAwaiter().GetResult();
            if (probe.ExitCode != 0 || probe.Stderr.Length != 0) throw new IOException(probe.Stderr);
            using var document = JsonDocument.Parse(json.ToArray()); var root = document.RootElement;
            var duration = SampleValidation.PacketDuration(root.GetProperty("packets").EnumerateArray().ToArray(), [new(0, "video", "h264", "", [])]);
            var declared = DerivedMedia.Text(root.GetProperty("format"), "duration");
            if (duration < 1.4 || duration > 1.6 || live && declared != "" || !live && declared != "1.000000") throw new IOException("Window duration oracle differs.");
            checks.Add(new { live, declaredDuration = declared, measuredDuration = duration });
        }
        input.Position = 0;
        if (Convert.ToHexStringLower(SHA256.HashData(input)) != sourceHash) throw new IOException("Synthetic source changed.");
        return ExecutionContract.Serialize(new { home, checks, sourcePreserved = true, outputMediaFiles = 0, actualWindowMustBeReported = true });
    }
}
