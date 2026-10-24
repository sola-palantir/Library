using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion.Tests;

// Explicit synthetic-only diagnostic. Lossless mode is a pixel-value oracle,
// never an alternate Library encoding profile or a real-media processing path.
public static class NativeColorQualification
{
    public static string Run(string toolsRoot)
    {
        using var tools = new Scope(toolsRoot);
        using var encoder = tools.Read("bin/ffmpeg.exe");
        using var probe = tools.Read("bin/ffprobe.exe");
        if (Convert.ToHexStringLower(SHA256.HashData(encoder)) != NativeMediaProcessor.FfmpegSha256 ||
            Convert.ToHexStringLower(SHA256.HashData(probe)) != NativeMediaProcessor.FfprobeSha256)
            throw new IOException("Qualification binaries changed.");
        // One 64x64 YUV420 ramp spans all possible luma/chroma code values.
        var pixels = Enumerable.Range(0, 64 * 64 * 3 / 2).Select(i => (byte)(i % 256)).ToArray();
        var checks = new List<object>();
        NativeProcessEvidence Run(bool inspect, string[] args, byte[] input, Stream output, long bound) {
            var result = NativeProcess.RunBufferedInput(tools.Resolve(inspect ? "bin/ffprobe.exe" : "bin/ffmpeg.exe"), args, input, output,
                NativeProcessLimits.Sample, bound, default).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || result.Stderr.Length != 0) throw new IOException("Color fixture failed: " + result.Stderr);
            return result;
        }
        foreach (var mode in new[] { "unknown", "tv", "pc", "metadata-only", "converted" })
        {
            using var encoded = new MemoryStream();
            var range = mode is "metadata-only" or "converted" ? "pc" : mode == "unknown" ? "unspecified" : mode;
            var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "pipe", "-f", "rawvideo",
                "-pixel_format", "yuv420p", "-video_size", "64x64", "-framerate", "24", "-color_range", range, "-i", "pipe:0", "-frames:v", "1" };
            if (mode == "metadata-only") args.AddRange(["-vf", "setparams=range=limited"]);
            if (mode == "converted") args.AddRange(["-vf", "scale=in_range=full:out_range=tv"]);
            args.AddRange(["-c:v", "libx265", "-preset", "slow", "-pix_fmt", "yuv420p", "-x265-params", "lossless=1:pools=none:frame-threads=1:log-level=error", "-f", "matroska", "pipe:1"]);
            Run(false, args.ToArray(), pixels, encoded, 1024 * 1024);
            var bytes = encoded.ToArray();
            using var json = new MemoryStream();
            Run(true, ["-v", "error", "-protocol_whitelist", "pipe", "-count_packets", "-show_streams", "-of", "json", "pipe:0"], bytes, json, 65536);
            using var document = JsonDocument.Parse(json.ToArray());
            var declared = DerivedMedia.Text(document.RootElement.GetProperty("streams")[0], "color_range");
            using var decoded = new MemoryStream();
            // No forced output pixel format: avoid introducing a conversion in
            // the measurement itself when a decoder reports yuvj420p.
            Run(false, ["-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "pipe", "-i", "pipe:0", "-map", "0:v:0", "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1"], bytes, decoded, pixels.Length);
            var actual = decoded.ToArray(); var same = actual.SequenceEqual(pixels);
            if (actual.Length != pixels.Length || same != (mode != "converted") || declared != (mode == "pc" ? "pc" : "tv"))
                throw new IOException("Color signaling/pixel oracle differed from the expected controlled fixture.");
            checks.Add(new { mode, inputRange = range, outputRange = declared, pixelValuesIdentical = same,
                sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)), decodedSha256 = Convert.ToHexStringLower(SHA256.HashData(actual)),
                firstLuma = actual[0], lastRampLuma = actual[255] });
        }
        return ExecutionContract.Serialize(new { checks, syntheticOnly = true, mediaFiles = 0, realMediaRangeInference = false });
    }
}
