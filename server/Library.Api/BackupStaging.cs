using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public static class BackupStaging
{
    public static async Task Run(Operation job, Catalog db, Storage storage, IDbContextFactory<Catalog> factory, CancellationToken ct)
    {
        var request = JobRunner.Decode<BackupStageRequest>(job.Payload);
        var disc = await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == request.DiscId, ct);
        if (storage.Root(request.RootId).Role != "staging" || disc.Sha256 != request.ManifestSha256 || BackupDomain.Digest(disc.ManifestJson) != disc.Sha256) throw new IOException("Stale manifest or invalid staging root.");
        var set = await db.Set<BackupSetRecord>().SingleAsync(x => x.Id == disc.SetId, ct);
        var preview = JsonSerializer.Deserialize<BackupPreview>(set.SnapshotJson)!;
        if (BackupDomain.Preview(preview.Request).Sha256 != set.Sha256) throw new IOException("Backup snapshot digest mismatch.");
        var files = preview.Packing.Discs[disc.Ordinal - 1].SelectMany(x => x.Files).ToArray();
        var progress = new JobProgress(factory, job);
        long checkedFiles = 0;
        async Task Check()
        {
            ct.ThrowIfCancellationRequested();
            await using var context = await factory.CreateDbContextAsync(ct);
            if (await context.Jobs.Where(x => x.Id == job.Id).Select(x => x.CancelRequested).SingleAsync(ct)) throw new OperationCanceledException();
        }
        async Task<string> Hash(FileStream file, string phase = "verifying-staged-file")
        {
            var observation = Storage.Observe(file);
            long done = 0;
            await progress.Report(phase, 0, observation.Bytes, checkedFiles, ct, true);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024]; int count;
            while ((count = await file.ReadAsync(buffer, ct)) > 0) { await Check(); hash.AppendData(buffer, 0, count); done += count; await progress.Report(phase, done, observation.Bytes, checkedFiles, ct); }
            await progress.Report(phase, done, observation.Bytes, checkedFiles, ct, true);
            if (Storage.Observe(file) != observation) throw new IOException("File changed while hashing staged content.");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        try
        {
            foreach (var file in files)
            {
                await Check(); var target = disc.Id + "/" + file.Path;
                if (!storage.Exists(request.RootId, target))
                {
                    if (file.SourceRootId == null || file.SourcePath == null || storage.Root(file.SourceRootId).Role != "library") throw new IOException("Missing frozen source binding.");
                    using var input = storage.Read(file.SourceRootId, file.SourcePath);
                    var source = Storage.Observe(input);
                    if (source.Bytes != file.Bytes || source.Identity != file.FileIdentity) throw new IOException("Source changed since the backup preview. Create a new backup revision.");
                    if (storage.FreeBytes(request.RootId) < checked(file.Bytes + 64L * 1024 * 1024)) throw new IOException("Insufficient staging space.");
                    var temporary = target + $".library-{job.Id}.partial";
                    storage.RemoveTemporaryIfPresent(request.RootId, temporary, job.Id);
                    using (var output = storage.Create(request.RootId, temporary))
                    {
                        var buffer = new byte[1024 * 1024]; int count; long copied = 0;
                        await progress.Report("staging-file", 0, file.Bytes, null, ct, true);
                        while ((count = await input.ReadAsync(buffer, ct)) > 0) { await Check(); await output.WriteAsync(buffer.AsMemory(0, count), ct); copied += count; await progress.Report("staging-file", copied, file.Bytes, null, ct); }
                        output.Flush(true);
                    }
                    if (Storage.Observe(input) != source || storage.Observe(file.SourceRootId, file.SourcePath) != source) throw new IOException("Backup input changed during staging.");
                    using var checkedFile = storage.Read(request.RootId, temporary); var checkedObservation = Storage.Observe(checkedFile);
                    if (checkedFile.Length != file.Bytes || await Hash(checkedFile) != file.Sha256) throw new IOException("Staged content does not match the frozen manifest.");
                    if (Storage.Observe(checkedFile) != checkedObservation || storage.Observe(request.RootId, temporary) != checkedObservation) throw new IOException("Staged input changed before publication.");
                    await Check(); storage.Publish(request.RootId, temporary, target);
                }
            }
            var manifestPath = disc.Id + "/LIBRARY-MANIFEST.json";
            if (!storage.Exists(request.RootId, manifestPath))
            {
                var temporary = manifestPath + $".library-{job.Id}.partial";
                storage.RemoveTemporaryIfPresent(request.RootId, temporary, job.Id);
                using (var output = storage.Create(request.RootId, temporary)) { await output.WriteAsync(Encoding.UTF8.GetBytes(disc.ManifestJson), ct); output.Flush(true); }
                storage.Publish(request.RootId, temporary, manifestPath);
            }
            var observations = new List<(string Path, FileObservation Evidence)>();
            foreach (var expected in files.Select(x => (Path: disc.Id + "/" + x.Path, x.Bytes, x.Sha256)).Append((manifestPath, Encoding.UTF8.GetByteCount(disc.ManifestJson), disc.Sha256)))
            {
                using var file = storage.Read(request.RootId, expected.Path); var observed = Storage.Observe(file);
                if (observed.Bytes != expected.Bytes || await Hash(file, "verifying-batch-file") != expected.Sha256 || storage.Observe(request.RootId, expected.Path) != observed) throw new IOException("Final staged batch verification failed.");
                observations.Add((expected.Path, observed));
                await progress.Report("verifying-batch-file", expected.Bytes, expected.Bytes, ++checkedFiles, ct, true);
            }
            foreach (var entry in observations) if (storage.Observe(request.RootId, entry.Path) != entry.Evidence) throw new IOException("Staged batch changed during final validation.");
            await Check();
            await new BackupService(db).RecordAsync(new(disc.Id, job.Id + "-staged", job.Id, "staged", JobRunner.Encode(new { request.RootId, disc.Sha256, files = files.Length, opticalVerification = false })));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException)
        {
            // Every failed retry is separate historical evidence. Keep staged media.
            db.Add(new BackupEvidence { DiscId = disc.Id, CommandId = Guid.NewGuid().ToString("N"), AttemptId = job.Id, State = "failed", DetailJson = JobRunner.Encode(new { error = ex.Message, operation = "stage" }), AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(CancellationToken.None); throw;
        }
    }
}
