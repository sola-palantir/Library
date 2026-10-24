using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// Offline-issued, single-job capability. Only its hash belongs in server config.
// There is intentionally no endpoint that creates grants and no default grant.
public sealed record SampleHandoffGrant(string JobId, long Bytes, string OutputSha256, string TokenSha256, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
public sealed record SampleHandoffDescriptor(int SchemaVersion, string JobId, long SourceAssetId, long OutputAssetId, long Bytes, string Sha256, string LineageSha256, string Kind, string DownloadPath, DateTimeOffset ExpiresUtc);

public static class SampleHandoff
{
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static int Authorize(LibraryOptions options, string authorization, string id, string expectedHash, DateTimeOffset now)
    {
        if (!ExecutionMode.IsLocal(options) || !options.EnableSampleHandoff || options.SampleHandoffGrant is not { } grant) return 404;
        if (grant.JobId is not { Length: 32 } || !grant.JobId.All(char.IsAsciiHexDigit) || grant.Bytes is <= 0 or > 268435456 || !Digest(grant.OutputSha256) || !Digest(grant.TokenSha256) || grant.ExpiresUtc <= grant.IssuedUtc || grant.ExpiresUtc - grant.IssuedUtc > TimeSpan.FromMinutes(10)) return 404;
        if (now < grant.IssuedUtc || now >= grant.ExpiresUtc || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return 401;
        var token = authorization[7..];
        if (!Digest(token) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(token)), Convert.FromHexString(grant.TokenSha256))) return 401;
        if (id != grant.JobId || !string.Equals(expectedHash, grant.OutputSha256, StringComparison.Ordinal)) return 403;
        return 200;
    }
    public static async Task<(PhysicalCopy Copy, SampleHandoffDescriptor Descriptor)> Describe(string id, Catalog db, Storage storage, SampleHandoffGrant grant, CancellationToken ct)
    {
        var lineage = await db.DerivedAssets.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == id, ct) ?? throw new IOException("Approved sample is unavailable.");
        if (lineage.Kind != "stream-copy-sample") throw new IOException("Only bounded samples may be handed off.");
        var copy = await db.Copies.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == lineage.OutputAssetId && x.Role == "staging", ct) ?? throw new IOException("Sample staging copy is unavailable.");
        if (copy.State != "sample-validated" || copy.Sha256 != grant.OutputSha256 || copy.Bytes != grant.Bytes || storage.Root(copy.RootId).Role != "staging") throw new IOException("Sample no longer matches the offline approval.");
        if (storage.Observe(copy.RootId, copy.RelativePath) != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "")) throw new IOException("Sample changed since validation.");
        return (copy, new(1, id, lineage.SourceAssetId, lineage.OutputAssetId, copy.Bytes, copy.Sha256, DerivedMedia.Digest(lineage.Evidence), lineage.Kind, $"/api/media/samples/{id}/content", grant.ExpiresUtc));
    }
    public static async Task<FileStream> OpenVerified(PhysicalCopy copy, Storage storage, CancellationToken ct)
    {
        var file = storage.Read(copy.RootId, copy.RelativePath);
        try
        {
            var expected = new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "");
            if (Storage.Observe(file) != expected || await JobRunner.Hash(file, ct) != copy.Sha256 || Storage.Observe(file) != expected || storage.Observe(copy.RootId, copy.RelativePath) != expected) throw new IOException("Sample content or identity changed; handoff refused.");
            file.Position = 0; return file;
        }
        catch { file.Dispose(); throw; }
    }
    public static void MapSampleHandoff(this WebApplication app)
    {
        async Task<IResult> Get(string id, bool content, HttpContext context, Catalog db, Storage storage, LibraryOptions options, CancellationToken ct)
        {
            // Secret headers are never copied into URLs, errors, logs, plans or receipts.
            var status = Authorize(options, context.Request.Headers.Authorization.ToString(), id, context.Request.Headers["X-Library-Sample-Sha256"].ToString(), DateTimeOffset.UtcNow);
            context.Response.Headers.CacheControl = "no-store";
            if (status != 200) return Results.StatusCode(status);
            var grant = options.SampleHandoffGrant!;
            var (copy, descriptor) = await Describe(id, db, storage, grant, ct);
            if (!content) return Results.Ok(descriptor);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(grant.ExpiresUtc - DateTimeOffset.UtcNow > TimeSpan.Zero ? grant.ExpiresUtc - DateTimeOffset.UtcNow : TimeSpan.Zero);
            var file = await OpenVerified(copy, storage, timeout.Token);
            return new Download(file, grant.ExpiresUtc, descriptor.Bytes);
        }
        app.MapGet("/api/media/samples/{id}/handoff", (string id, HttpContext context, Catalog db, Storage storage, LibraryOptions options, CancellationToken ct) => Get(id, false, context, db, storage, options, ct));
        app.MapGet("/api/media/samples/{id}/content", (string id, HttpContext context, Catalog db, Storage storage, LibraryOptions options, CancellationToken ct) => Get(id, true, context, db, storage, options, ct));
    }
    private sealed class Download(FileStream file, DateTimeOffset expires, long bytes) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await using var owned = file;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var remaining = expires - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { context.Response.StatusCode = 401; return; }
            deadline.CancelAfter(remaining);
            context.Response.ContentType = "video/x-matroska";
            context.Response.ContentLength = bytes;
            await file.CopyToAsync(context.Response.Body, 1024 * 1024, deadline.Token);
        }
    }
}
