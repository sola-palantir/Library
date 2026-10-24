using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Library.Execution;
public record FileObservation(long Bytes, long Ticks, string Identity);
public record ReviewedTrack(int Index, string Language, string Role);
public record SampleRequest(long SourceCopyId, string TargetRootId, int Seconds, string[] OriginalLanguages, ReviewedTrack[] Tracks, bool ConfirmSourceComplete, bool ConfirmSdr, string Mode = "stream-copy");
public record SelectedStream(int Index, string Type, string Codec, string Language, string[] Dispositions);
public record SamplePlan(SampleRequest Request, long SourceAssetId, FileObservation SourceObservation, SelectedStream[] Streams, string ToolVersion, string ProbeSignature,
    string? Hash = null, long OutputBytes = 0, string? Validation = null, int? QBitRevision = null, SampleEncoding? Encoding = null);
public record SampleEncoding(string Id, string PresetSha256, string Encoder, int Crf, string Preset, string PixelFormat, string Timing, string ResourceOptions);

public static class DerivedMedia
{
    public const string EncodeMode = "x265-sdr-sample";
    public static readonly SampleEncoding SdrEncoding = new("handbrake-1080-rf22-slow-v1", "2b53699a614ea5b260d844a37e4f79425c8ae40b2abca21f677dc63f33e2b383", "libx265", 22, "slow", "yuv420p", "passthrough", "pools=none:frame-threads=2:log-level=error");
    public static string Kind(SamplePlan plan) => plan.Encoding == null ? "stream-copy-sample" : "video-encode-sample";
    public static void ValidateProfile(SamplePlan plan)
    {
        if (plan.Request.Mode is not ("stream-copy" or EncodeMode) || plan.Encoding != (plan.Request.Mode == EncodeMode ? SdrEncoding : null))
            throw new ArgumentException("Unknown or changed immutable sample encoding profile.");
    }
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string ProbeSignature(JsonElement probe) => Digest(JsonSerializer.Serialize(probe.GetProperty("streams")));
    public static string Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) ? item.ToString() : "";
    public static string Language(string? input)
    {
        input = (input ?? "").Trim().ToLowerInvariant();
        input = input switch { "fre" => "fra", "ger" => "deu", "dut" => "nld", "chi" => "zho", _ => input };
        if (input is "und" or "mul" or "mis" or "zxx" || input.Length is < 2 or > 3) throw new ArgumentException("Confirm a recognized, single track language.");
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.TwoLetterISOLanguageName == input || c.ThreeLetterISOLanguageName == input);
        return culture?.ThreeLetterISOLanguageName ?? throw new ArgumentException("Unrecognized language requires manual review.");
    }
    public static SelectedStream[] Select(JsonElement probe, SampleRequest request)
    {
        if (request.Mode is not ("stream-copy" or EncodeMode)) throw new ArgumentException("Unknown sample mode.");
        if (!request.ConfirmSourceComplete || !request.ConfirmSdr) throw new ArgumentException("Confirm source completion and SDR review before a sample preview.");
        if (request.Seconds is < 1 or > 30) throw new ArgumentException("Only 1-30 second samples from the beginning are supported.");
        if (request.Tracks is null || request.OriginalLanguages is null || request.Tracks.Length > 256 || request.OriginalLanguages.Length > 10) throw new ArgumentException("Bounded explicit track review is required.");
        if (!probe.TryGetProperty("format", out var format) || !Text(format, "format_name").Split(',').Contains("matroska")) throw new ArgumentException("This milestone requires a Matroska source.");
        var streams = probe.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length is < 1 or > 256 || streams.Select(s => s.GetProperty("index").GetInt32()).Distinct().Count() != streams.Length) throw new ArgumentException("Invalid stream inventory.");
        var videos = streams.Where(s => Text(s, "codec_type") == "video").ToArray();
        if (videos.Length != 1 || Text(videos[0], "codec_name") != "h264" || Text(videos[0], "pix_fmt") != "yuv420p") throw new ArgumentException("Only reviewed SDR H.264 8-bit samples are qualified. HEVC/HDR/Dolby Vision processing is blocked.");
        var description = videos[0].GetRawText();
        if (new[] { "smpte2084", "arib-std-b67", "bt2020", "DOVI", "Dolby", "Mastering display", "Content light", "HDR", "SMPTE2094", "smpte2094" }.Any(s => description.Contains(s, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("HDR/HDR10+/Dolby Vision processing is not qualified; preserve the source.");
        if (request.Mode == EncodeMode)
        {
            var video = videos[0];
            if (Text(video, "color_range") is not ("tv" or "pc"))
                throw new ArgumentException("Encoding requires a declared source color range; unknown range must remain unchanged or receive separate evidence-based review.");
            if (!video.TryGetProperty("width", out var width) || !video.TryGetProperty("height", out var height) || width.GetInt32() is < 16 or > 1920 || height.GetInt32() is < 16 or > 1080 || width.GetInt32() % 2 != 0 || height.GetInt32() % 2 != 0)
                throw new ArgumentException("Encoding samples require even source dimensions up to 1920x1080; no scaling or crop is applied.");
            if (Text(video, "field_order") != "progressive" || description.Contains("Display Matrix", StringComparison.OrdinalIgnoreCase) || description.Contains("Stereo 3D", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Interlaced, unclassified, rotated or stereo video requires separate qualification.");
            var rate = Text(video, "r_frame_rate").Split('/');
            if (rate.Length != 2 || !double.TryParse(rate[0], CultureInfo.InvariantCulture, out var n) || !double.TryParse(rate[1], CultureInfo.InvariantCulture, out var d) || d <= 0 || !double.IsFinite(n / d) || n / d is <= 0 or > 60)
                throw new ArgumentException("Sample encoding requires a known source rate up to 60 fps.");
        }
        var reviewed = request.Tracks.ToDictionary(t => t.Index);
        var required = streams.Where(s => Text(s, "codec_type") is "audio" or "subtitle").Select(s => s.GetProperty("index").GetInt32()).ToHashSet();
        if (!required.SetEquals(reviewed.Keys)) throw new ArgumentException("Review the language and role of every audio/subtitle stream explicitly.");
        var originals = request.OriginalLanguages.Select(Language).ToHashSet();
        var selected = new List<SelectedStream>(); var mainAudio = 0;
        foreach (var stream in streams)
        {
            var index = stream.GetProperty("index").GetInt32(); var type = Text(stream, "codec_type");
            var flags = stream.TryGetProperty("disposition", out var disposition) ? disposition.EnumerateObject().Where(p => p.Value.ToString() == "1").Select(p => p.Name).ToArray() : [];
            if (flags.Contains("attached_pic")) throw new ArgumentException("Attached pictures require separate qualification.");
            var language = ""; var keep = true;
            if (type is "audio" or "subtitle")
            {
                var review = reviewed[index]; language = Language(review.Language);
                if (type == "subtitle") { if (review.Role != "subtitle") throw new ArgumentException("Confirm subtitle role."); keep = language == "eng"; }
                else
                {
                    if (review.Role is not ("main" or "commentary" or "descriptive")) throw new ArgumentException("Unknown audio roles require review.");
                    if (review.Role == "main" && language != "eng" && originals.Count == 0) throw new ArgumentException("Confirm the foreign-film original language before excluding other audio.");
                    keep = review.Role == "descriptive" || review.Role == "main" && (language == "eng" || originals.Contains(language));
                    if (keep && review.Role == "main") mainAudio++;
                }
            }
            else if (type is not ("video" or "attachment")) throw new ArgumentException("Unsupported stream kind requires review.");
            if (keep) selected.Add(new(index, type, Text(stream, "codec_name"), language, flags));
        }
        if (mainAudio == 0) throw new ArgumentException("A confirmed main soundtrack is required; never produce silent or commentary-only media.");
        return selected.ToArray();
    }
    public static string[] Arguments(string input, string output, SamplePlan plan)
    {
        ValidateProfile(plan);
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-max_alloc", "67108864", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-noautorotate" };
        // Bound decoded work and queues; sparse subtitle probing can still read ahead.
        if (plan.Encoding != null) args.AddRange(["-t", (plan.Request.Seconds + 5).ToString(CultureInfo.InvariantCulture), "-thread_queue_size", "16", "-threads", "2"]);
        args.AddRange(["-i", input, "-t", plan.Request.Seconds.ToString(CultureInfo.InvariantCulture)]);
        foreach (var stream in plan.Streams) { args.Add("-map"); args.Add($"0:{stream.Index}"); }
        args.AddRange(["-map_metadata", "0", "-map_chapters", "-1", "-c", "copy"]);
        if (plan.Encoding is { } encoding)
            args.AddRange(["-c:v:0", encoding.Encoder, "-crf:v:0", "22", "-preset:v:0", encoding.Preset, "-pix_fmt:v:0", encoding.PixelFormat, "-fps_mode:v:0", encoding.Timing, "-enc_time_base:v:0", "-1", "-x265-params", encoding.ResourceOptions, "-max_muxing_queue_size", "512", "-muxing_queue_data_threshold", "16777216"]);
        for (var i = 0; i < plan.Streams.Length; i++)
        {
            var stream = plan.Streams[i];
            args.AddRange([$"-disposition:{i}", stream.Dispositions.Length == 0 ? "0" : string.Join('+', stream.Dispositions)]);
            if (stream.Language.Length > 0) args.AddRange([$"-metadata:s:{i}", $"language={stream.Language}"]);
        }
        // -y addresses ONLY an exclusively created, pinned job-owned partial descriptor.
        args.AddRange(["-fs", "268435456", "-f", "matroska", "-y", output]);
        return args.ToArray();
    }
    public static string[] PacketWindowArguments(string input, SamplePlan plan) => Arguments(input, "pipe:1", plan with { Request = plan.Request with { Mode = "stream-copy" }, Encoding = null });
    public static string[] EncodeWindowArguments(string output, SamplePlan plan)
    {
        if (plan.Encoding == null) throw new ArgumentException("An encoding profile is required for the packet window.");
        var args = Arguments("pipe:0", output, plan with { Streams = plan.Streams.Select((stream, index) => stream with { Index = index }).ToArray() });
        args[Array.IndexOf(args, "-protocol_whitelist") + 1] = "file,pipe";
        return args;
    }
}
