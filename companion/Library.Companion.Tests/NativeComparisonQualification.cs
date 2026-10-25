using System.Security.Cryptography;
namespace Library.Companion.Tests;

public static class NativeComparisonQualification
{
    public static byte[] SideBySide(byte[] left, byte[] right, int width, int height)
    {
        if (width is < 1 or > 1920 || height is < 1 or > 3240 || left.Length != checked(width * height * 3) || right.Length != left.Length) throw new IOException("Incomplete comparison pixels.");
        var result = new byte[left.Length * 2]; var row = width * 3;
        for (var y = 0; y < height; y++) { Buffer.BlockCopy(left, y * row, result, y * row * 2, row); Buffer.BlockCopy(right, y * row, result, y * row * 2 + row, row); }
        return result;
    }
    public static string Preflight(string toolsRoot)
    {
        using var scope = new Scope(toolsRoot); using var pin = scope.Read("bin/ffmpeg.exe");
        if (Convert.ToHexStringLower(SHA256.HashData(pin)) != NativeMediaProcessor.FfmpegSha256) throw new IOException("Binary changed.");
        var exe = scope.Resolve("bin/ffmpeg.exe");
        using var session = new SampleReviewSession();
        var generate = NativeProcess.Run(exe, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=24:duration=12", "-c:v", "ffv1", "-threads", "1", "-f", "matroska", "pipe:1"], null, session.Output, NativeProcessLimits.Sample, 4194304, default).GetAwaiter().GetResult();
        if (generate.ExitCode != 0 || generate.Stderr.Length != 0) throw new IOException(generate.Stderr); var hash = session.Seal();
        using var pixels = new MemoryStream();
        var render = NativeProcess.RunBufferedInput(exe, ["-nostdin", "-v", "error", "-xerror", "-threads", "2", "-protocol_whitelist", "pipe", "-i", "pipe:0", "-map", "0:v:0", "-vf", "select='eq(n,12)+eq(n,120)+eq(n,240)',setparams=range=limited,scale=960:540,tile=1x3", "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-threads", "1", "-f", "rawvideo", "pipe:1"], session.Read(), pixels, NativeProcessLimits.Sample, 4665600, default).GetAwaiter().GetResult();
        if (render.ExitCode != 0 || render.Stderr.Length != 0 || pixels.Length != 4665600) throw new IOException("EOF-draining comparison render failed: " + render.Stderr);
        var combined = SideBySide(pixels.ToArray(), pixels.ToArray(), 960, 1620);
        using var png = new MemoryStream();
        var encode = NativeProcess.RunBufferedInput(exe, ["-nostdin", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "1920x1620", "-i", "pipe:0", "-c:v", "png", "-threads", "1", "-f", "image2pipe", "pipe:1"], combined, png, NativeProcessLimits.Sample, 16777216, default).GetAwaiter().GetResult();
        if (encode.ExitCode != 0 || encode.Stderr.Length != 0 || png.Length < 8) throw new IOException("Comparison PNG failed: " + encode.Stderr);
        session.TechnicalPassed(hash); session.VisualReview(hash, Convert.ToHexStringLower(SHA256.HashData(png.ToArray())), false, "Synthetic lifecycle exercise only; no real-media visual acceptance."); session.Complete();
        return Library.Execution.ExecutionContract.Serialize(new { synthetic = true, lifecycle = session.Decision, frames = 3, fullInputConsumed = true, pngBytes = png.Length, outputRetainedThroughDecision = session.Bytes > 0, mediaFiles = 0 });
    }
}
