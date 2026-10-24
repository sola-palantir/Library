using System.Globalization;
using System.Text.Json;
namespace Library.Execution;
public static class SampleValidation
{
    public record FrameProof(int Frames, double TimestampShiftSeconds, long SourceVideoPacketBytes, long OutputVideoPacketBytes, double VideoPayloadReductionPercent);
    public static FrameProof ValidateFrames(SamplePlan plan, JsonElement sourceProbe, JsonElement[] source, JsonElement[] output)
    {
        static double Time(JsonElement frame)
        {
            if (!double.TryParse(DerivedMedia.Text(frame, "best_effort_timestamp_time"), CultureInfo.InvariantCulture, out var time) || !double.IsFinite(time)) throw new IOException("Missing decoded frame timing.");
            return time;
        }
        if (!double.TryParse(DerivedMedia.Text(sourceProbe.GetProperty("format"), "start_time"), CultureInfo.InvariantCulture, out var start) || !double.IsFinite(start)) throw new IOException("Missing input time origin.");
        var expected = source.Where(f => Time(f) < start + plan.Request.Seconds - 0.000001).ToArray();
        if (expected.Length == 0 || expected.Length != output.Length) throw new IOException("Encoded sample duplicated or dropped source frames.");
        var shift = Time(output[0]) - Time(expected[0]);
        for (var i = 0; i < output.Length; i++)
        {
            if (i > 0 && (Time(expected[i]) <= Time(expected[i - 1]) || Time(output[i]) <= Time(output[i - 1]))) throw new IOException("Non-monotonic decoded frame timestamps.");
            if (Math.Abs(Time(output[i]) - Time(expected[i]) - shift) > 0.002) throw new IOException("Encoded frame timing changed.");
            foreach (var key in new[] { "width", "height", "pix_fmt", "sample_aspect_ratio" })
                if (DerivedMedia.Text(expected[i], key) != DerivedMedia.Text(output[i], key)) throw new IOException($"Encoded frame changed {key}.");
            if (DerivedMedia.Text(expected[i], "interlaced_frame") != "0" || DerivedMedia.Text(output[i], "interlaced_frame") != "0") throw new IOException("Only progressive decoded frames are qualified.");
        }
        static long Bytes(JsonElement[] frames) => frames.Sum(f => long.TryParse(DerivedMedia.Text(f, "pkt_size"), out var size) && size > 0 ? size : throw new IOException("Missing frame packet size evidence."));
        var before = Bytes(expected); var after = Bytes(output);
        return new(output.Length, shift, before, after, 100.0 * (before - after) / before);
    }
    public static object ValidatePackets(SamplePlan plan, JsonElement[] source, JsonElement[] output, double? videoShift = null)
    {
        double? shift = videoShift; var counts = new List<object>();
        if (plan.Encoding != null && videoShift == null) throw new IOException("Encoded video requires a decoded-frame timing proof first.");
        var firstVideo = source.FirstOrDefault(p => p.GetProperty("stream_index").GetInt32() == plan.Streams.Single(s => s.Type == "video").Index);
        if (!double.TryParse(DerivedMedia.Text(firstVideo, "pts_time"), CultureInfo.InvariantCulture, out var start)) throw new IOException("Source video timing is unavailable.");
        for (var i = 0; i < plan.Streams.Length; i++)
        {
            var stream = plan.Streams[i];
            if (plan.Encoding != null && stream.Type == "video") continue;
            var before = source.Where(p => p.GetProperty("stream_index").GetInt32() == stream.Index).ToArray();
            var after = output.Where(p => p.GetProperty("stream_index").GetInt32() == i).ToArray();
            if (after.Length > before.Length || stream.Type is "video" or "audio" && after.Length == 0) throw new IOException("Missing or unexpected copied packets.");
            var minimum = before.Count(p => double.TryParse(DerivedMedia.Text(p, "pts_time"), CultureInfo.InvariantCulture, out var pts) && pts < start + plan.Request.Seconds - 0.1);
            if (after.Length < minimum) throw new IOException("Sample dropped packets before the permitted clip boundary.");
            for (var n = 0; n < after.Length; n++)
            {
                var expected = DerivedMedia.Text(before[n], "data_hash");
                if (!expected.StartsWith("SHA256:", StringComparison.Ordinal) || expected != DerivedMedia.Text(after[n], "data_hash") || DerivedMedia.Text(before[n], "size") != DerivedMedia.Text(after[n], "size")) throw new IOException("Stream-copy packet validation failed; encoded payloads differ or packets were reordered.");
                if (stream.Type is "audio" or "video" or "subtitle")
                {
                    if (!double.TryParse(DerivedMedia.Text(before[n], "pts_time"), CultureInfo.InvariantCulture, out var a) || !double.TryParse(DerivedMedia.Text(after[n], "pts_time"), CultureInfo.InvariantCulture, out var b)) throw new IOException("Missing packet timing prevents synchronization validation.");
                    shift ??= b - a;
                    if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Abs(b - a - shift.Value) > 0.002) throw new IOException("Sample audio/video/subtitle timing changed.");
                }
            }
            counts.Add(new { sourceIndex = stream.Index, outputIndex = i, packets = after.Length });
        }
        if (output.Any(p => p.GetProperty("stream_index").GetInt32() < 0 || p.GetProperty("stream_index").GetInt32() >= plan.Streams.Length)) throw new IOException("Unexpected output stream.");
        return new { counts, timestampShiftSeconds = shift, encodedPayloadsIdentical = plan.Encoding == null, copiedPayloadsIdentical = true };
    }
    public static double PacketDuration(JsonElement[] packets, SelectedStream[] streams)
    {
        // Nonseekable mux output has no header duration. Measure every A/V packet
        // at EOF instead of trusting copied source DURATION tags or an estimate.
        var indices = streams.Select((s, i) => (s, i)).Where(x => x.s.Type is "video" or "audio").Select(x => x.i).ToHashSet();
        var timings = packets.Where(p => indices.Contains(p.GetProperty("stream_index").GetInt32())).Select(p => {
            if (!double.TryParse(DerivedMedia.Text(p, "pts_time"), CultureInfo.InvariantCulture, out var pts) || !double.IsFinite(pts) ||
                !double.TryParse(DerivedMedia.Text(p, "duration_time"), CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration <= 0)
                throw new IOException("Missing finite packet duration prevents streamed-container validation.");
            return (pts, end: pts + duration);
        }).ToArray();
        if (timings.Length == 0) throw new IOException("Missing output packet timing.");
        return timings.Max(t => t.end) - timings.Min(t => t.pts);
    }
    public static void ValidateInventory(SamplePlan plan, JsonElement source, JsonElement output, double? measuredPacketDuration = null)
    {
        var before = source.GetProperty("streams").EnumerateArray().ToDictionary(s => s.GetProperty("index").GetInt32());
        var after = output.GetProperty("streams").EnumerateArray().ToArray();
        if (after.Length != plan.Streams.Length) throw new IOException("Output stream count differs from the explicit selection.");
        for (var i = 0; i < after.Length; i++)
        {
            var expected = before[plan.Streams[i].Index];
            var encoded = plan.Encoding != null && plan.Streams[i].Type == "video";
            if (encoded && (DerivedMedia.Text(after[i], "codec_name") != "hevc" || DerivedMedia.Text(after[i], "profile") != "Main")) throw new IOException("Expected 8-bit HEVC Main output.");
            foreach (var key in new[] { "codec_name", "codec_type", "profile", "width", "height", "pix_fmt", "sample_rate", "channels", "channel_layout", "color_transfer", "color_space", "color_primaries", "color_range", "extradata_hash" })
            {
                if (encoded && key is "codec_name" or "profile" or "extradata_hash") continue;
                if (DerivedMedia.Text(expected, key) != DerivedMedia.Text(after[i], key)) throw new IOException($"Copied stream changed {key}; validation refused.");
            }
            var actualFlags = after[i].GetProperty("disposition").EnumerateObject().Where(p => p.Value.ToString() == "1").Select(p => p.Name).ToHashSet();
            if (!actualFlags.SetEquals(plan.Streams[i].Dispositions)) throw new IOException("Track dispositions changed, including forced/accessibility flags.");
            if (plan.Streams[i].Language.Length > 0 && (!after[i].TryGetProperty("tags", out var tags) || DerivedMedia.Language(DerivedMedia.Text(tags, "language")) != plan.Streams[i].Language)) throw new IOException("Selected track language was not preserved.");
        }
        var declared = double.TryParse(DerivedMedia.Text(output.GetProperty("format"), "duration"), CultureInfo.InvariantCulture, out var duration);
        if (!declared && measuredPacketDuration is { } measured) duration = measured;
        if ((!declared && measuredPacketDuration == null) || !double.IsFinite(duration) || duration < Math.Max(0.1, plan.Request.Seconds - 1) || duration > plan.Request.Seconds + 2) throw new IOException("Sample duration is missing, truncated or outside the approved boundary.");
        if (measuredPacketDuration is { } packetDuration && (!double.IsFinite(packetDuration) || packetDuration < Math.Max(0.1, plan.Request.Seconds - 1) || packetDuration > plan.Request.Seconds + 2 || declared && Math.Abs(packetDuration - duration) > 0.25)) throw new IOException("Measured packet duration differs from the approved sample or container duration.");
    }
}
