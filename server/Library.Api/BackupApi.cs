using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public record SelectedSidecar(long MediaCopyId, string Path);
public record BackupSelection(string Name, long[] CopyIds, long CapacityBytes, long ReserveBytes, string Objective = "minimize-discs", SeasonScope? Season = null, SelectedSidecar[]? Sidecars = null);
public record BackupApproval(string CommandId);
public record BackupDraft(BackupRequest Request, string Digest);
public record BackupStageRequest(string DiscId, string RootId, string ManifestSha256);
public record CompanionKey(string PublicKeyPem);
public record SignedReceipt(string Payload, string Signature);
public record CompanionEvidence(int SchemaVersion, string CommandId, string DiscId, string ManifestSha256, string RecorderId, string State, DateTime AtUtc,
    string? ImageSha256 = null, long? ImageBytes = null, long? FreeSectors = null, string? Detail = null, bool Simulated = false, string Mode = "hardware",
    string? OpticalRoot = null, bool ManifestMatched = false, bool AllFilesVerified = false, string? RequestSha256 = null, long Sequence = 0);

public static class BackupApi
{
    private static readonly SemaphoreSlim ReceiptGate = new(1);
    public static void MapBackups(this WebApplication app)
    {
        app.MapPost("/api/backups/preview", async (BackupSelection selection, Catalog db, Storage storage, CancellationToken ct) =>
        {
            var request = await Resolve(selection, db, storage, ct);
            var preview = BackupDomain.Preview(request);
            var job = new Operation { Type = "backup-plan", Status = "draft", Payload = JobRunner.Encode(new BackupDraft(request, preview.Sha256)) };
            db.Add(job); await db.SaveChangesAsync(ct);
            return Results.Ok(new { job, preview });
        });
        app.MapPost("/api/backups/{id}/approve", async (string id, BackupApproval approval, Catalog db, Storage storage) =>
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == id && x.Type == "backup-plan");
            var draft = JobRunner.Decode<BackupDraft>(job.Payload);
            // The immutable draft ID is the idempotency authority, including after a
            // lost response between set creation and the operation status update.
            var command = "backup-" + job.Id;
            var previous = await db.Set<BackupSetRecord>().SingleOrDefaultAsync(x => x.CommandId == command);
            if (previous != null) return Results.Ok(previous);
            if (job.Status != "draft") throw new ArgumentException("Backup preview is not approvable.");
            await ValidateSources(draft.Request, db, storage);
            var set = await new BackupService(db).ApproveAsync(draft.Request, draft.Digest, command);
            job.Status = "completed"; db.Audit("backup-approved", set.Id); await db.SaveChangesAsync(); return Results.Ok(set);
        });
        app.MapGet("/api/backups", async (string? q, Catalog db) =>
        {
            var query = db.Set<BackupSetRecord>().AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Name.Contains(q) || x.SnapshotJson.Contains(q) || db.Set<BackupPhysicalDisc>().Any(d => d.SetId == x.Id && d.Label.Contains(q)));
            var sets = await query.OrderByDescending(x => x.CreatedUtc).Take(100).ToArrayAsync();
            var ids = sets.Select(x => x.Id).ToArray();
            var discs = await db.Set<BackupPhysicalDisc>().Where(x => ids.Contains(x.SetId)).OrderBy(x => x.Ordinal).ToArrayAsync();
            var discIds = discs.Select(x => x.Id).ToArray();
            var evidence = await db.Set<BackupEvidence>().Where(x => discIds.Contains(x.DiscId)).OrderBy(x => x.Id).ToArrayAsync();
            var coverage = sets.Select(set =>
            {
                var preview = JsonSerializer.Deserialize<BackupPreview>(set.SnapshotJson)!;
                var verifiedOrdinals = discs.Where(d => d.SetId == set.Id && evidence.LastOrDefault(e => e.DiscId == d.Id)?.State == "verified").Select(d => d.Ordinal).Distinct().Count();
                return new { setId = set.Id, scope = preview.Coverage, verifiedDiscs = verifiedOrdinals, plannedDiscs = preview.Packing.Discs.Length,
                    selectedScopeVerified = verifiedOrdinals == preview.Packing.Discs.Length,
                    entireSeasonVerified = preview.Coverage?.EntireSeasonIncluded == true && verifiedOrdinals == preview.Packing.Discs.Length };
            }).ToArray();
            return Results.Ok(new { sets, discs, evidence, coverage, limit = 100 });
        });
        app.MapPost("/api/backups/discs/{id}/stage", async (string id, RootRequest request, Catalog db, Storage storage) =>
        {
            if (storage.Root(request.RootId).Role != "staging") throw new ArgumentException("Choose an archive staging root.");
            var disc = await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == id);
            var payload = JobRunner.Encode(new BackupStageRequest(id, request.RootId, disc.Sha256));
            var pending = await db.Jobs.FirstOrDefaultAsync(x => x.Type == "backup-stage" && x.Payload == payload && (x.Status == "running" || x.Status == "queued"));
            if (pending != null) return Results.Ok(pending);
            if (await db.Jobs.CountAsync(x => x.Status == "running" || x.Status == "queued") >= 100) throw new ArgumentException("Job queue is full.");
            var job = new Operation { Type = "backup-stage", Payload = payload }; db.Add(job); await db.SaveChangesAsync(); return Results.Ok(job);
        });
        app.MapGet("/api/backups/discs/{id}/manifest", async (string id, Catalog db) =>
        {
            var disc = await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == id);
            return Results.File(Encoding.UTF8.GetBytes(disc.ManifestJson), "application/json", "LIBRARY-MANIFEST.json");
        });
        app.MapGet("/api/backups/discs/{id}/command", async (string id, Catalog db) =>
        {
            var disc = await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == id);
            var set = await db.Set<BackupSetRecord>().SingleAsync(x => x.Id == disc.SetId);
            var preview = JsonSerializer.Deserialize<BackupPreview>(set.SnapshotJson)!;
            return Results.File(Encoding.UTF8.GetBytes(JobRunner.Encode(new { commandId = Guid.NewGuid().ToString("N"), discId = id, manifestSha256 = disc.Sha256, recorderId = "SELECT-IN-WINDOWS-COMPANION", reserveBytes = preview.Request.ReserveBytes })), "application/json", disc.Label + "-command.json");
        });
        app.MapPost("/api/backups/discs/{id}/replacement", async (string id, BackupApproval request, Catalog db) => Results.Ok(await new BackupService(db).CreateReplacementAsync(id, request.CommandId)));
        app.MapPost("/api/backups/report", async (BackupAttemptRequest request, Catalog db) => Results.Ok(await new BackupService(db).RecordAsync(request)));
        app.MapGet("/api/backups/companion", async (Catalog db) =>
        {
            var key = await db.Integrations.FindAsync("companion-public-key");
            return Results.Ok(new { configured = key != null, keyFingerprint = key == null ? null : BackupDomain.Digest(key.Payload) });
        });
        app.MapPut("/api/backups/companion", async (CompanionKey request, Catalog db) =>
        {
            using var key = ECDsa.Create();
            try { key.ImportFromPem(request.PublicKeyPem); } catch (Exception ex) when (ex is ArgumentException or CryptographicException) { throw new ArgumentException("A valid companion ECDSA public key is required."); }
            if (request.PublicKeyPem.Contains("PRIVATE", StringComparison.Ordinal) || key.KeySize != 256) throw new ArgumentException("Only a public P-256 pairing key is accepted.");
            var canonical = key.ExportSubjectPublicKeyInfoPem();
            var existing = await db.Integrations.FindAsync("companion-public-key");
            if (existing != null && existing.Payload != canonical) throw new ArgumentException("Companion key is already pinned. Rotation requires an explicit offline review; history must be retained.");
            if (existing == null) db.Integrations.Add(new() { Id = "companion-public-key", Payload = canonical, Revision = 1 });
            db.Audit("companion-key-pinned", BackupDomain.Digest(canonical)); await db.SaveChangesAsync(); return Results.Ok();
        });
        app.MapPost("/api/backups/receipts", async (SignedReceipt receipt, Catalog db) => Results.Ok(await ImportReceipt(receipt, db)));
    }

    public static async Task<BackupRequest> Resolve(BackupSelection selection, Catalog db, Storage storage, CancellationToken ct)
    {
        if (selection.CopyIds.Length is < 1 or > 500 || selection.CopyIds.Distinct().Count() != selection.CopyIds.Length) throw new ArgumentException("Select 1–500 distinct media copies.");
        if ((selection.Sidecars?.Length ?? 0) > 2000 || selection.Sidecars?.Any(x => !selection.CopyIds.Contains(x.MediaCopyId)) == true) throw new ArgumentException("Sidecars must belong to selected media.");
        var bundles = new List<BackupBundle>();
        long sidecarBytes = 0;
        foreach (var id in selection.CopyIds)
        {
            var row = await (from c in db.Copies join a in db.Assets on c.AssetId equals a.Id join e in db.Editions on a.EditionId equals e.Id join t in db.Titles on e.TitleId equals t.Id where c.Id == id select new { c, e, t }).SingleAsync(ct);
            var copy = row.c;
            if (copy.Role != "library" || copy.State != "verified" || copy.Sha256 == null || copy.FileIdentity == null) throw new ArgumentException("Choose verified library copies; audit changed or unverified files first.");
            if (storage.Observe(copy.RootId, copy.RelativePath) != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity)) throw new IOException("Library copy changed. Run an audit before planning.");
            if (selection.Season != null && (row.t.Kind != "tv" || row.e.Season != selection.Season.Season || row.t.Name != selection.Season.Title)) throw new ArgumentException("Season selection must belong to the declared show and season.");
            var files = new List<BackupFile> { new(id, copy.RelativePath, copy.Bytes, copy.Sha256, copy.FileIdentity, SourceRootId: copy.RootId, SourcePath: copy.RelativePath) };
            foreach (var sidecar in selection.Sidecars?.Where(x => x.MediaCopyId == id) ?? [])
            {
                Storage.Components(sidecar.Path);
                var extension = Path.GetExtension(sidecar.Path).ToLowerInvariant();
                if (!new[] { ".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".jpg", ".jpeg", ".png" }.Contains(extension) || Path.GetDirectoryName(sidecar.Path) != Path.GetDirectoryName(copy.RelativePath)) throw new ArgumentException("Selected sidecars must use a supported extension in the media folder.");
                using var file = storage.Read(copy.RootId, sidecar.Path);
                var observed = Storage.Observe(file);
                if (observed.Bytes > 64L * 1024 * 1024 || observed.Bytes > 256L * 1024 * 1024 - sidecarBytes) throw new ArgumentException("Sidecar preview limit: 64 MiB per file and 256 MiB total. Reduce the selected batch.");
                sidecarBytes += observed.Bytes;
                var hash = await JobRunner.Hash(file, ct);
                if (Storage.Observe(file) != observed || storage.Observe(copy.RootId, sidecar.Path) != observed) throw new IOException("Sidecar changed while reading.");
                files.Add(new(id, sidecar.Path, observed.Bytes, hash, observed.Identity, true, copy.RootId, sidecar.Path));
            }
            bundles.Add(new(id.ToString(), row.t.Name, files.ToArray(), row.e.Episode));
        }
        return new(selection.Name, bundles.ToArray(), selection.CapacityBytes, selection.ReserveBytes, selection.Objective, selection.Season);
    }
    public static async Task ValidateSources(BackupRequest request, Catalog db, Storage storage)
    {
        foreach (var file in request.Bundles.SelectMany(x => x.Files))
        {
            var copy = await db.Copies.SingleAsync(x => x.Id == file.CopyId);
            if (copy.Role != "library" || file.SourceRootId != copy.RootId || file.SourcePath != file.Path) throw new IOException("Backup source binding changed.");
            var observation = storage.Observe(copy.RootId, file.SourcePath);
            if (observation.Bytes != file.Bytes || observation.Identity != file.FileIdentity || (!file.Sidecar && (copy.Sha256 != file.Sha256 || copy.State != "verified"))) throw new IOException("Backup preview is stale. Create a fresh preview.");
        }
    }
    public static async Task<BackupEvidence> ImportReceipt(SignedReceipt receipt, Catalog db)
    {
        await ReceiptGate.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await ImportReceiptCore(receipt, db);
            await transaction.CommitAsync(); return result;
        }
        finally { ReceiptGate.Release(); }
    }
    private static async Task<BackupEvidence> ImportReceiptCore(SignedReceipt receipt, Catalog db)
    {
        if (receipt.Payload.Length > 128000 || receipt.Signature.Length > 200) throw new ArgumentException("Receipt too large.");
        var pin = await db.Integrations.FindAsync("companion-public-key") ?? throw new ArgumentException("Pair the Windows companion public key first.");
        using var key = ECDsa.Create(); key.ImportFromPem(pin.Payload);
        byte[] signature; try { signature = Convert.FromBase64String(receipt.Signature); } catch (FormatException) { throw new ArgumentException("Invalid receipt signature."); }
        if (!key.VerifyData(Encoding.UTF8.GetBytes(receipt.Payload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new ArgumentException("Receipt signature does not match the pinned companion.");
        var e = JobRunner.Decode<CompanionEvidence>(receipt.Payload);
        if (e.SchemaVersion != 1 || e.Sequence <= 0 || e.Mode != "hardware" || e.Simulated || string.IsNullOrWhiteSpace(e.CommandId) || string.IsNullOrWhiteSpace(e.RecorderId)) throw new ArgumentException("Receipt lacks hardware evidence or sequence.");
        var disc = await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == e.DiscId);
        if (e.ManifestSha256 != disc.Sha256 || BackupDomain.Digest(disc.ManifestJson) != disc.Sha256) throw new ArgumentException("Receipt is for a different manifest.");
        if (e.State is not ("staged" or "writing" or "finalizing" or "written-unverified" or "verifying" or "verified" or "failed" or "interrupted")) throw new ArgumentException("Unsupported receipt state.");
        if (e.State == "verified" && (!e.ManifestMatched || !e.AllFilesVerified || string.IsNullOrWhiteSpace(e.OpticalRoot) || e.ImageSha256?.Length != 64)) throw new ArgumentException("Full optical read-back evidence is required.");
        var command = "receipt-" + BackupDomain.Digest(receipt.Payload);
        var duplicate = await db.Set<BackupEvidence>().SingleOrDefaultAsync(x => x.CommandId == command); if (duplicate != null) return duplicate;
        var prior = await db.Set<BackupEvidence>().Where(x => x.DiscId == disc.Id).OrderByDescending(x => x.Id).ToArrayAsync();
        if (prior.Any(x => x.State == "retired")) throw new ArgumentException("Physical disc is retired.");
        if (e.AtUtc > DateTime.UtcNow.AddMinutes(5)) throw new ArgumentException("Companion clock is ahead; reconcile clocks before importing evidence.");
        if (e.State == "verified" && prior.Any(x => !x.CommandId.StartsWith("receipt-", StringComparison.Ordinal) && x.State is "failed" or "interrupted" or "review-required" && x.AtUtc >= e.AtUtc))
            throw new ArgumentException("Verification predates a recorded problem; perform a new optical read-back.");
        foreach (var item in prior.Where(x => x.CommandId.StartsWith("receipt-", StringComparison.Ordinal)))
        {
            var old = JobRunner.Decode<SignedReceipt>(item.DetailJson); var oldEvidence = JobRunner.Decode<CompanionEvidence>(old.Payload);
            if (oldEvidence.ImageSha256 != null && e.ImageSha256 != null && oldEvidence.ImageSha256 != e.ImageSha256) throw new ArgumentException("Physical disc image identity changed.");
            if (oldEvidence.Sequence >= e.Sequence) throw new ArgumentException("Stale companion receipt cannot replace newer evidence.");
        }
        var result = new BackupEvidence { DiscId = e.DiscId, CommandId = command, AttemptId = e.CommandId, State = e.State, AtUtc = DateTime.UtcNow, DetailJson = JobRunner.Encode(receipt) };
        db.Add(result); await db.SaveChangesAsync(); return result;
    }
}
