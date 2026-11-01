using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library.Execution;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// Offline provisioned, expiring read-only access. Never creates credentials or pairing.
public record ReviewSessionOptions(string SessionId, string Folder, string ApiOrigin, string TokenSha256, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc)
{
    public string RootId => "review-" + DerivedMedia.Digest(Folder.TrimEnd('\\').ToUpperInvariant())[..24];
    public void Validate()
    {
        if (!Guid.TryParseExact(SessionId, "N", out _) || Folder is null || Folder.Length is < 4 or > 240 || !char.IsAsciiLetter(Folder[0]) || !Folder.StartsWith(Folder[..1] + ":\\") || Folder.EndsWith('\\') || Folder[3..].Split('\\').Any(p => p is "" or "." or ".." || p.Any(c => c < 32 || "/:<>\"|?*".Contains(c)))) throw new ArgumentException("Explicit bounded Windows sample folder required.");
        if (!Uri.TryCreate(ApiOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || ApiOrigin != uri.GetLeftPart(UriPartial.Authority)) throw new ArgumentException("Review sessions require an exact localhost API origin.");
        ExecutionContract.Hash(TokenSha256);
        if (ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > TimeSpan.FromMinutes(10)) throw new ArgumentException("Review session must expire within ten minutes.");
    }
    public bool Active(DateTimeOffset now) => now >= IssuedUtc && now < ExpiresUtc;
    public bool Authorize(string origin, string header, DateTimeOffset now)
    {
        if (origin != ApiOrigin || !Active(now) || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var token = header[7..];
        return token.Length == 64 && token.All(char.IsAsciiHexDigit) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(token)), Convert.FromHexString(TokenSha256));
    }
}

