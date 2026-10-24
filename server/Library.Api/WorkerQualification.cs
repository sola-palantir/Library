using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// A single offline-issued, temporary synthetic qualification grant. No pairing
// endpoint, media download, generalized job queue or persistent worker access.
public record WorkerQualificationGrant(string CommandJson, string TokenSha256, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc)
{
    // IConfiguration flattens empty arrays. Keep the exact reviewed JSON intact.
    [JsonIgnore] public WorkerCommand Command => JsonSerializer.Deserialize<WorkerCommand>(CommandJson, ExecutionContract.Json) ?? throw new ArgumentException("Missing offline command.");
}
public static class WorkerQualification
{
    public const string OperationType = "worker-qualification";
    public const string ValidationDetail = "Synthetic fixture; not media verification.";
    static readonly JsonSerializerOptions StrictJson = new(ExecutionContract.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static int Authorize(LibraryOptions options, string host, string authorization, string jobId, string planHash, DateTimeOffset now)
    {
        var grant = options.WorkerQualification;
        if (options.ExecutionBackend != ExecutionMode.Windows || grant == null || host != "127.0.0.1:5084") return 404;
        if (grant.CommandJson is null || grant.CommandJson.Length > 65536) return 404;
        try { ExecutionContract.Validate(grant.Command); ExecutionContract.Hash(grant.TokenSha256); }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { return 404; }
        if (grant.ExpiresUtc <= grant.IssuedUtc || grant.ExpiresUtc - grant.IssuedUtc > TimeSpan.FromMinutes(10)) return 404;
        if (now < grant.IssuedUtc || now >= grant.ExpiresUtc || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return 401;
        var token = authorization[7..];
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(token)), Convert.FromHexString(grant.TokenSha256))) return 401;
        if (jobId != grant.Command.JobId || planHash != ExecutionContract.Digest(grant.Command)) return 403;
        return 200;
    }
    public static async Task<WorkerCommand> Command(Catalog db, WorkerQualificationGrant grant, CancellationToken ct)
    {
        var command = grant.Command; var encoded = ExecutionContract.Serialize(command);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == command.JobId, ct);
        if (job != null)
        {
            if (job.Type != OperationType || job.Payload != encoded) throw new IOException("Qualification job collides with an existing immutable catalog operation.");
            return command;
        }
        // GET is read-only. Native journal owns in-flight execution; the API
        // records the one synthetic result transactionally when it arrives.
        return command;
    }
    public static async Task<object> Accept(Catalog db, WorkerQualificationGrant grant, WorkerResult result, CancellationToken ct)
    {
        ExecutionContract.Match(grant.Command, result, allowSynthetic: true);
        if (result.Validation.Kind != "synthetic" || result.Validation.Detail != ValidationDetail) throw new IOException("This route accepts only synthetic qualification evidence.");
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == grant.Command.JobId, ct);
        if (job == null) { job = new() { Id = grant.Command.JobId, Type = OperationType, Status = "qualification-ready", Payload = ExecutionContract.Serialize(grant.Command) }; db.Add(job); }
        if (job.Type != OperationType || job.Payload != ExecutionContract.Serialize(grant.Command)) throw new IOException("Qualification command changed.");
        var encoded = ExecutionContract.Serialize(result);
        if (job.Status == "qualification-completed")
        {
            if (job.ProgressJson != encoded) throw new IOException("Conflicting qualification receipt; existing evidence retained.");
            return new { status = job.Status, duplicate = true, catalogMediaVerified = false };
        }
        if (job.Status != "qualification-ready" || job.CancelRequested) throw new IOException("Qualification operation is no longer accepting results.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        job.Status = "qualification-completed"; job.ProgressJson = encoded; job.UpdatedUtc = DateTime.UtcNow;
        db.Audit("worker-qualification-completed", encoded);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new { status = job.Status, duplicate = false, catalogMediaVerified = false };
    }
    public static void MapWorkerQualification(this WebApplication app)
    {
        // Also serialize GET collision checks with receipt registration.
        var gate = new SemaphoreSlim(1, 1);
        async Task<IResult> Handle(string id, bool result, HttpContext context, Catalog db, LibraryOptions options)
        {
            context.Response.Headers.CacheControl = "no-store";
            var authorized = Authorize(options, context.Request.Host.Value ?? "", context.Request.Headers.Authorization.ToString(), id,
                context.Request.Headers["X-Library-Plan-Sha256"].ToString(), DateTimeOffset.UtcNow);
            if (authorized != 200) return Results.StatusCode(authorized);
            var grant = options.WorkerQualification!; var ct = context.RequestAborted;
            var remaining = grant.ExpiresUtc - DateTimeOffset.UtcNow; if (remaining <= TimeSpan.Zero) return Results.StatusCode(401);
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct); expiry.CancelAfter(remaining);
            var entered = false;
            try
            {
                await gate.WaitAsync(expiry.Token); entered = true;
                if (!result) return Results.Json(await Command(db, grant, expiry.Token), ExecutionContract.Json);
                if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
                using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, expiry.Token)) > 0)
                { if (bytes.Length + read > 131072) return Results.StatusCode(413); bytes.Write(buffer, 0, read); }
                var receipt = JsonSerializer.Deserialize<WorkerResult>(bytes.ToArray(), StrictJson) ?? throw new ArgumentException("Missing qualification receipt.");
                return Results.Ok(await Accept(db, grant, receipt, expiry.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(401); }
            finally { if (entered) gate.Release(); }
        }
        app.MapGet("/api/worker-qualification/{id}/command", (string id, HttpContext context, Catalog db, LibraryOptions options) => Handle(id, false, context, db, options));
        app.MapPost("/api/worker-qualification/{id}/result", (string id, HttpContext context, Catalog db, LibraryOptions options) => Handle(id, true, context, db, options));
    }
}
