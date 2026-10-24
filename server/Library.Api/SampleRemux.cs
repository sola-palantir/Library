using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// One worker and source reservation for bounded copy/encode samples. No crop,
// scaling, replacement, promotion-to-library or optical claim is supported.
public sealed class SampleRemuxService(Storage storage, LibraryOptions options, MediaInspectionService inspection, IDbContextFactory<Catalog> factory, IQBitCopyGuard guard)
{
    public bool Available => options.EnableSampleRemux && inspection.Available && options.FfmpegPath is { } path && Path.IsPathFullyQualified(path) && File.Exists(path);
    public bool EncodeAvailable => options.EnableSampleEncode && inspection.Available && options.FfmpegPath is { } path && Path.IsPathFullyQualified(path) && File.Exists(path);
    public bool CanExecute(SamplePlan plan) => plan.Request.Mode == DerivedMedia.EncodeMode ? EncodeAvailable : Available;
    public static string Destination(string id) => $"samples/{id}.mkv";
    public static string Partial(string id) => Destination(id) + $".library-{id}.partial";
    private static string Descriptor(FileStream file) => $"/proc/{Environment.ProcessId}/fd/{file.SafeFileHandle.DangerousGetHandle().ToInt64()}";
    private static FileObservation Observation(PhysicalCopy copy) => new(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "");
    public async Task<SamplePlan> Preview(SampleRequest request, Catalog db, CancellationToken ct)
    {
        if (storage.Root(request.TargetRootId).Role != "staging") throw new ArgumentException("Samples require an isolated staging root.");
        var copy = await db.Copies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SourceCopyId, ct) ?? throw new ArgumentException("Unknown source copy.");
        if (copy.Role is not ("source" or "library")) throw new ArgumentException("Choose a source or library copy.");
        var completion = await guard.CheckAsync(copy, null, ct);
        if (await db.ProcessingLeases.AnyAsync(x => x.SourceAssetId == copy.AssetId, ct)) throw new InvalidOperationException("This source already has a reserved sample. Review or retry that job; a second output is not permitted.");
        var result = JsonSerializer.SerializeToElement(await inspection.Inspect(copy, ct));
        var probe = result.GetProperty("probe");
        var selected = DerivedMedia.Select(probe, request);
        return new(request, copy.AssetId, Observation(copy), selected, result.GetProperty("evidence").GetProperty("toolVersion").GetString()!, DerivedMedia.ProbeSignature(probe), QBitRevision: completion.ConfigurationRevision, Encoding: request.Mode == DerivedMedia.EncodeMode ? DerivedMedia.SdrEncoding : null);
    }
    private void RequireSource(PhysicalCopy source, SamplePlan plan, FileStream? input = null)
    {
        if (source.AssetId != plan.SourceAssetId || source.Role is not ("source" or "library") || storage.Observe(source.RootId, source.RelativePath) != plan.SourceObservation || input != null && Storage.Observe(input) != plan.SourceObservation)
            throw new IOException("Source changed since preview; no sample can be validated against stale evidence.");
    }
    private async Task<JsonElement> Probe(FileStream file, CancellationToken ct)
    {
        var json = await ProbeProcess.Run(options.FfprobePath!, ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-show_streams", "-show_format", "-show_data_hash", "sha256", "-of", "json", Descriptor(file)], 4 * 1024 * 1024, ct);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); return document.RootElement.Clone();
    }
    private async Task<JsonElement[]> Packets(FileStream file, int? seconds, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm" };
        if (seconds != null) args.AddRange(["-read_intervals", $"%+{seconds.Value}"]);
        args.AddRange(["-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=stream_index,data_hash,pts_time,dts_time,size", "-of", "json", Descriptor(file)]);
        var json = await ProbeProcess.Run(options.FfprobePath!, args.ToArray(), 16 * 1024 * 1024, ct);
        using var parsed = JsonDocument.Parse(json);
        var packets = parsed.RootElement.GetProperty("packets");
        if (packets.GetArrayLength() > 50000) throw new IOException("Packet validation exceeded its bounded sample limit.");
        return packets.EnumerateArray().Select(p => p.Clone()).ToArray();
    }
    private async Task<JsonElement[]> Frames(FileStream file, int? seconds, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-select_streams", "v:0" };
        if (seconds != null) args.AddRange(["-read_intervals", $"%+{seconds.Value}"]);
        args.AddRange(["-show_frames", "-show_entries", "frame=best_effort_timestamp_time,width,height,pix_fmt,sample_aspect_ratio,interlaced_frame,pkt_size", "-of", "json", Descriptor(file)]);
        using var parsed = JsonDocument.Parse(await ProbeProcess.Run(options.FfprobePath!, args.ToArray(), 4 * 1024 * 1024, ct));
        var frames = parsed.RootElement.GetProperty("frames");
        if (frames.GetArrayLength() > 3600) throw new IOException("Frame validation exceeded its bounded sample limit.");
        return frames.EnumerateArray().Select(f => f.Clone()).ToArray();
    }
    public static SampleValidation.FrameProof ValidateFrames(SamplePlan plan, JsonElement sourceProbe, JsonElement[] source, JsonElement[] output) => SampleValidation.ValidateFrames(plan, sourceProbe, source, output);
    public static object ValidatePackets(SamplePlan plan, JsonElement[] source, JsonElement[] output, double? videoShift = null) => SampleValidation.ValidatePackets(plan, source, output, videoShift);
    public static void ValidateInventory(SamplePlan plan, JsonElement source, JsonElement output) => SampleValidation.ValidateInventory(plan, source, output);
    private void QualifyTarget(string root, string id)
    {
        var token = Guid.NewGuid().ToString("N");
        var from = $".qualification-{token}.library-{id}.partial";
        var to = $".qualification-published-{token}.library-{id}.partial";
        try
        {
            using (var file = storage.Create(root, from)) file.Flush(true);
            storage.Publish(root, from, to);
        }
        finally { storage.RemoveTemporaryIfPresent(root, from, id); storage.RemoveTemporaryIfPresent(root, to, id); }
    }
    public async Task Run(Operation job, Catalog db, CancellationToken stop)
    {
        var plan = JobRunner.Decode<SamplePlan>(job.Payload);
        DerivedMedia.ValidateProfile(plan);
        if (!CanExecute(plan)) throw new InvalidOperationException("This sample mode is disabled; enable it only with isolated qualified staging storage.");
        if (storage.Root(plan.Request.TargetRootId).Role != "staging") throw new ArgumentException("Staging root required.");
        if (!await db.ProcessingLeases.AnyAsync(x => x.SourceAssetId == plan.SourceAssetId && x.JobId == job.Id, stop)) throw new InvalidOperationException("Missing exclusive source reservation.");
        var source = await db.Copies.AsNoTracking().SingleAsync(x => x.Id == plan.Request.SourceCopyId, stop);
        await guard.CheckAsync(source, plan.QBitRevision, stop);
        RequireSource(source, plan);
        var target = Destination(job.Id); var partial = Partial(job.Id); var root = plan.Request.TargetRootId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var watcherStop = new CancellationTokenSource();
        async Task WatchCancellation()
        {
            try { while (true) { await Task.Delay(250, watcherStop.Token); await using var poll = await factory.CreateDbContextAsync(watcherStop.Token); if (await poll.Jobs.Where(j => j.Id == job.Id).Select(j => j.CancelRequested).SingleAsync(watcherStop.Token)) { deadline.Cancel(); return; } } }
            catch (OperationCanceledException) when (watcherStop.IsCancellationRequested) { }
        }
        var watcher = WatchCancellation(); var ct = deadline.Token;
        try
        {
            using var input = storage.Read(source.RootId, source.RelativePath); RequireSource(source, plan, input);
            var sourceProbe = await Probe(input, ct);
            var selection = DerivedMedia.Select(sourceProbe, plan.Request);
            if (DerivedMedia.ProbeSignature(sourceProbe) != plan.ProbeSignature || JobRunner.Encode(selection) != JobRunner.Encode(plan.Streams)) throw new IOException("Technical inventory changed since preview.");
            if (!storage.Exists(root, target))
            {
                QualifyTarget(root, job.Id); // fail before FFmpeg if no-replace publication is unsupported
                if (storage.FreeBytes(root) < 320L * 1024 * 1024) throw new IOException("Insufficient sample staging space (256 MiB bound plus reserve).");
                storage.RemoveTemporaryIfPresent(root, partial, job.Id);
                await new JobProgress(factory, job).Report(plan.Encoding == null ? "remuxing-sample" : "encoding-sdr-sample", 0, null, null, ct, true);
                PacketWindowEvidence? packetWindow = null;
                using (var output = storage.Create(root, partial))
                {
                    if (plan.Encoding == null) await ProbeProcess.Run(options.FfmpegPath!, DerivedMedia.Arguments(Descriptor(input), Descriptor(output), plan), 64 * 1024, ct);
                    else packetWindow = await SamplePipeline.Run(options.FfmpegPath!, DerivedMedia.PacketWindowArguments(Descriptor(input), plan), DerivedMedia.EncodeWindowArguments(Descriptor(output), plan), ct);
                    output.Flush(true);
                }
                RequireSource(source, plan, input);
                using var staged = storage.Read(root, partial); var observed = Storage.Observe(staged);
                if (observed.Bytes <= 0 || observed.Bytes > 256L * 1024 * 1024) throw new IOException("Sample exceeds its output size bound.");
                await new JobProgress(factory, job).Report("validating-sample", 0, null, null, ct, true);
                var outputProbe = await Probe(staged, ct); ValidateInventory(plan, sourceProbe, outputProbe);
                var frames = plan.Encoding == null ? null : ValidateFrames(plan, sourceProbe, await Frames(input, plan.Request.Seconds + 5, ct), await Frames(staged, null, ct));
                var proof = ValidatePackets(plan, await Packets(input, plan.Request.Seconds + 5, ct), await Packets(staged, null, ct), frames?.TimestampShiftSeconds);
                await ProbeProcess.Run(options.FfmpegPath!, ["-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm", "-i", Descriptor(staged), "-map", "0:v", "-map", "0:a", "-f", "null", "-"], 64 * 1024, ct);
                var hash = await JobRunner.Hash(staged, ct);
                if (Storage.Observe(staged) != observed || storage.Observe(root, partial) != observed) throw new IOException("Staged sample changed during validation.");
                RequireSource(source, plan, input);
                plan = plan with { Hash = hash, OutputBytes = observed.Bytes, Validation = JobRunner.Encode(new { proof, frames, packetWindow, decode = "passed", visualQuality = "not assessed", sourceObservation = plan.SourceObservation, sourceContentHash = (string?)null, ffmpeg = (await ProbeProcess.Run(options.FfmpegPath!, ["-version"], 64 * 1024, ct)).Split('\n')[0], sampleOnly = true, chapters = "omitted from bounded sample", archiveEligible = false }) };
                job.Payload = JobRunner.Encode(plan); await db.SaveChangesAsync(ct);
                ct.ThrowIfCancellationRequested();
                storage.Publish(root, partial, target);
            }
            if (plan.Hash == null || plan.Validation == null) throw new IOException("Unbound destination collision; no validation checkpoint exists.");
            using var published = storage.Read(root, target); var final = Storage.Observe(published);
            if (final.Bytes != plan.OutputBytes || await JobRunner.Hash(published, ct) != plan.Hash || Storage.Observe(published) != final || storage.Observe(root, target) != final) throw new IOException("Published sample differs from its validation checkpoint.");
            RequireSource(source, plan, input); ct.ThrowIfCancellationRequested();
            if (!await db.DerivedAssets.AnyAsync(x => x.JobId == job.Id, ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var sourceAsset = await db.Assets.SingleAsync(x => x.Id == source.AssetId, ct);
                var asset = new Asset { EditionId = sourceAsset.EditionId, FileName = target, Bytes = final.Bytes, Sha256 = plan.Hash };
                db.Assets.Add(asset); await db.SaveChangesAsync(ct);
                db.Copies.Add(new() { AssetId = asset.Id, RootId = root, RelativePath = target, Role = "staging", State = "sample-validated", Bytes = final.Bytes, ModifiedTicks = final.Ticks, FileIdentity = final.Identity, Sha256 = plan.Hash });
                db.DerivedAssets.Add(new() { OutputAssetId = asset.Id, SourceAssetId = source.AssetId, SourceCopyId = source.Id, JobId = job.Id, Kind = DerivedMedia.Kind(plan), Evidence = JobRunner.Encode(plan) });
                db.Audit("sample-validated-not-archive", job.Id); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            if (storage.Observe(root, target) != final) throw new IOException("Sample changed after registration.");
        }
        catch (Exception error)
        {
            // Logical rollback never deletes a published file or changes its source.
            var outputIds = await db.DerivedAssets.Where(x => x.JobId == job.Id).Select(x => x.OutputAssetId).ToArrayAsync(CancellationToken.None);
            await db.Copies.Where(x => outputIds.Contains(x.AssetId)).ExecuteUpdateAsync(x => x.SetProperty(c => c.State, "review-required").SetProperty(c => c.Sha256, (string?)null), CancellationToken.None);
            await db.Assets.Where(x => outputIds.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(a => a.Sha256, (string?)null), CancellationToken.None);
            if (error is OperationCanceledException && !stop.IsCancellationRequested && !await db.Jobs.Where(x => x.Id == job.Id).Select(x => x.CancelRequested).SingleAsync(CancellationToken.None))
                throw new IOException("Sample processing exceeded its five-minute deadline. Original and staged data are retained for review.");
            throw;
        }
        finally { watcherStop.Cancel(); await watcher; }
    }
    public async Task Withdraw(Operation job, Catalog db, CancellationToken ct)
    {
        if (job.Type != "sample-remux" || job.Status is "queued" or "running") throw new ArgumentException("Cancel and wait for the sample job before rollback.");
        var plan = JobRunner.Decode<SamplePlan>(job.Payload);
        storage.RemoveTemporaryIfPresent(plan.Request.TargetRootId, Partial(job.Id), job.Id);
        var published = storage.Exists(plan.Request.TargetRootId, Destination(job.Id));
        var outputs = await db.DerivedAssets.Where(x => x.JobId == job.Id).Select(x => x.OutputAssetId).ToArrayAsync(ct);
        await db.Copies.Where(x => outputs.Contains(x.AssetId)).ExecuteUpdateAsync(x => x.SetProperty(c => c.State, "withdrawn"), ct);
        if (!published && outputs.Length == 0) await db.ProcessingLeases.Where(x => x.JobId == job.Id).ExecuteDeleteAsync(ct);
        job.Status = "withdrawn"; db.Audit("sample-withdrawn-source-retained", job.Id); await db.SaveChangesAsync(ct);
    }
}

public static class SampleRemuxEndpoints
{
    public static void MapSampleRemux(this WebApplication app)
    {
        app.MapGet("/api/media/processing", (SampleRemuxService service) => new { sampleRemux = service.Available, sampleEncode = service.EncodeAvailable, maxSeconds = 30, video = "SDR H.264 8-bit source only", audio = "copy-only", hdrProcessing = false, encoding = DerivedMedia.SdrEncoding, outputRole = "staging", archiveEligible = false });
        app.MapGet("/api/media/derived", async (Catalog db) => await db.DerivedAssets.OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync());
        app.MapPost("/api/media/remux/preview", async (SampleRequest request, Catalog db, SampleRemuxService service, CancellationToken ct) => {
            var plan = await service.Preview(request, db, ct);
            var job = new Operation { Type = "sample-remux", Status = "draft", Payload = JobRunner.Encode(plan) };
            db.Jobs.Add(job); db.Audit("sample-remux-preview", job.Id); await db.SaveChangesAsync(ct);
            return Results.Ok(new { job, plan, targetPath = SampleRemuxService.Destination(job.Id), executionAvailable = service.CanExecute(plan), warning = "Bounded sample only. Chapters omitted. No source replacement or archive eligibility. CRF 22 is a calibration starting point; visual quality has not been assessed." });
        });
        app.MapPost("/api/media/remux/{id}/approve", async (string id, Catalog db, SampleRemuxService service, CancellationToken ct) => {
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id && x.Type == "sample-remux", ct) ?? throw new ArgumentException("Unknown sample plan.");
            if (job.Status != "draft") throw new ArgumentException("Sample plan is no longer a draft.");
            var plan = JobRunner.Decode<SamplePlan>(job.Payload);
            DerivedMedia.ValidateProfile(plan);
            if (!service.CanExecute(plan)) throw new InvalidOperationException("This sample mode is disabled.");
            if (await db.Jobs.CountAsync(x => x.Status == "queued" || x.Status == "running", ct) >= 100) throw new InvalidOperationException("Job queue is full.");
            db.ProcessingLeases.Add(new() { SourceAssetId = plan.SourceAssetId, JobId = job.Id });
            job.Status = "queued"; db.Audit("sample-remux-approved", id); await db.SaveChangesAsync(ct); return Results.Ok();
        });
        app.MapPost("/api/media/remux/{id}/withdraw", async (string id, Catalog db, SampleRemuxService service, CancellationToken ct) => {
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ArgumentException("Unknown sample job.");
            await service.Withdraw(job, db, ct); return Results.Ok();
        });
    }
}