public sealed class ReadOnlyReview(LibraryOptions options, IDbContextFactory<Catalog> factory) : BackgroundService
{
    public const string JobType = "read-only-sample-scan";
    readonly SemaphoreSlim gate = new(1, 1);
    DateTimeOffset lastWorker;
    ReviewSessionOptions Grant => options.ReviewSession ?? throw new InvalidOperationException("Start an explicitly scoped read-only worker session before adding a sample folder. No permanent pairing is enabled.");
    public bool Connected => options.ReviewSession?.Active(DateTimeOffset.UtcNow) == true && DateTimeOffset.UtcNow - lastWorker < TimeSpan.FromSeconds(5);
    public object Capabilities() => new { enabled = options.ReviewSession?.Active(DateTimeOffset.UtcNow) == true, connected = Connected, folder = options.ReviewSession?.Folder, expiresUtc = options.ReviewSession?.ExpiresUtc, maxFiles = ReviewScan.MaxFiles, recursive = false, encoding = false, mediaTransfer = false, persistentPairing = false };
    async Task<T> Locked<T>(Func<Catalog, Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await using var db = await factory.CreateDbContextAsync(ct); return await action(db); }
        finally { gate.Release(); }
    }
    public Task<Operation> Queue(string folder, CancellationToken ct) => Locked(async db => {
        if (!Grant.Active(DateTimeOffset.UtcNow) || !Connected) throw new InvalidOperationException("The temporary worker is disconnected or expired. Start a fresh session, then retry.");
        if (!string.Equals(folder.TrimEnd('\\'), Grant.Folder, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("This session can inspect only its locally approved sample folder.");
        await Sweep(db, false, ct);
        if (await db.Jobs.AnyAsync(j => j.Type == JobType && (j.Status == "queued" || j.Status == "running"), ct)) throw new IOException("A sample scan is already active. Cancel it or wait for completion.");
        var job = new Operation { Type = JobType };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3); if (deadline > Grant.ExpiresUtc) deadline = Grant.ExpiresUtc;
        job.Payload = ExecutionContract.Serialize(new ReviewScanCommand(job.Id, Grant.SessionId, Grant.RootId, deadline));
        db.Add(job); db.Audit("read-only-scan-queued", job.Id); await db.SaveChangesAsync(ct); return job;
    }, ct);
    public Task<ReviewScanCommand?> Poll(CancellationToken ct) => Locked(async db => {
        lastWorker = DateTimeOffset.UtcNow; await Sweep(db, false, ct);
        var job = await db.Jobs.Where(j => j.Type == JobType && (j.Status == "queued" || j.Status == "running")).OrderBy(j => j.CreatedUtc).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        // A second worker never acquires a running command. A lost worker times out;
        // retry creates a new job ID, so delayed receipts cannot commit over it.
        if (job.Status == "running") return null;
        job.Status = "running"; job.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        return JobRunner.Decode<ReviewScanCommand>(job.Payload);
    }, ct);
    public Task<object> State(string? id, CancellationToken ct) => Locked<object>(async db => {
        await Sweep(db, false, ct);
        return await db.Jobs.Where(j => j.Type == JobType && (id == null || j.Id == id)).OrderByDescending(j => j.CreatedUtc).Take(20).Select(j => new { j.Id, j.Status, j.Error, j.CreatedUtc, j.UpdatedUtc }).ToListAsync(ct);
    }, ct);
    public Task<bool> KeepRunning(string id, CancellationToken ct) => Locked(async db => {
        lastWorker = DateTimeOffset.UtcNow; await Sweep(db, false, ct);
        return await db.Jobs.AnyAsync(j => j.Id == id && j.Type == JobType && j.Status == "running" && !j.CancelRequested, ct);
    }, ct);
    public Task<bool> Cancel(string id, CancellationToken ct) => Locked(async db => {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id && j.Type == JobType, ct) ?? throw new ArgumentException("Unknown review scan.");
        if (job.Status is "queued" or "running") { job.Status = "cancelled"; job.CancelRequested = true; job.UpdatedUtc = DateTime.UtcNow; db.Audit("read-only-scan-cancelled", id); await db.SaveChangesAsync(ct); }
        return true;
    }, ct);
    public async Task<Operation> Retry(string id, CancellationToken ct)
    {
        await Locked(async db => {
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id && j.Type == JobType, ct) ?? throw new ArgumentException("Unknown review scan.");
            if (job.Status is not ("cancelled" or "failed") || JobRunner.Decode<ReviewScanCommand>(job.Payload).RootId != Grant.RootId) throw new ArgumentException("Retry requires a failed or cancelled scan of this session's approved folder.");
            return true;
        }, ct);
        return await Queue(Grant.Folder, ct);
    }
    public Task<bool> Fail(string id, string error, CancellationToken ct) => Locked(async db => {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id && j.Type == JobType, ct) ?? throw new ArgumentException("Unknown review scan.");
        if (job.Status == "running") { job.Status = "failed"; job.Error = new string(error.Where(c => !char.IsControl(c)).Take(512).ToArray()); job.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        return true;
    }, ct);
    public Task<object> Accept(ReviewScanResult result, CancellationToken ct) => Locked<object>(async db => {
        ReviewScan.Validate(result); await Sweep(db, false, ct);
        if (result.SessionId != Grant.SessionId || result.RootId != Grant.RootId) throw new IOException("Scan result belongs to a different session or folder.");
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == result.Id && j.Type == JobType, ct) ?? throw new ArgumentException("Unknown review scan.");
        var encoded = ExecutionContract.Serialize(result);
        if (job.Status == "completed" && job.ProgressJson == encoded) return new { duplicate = true, count = result.Files.Length };
        if (job.Status != "running" || job.CancelRequested || JobRunner.Decode<ReviewScanCommand>(job.Payload).SessionId != result.SessionId) throw new IOException("Scan is no longer accepting results. No catalog files were changed.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var file in result.Files)
        {
            Storage.Components(file.RelativePath);
            var copy = await db.Copies.SingleOrDefaultAsync(c => c.RootId == result.RootId && c.RelativePath.ToUpper() == file.RelativePath.ToUpper(), ct);
            if (copy == null)
            {
                var inferred = Naming.Infer(Path.GetFileNameWithoutExtension(file.RelativePath));
                db.Titles.Add(inferred.Title); await db.SaveChangesAsync(ct);
                inferred.Edition.TitleId = inferred.Title.Id; db.Editions.Add(inferred.Edition); await db.SaveChangesAsync(ct);
                var asset = new Asset { EditionId = inferred.Edition.Id, FileName = file.RelativePath, Bytes = file.Observation.Bytes }; db.Assets.Add(asset); await db.SaveChangesAsync(ct);
                copy = new() { AssetId = asset.Id, RootId = result.RootId, RelativePath = file.RelativePath, Role = "source" }; db.Copies.Add(copy);
            }
            if (copy.Bytes != file.Observation.Bytes || copy.ModifiedTicks != file.Observation.Ticks || copy.FileIdentity != file.Observation.Identity)
            {
                copy.Sha256 = null;
                var asset = await db.Assets.SingleAsync(a => a.Id == copy.AssetId, ct); asset.Sha256 = null; asset.Bytes = file.Observation.Bytes;
            }
            copy.RelativePath = file.RelativePath;
            copy.Bytes = file.Observation.Bytes; copy.ModifiedTicks = file.Observation.Ticks; copy.FileIdentity = file.Observation.Identity;
            copy.State = "observed"; copy.ScanId = job.Id;
        }
        // A failed/cancelled/partial scan never marks missing files or writes a partial inventory.
        job.Status = "completed"; job.ProgressJson = encoded; job.UpdatedUtc = DateTime.UtcNow;
        db.Audit("read-only-scan-completed", $"{job.Id}: {result.Files.Length} observations; no content hashes, media writes or processing eligibility");
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new { duplicate = false, count = result.Files.Length };
    }, ct);
    public Task<object> Inspect(long copyId, CancellationToken ct) => Locked<object>(async db => {
        var copy = await db.Copies.SingleOrDefaultAsync(c => c.Id == copyId, ct) ?? throw new ArgumentException("Unknown catalog file.");
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == copy.ScanId && j.Type == JobType && j.Status == "completed", ct) ?? throw new ArgumentException("No completed native sample observation. Scan the folder first.");
        var result = JobRunner.Decode<ReviewScanResult>(job.ProgressJson);
        var file = result.Files.Single(f => f.RelativePath == copy.RelativePath);
        if (file.Observation != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "")) throw new IOException("Catalog observation changed; scan again.");
        return new { probe = file.Probe, evidence = new { origin = "native-read-only-scan", copyId = copy.Id, observedUtc = job.UpdatedUtc, toolVersion = result.ToolVersion, contentVerified = false, executable = false, cached = true } };
    }, ct);
    async Task Sweep(Catalog db, bool restart, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in await db.Jobs.Where(j => j.Type == JobType && (j.Status == "queued" || j.Status == "running")).ToListAsync(ct))
        {
            var command = JobRunner.Decode<ReviewScanCommand>(job.Payload);
            if (restart || options.ReviewSession?.SessionId != command.SessionId || options.ReviewSession?.Active(now) != true || command.DeadlineUtc <= now || job.CancelRequested)
            { job.Status = job.CancelRequested ? "cancelled" : "failed"; job.Error = restart ? "API restarted. Start a worker session and retry; no partial inventory was registered." : "Scan deadline or session ended. Retry with a connected worker; no partial inventory was registered."; job.UpdatedUtc = DateTime.UtcNow; }
        }
        await db.SaveChangesAsync(ct);
    }
    public override async Task StartAsync(CancellationToken ct)
    {
        await Locked(async db => { await Sweep(db, true, ct); return true; }, ct);
        await base.StartAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) { await Task.Delay(1000, ct); await Locked(async db => { await Sweep(db, false, ct); return true; }, ct); }
    }
}

