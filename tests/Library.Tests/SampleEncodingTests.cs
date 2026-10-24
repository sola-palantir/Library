using System.Text.Json;
using System.Text.Json.Nodes;
using Library.Api;
using Xunit;

namespace Library.Tests;

public sealed class SampleEncodingTests
{
    private static JsonElement Probe() => JsonSerializer.SerializeToElement(new {
        format = new { format_name = "matroska,webm", start_time = "0", duration = "2" },
        streams = new object[] {
            new { index = 0, codec_type = "video", codec_name = "h264", profile = "High", pix_fmt = "yuv420p", width = 1920, height = 1080, field_order = "progressive", r_frame_rate = "24000/1001", disposition = new { } },
            new { index = 2, codec_type = "audio", codec_name = "dts", disposition = new { original = 1 }, tags = new { language = "jpn" } },
            new { index = 3, codec_type = "subtitle", codec_name = "hdmv_pgs_subtitle", disposition = new { hearing_impaired = 1 }, tags = new { language = "eng" } }
        }
    });
    private static SampleRequest Request() => new(1, "staging", 2, ["jpn"], [new(2, "jpn", "main"), new(3, "eng", "subtitle")], true, true, DerivedMedia.EncodeMode);
    private static SamplePlan Plan() => new(Request(), 1, new(123, 4, "invented"), DerivedMedia.Select(Probe(), Request()), "fixture", "fixture", Encoding: DerivedMedia.SdrEncoding);
    private static JsonElement Frame(double time, int width = 1920, int size = 100) => JsonSerializer.SerializeToElement(new { best_effort_timestamp_time = time, width, height = 1080, pix_fmt = "yuv420p", sample_aspect_ratio = "1:1", interlaced_frame = 0, pkt_size = size });
    private static JsonElement Packet(int index, string hash, double time) => JsonSerializer.SerializeToElement(new { stream_index = index, data_hash = "SHA256:" + hash, size = "100", pts_time = time });
    [Fact]
    public void EncodingNeedsItsOwnOptInEvenWhenStreamCopyIsEnabled()
    {
        var options = new LibraryOptions { FfmpegPath = typeof(SampleEncodingTests).Assembly.Location, FfprobePath = typeof(SampleEncodingTests).Assembly.Location, EnableSampleRemux = true };
        var storage = new Storage(options);
        var service = new SampleRemuxService(storage, options, new(storage, options), null!, null!);
        Assert.True(service.Available); Assert.False(service.EncodeAvailable); Assert.False(service.CanExecute(Plan()));
        options.EnableSampleEncode = true; options.EnableSampleRemux = false;
        Assert.False(service.Available); Assert.True(service.EncodeAvailable); Assert.True(service.CanExecute(Plan()));
        Assert.False(options.EnableSampleHandoff);
    }
    [Fact]
    public void FixedProfileCannotAcceptArbitraryArgumentsOrAlterAudioPolicy()
    {
        var plan = Plan(); var args = DerivedMedia.Arguments("pinned-input", "pinned-output", plan);
        Assert.Equal("video-encode-sample", DerivedMedia.Kind(plan));
        Assert.Equal("copy", args[Array.IndexOf(args, "-c") + 1]);
        Assert.Equal("libx265", args[Array.IndexOf(args, "-c:v:0") + 1]);
        Assert.Equal("22", args[Array.IndexOf(args, "-crf:v:0") + 1]);
        Assert.Equal("slow", args[Array.IndexOf(args, "-preset:v:0") + 1]);
        Assert.Equal("passthrough", args[Array.IndexOf(args, "-fps_mode:v:0") + 1]);
        Assert.Equal("7", args[Array.IndexOf(args, "-t") + 1]);
        Assert.True(Array.IndexOf(args, "-t") < Array.IndexOf(args, "-i"));
        Assert.Equal("2", args[Array.LastIndexOf(args, "-t") + 1]);
        Assert.Contains("-max_muxing_queue_size", args); Assert.Contains("-muxing_queue_data_threshold", args);
        Assert.DoesNotContain("-r", args); Assert.DoesNotContain("-vf", args); Assert.DoesNotContain("-af", args);
        Assert.Throws<ArgumentException>(() => DerivedMedia.Arguments("i", "o", plan with { Encoding = plan.Encoding! with { Crf = 18 } }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.ValidateProfile(plan with { Encoding = null }));
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(Probe(), Request() with { Mode = "full-movie" }));
    }
    [Fact]
    public async Task WorkingSetBudgetStopsTheChildInsteadOfPublishing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var failure = await Assert.ThrowsAsync<IOException>(() => ProbeProcess.Run("/bin/sleep", ["5"], 100, CancellationToken.None, 1));
        Assert.Contains("resident-memory budget", failure.Message);
    }
    [Fact]
    public void PacketWindowCopiesOriginalIndexesAndEncoderConsumesRenumberedIndexes()
    {
        var producer = DerivedMedia.PacketWindowArguments("pinned-source", Plan());
        var consumer = DerivedMedia.EncodeWindowArguments("owned-partial", Plan());
        static string[] Maps(string[] args) => args.Select((v, i) => (v, i)).Where(x => x.v == "-map").Select(x => args[x.i + 1]).ToArray();
        Assert.Equal(new[] { "0:0", "0:2", "0:3" }, Maps(producer));
        Assert.Equal(new[] { "0:0", "0:1", "0:2" }, Maps(consumer));
        Assert.DoesNotContain("libx265", producer); Assert.Equal("pipe:1", producer[^1]);
        Assert.Equal("pipe:0", consumer[Array.IndexOf(consumer, "-i") + 1]); Assert.Equal("owned-partial", consumer[^1]);
        Assert.Equal("file", producer[Array.IndexOf(producer, "-protocol_whitelist") + 1]);
        Assert.Equal("file,pipe", consumer[Array.IndexOf(consumer, "-protocol_whitelist") + 1]);
        Assert.Equal("copy", consumer[Array.IndexOf(consumer, "-c") + 1]);
    }
    [Theory]
    [InlineData("width", "3840")]
    [InlineData("height", "2160")]
    [InlineData("width", "1919")]
    [InlineData("field_order", "tt")]
    [InlineData("field_order", "unknown")]
    [InlineData("r_frame_rate", "120/1")]
    [InlineData("r_frame_rate", "0/0")]
    [InlineData("codec_name", "hevc")]
    [InlineData("color_transfer", "smpte2084")]
    public void UnsupportedSourceCannotEnableEncoding(string key, string value)
    {
        var probe = JsonNode.Parse(Probe().GetRawText())!;
        probe["streams"]![0]![key] = key is "width" or "height" ? JsonValue.Create(int.Parse(value)) : JsonValue.Create(value);
        Assert.Throws<ArgumentException>(() => DerivedMedia.Select(JsonSerializer.SerializeToElement(probe), Request()));
    }
    [Fact]
    public void FrameProofCountsFramesPreservesVfrAndRejectsCropOrTimingChanges()
    {
        JsonElement[] source = [Frame(0), Frame(0.04), Frame(0.09), Frame(2)];
        JsonElement[] output = [Frame(0.1, size: 60), Frame(0.14, size: 60), Frame(0.19, size: 60)];
        var proof = SampleRemuxService.ValidateFrames(Plan(), Probe(), source, output);
        Assert.Equal(3, proof.Frames); Assert.Equal(40, proof.VideoPayloadReductionPercent); Assert.Equal(0.1, proof.TimestampShiftSeconds, 5);
        Assert.Throws<IOException>(() => SampleRemuxService.ValidateFrames(Plan(), Probe(), source, output[..2]));
        Assert.Throws<IOException>(() => SampleRemuxService.ValidateFrames(Plan(), Probe(), source, [output[0], output[1], Frame(0.2)]));
        Assert.Throws<IOException>(() => SampleRemuxService.ValidateFrames(Plan(), Probe(), source, [output[0], output[1], Frame(0.19, 1880)]));
    }
    [Fact]
    public void EncodedVideoStillRequiresExactCopiedPayloadsAndCommonTimestampShift()
    {
        JsonElement[] source = [Packet(0, "source-video", 0), Packet(2, "dts", 0), Packet(3, "pgs", 0.5)];
        JsonElement[] output = [Packet(0, "hevc", 0.1), Packet(1, "dts", 0.1), Packet(2, "pgs", 0.6)];
        SampleRemuxService.ValidatePackets(Plan(), source, output, 0.1);
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(Plan(), source, output));
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(Plan(), source, [output[0], Packet(1, "aac", 0.1), output[2]], 0.1));
        Assert.Throws<IOException>(() => SampleRemuxService.ValidatePackets(Plan(), source, [output[0], output[1], Packet(2, "pgs", 0.8)], 0.1));
    }
    [Fact]
    public void InventoryAllowsOnlyVideoCodecChangeAndPreservesAccessibilityAndColor()
    {
        var output = JsonNode.Parse(Probe().GetRawText())!; output["streams"]![0]!["codec_name"] = "hevc"; output["streams"]![0]!["profile"] = "Main";
        SampleRemuxService.ValidateInventory(Plan(), Probe(), JsonSerializer.SerializeToElement(output));
        output["streams"]![1]!["codec_name"] = "aac";
        Assert.Throws<IOException>(() => SampleRemuxService.ValidateInventory(Plan(), Probe(), JsonSerializer.SerializeToElement(output)));
        output["streams"]![1]!["codec_name"] = "dts"; output["streams"]![0]!["color_transfer"] = "bt709";
        Assert.Throws<IOException>(() => SampleRemuxService.ValidateInventory(Plan(), Probe(), JsonSerializer.SerializeToElement(output)));
    }
    [Theory]
    [InlineData("tv")]
    [InlineData("pc")]
    public void EncoderCannotSilentlyDeclareAnUnknownSourceColorRange(string range)
    {
        var output = JsonNode.Parse(Probe().GetRawText())!;
        output["streams"]![0]!["codec_name"] = "hevc";
        output["streams"]![0]!["profile"] = "Main";
        output["streams"]![0]!["color_range"] = range;
        var failure = Assert.Throws<IOException>(() => SampleRemuxService.ValidateInventory(Plan(), Probe(), JsonSerializer.SerializeToElement(output)));
        Assert.Contains("color_range", failure.Message);
        // The same declared value on both sides remains valid. Unknown does not
        // mean permission to choose a range, even if an encoder has a default.
        var source = JsonNode.Parse(Probe().GetRawText())!;
        source["streams"]![0]!["color_range"] = range;
        SampleRemuxService.ValidateInventory(Plan(), JsonSerializer.SerializeToElement(source), JsonSerializer.SerializeToElement(output));
    }
}
