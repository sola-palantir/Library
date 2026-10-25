using Library.Execution;

namespace Library.Manager;

public sealed class Coordinator(StateStore store, IRuntime runtime, TimeSpan? wait = null, Action<UpdateState>? checkpoint = null)
{
    readonly TimeSpan timeout = wait ?? TimeSpan.FromSeconds(60);
    void Save(UpdateState state) { store.Append(state); checkpoint?.Invoke(state); }
    Release NewRelease(string directory, string digest, Manifest manifest)
    {
        var prefix = "library-" + store.Installation.Id + "-" + Guid.NewGuid().ToString("N");
        return new(directory, digest, manifest.Version, manifest.CatalogSchema, manifest.ImageId, prefix + "-data", prefix + "-api");
    }
    public void Apply(string bundle, string digest)
    {
        var previous = store.Read();
        if (previous != null && previous.Phase != "Ready") throw new IOException("An unfinished update exists. Run recover; do not start another update.");
        var manifest = Bundle.Read(bundle, digest);
        if (previous != null && manifest.CatalogSchema < previous.Candidate.Schema) throw new IOException("Schema downgrade refused.");
        if (previous?.Candidate.ManifestSha256 == digest) throw new IOException("This release is already installed.");
        var relative = "releases/" + Guid.NewGuid().ToString("N"); var path = Path.Combine(store.Home, relative);
        Bundle.Stage(bundle, digest, path); runtime.Load(path, manifest);
        var state = new UpdateState("Prepared", NewRelease(relative, digest, manifest), previous?.Candidate, null);
        Save(state); // Before any resource creation/stop: uncertain outcomes stay discoverable.
        runtime.CreateVolume(state.Candidate.Volume);
        if (state.Previous != null)
        {
            runtime.Pause(); Wait(state.Previous, paused: true, drain: true);
            Save(state = state with { Phase = "Quiesced" });
            runtime.Stop(state.Previous);
        }
        var backup = store.BackupLocal();
        Save(state = state with { Phase = "LocalBackup", Backup = backup });
        if (state.Previous != null)
        {
            var frozen = NewRelease(state.Previous.Directory, state.Previous.ManifestSha256, Bundle.Read(Path.Combine(store.Home, state.Previous.Directory), state.Previous.ManifestSha256));
            // Record intended volume before creating it, including failed snapshot evidence.
            store.WriteNew($"backups/{backup}/intent.json", frozen);
            runtime.CreateVolume(frozen.Volume);
            var receipt = runtime.Snapshot(state.Previous, frozen);
            store.WriteNew($"backups/{backup}/restore-point.json", new RestorePoint(frozen, receipt, Bundle.HashFile(Path.Combine(store.Home, "backups", backup, "files.json"))));
            runtime.Snapshot(frozen, state.Candidate, receipt.Sha256);
        }
        Save(state = state with { Phase = "BackedUp" });
        runtime.Start(state.Candidate);
        Wait(state.Candidate, paused: true, drain: true);
        Save(state = state with { Phase = "Verified" });
        Resume(state);
    }
    void Resume(UpdateState state)
    {
        // Persist BEFORE unpausing. A crash after this record never permits rollback to
        // older history, even if the resume response was lost.
        Save(state = state with { Phase = "ResumeIntent" });
        runtime.Resume(); Wait(state.Candidate, paused: false, drain: true);
        Save(state with { Phase = "Ready" });
    }
    public void Recover()
    {
        var state = store.Read() ?? throw new IOException("No update journal.");
        if (state.Phase is "Ready" or "ResumeIntent")
        {
            // Candidate may have accepted mutations: forward-only recovery.
            Start(state); return;
        }
        if (state.Previous == null) throw new IOException("Initial install is incomplete. Evidence is retained; inspect failure and use a new installation directory. No older catalog exists.");
        Bundle.Verify(Path.Combine(store.Home, state.Previous.Directory), state.Previous.ManifestSha256);
        runtime.Stop(state.Candidate);
        // Old catalog was never migrated or replaced, and was never resumed.
        runtime.Start(state.Previous); Wait(state.Previous, paused: null, drain: false); runtime.Pause(); Wait(state.Previous, paused: true, drain: true);
        Resume(new UpdateState("Verified", state.Previous, null, state.Backup));
    }
    public void Start(UpdateState? value = null)
    {
        var state = value ?? store.Read() ?? throw new IOException("No installed release.");
        if (state.Phase is not ("Ready" or "ResumeIntent")) throw new IOException("Interrupted update: run recover first.");
        Bundle.Verify(Path.Combine(store.Home, state.Candidate.Directory), state.Candidate.ManifestSha256);
        runtime.Start(state.Candidate);
        // Managed container startup always pauses before serving. Start intentionally
        // releases only the catalog UI; production execution remains disabled.
        Wait(state.Candidate, paused: null, drain: true);
        Resume(state);
    }
    public void Stop()
    {
        var state = store.Read() ?? throw new IOException("No installation.");
        if (state.Phase != "Ready") throw new IOException("Unfinished update; use recover.");
        runtime.Pause(); Wait(state.Candidate, true, true); runtime.Stop(state.Candidate);
    }
    public void Rollback()
    {
        var state = store.Read() ?? throw new IOException("No installation.");
        if (state.Phase is "Ready" or "ResumeIntent") throw new IOException("Rollback after resume is refused. Restore offline to a new installation and reconcile catalog and companion history first.");
        Recover();
    }
    public void Backup()
    {
        var current = store.Read() ?? throw new IOException("No installation.");
        if (current.Phase != "Ready") throw new IOException("Recover the unfinished update first.");
        var state = current with { Phase = "Prepared", Previous = current.Candidate };
        Save(state); runtime.Pause(); Wait(state.Candidate, true, true); runtime.Stop(state.Candidate);
        var name = store.BackupLocal(); Save(state = state with { Phase = "LocalBackup", Backup = name });
        var frozen = NewRelease(state.Candidate.Directory, state.Candidate.ManifestSha256, Bundle.Verify(Path.Combine(store.Home, state.Candidate.Directory), state.Candidate.ManifestSha256));
        store.WriteNew($"backups/{name}/intent.json", frozen); runtime.CreateVolume(frozen.Volume);
        var receipt = runtime.Snapshot(state.Candidate, frozen);
        store.WriteNew($"backups/{name}/restore-point.json", new RestorePoint(frozen, receipt, Bundle.HashFile(Path.Combine(store.Home, "backups", name, "files.json"))));
        runtime.Start(state.Candidate); Wait(state.Candidate, true, true); Resume(state with { Previous = current.Previous });
    }
    public void Restore(StateStore source, string backup)
    {
        Library.Companion.Scope.Relative(backup);
        if (backup.Contains('/') || store.Read() != null) throw new IOException("Restore requires a fresh installation and one backup ID.");
        var path = Path.Combine(source.Home, "backups", backup);
        using var scope = new Library.Companion.Scope(path); using var file = scope.Read("restore-point.json");
        var point = System.Text.Json.JsonSerializer.Deserialize<RestorePoint>(file, Bundle.Json) ?? throw new IOException("Missing completed restore point.");
        source.ValidateRelease(point.Release);
        if (!Bundle.Digest(point.Catalog.Sha256) || point.Catalog.Schema != point.Release.Schema || point.Catalog.Bytes <= 0) throw new IOException("Invalid restore-point evidence.");
        var relative = "releases/" + Guid.NewGuid().ToString("N");
        var manifest = Bundle.Stage(Path.Combine(source.Home, point.Release.Directory), point.Release.ManifestSha256, Path.Combine(store.Home, relative));
        runtime.Load(Path.Combine(store.Home, relative), manifest);
        var target = NewRelease(relative, point.Release.ManifestSha256, manifest);
        Save(new("Restoring", target, null, backup)); runtime.CreateVolume(target.Volume);
        store.RestoreLocal(path, point.FilesSha256);
        runtime.Snapshot(point.Release, target, point.Catalog.Sha256, source.Installation.Id);
        var report = new { backup, source = source.Installation.Id, catalog = point.Catalog, target, warning = "Reconcile pending work and newer companion/media/disc history. Restored configuration and worker journal are quarantined in restored-state, never activated. No media was restored. Production and pairing stay off." };
        store.WriteNew("restore-review.json", report);
        Save(new("RestoredPendingReview", target, null, backup, Bundle.HashFile(Path.Combine(store.Home, "restore-review.json"))));
    }
    public void ApproveRestore(string review)
    {
        var state = store.Read() ?? throw new IOException("No restore.");
        if (state.Phase != "RestoredPendingReview" || !Bundle.Digest(review) || review != state.Review || Bundle.HashFile(Path.Combine(store.Home, "restore-review.json")) != review) throw new IOException("Review the exact restore report before starting this new catalog.");
        Bundle.Verify(Path.Combine(store.Home, state.Candidate.Directory), state.Candidate.ManifestSha256);
        runtime.Start(state.Candidate); Wait(state.Candidate, true, true); Resume(state);
    }
    void Wait(Release release, bool? paused, bool drain)
    {
        var end = DateTime.UtcNow + timeout; Exception? last = null;
        do
        {
            try
            {
                var status = runtime.Status();
                if (status.Version != release.Version || status.CatalogSchema != release.Schema || status.Protocol != ReleaseInfo.Protocol || status.DeploymentProtocol != ReleaseInfo.DeploymentProtocol || status.Mode != ReleaseInfo.Mode || status.Backend != "windows-worker" || status.Production || status.Pairing)
                    throw new InvalidDataException("Readiness identity/compatibility mismatch.");
                if (status.ReadyState && (!paused.HasValue || status.Paused == paused) && (!drain || status.ActiveJobs == 0)) return;
                last = new IOException("Active/unreconciled work or maintenance/readiness state prevents update.");
            }
            catch (InvalidDataException) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { last = e; }
            if (DateTime.UtcNow < end) Thread.Sleep(250);
        } while (DateTime.UtcNow < end);
        throw new IOException("Readiness/drain deadline expired; installation remains paused. Run recover after diagnosing the cause.", last);
    }
}
