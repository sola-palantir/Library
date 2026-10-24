using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public record AuditPolicy(bool Enabled = false, int IntervalHours = 168, int MaxFiles = 10, long MaxBytes = 100_000_000_000, long Cursor = 0, DateTime? LastRunUtc = null);
public record PreservationResult(long CopyId, string Outcome, string? ExpectedHash, string? ActualHash, FileObservation? Observation, string? Error, DateTime AtUtc);
public record RepairRequest(long HealthyCopyId, string TargetRootId, string TargetPath);

// Audits record observations, never grant optical verification or mutate media.
public sealed class PreservationService(IDbContextFactory<Catalog> factory, Storage storage)
{
    readonly SemaphoreSlim gate = new(1);
    readonly SemaphoreSlim policyGate = new(1);
    const string PolicyId = "preservation-audits";
    public const string SchemaSql = """
        CREATE TRIGGER IF NOT EXISTS PreservationHistoryNoUpdate BEFORE UPDATE ON History
        WHEN OLD.Action LIKE 'preservation-%' OR OLD.Action = 'repair-preview'
        BEGIN SELECT RAISE(ABORT,'Preservation evidence is append only'); END;
        CREATE TRIGGER IF NOT EXISTS PreservationHistoryNoDelete BEFORE DELETE ON History
        WHEN OLD.Action LIKE 'preservation-%' OR OLD.Action = 'repair-preview'
        BEGIN SELECT RAISE(ABORT,'Preservation evidence is append only'); END;
        """;
    public async Task<AuditPolicy> PolicyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Integrations.FindAsync([PolicyId], ct);
        return row == null ? new() : JsonSerializer.Deserialize<AuditPolicy>(row.Payload)!;
    }
    public async Task SavePolicyAsync(AuditPolicy policy, CancellationToken ct = default, bool progressOnly = false)
    {
        if (policy.IntervalHours is < 1 or > 8760 || policy.MaxFiles is < 1 or > 100 || policy.MaxBytes is < 1 or > 10_000_000_000_000) throw new ArgumentException("Audit policy exceeds bounded limits.");
        await policyGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Integrations.FindAsync([PolicyId], ct);
            if (row == null) { row = new() { Id = PolicyId }; db.Add(row); }
            else if (progressOnly) { var latest = JsonSerializer.Deserialize<AuditPolicy>(row.Payload)!; policy = latest with { Cursor = policy.Cursor, LastRunUtc = policy.LastRunUtc }; }
            row.Payload = JsonSerializer.Serialize(policy); row.Revision++;
            await db.SaveChangesAsync(ct);
        }
        finally { policyGate.Release(); }
    }
    public async Task<PreservationResult> AuditAsync(long copyId, CancellationToken ct = default, long maxBytes = long.MaxValue)
    {
        storage.RequireLocalExecution();
        await gate.WaitAsync(ct);
        try { return await AuditCore(copyId, ct, maxBytes); }
        finally { gate.Release(); }
    }
    async Task<PreservationResult> AuditCore(long copyId, CancellationToken ct, long maxBytes)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var copy = await db.Copies.SingleAsync(x => x.Id == copyId, ct);
        if (copy.Role is not ("source" or "library")) throw new ArgumentException("Offline disc verification requires the optical companion.");
        var job = new Operation { Type = "preservation-audit", Status = "auditing", Payload = JsonSerializer.Serialize(new { copyId, expectedHash = copy.Sha256 }) };
        db.Jobs.Add(job); await db.SaveChangesAsync(ct);
        var progress = new JobProgress(factory, job);
        var expected = copy.Sha256; string? actual = null, error = null; FileObservation? observation = null; var outcome = "review-required";
        try
        {
            if (expected == null) throw new IOException("No independent expected hash; review required, not a verification success.");
            using var file = storage.Read(copy.RootId, copy.RelativePath);
            observation = Storage.Observe(file);
            if (observation.Bytes > maxBytes) throw new IOException("File now exceeds the audit batch byte budget.");
            await progress.Report("auditing-file", 0, observation.Bytes, null, ct, true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024]; long readBytes = 0; int read;
            while ((read = await file.ReadAsync(buffer, ct)) != 0)
            {
                if (read > maxBytes - readBytes) throw new IOException("File grew beyond the audit batch byte budget.");
                readBytes += read; hasher.AppendData(buffer, 0, read);
                await progress.Report("auditing-file", readBytes, observation.Bytes, null, ct);
            }
            await progress.Report("auditing-file", readBytes, observation.Bytes, null, ct, true);
            actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (Storage.Observe(file) != observation || storage.Observe(copy.RootId, copy.RelativePath) != observation) throw new IOException("File changed or was replaced during audit.");
            if (actual != expected) throw new IOException("SHA-256 differs from the recorded expectation.");
            if (observation != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "")) throw new IOException("Content matches but file identity changed; review the replacement.");
            outcome = "content-matched";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { error = ex is OperationCanceledException ? "Audit interrupted; no successful evidence." : ex.Message; outcome = ex is OperationCanceledException ? "interrupted" : "review-required"; }
        // No successful audit updates a cached fingerprint/hash: evidence applies only to its observed identity.
        // Conditional invalidation avoids clearing a newer concurrent scan/copy observation.
        if (outcome != "content-matched")
        {
            await db.Copies.Where(x => x.Id == copyId && x.FileIdentity == copy.FileIdentity && x.Sha256 == expected)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256, (string?)null).SetProperty(x => x.State, "review-required"), CancellationToken.None);
            await db.Assets.Where(x => x.Id == copy.AssetId && x.Sha256 == expected).ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256, (string?)null), CancellationToken.None);
        }
        var result = new PreservationResult(copyId, outcome, expected, actual, observation, error, DateTime.UtcNow);
        job.Status = outcome == "content-matched" ? "completed" : outcome; job.Error = error; job.UpdatedUtc = result.AtUtc;
        db.Audit("preservation-audit", JsonSerializer.Serialize(result));
        await db.SaveChangesAsync(CancellationToken.None);
        return result;
    }
    public async Task<int> RunDueBatchAsync(CancellationToken ct = default)
    {
        var policy = await PolicyAsync(ct);
        if (!policy.Enabled || policy.LastRunUtc > DateTime.UtcNow.AddHours(-policy.IntervalHours)) return 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        // One batch per interval; large files above the explicit budget are visibly skipped, never silently hashed.
        var candidates = await db.Copies.AsNoTracking().Where(x => x.Id > policy.Cursor && (x.Role == "library" || x.Role == "source"))
            .OrderBy(x => x.Id).Take(policy.MaxFiles).ToArrayAsync(ct);
        long bytes = 0; var count = 0;
        foreach (var copy in candidates)
        {
            if (!(await PolicyAsync(ct)).Enabled) break;
            if (copy.Bytes <= policy.MaxBytes - bytes) { var result = await AuditAsync(copy.Id, ct, policy.MaxBytes - bytes); bytes += Math.Min(policy.MaxBytes - bytes, Math.Max(copy.Bytes, result.Observation?.Bytes ?? 0)); count++; }
            else { db.Audit("preservation-budget-skipped", JsonSerializer.Serialize(new { copyId = copy.Id, bytes = copy.Bytes, policy.MaxBytes })); await db.SaveChangesAsync(ct); }
            policy = policy with { Cursor = copy.Id }; await SavePolicyAsync(policy, ct, progressOnly: true);
        }
        await SavePolicyAsync(policy with { Cursor = candidates.Length < policy.MaxFiles ? 0 : policy.Cursor, LastRunUtc = DateTime.UtcNow }, ct, progressOnly: true);
        return count;
    }
    public async Task<object> PreviewRepairAsync(RepairRequest request, IQBitCopyGuard guard, CancellationToken ct)
    {
        if (storage.Root(request.TargetRootId).Role != "library") throw new ArgumentException("Repair requires a new library destination.");
        Storage.Components(request.TargetPath);
        var result = await AuditAsync(request.HealthyCopyId, ct);
        if (result.Outcome != "content-matched") throw new IOException("Repair source did not pass a fresh independent content audit.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var copy = await db.Copies.SingleAsync(x => x.Id == request.HealthyCopyId, ct);
        if (storage.Exists(request.TargetRootId, request.TargetPath) || await db.Copies.AnyAsync(x => x.RootId == request.TargetRootId && x.RelativePath.ToUpper() == request.TargetPath.ToUpper(), ct)) throw new IOException("Repair must use an unused destination; originals remain untouched.");
        var completion = await guard.CheckAsync(copy, null, ct);
        var observed = storage.Observe(copy.RootId, copy.RelativePath);
        if (observed != result.Observation) throw new IOException("Repair source changed after audit.");
        var plan = new CopyPlan(copy.Id, request.TargetRootId, request.TargetPath, observed.Bytes, observed.Ticks, SourceIdentity: observed.Identity, QBitRevision: completion.ConfigurationRevision);
        var job = new Operation { Type = "copy", Status = "draft", Payload = JobRunner.Encode(plan) };
        db.Jobs.Add(job); db.Audit("repair-preview", JsonSerializer.Serialize(new { job.Id, request, expectedHash = result.ExpectedHash })); await db.SaveChangesAsync(ct);
        return new { job, plan, completion, requiresApproval = true };
    }
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var job in await db.Jobs.Where(x => x.Type == "preservation-audit" && x.Status == "auditing").ToArrayAsync(ct))
        { job.Status = "interrupted"; job.Error = "Service restarted during audit; no successful evidence."; db.Audit("preservation-interrupted", job.Id); }
        await db.SaveChangesAsync(ct);
    }
}
public sealed class PreservationScheduler(PreservationService service, ILogger<PreservationScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await service.RecoverInterruptedAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do { try { await service.RunDueBatchAsync(stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch (Exception ex) { logger.LogError(ex, "Preservation audit batch failed; no repair was attempted."); } }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public static class PreservationEndpoints
{
    public static void MapPreservation(this WebApplication app)
    {
        app.MapGet("/api/preservation/policy", (PreservationService service, CancellationToken ct) => service.PolicyAsync(ct));
        app.MapPut("/api/preservation/policy", async (AuditPolicy policy, PreservationService service, CancellationToken ct) => { var prior = await service.PolicyAsync(ct); await service.SavePolicyAsync(policy with { Cursor = prior.Cursor, LastRunUtc = prior.LastRunUtc }, ct); return Results.Ok(); });
        app.MapPost("/api/preservation/audit/{copyId:long}", (long copyId, PreservationService service, CancellationToken ct) => service.AuditAsync(copyId, ct));
        app.MapPost("/api/preservation/repair-preview", (RepairRequest request, PreservationService service, IQBitCopyGuard guard, CancellationToken ct) => service.PreviewRepairAsync(request, guard, ct));
        app.MapGet("/api/preservation/history", async (Catalog db) => await db.History.AsNoTracking().Where(x => x.Action.StartsWith("preservation-") || x.Action == "repair-preview").OrderByDescending(x => x.Id).Take(200).ToArrayAsync());
    }
}