public static class ReadOnlyReviewEndpoints
{
    public static void MapReadOnlyReview(this WebApplication app)
    {
        app.MapGet("/api/review-scan", (ReadOnlyReview service) => service.Capabilities());
        app.MapPost("/api/review-scan", (ReviewFolderRequest request, ReadOnlyReview service, CancellationToken ct) => service.Queue(request.Folder, ct));
        app.MapGet("/api/review-scan/jobs", (ReadOnlyReview service, CancellationToken ct) => service.State(null, ct));
        app.MapPost("/api/review-scan/jobs/{id}/cancel", (string id, ReadOnlyReview service, CancellationToken ct) => service.Cancel(id, ct));
        app.MapPost("/api/review-scan/jobs/{id}/retry", (string id, ReadOnlyReview service, CancellationToken ct) => service.Retry(id, ct));
        app.MapGet("/api/review-scan/copies/{id:long}", (long id, ReadOnlyReview service, CancellationToken ct) => service.Inspect(id, ct));
        var group = app.MapGroup("/api/review-worker");
        group.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext; var options = http.RequestServices.GetRequiredService<LibraryOptions>();
            if (options.ExecutionBackend != ExecutionMode.Windows || options.ReviewSession == null) return Results.NotFound();
            if (!options.ReviewSession.Authorize($"{http.Request.Scheme}://{http.Request.Host}", http.Request.Headers.Authorization.ToString(), DateTimeOffset.UtcNow)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/command", (ReadOnlyReview service, CancellationToken ct) => service.Poll(ct));
        group.MapGet("/jobs/{id}/running", (string id, ReadOnlyReview service, CancellationToken ct) => service.KeepRunning(id, ct));
        group.MapPost("/jobs/{id}/failure", (string id, ReviewFailure request, ReadOnlyReview service, CancellationToken ct) => service.Fail(id, request.Error, ct));
        group.MapPost("/result", async (HttpContext context, ReadOnlyReview service, CancellationToken ct) => {
            using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while ((count = await context.Request.Body.ReadAsync(buffer, deadline.Token)) > 0) { if (body.Length + count > ReviewScan.MaxResultBytes) return Results.StatusCode(413); body.Write(buffer, 0, count); }
            var result = JsonSerializer.Deserialize<ReviewScanResult>(body.ToArray(), ExecutionContract.Json) ?? throw new ArgumentException("Missing scan result.");
            return Results.Ok(await service.Accept(result, deadline.Token));
        });
    }
}
public record ReviewFolderRequest(string Folder);
public record ReviewFailure(string Error);
