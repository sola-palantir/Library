using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public sealed class JobRunner(IDbContextFactory<Catalog> factory, Storage storage, ILogger<JobRunner> logger, IQBitCopyGuard? guard = null) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new ArgumentException("Invalid plan.");
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
        {
            foreach (var job in await db.Jobs.Where(x => x.Status == "running").ToListAsync(stoppingToken))
            { job.Status = "queued"; db.Audit("recovered", job.Id); }
            await db.SaveChangesAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            string? id;
            await using (var db = await factory.CreateDbContextAsync(stoppingToken))
                id = await db.Jobs.Where(x => x.Status == "queued").OrderBy(x => x.CreatedUtc).Select(x => x.Id).FirstOrDefaultAsync(stoppingToken);
            if (id == null) { await Task.Delay(500, stoppingToken); continue; }
            await Run(id, stoppingToken);
        }
    }
    public async Task Run(string id, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var job = await db.Jobs.SingleAsync(x => x.Id == id, stop);
        if (job.CancelRequested) { job.Status = "cancelled"; await db.SaveChangesAsync(stop); return; }
        job.Status = "running"; job.Error = null; job.UpdatedUtc = DateTime.UtcNow;
        db.Audit("started", job.Id); await db.SaveChangesAsync(stop);
        try
        {
            switch (job.Type)
            {
                case "scan": await Scan(job, stop); break;
                case "copy": await Copy(job, db, stop); break;
                case "disc-stage": await StageDisc(job, db, stop); break;
                case "disc-verify": await VerifyDisc(job, db, stop); break;
                default: throw new ArgumentException("Unknown job type.");
            }
            job.Status = "completed";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { return; } // remains running; next start recovers from journal
        catch (OperationCanceledException) { job.Status = "cancelled"; job.Error = "Cancelled. Source retained; no completed media was removed."; }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or CryptographicException)
        { job.Status = "failed"; job.Error = ex.Message; logger.LogWarning("Job {Id} failed: {Message}", job.Id, ex.Message); }
        job.UpdatedUtc = DateTime.UtcNow;
        db.Audit(job.Status, job.Id); await db.SaveChangesAsync(CancellationToken.None);
    }
    private async Task CheckCancellation(string id, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(stop);
        if (await db.Jobs.Where(x => x.Id == id).Select(x => x.CancelRequested).SingleAsync(stop)) throw new OperationCanceledException();
    }
    private async Task Scan(Operation job, CancellationToken stop)
    {
        var rootId = Decode<string>(job.Payload);
        var root = storage.Root(rootId);
        if (root.Role == "disc" || root.Role == "staging") throw new ArgumentException("Scan a source or library root.");
        using var rootHandle = storage.OpenRoot(rootId); // offline root fails, never marks entire catalog missing
        await using var db = await factory.CreateDbContextAsync(stop);
        var count = 0;
        foreach (var path in storage.EnumerateMedia(rootId))
        {
            await CheckCancellation(job.Id, stop);
            var fingerprint = storage.Observe(rootId, path);
            await using var transaction = await db.Database.BeginTransactionAsync(stop);
            var existing = await db.Copies.SingleOrDefaultAsync(x => x.RootId == rootId && x.RelativePath == path, stop);
            if (existing == null)
            {
                var title = Naming.Infer(Path.GetFileNameWithoutExtension(path));
                var catalogTitle = await db.Titles.FirstOrDefaultAsync(x => x.Name == title.Title.Name && x.Year == title.Title.Year && x.Kind == title.Title.Kind, stop);
                if (catalogTitle == null) { catalogTitle = title.Title; db.Titles.Add(catalogTitle); await db.SaveChangesAsync(stop); }
                var edition = await db.Editions.FirstOrDefaultAsync(x => x.TitleId == catalogTitle.Id && x.Season == title.Edition.Season && x.Episode == title.Edition.Episode && x.Name == title.Edition.Name, stop);
                if (edition == null) { edition = title.Edition; edition.TitleId = catalogTitle.Id; db.Editions.Add(edition); await db.SaveChangesAsync(stop); }
                var asset = new Asset { EditionId = edition.Id, FileName = Path.GetFileName(path), Bytes = fingerprint.Bytes };
                db.Assets.Add(asset); await db.SaveChangesAsync(stop);
                existing = new PhysicalCopy { AssetId = asset.Id, RootId = rootId, RelativePath = path, Role = root.Role };
                db.Copies.Add(existing);
            }
            if (existing.Bytes != fingerprint.Bytes || existing.ModifiedTicks != fingerprint.Ticks || existing.FileIdentity != fingerprint.Identity)
            {
                existing.Sha256 = null;
                var asset = await db.Assets.FindAsync([existing.AssetId], stop);
                if (asset != null) asset.Sha256 = null;
            }
            existing.Bytes = fingerprint.Bytes; existing.ModifiedTicks = fingerprint.Ticks; existing.FileIdentity = fingerprint.Identity;
            existing.ScanId = job.Id; existing.State = existing.Role == "library" && existing.Sha256 != null ? "verified" : "online";
            await db.SaveChangesAsync(stop);
            await transaction.CommitAsync(stop);
            if (++count % 100 == 0) { db.ChangeTracker.Clear(); await Task.Yield(); }
        }
        await db.Copies.Where(x => x.RootId == rootId && x.ScanId != job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "offline"), stop);
        db.Audit("scan-finished", $"{rootId}: {count} media files; no content rehash"); await db.SaveChangesAsync(stop);
    }
    private async Task Copy(Operation job, Catalog db, CancellationToken stop)
    {
        var plan = Decode<CopyPlan>(job.Payload);
        var source = await db.Copies.SingleAsync(x => x.Id == plan.SourceCopyId, stop);
        if (storage.Root(plan.TargetRootId).Role != "library") throw new ArgumentException("Copies must target a library root.");
        var registered = await db.Copies.SingleOrDefaultAsync(x => x.RootId == plan.TargetRootId && x.RelativePath.ToUpper() == plan.TargetPath.ToUpper(), stop);
        if (registered != null && (plan.Hash == null || registered.AssetId != source.AssetId || registered.Sha256 != null && registered.Sha256 != plan.Hash)) throw new IOException("The catalog already reserves this destination for a different copy. Choose a new path.");
        var temp = plan.TargetPath + $".library-{job.Id}.partial";
        // A persisted hash means data was flushed and verified before publishing. After a
        // crash, an existing destination is accepted only when its complete hash matches.
        if (storage.Exists(plan.TargetRootId, plan.TargetPath))
        {
            if (plan.Hash == null) throw new IOException("Destination collision. Preview a new path.");
            using var recovered = storage.Read(plan.TargetRootId, plan.TargetPath);
            if (recovered.Length != plan.Bytes || await HashChecked(recovered, job.Id, stop) != plan.Hash) throw new IOException("Existing destination does not match the journal.");
        }
        else
        {
            if (plan.SourceIdentity == null) throw new IOException("Preview lacks source identity. Create a new preview before copying.");
            if (guard != null) await guard.CheckAsync(source, plan.QBitRevision, stop);
            var before = storage.Fingerprint(source.RootId, source.RelativePath);
            if (before != (plan.Bytes, plan.ModifiedTicks) || plan.SourceIdentity != null && storage.Identity(source.RootId, source.RelativePath) != plan.SourceIdentity) throw new IOException("Source changed since preview. Create a new plan.");
            storage.RemoveTemporaryIfPresent(plan.TargetRootId, temp, job.Id);
            if (storage.FreeBytes(plan.TargetRootId) < plan.Bytes + 64L * 1024 * 1024) throw new IOException("Insufficient free space (includes 64 MiB safety reserve).");
            using var input = storage.Read(source.RootId, source.RelativePath);
            var inputObservation = Storage.Observe(input);
            if ((inputObservation.Bytes, inputObservation.Ticks) != (plan.Bytes, plan.ModifiedTicks) || plan.SourceIdentity != null && inputObservation.Identity != plan.SourceIdentity) throw new IOException("Source changed while opening the copy input.");
            string hash;
            try
            {
                using (var output = storage.Create(plan.TargetRootId, temp))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[1024 * 1024]; long copied = 0; long lastCheckpoint = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, stop)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), stop); sha.AppendData(buffer, 0, read); copied += read;
                        if (copied - lastCheckpoint >= 16 * 1024 * 1024)
                        {
                            await CheckCancellation(job.Id, stop); job.ProgressBytes = copied; job.UpdatedUtc = DateTime.UtcNow;
                            await db.SaveChangesAsync(stop); lastCheckpoint = copied;
                        }
                    }
                    output.Flush(true); hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                    if (copied != plan.Bytes) throw new IOException("Source length changed during copy.");
                }
                if (Storage.Observe(input) != inputObservation || storage.Fingerprint(source.RootId, source.RelativePath) != before || plan.SourceIdentity != null && storage.Identity(source.RootId, source.RelativePath) != plan.SourceIdentity) throw new IOException("Source changed during copy.");
                using var destination = storage.Read(plan.TargetRootId, temp);
                if (await HashChecked(destination, job.Id, stop) != hash) throw new IOException("Destination SHA-256 verification failed.");
                await CheckCancellation(job.Id, stop);
                plan = plan with { Hash = hash }; job.Payload = Encode(plan); job.ProgressBytes = plan.Bytes;
                await db.SaveChangesAsync(stop); // write-ahead checkpoint before atomic publication
                storage.Publish(plan.TargetRootId, temp, plan.TargetPath);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { storage.RemoveTemporaryIfPresent(plan.TargetRootId, temp, job.Id); throw; }
        }
        var fingerprint = storage.Observe(plan.TargetRootId, plan.TargetPath);
        if (registered == null)
        {
            registered = new() { AssetId = source.AssetId, RootId = plan.TargetRootId, RelativePath = plan.TargetPath, Role = "library" };
            db.Copies.Add(registered);
        }
        registered.State = "verified"; registered.Bytes = fingerprint.Bytes; registered.ModifiedTicks = fingerprint.Ticks; registered.FileIdentity = fingerprint.Identity; registered.Sha256 = plan.Hash;
        var assetRecord = await db.Assets.SingleAsync(x => x.Id == source.AssetId, stop); assetRecord.Sha256 = plan.Hash;
        // Only assert a source hash when its fingerprint still matches this plan.
        try
        {
            source.FileIdentity = storage.Identity(source.RootId, source.RelativePath);
            source.Sha256 = storage.Fingerprint(source.RootId, source.RelativePath) == (plan.Bytes, plan.ModifiedTicks) && (plan.SourceIdentity == null || storage.Identity(source.RootId, source.RelativePath) == plan.SourceIdentity) ? plan.Hash : null;
        }
        catch (IOException) { source.State = "offline"; source.Sha256 = null; }
        catch (UnauthorizedAccessException) { source.State = "offline"; source.Sha256 = null; }
        await db.SaveChangesAsync(stop);
    }
    private async Task StageDisc(Operation job, Catalog db, CancellationToken stop)
    {
        var request = Decode<DiscJob>(job.Payload);
        var disc = await db.Discs.SingleAsync(x => x.Id == request.DiscId, stop);
        if (storage.Root(request.RootId).Role != "staging") throw new ArgumentException("A staging root is required.");
        foreach (var entry in Decode<ManifestEntry[]>(disc.Manifest))
        {
            await CheckCancellation(job.Id, stop);
            var source = await db.Copies.SingleAsync(x => x.Id == entry.CopyId, stop);
            var target = disc.Id + "/" + entry.Path;
            if (storage.Exists(request.RootId, target))
            {
                using var existing = storage.Read(request.RootId, target);
                if (existing.Length != entry.Bytes || await HashChecked(existing, job.Id, stop) != entry.Sha256) throw new IOException("Disc staging collision.");
                continue;
            }
            if (storage.FreeBytes(request.RootId) < entry.Bytes + 64L * 1024 * 1024) throw new IOException("Insufficient staging space.");
            var temp = target + $".library-{job.Id}.partial";
            storage.RemoveTemporaryIfPresent(request.RootId, temp, job.Id);
            using (var input = storage.Read(source.RootId, source.RelativePath))
            using (var output = storage.Create(request.RootId, temp))
            {
                var buffer = new byte[1024 * 1024]; int read; long checkpoint = 0;
                while ((read = await input.ReadAsync(buffer, stop)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), stop); checkpoint += read;
                    if (checkpoint >= 16 * 1024 * 1024) { await CheckCancellation(job.Id, stop); checkpoint = 0; }
                }
                output.Flush(true);
            }
            using (var staged = storage.Read(request.RootId, temp))
                if (staged.Length != entry.Bytes || await HashChecked(staged, job.Id, stop) != entry.Sha256) throw new IOException("Staged data no longer matches the manifest.");
            await CheckCancellation(job.Id, stop);
            storage.Publish(request.RootId, temp, target);
        }
        var manifestPath = disc.Id + "/manifest.json";
        var manifest = Encode(new { version = 1, disc.Id, disc.Label, disc.CapacityBytes, disc.ReserveBytes, files = Decode<ManifestEntry[]>(disc.Manifest) });
        if (!storage.Exists(request.RootId, manifestPath))
        {
            var temp = manifestPath + $".library-{job.Id}.partial";
            storage.RemoveTemporaryIfPresent(request.RootId, temp, job.Id);
            using (var output = storage.Create(request.RootId, temp)) { var bytes = System.Text.Encoding.UTF8.GetBytes(manifest); await output.WriteAsync(bytes, stop); output.Flush(true); }
            storage.Publish(request.RootId, temp, manifestPath);
        }
        else
        {
            using var existing = new StreamReader(storage.Read(request.RootId, manifestPath));
            if (await existing.ReadToEndAsync(stop) != manifest) throw new IOException("Staging manifest collision.");
        }
        db.Audit("disc-staged", disc.Id); // physical disc remains planned until human reports a burn
    }
    private async Task VerifyDisc(Operation job, Catalog db, CancellationToken stop)
    {
        var request = Decode<DiscJob>(job.Payload);
        if (storage.Root(request.RootId).Role != "disc") throw new ArgumentException("Verification requires a separate read-only mounted disc root.");
        var disc = await db.Discs.SingleAsync(x => x.Id == request.DiscId, stop);
        if (disc.Status is not ("reported-written" or "read-back-verified")) throw new ArgumentException("Report the disc written before read-back verification.");
        // A re-verification must not leave a stale verified status after a mismatch.
        disc.Status = "reported-written";
        foreach (var copy in await db.Copies.Where(x => x.DiscId == disc.Id).ToListAsync(stop)) copy.State = "reported-written";
        await db.SaveChangesAsync(stop);
        foreach (var entry in Decode<ManifestEntry[]>(disc.Manifest))
        {
            await CheckCancellation(job.Id, stop);
            using var file = storage.Read(request.RootId, entry.Path);
            if (file.Length != entry.Bytes || await HashChecked(file, job.Id, stop) != entry.Sha256) throw new IOException($"Read-back mismatch: {entry.Path}");
        }
        disc.Status = "read-back-verified";
        foreach (var entry in Decode<ManifestEntry[]>(disc.Manifest))
        {
            var copy = await db.Copies.SingleOrDefaultAsync(x => x.DiscId == disc.Id && x.AssetId == entry.AssetId, stop);
            if (copy != null) copy.State = "read-back-verified";
        }
    }
    private async Task<string> HashChecked(Stream stream, string jobId, CancellationToken stop)
    {
        var observation = stream is FileStream file ? Storage.Observe(file) : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; int read; long checkpoint = 0;
        await CheckCancellation(jobId, stop);
        while ((read = await stream.ReadAsync(buffer, stop)) > 0)
        {
            hash.AppendData(buffer, 0, read); checkpoint += read;
            if (checkpoint >= 16 * 1024 * 1024) { await CheckCancellation(jobId, stop); checkpoint = 0; }
        }
        if (stream is FileStream checkedFile && Storage.Observe(checkedFile) != observation) throw new IOException("File changed during verification.");
        await CheckCancellation(jobId, stop);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static async Task<string> Hash(Stream stream, CancellationToken ct = default) => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
}
public record DiscJob(string DiscId, string RootId);
public static class StorageExtensions
{
    public static void RemoveTemporaryIfPresent(this Storage storage, string root, string path, string job)
    { try { storage.RemoveTemporary(root, path, job); } catch (FileNotFoundException) { } }
}
