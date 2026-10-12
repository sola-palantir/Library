using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// Telemetry is saved separately: reporting progress must not flush pending integrity changes.
public sealed class JobProgress(IDbContextFactory<Catalog> factory, Operation job)
{
    private DateTime last = DateTime.MinValue;
    public async Task Report(string phase, long bytes, long? totalBytes, long? files, CancellationToken ct, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - last < TimeSpan.FromMilliseconds(500)) return;
        var json = JobRunner.Encode(new { phase, bytes, totalBytes, files, atUtc = now });
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Jobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressJson, json).SetProperty(x => x.UpdatedUtc, now), ct);
        job.ProgressJson = json; job.UpdatedUtc = now; last = now;
    }
}
