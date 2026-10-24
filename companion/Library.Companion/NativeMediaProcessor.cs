using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion;

// Implements the local processing boundary. Activation is deliberately limited
// to an offline synthetic-fixture hash set; no API/CLI/service enables real media.
public sealed class NativeMediaProcessor : IStagedMediaProcessor, IDisposable
{
    public const string FfmpegSha256 = "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec";
    public const string FfprobeSha256 = "f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52";
    readonly Scope tools;
    readonly FileStream ffmpegPin, ffprobePin;
    readonly HashSet<string> fixtures;
    readonly NativeProcessLimits limits;
    public string Qualification => "synthetic";
    public int ProcessRuns { get; private set; }
    public List<NativeProcessEvidence> Processes { get; } = [];
    public NativeMediaProcessor(string toolsRoot, IEnumerable<string> syntheticFixtureHashes, NativeProcessLimits? limits = null)
    {
        this.limits = limits ?? NativeProcessLimits.Sample; this.limits.Validate();
        fixtures = syntheticFixtureHashes.ToHashSet(StringComparer.Ordinal);
        if (fixtures.Count is < 1 or > 32) throw new ArgumentException("An explicit bounded synthetic fixture allowlist is required.");
        foreach (var hash in fixtures) ExecutionContract.Hash(hash);
        tools = new(toolsRoot);
        try
        {
            ffmpegPin = tools.Read("bin/ffmpeg.exe");
            try
            {
                ffprobePin = tools.Read("bin/ffprobe.exe");
                try { Verify(ffmpegPin, FfmpegSha256); Verify(ffprobePin, FfprobeSha256); }
                catch { ffprobePin.Dispose(); throw; }
            }
            catch { ffmpegPin.Dispose(); throw; }
        }
        catch { tools.Dispose(); throw; }
    }
    static void Verify(FileStream file, string hash)
    { if (Convert.ToHexStringLower(SHA256.HashData(file)) != hash) throw new IOException("Native binary differs from the verified project-local build."); file.Position = 0; }
    static FileStream Pinned(Stream input) => input is FileStream file && file.CanSeek ? file : throw new IOException("A pinned seekable native file handle is required.");
    async Task<NativeProcessEvidence> Run(bool probe, string[] args, FileStream input, Stream output, long bound, CancellationToken ct)
    {
        var result = await NativeProcess.Run(tools.Resolve(probe ? "bin/ffprobe.exe" : "bin/ffmpeg.exe"), args, input, output, limits, bound, ct);
        Processes.Add(result);
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Stderr))
            throw new IOException($"Native media tool failed (exit {result.ExitCode}); publication refused. {new string(result.Stderr.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(1024).ToArray())}");
        return result;
    }
    static List<string> ProbeArguments() => ["-v", "error", "-max_alloc", "67108864", "-threads", "2", "-protocol_whitelist", "fd", "-format_whitelist", "matroska,webm"];
    async Task<JsonElement> Json(FileStream input, string[] args, int bound, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); await Run(true, args, input, buffer, bound, ct);
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 }); return document.RootElement.Clone();
    }
    public Task<JsonElement> InspectAsync(FileStream input, CancellationToken ct)
    {
        var args = ProbeArguments(); args.AddRange(["-show_streams", "-show_format", "-show_data_hash", "sha256", "-of", "json", "fd:"]);
        return Json(input, args.ToArray(), 4 * 1024 * 1024, ct);
    }
    async Task<JsonElement[]> Items(FileStream input, int? seconds, bool frames, CancellationToken ct)
    {
        var args = ProbeArguments();
        if (seconds != null) args.AddRange(["-read_intervals", $"%+{seconds.Value}"]);
        if (frames) args.AddRange(["-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time,width,height,pix_fmt,sample_aspect_ratio,interlaced_frame,pkt_size"]);
        else args.AddRange(["-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=stream_index,data_hash,pts_time,dts_time,duration_time,size"]);
        args.AddRange(["-of", "json", "fd:"]);
        var json = await Json(input, args.ToArray(), frames ? 4 * 1024 * 1024 : 16 * 1024 * 1024, ct);
        var values = json.GetProperty(frames ? "frames" : "packets");
        if (values.GetArrayLength() > (frames ? 3600 : 50000)) throw new IOException("Native validation item limit exceeded.");
        return values.EnumerateArray().Select(v => v.Clone()).ToArray();
    }
    public async Task CheckSourceAsync(Stream input, SamplePlan plan, CancellationToken ct)
    {
        DerivedMedia.ValidateProfile(plan); var file = Pinned(input); file.Position = 0;
        if (!fixtures.Contains(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)))) throw new IOException("This activation accepts only offline-approved synthetic fixtures.");
        var probe = await InspectAsync(file, ct);
        if (ExecutionContract.Serialize(DerivedMedia.Select(probe, plan.Request)) != ExecutionContract.Serialize(plan.Streams) || DerivedMedia.ProbeSignature(probe) != plan.ProbeSignature)
            throw new IOException("Source inventory differs from the immutable native review.");
    }
    internal static string[] Arguments(SamplePlan plan)
    {
        var args = DerivedMedia.Arguments("fd:", "pipe:1", plan);
        args[Array.IndexOf(args, "-protocol_whitelist") + 1] = "fd";
        // FFmpeg 9 spells the old -1 source-timebase selection "demux".
        var timebase = Array.IndexOf(args, "-enc_time_base:v:0");
        if (timebase >= 0) args[timebase + 1] = "demux";
        // Only the inherited disk input and stdout pipe are exposed. The binary
        // output is drained through the worker's hard byte limit with backpressure.
        // Streaming MKV must not advertise an estimated duration from -t:
        // stream-copy keyframe pre-roll can make that estimate wrong. The
        // validator measures all packet timestamps/durations at EOF instead.
        return args[..^1].Concat(new[] { "-live", "1", "-protocol_whitelist", "pipe", "pipe:1" }).ToArray();
    }
    public async Task ProcessAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct)
    { ProcessRuns++; await Run(false, Arguments(plan), Pinned(input), output, ExecutionContract.MaximumOutputBytes, ct); }
    public async Task<WorkerValidation> ValidateAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct)
    {
        var source = Pinned(input); var staged = Pinned(output);
        var before = await InspectAsync(source, ct); var after = await InspectAsync(staged, ct);
        var sourcePackets = await Items(source, plan.Request.Seconds + 5, false, ct); var outputPackets = await Items(staged, null, false, ct);
        var duration = SampleValidation.PacketDuration(outputPackets, plan.Streams);
        SampleValidation.ValidateInventory(plan, before, after, duration);
        var frames = plan.Encoding == null ? null : SampleValidation.ValidateFrames(plan, before,
            await Items(source, plan.Request.Seconds + 5, true, ct), await Items(staged, null, true, ct));
        var packets = SampleValidation.ValidatePackets(plan, sourcePackets, outputPackets, frames?.TimestampShiftSeconds);
        using var sink = new MemoryStream();
        await Run(false, ["-nostdin", "-v", "error", "-xerror", "-max_alloc", "67108864", "-threads", "2", "-protocol_whitelist", "fd", "-format_whitelist", "matroska,webm", "-i", "fd:", "-map", "0:v", "-map", "0:a", "-threads", "2", "-f", "null", "-"], staged, sink, 65536, ct);
        return new("synthetic", ExecutionContract.Serialize(new { nativeMediaValidation = "passed", packets, frames, measuredPacketDuration = duration,
            decode = "passed", sampleOnly = true, archiveEligible = false, syntheticFixture = true, visualQuality = "not assessed", plan.Encoding,
            toolSha256 = FfmpegSha256, probeSha256 = FfprobeSha256, limits, peakCommittedBytes = Processes.Max(p => p.PeakCommittedBytes) }));
    }
    public void Dispose() { ffprobePin.Dispose(); ffmpegPin.Dispose(); tools.Dispose(); }
}
