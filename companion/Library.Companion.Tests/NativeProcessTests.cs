using System.Text.Json;
using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;

public class NativeProcessTests
{
    [Fact] public void ArgumentsCannotEscapeWindowsQuoting()
    {
        Assert.Equal("\"\"", NativeProcess.Quote(""));
        Assert.Equal("\"a b\"", NativeProcess.Quote("a b"));
        Assert.Equal("\"C:\\test\\\\\"", NativeProcess.Quote("C:\\test\\"));
        Assert.Equal("\"a\\\"b\"", NativeProcess.Quote("a\"b"));
        Assert.Throws<ArgumentException>(() => NativeProcess.Quote("bad\0argument"));
    }
    [Fact] public void ResourceLimitsCannotBeDisabledOrUnbounded()
    {
        NativeProcessLimits.Sample.Validate();
        foreach (var bad in new[] { NativeProcessLimits.Sample with { MemoryBytes = long.MaxValue }, NativeProcessLimits.Sample with { CpuRate = 0 }, NativeProcessLimits.Sample with { Processes = 10 }, NativeProcessLimits.Sample with { Timeout = Timeout.InfiniteTimeSpan } })
            Assert.Throws<ArgumentException>(bad.Validate);
    }
    [Fact] public void StreamedDurationRequiresFiniteCompletePacketTiming()
    {
        SelectedStream[] streams = [new(0, "video", "h264", "", [])];
        static JsonElement Packet(string pts, string duration) => JsonSerializer.SerializeToElement(new { stream_index = 0, pts_time = pts, duration_time = duration });
        Assert.Equal(2, SampleValidation.PacketDuration([Packet("0", "1"), Packet("1", "1")], streams));
        Assert.Throws<IOException>(() => SampleValidation.PacketDuration([Packet("NaN", "1")], streams));
        Assert.Throws<IOException>(() => SampleValidation.PacketDuration([Packet("0", "0")], streams));
        Assert.Throws<IOException>(() => SampleValidation.PacketDuration([], streams));
    }
    [Fact] public void NativeCommandsOnlyExposeInheritedInputAndBoundedOutput()
    {
        var observation = new FileObservation(4096, 10, "fixture");
        var plan = new SamplePlan(new(1, "stage", 2, ["jpn"], [new(1, "jpn", "main")], true, true, DerivedMedia.EncodeMode), 1, observation,
            [new(0, "video", "h264", "", []), new(1, "audio", "pcm_s16le", "jpn", [])], "native fixture", "signature", Encoding: DerivedMedia.SdrEncoding);
        var args = NativeMediaProcessor.Arguments(plan);
        Assert.Equal("fd:", args[Array.IndexOf(args, "-i") + 1]); Assert.Equal("pipe:1", args[^1]);
        Assert.DoesNotContain("file", args); Assert.DoesNotContain("aac", args); Assert.DoesNotContain("-vf", args);
        Assert.Equal("copy", args[Array.IndexOf(args, "-c") + 1]); Assert.Contains("libx265", args);
        Assert.Equal("demux", args[Array.IndexOf(args, "-enc_time_base:v:0") + 1]);
    }
}
