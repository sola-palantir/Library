using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// Inspection observations never grant copy/archive verification or alter Asset identity.
public sealed class MediaInspectionService(Storage storage, LibraryOptions options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Available => ExecutionMode.IsLocal(options) && options.FfprobePath is { } path && Path.IsPathFullyQualified(path) && File.Exists(path);
    public async Task<object> Inspect(PhysicalCopy copy, CancellationToken cancellation)
    {
        if (!Available) throw new InvalidOperationException("Read-only probing is unconfigured. Configure an existing FFprobe binary or import probe JSON for review.");
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Live inspection uses the Linux storage boundary.");
        if (copy.Role is not ("source" or "library")) throw new ArgumentException("Inspect a source or library copy.");
        if (!await gate.WaitAsync(0, cancellation)) throw new InvalidOperationException("A media inspection is already running. Retry after it finishes.");
        try
        {
            using var input = storage.Read(copy.RootId, copy.RelativePath);
            var before = Storage.Observe(input);
            if (before != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "")) throw new IOException("Catalog observation is stale; scan and review this file first.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var version = await ProbeProcess.Run(options.FfprobePath!, ["-version"], 64 * 1024, timeout.Token);
                // Child opens the parent's pinned regular-file descriptor, not a mutable media pathname.
                // Keep the handle alive until the child exits; the /proc descriptor is seekable.
                var descriptor = $"/proc/{Environment.ProcessId}/fd/{input.SafeFileHandle.DangerousGetHandle().ToInt64()}";
                var json = await ProbeProcess.Run(options.FfprobePath!, [
                    "-v", "error", "-max_alloc", "67108864", "-probesize", "16777216", "-analyzeduration", "10000000",
                    "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,mp4,m4a,3gp,3g2,mj2,mpegts,avi,mpeg",
                    "-show_format", "-show_streams", "-show_chapters", "-show_data_hash", "sha256", "-of", "json", descriptor
                ], 4 * 1024 * 1024, timeout.Token);
                if (Storage.Observe(input) != before || storage.Observe(copy.RootId, copy.RelativePath) != before) throw new IOException("Source changed during inspection; discard the observation and retry when stable.");
                using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.TryGetProperty("error", out _) ||
                    !parsed.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() is < 1 or > 256)
                    throw new IOException("FFprobe did not return a usable bounded stream inventory.");
                return new { probe = parsed.RootElement.Clone(), evidence = new {
                    origin = "live-read-only", copyId = copy.Id, assetId = copy.AssetId, observation = before,
                    observedUtc = DateTime.UtcNow, toolVersion = version.Split('\n')[0].Trim(),
                    contentVerified = false, executable = false
                } };
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new IOException("Media inspection exceeded 45 seconds. No media was changed."); }
        }
        finally { gate.Release(); }
    }
}

public static class ProbeProcess
{
    public static async Task<string> Run(string executable, string[] arguments, int outputLimit, CancellationToken ct, long? maxWorkingSetBytes = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException("Could not start FFprobe."); }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Configured FFprobe could not be started."); }
        using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // Readers kill the process on overflow so the other stream cannot leave a blocked child.
        async Task<string> ReadBounded(StreamReader reader, int limit)
        {
            var output = new StringBuilder(); var buffer = new char[4096]; var bytes = 0;
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
                    if (bytes > limit) throw new IOException("FFprobe output exceeded the inspection limit.");
                    output.Append(buffer, 0, count);
                }
                return output.ToString();
            }
            catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        }
        var stdout = ReadBounded(process.StandardOutput, outputLimit);
        var stderr = ReadBounded(process.StandardError, 64 * 1024);
        async Task WatchMemory()
        {
            if (maxWorkingSetBytes == null) return;
            while (!process.HasExited)
            {
                long workingSet;
                try { process.Refresh(); workingSet = process.WorkingSet64; }
                catch (InvalidOperationException) when (process.HasExited) { return; }
                if (workingSet > maxWorkingSetBytes)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new IOException("Sample encoder exceeded its 1 GiB resident-memory budget; the source is retained and publication is refused.");
                }
                await Task.Delay(100, ct);
            }
        }
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct), WatchMemory());
            ct.ThrowIfCancellationRequested();
            // Bounded plain-text diagnostics make an encoder refusal actionable.
            // Arguments (including paths) are never logged; media messages are evidence, not instructions.
            var detail = new string(stderr.Result.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(1024).ToArray()).Trim();
            if (process.ExitCode != 0) throw new IOException($"Media tool exited with code {process.ExitCode}. {detail}");
            if (!string.IsNullOrWhiteSpace(stderr.Result)) throw new IOException($"Media tool reported errors; validation refused. {detail}");
            return stdout.Result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}

public static class MediaInspectionEndpoints
{
    public static void MapMediaInspection(this WebApplication app)
    {
        app.MapGet("/api/media/capabilities", (MediaInspectionService service) => new {
            liveInspection = service.Available, importedProbeReview = true, presetPreview = true,
            encoding = false, remuxing = false, audioHandling = "copy-only",
            message = service.Available ? "Read-only inspection available; media is never changed." : "Live probing is unconfigured. Imported probe JSON and preset previews remain available."
        });
        app.MapPost("/api/media/inspect/{copyId:long}", async (long copyId, Catalog db, MediaInspectionService service, CancellationToken ct) => {
            var copy = await db.Copies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == copyId, ct) ?? throw new ArgumentException("Unknown catalog copy.");
            return Results.Ok(await service.Inspect(copy, ct));
        });
    }
}
