using System.Text.Json;
using Library.Api;
using Library.Execution;
using Library.Manager;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public sealed class DeploymentTests
{
    static string NewDirectory() { var p = Path.Combine(Path.GetTempPath(), "library-deploy-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    static (string Path, string Hash) BundleFixture(string version = "0.2.0-preview.1")
    {
        var path = NewDirectory(); var files = new List<Artifact>();
        foreach (var name in new[] { "image.tar", "companion/Library.Companion.exe", "manager/Library.Manager.exe" })
        { var file = Path.Combine(path, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "synthetic-" + version + name); files.Add(new(name, new FileInfo(file).Length, Bundle.HashFile(file))); }
        File.WriteAllText(Path.Combine(path, "release.json"), JsonSerializer.Serialize(new Manifest(1, version, 1, 1, 6, "catalog-only", "sha256:" + new string('a', 64), "image.tar", false, files.ToArray()), Bundle.Json));
        return (path, Bundle.HashFile(Path.Combine(path, "release.json")));
    }
    static StateStore Store() => new(Path.Combine(NewDirectory(), "install"), true, 5190);
    sealed class Runtime : IRuntime
    {
        public bool Paused = true, BadHealth, BackupFails, ResumeFails;
        public int Active, Snapshots, ResumeCount, Starts;
        public Release? Current;
        public HashSet<string> Volumes = [];
        public void Load(string path, Manifest m) { }
        public void CreateVolume(string name) { if (!Volumes.Add(name)) throw new IOException("collision"); }
        public void Start(Release release) { Current = release; Paused = true; Starts++; }
        public void Stop(Release release) { }
        public SnapshotReceipt Snapshot(Release source, Release target, string? expectedHash = null, string? sourceOwner = null)
        { if (BackupFails) throw new IOException("backup failed"); Snapshots++; return new(6, new string('b', 64), 100); }
        public Ready Status() => new(!BadHealth, Current!.Version, 1, 1, 6, "catalog-only", Paused, Active, false, false, "windows-worker");
        public void Pause() => Paused = true;
        public void Resume() { ResumeCount++; if (ResumeFails) throw new IOException("response lost"); Paused = false; }
    }
    [Fact] public void InstallAndUpdateKeepPreviousAndVerifiedSnapshot()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero);
        var a = BundleFixture(); c.Apply(a.Path, a.Hash); var original = s.Read()!.Candidate;
        File.WriteAllText(Path.Combine(s.Home, "worker-state", "journal.json"), "retained history");
        var b = BundleFixture("0.2.0-preview.2"); c.Apply(b.Path, b.Hash);
        Assert.Equal("Ready", s.Read()!.Phase); Assert.Equal(original, s.Read()!.Previous); Assert.Equal(2, r.Snapshots);
        Assert.Equal(3, r.Volumes.Count); Assert.False(r.Paused);
        Assert.Equal("retained history", File.ReadAllText(Path.Combine(s.Home, "backups", s.Read()!.Backup!, "worker-state", "journal.json")));
        Assert.Throws<IOException>(() => c.Rollback());
    }
    [Theory] [InlineData("Protocol")] [InlineData("Schema")] [InlineData("Mode")]
    public void IncompatibleReleaseRejectedBeforeExecution(string field)
    {
        var a = BundleFixture(); var m = Bundle.Read(a.Path, a.Hash);
        m = field switch { "Protocol" => m with { Protocol = 2 }, "Schema" => m with { CatalogSchema = 7 }, _ => m with { Mode = "production" } };
        Assert.Throws<InvalidDataException>(() => Bundle.Validate(m));
    }
    [Fact] public void PartialArtifactAndUntrustedManifestCannotBeStaged()
    {
        var a = BundleFixture(); Assert.Throws<InvalidDataException>(() => Bundle.Read(a.Path, new string('0', 64)));
        File.WriteAllText(Path.Combine(a.Path, "image.tar"), "truncated");
        Assert.Throws<InvalidDataException>(() => Bundle.Stage(a.Path, a.Hash, Path.Combine(NewDirectory(), "candidate")));
    }
    [Fact] public void BundleTraversalAndCaseCollisionAreRejected()
    {
        var a = BundleFixture(); var m = Bundle.Read(a.Path, a.Hash);
        Assert.Throws<InvalidDataException>(() => Bundle.Validate(m with { Files = [.. m.Files, m.Files[0] with { Path = "../outside" }] }));
        Assert.Throws<InvalidDataException>(() => Bundle.Validate(m with { Files = [.. m.Files, m.Files[0] with { Path = "IMAGE.TAR" }] }));
    }
    [Fact] public void ActiveWorkPreventsStopBackupOrResume()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash);
        r.Active = 1; var b = BundleFixture("0.2.0-preview.2"); Assert.Throws<IOException>(() => c.Apply(b.Path, b.Hash));
        Assert.True(r.Paused); Assert.Equal(0, r.Snapshots); Assert.Equal(1, r.ResumeCount);
    }
    [Fact] public void BackupFailureRetainsOriginalAndRecoverNeverUsesPartialCopy()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash); var original = s.Read()!.Candidate;
        r.BackupFails = true; var b = BundleFixture("0.2.0-preview.2"); Assert.Throws<IOException>(() => c.Apply(b.Path, b.Hash));
        Assert.Equal("LocalBackup", s.Read()!.Phase); c.Recover(); Assert.Equal(original, s.Read()!.Candidate); Assert.Equal("Ready", s.Read()!.Phase);
    }
    [Fact] public void FailedHealthNeverResumesCandidate()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash);
        var b = BundleFixture("0.2.0-preview.2");
        var update = new Coordinator(s, r, TimeSpan.Zero, state => { if (state.Phase == "BackedUp") r.BadHealth = true; });
        Assert.Throws<IOException>(() => update.Apply(b.Path, b.Hash)); Assert.Equal(1, r.ResumeCount); Assert.True(r.Paused);
        r.BadHealth = false; c.Recover(); Assert.Equal("0.2.0-preview.1", s.Read()!.Candidate.Version);
    }
    [Theory] [InlineData("Prepared")] [InlineData("Quiesced")] [InlineData("LocalBackup")] [InlineData("BackedUp")] [InlineData("Verified")]
    public void CrashBeforeResumeCanRecoverOldCatalog(string stage)
    {
        using var s = Store(); var r = new Runtime(); var a = BundleFixture(); new Coordinator(s, r, TimeSpan.Zero).Apply(a.Path, a.Hash);
        var old = s.Read()!.Candidate; var b = BundleFixture("0.2.0-preview.2");
        Assert.Throws<IOException>(() => new Coordinator(s, r, TimeSpan.Zero, state => { if (state.Phase == stage) throw new IOException("simulated process exit"); }).Apply(b.Path, b.Hash));
        new Coordinator(s, r, TimeSpan.Zero).Recover(); Assert.Equal(old, s.Read()!.Candidate);
    }
    [Fact] public void LostResumeResponseForcesForwardRecovery()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash);
        r.ResumeFails = true; var b = BundleFixture("0.2.0-preview.2"); Assert.Throws<IOException>(() => c.Apply(b.Path, b.Hash));
        Assert.Equal("ResumeIntent", s.Read()!.Phase); Assert.Throws<IOException>(() => c.Rollback());
        r.ResumeFails = false; c.Recover(); Assert.Equal("0.2.0-preview.2", s.Read()!.Candidate.Version);
    }
    [Fact] public void TornJournalFailsClosedAndExistingInstallIsNeverAdopted()
    {
        using var s = Store(); Assert.Throws<IOException>(() => new StateStore(s.Home, true));
        File.WriteAllText(Path.Combine(s.Home, "events", "00000001.json"), "{\"partial\":");
        Assert.Throws<JsonException>(() => s.Read());
    }
    [Fact] public void ManualBackupKeepsCurrentReleaseAndCreatesRestorePoint()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash); var current = s.Read()!.Candidate;
        c.Backup(); Assert.Equal(current, s.Read()!.Candidate); Assert.Equal("Ready", s.Read()!.Phase); Assert.Equal(1, r.Snapshots);
        Assert.True(File.Exists(Path.Combine(s.Home, "backups", s.Read()!.Backup!, "restore-point.json")));
    }
    [Fact] public void ModifiedInstalledArtifactBlocksStart()
    {
        using var s = Store(); var r = new Runtime(); var c = new Coordinator(s, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash);
        File.AppendAllText(Path.Combine(s.Home, s.Read()!.Candidate.Directory, "companion", "Library.Companion.exe"), "tampered");
        Assert.Throws<InvalidDataException>(() => c.Start()); Assert.Equal(1, r.Starts);
    }
    [Fact] public void DamagedLocalBackupCannotBeRestored()
    {
        using var source = Store(); var r = new Runtime(); var c = new Coordinator(source, r, TimeSpan.Zero); var a = BundleFixture(); c.Apply(a.Path, a.Hash); c.Backup();
        var backup = source.Read()!.Backup!; File.AppendAllText(Path.Combine(source.Home, "backups", backup, "configuration", "settings.json"), "damage");
        using var target = Store(); var targetRuntime = new Runtime();
        Assert.Throws<IOException>(() => new Coordinator(target, targetRuntime, TimeSpan.Zero).Restore(source, backup)); Assert.Equal(0, targetRuntime.Starts);
    }
    [Fact] public void CompetingManagerCannotTakeInstallationLease()
    {
        using var s = Store(); Assert.Throws<IOException>(() => new StateStore(s.Home));
    }
    [Fact] public void ChainedJournalDetectsEarlierRecordMutation()
    {
        using var s = Store(); var a = BundleFixture(); new Coordinator(s, new Runtime(), TimeSpan.Zero).Apply(a.Path, a.Hash);
        var file = Path.Combine(s.Home, "events", "00000001.json"); File.AppendAllText(file, " ");
        Assert.Throws<IOException>(() => s.Read());
    }
    [Fact] public void ExistingPartialRecordCannotBeOverwrittenOrIgnored()
    {
        using var s = Store(); File.WriteAllText(Path.Combine(s.Home, "events", "00000001.json.partial"), "partial");
        Assert.Throws<IOException>(() => s.Read());
    }
    [Fact] public void JournalCannotSelectAnImageOutsideItsVerifiedRelease()
    {
        using var s = Store(); var a = BundleFixture(); new Coordinator(s, new Runtime(), TimeSpan.Zero).Apply(a.Path, a.Hash);
        Assert.Throws<IOException>(() => s.ValidateRelease(s.Read()!.Candidate with { ImageId = "sha256:" + new string('c', 64) }));
    }
    [Fact] public void OfflineRestoreRequiresExactReviewAndKeepsWorkerStateQuarantined()
    {
        using var source = Store(); var r = new Runtime(); var c = new Coordinator(source, r, TimeSpan.Zero);
        var a = BundleFixture(); c.Apply(a.Path, a.Hash); File.WriteAllText(Path.Combine(source.Home, "worker-state", "history"), "do not rewind");
        var b = BundleFixture("0.2.0-preview.2"); c.Apply(b.Path, b.Hash);
        using var target = Store(); var restoredRuntime = new Runtime(); var restored = new Coordinator(target, restoredRuntime, TimeSpan.Zero);
        restored.Restore(source, source.Read()!.Backup!);
        Assert.Equal(0, restoredRuntime.Starts); Assert.Equal("RestoredPendingReview", target.Read()!.Phase);
        Assert.Throws<IOException>(() => restored.Start()); Assert.Throws<IOException>(() => restored.ApproveRestore(new string('0', 64)));
        Assert.Empty(Directory.GetFiles(Path.Combine(target.Home, "worker-state")));
        Assert.Equal("do not rewind", File.ReadAllText(Path.Combine(target.Home, "restored-state", "worker-state", "history")));
        restored.ApproveRestore(target.Read()!.Review!); Assert.Equal("Ready", target.Read()!.Phase);
    }
    [Fact] public async Task SqliteSnapshotVerifiesIntegrityHashAndNeverDowngrades()
    {
        var root = NewDirectory(); var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        var options = new DbContextOptionsBuilder<Catalog>().UseSqlite($"Data Source={Path.Combine(source, "catalog.db")}").Options;
        using (var db = new Catalog(options)) { CatalogSchema.Initialize(db); db.Titles.Add(new Title { Name = "synthetic preserved" }); await db.SaveChangesAsync(); }
        SqliteConnection.ClearAllPools(); Deployment.Pause(new LibraryOptions { DataPath = source });
        File.WriteAllText(Path.Combine(source, "catalog.db-wal"), "unreconciled");
        Assert.Throws<IOException>(() => Deployment.Snapshot(source, target));
        File.Delete(Path.Combine(source, "catalog.db-wal")); // Only this test's invented marker.
        Assert.Throws<IOException>(() => Deployment.Snapshot(source, target, new string('0', 64)));
        Deployment.Snapshot(source, target);
        Assert.True(File.Exists(Path.Combine(target, "snapshot.json"))); Assert.True(File.Exists(Path.Combine(target, "deployment-paused")));
        using (var db = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite($"Data Source={Path.Combine(target, "catalog.db")}").Options)) Assert.Equal("synthetic preserved", db.Titles.Single().Name);
        Assert.Throws<IOException>(() => Deployment.Snapshot(source, target));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(source, "catalog.db")}"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); connection.Close(); SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidDataException>(() => Deployment.Snapshot(source, NewDirectory()));
    }
    [Fact] public void SnapshotRefusesForeignKeyDamage()
    {
        var source = NewDirectory(); var database = Path.Combine(source, "catalog.db");
        using (var db = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite($"Data Source={database}").Options)) CatalogSchema.Initialize(db);
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; INSERT INTO ProcessingLeases (SourceAssetId, JobId) VALUES (987654, 'orphan');"; command.ExecuteNonQuery();
        }
        Deployment.Pause(new LibraryOptions { DataPath = source });
        Assert.Throws<InvalidDataException>(() => Deployment.Snapshot(source, NewDirectory()));
    }
}
