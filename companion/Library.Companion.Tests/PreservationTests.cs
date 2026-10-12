using System.Text.Json;
using Library.Companion;
using Xunit;
namespace Library.Companion.Tests;
public sealed class PreservationTests
{
    [Theory]
    [InlineData(18, true, true, 4096, 0, true)]
    [InlineData(18, true, true, 4096, 1, false)]
    [InlineData(19, true, true, 2048, 0, false)]
    [InlineData(18, false, true, 2048, 0, false)]
    [InlineData(18, true, false, 2048, 0, false)]
    [InlineData(18, true, true, 2049, 0, false)]
    public void CapacityUsesMeasuredSectorsAndFinalWholeImage(int type, bool physical, bool heuristic, long bytes, long reserve, bool fits)
    { var m = new Media("drive", type, physical, heuristic, 2, 2048, []); if (fits) Contract.Fit(m, bytes, reserve); else Assert.ThrowsAny<Exception>(() => Contract.Fit(m, bytes, reserve)); }
    [Theory]
    [InlineData(25_000_001)]
    [InlineData(48_828_125)]
    [InlineData(62_500_000)]
    public void BdxlCapacityOutsideInitialSupportEnvelopeIsRefused(long sectors)
    { Assert.Throws<InvalidOperationException>(() => Contract.Fit(new("drive", 18, true, true, sectors, 2048, []), 2048, 0)); }
    [Fact] public void Bd50EnvelopeBoundaryUsesMeasuredFit()
    { Contract.Fit(new("drive", 18, true, true, 25_000_000, 2048, []), 51_200_000_000, 0); }
    [Theory]
    [InlineData("../escape")][InlineData("NUL.txt")][InlineData("dir/file:stream")][InlineData("dir\\file")][InlineData("dir/../file")][InlineData("file.")]
    public void UnsafePathsRefused(string path) => Assert.Throws<InvalidDataException>(() => Scope.Relative(path));
    [Fact] public void StageHashesEverySelectedFileAndKeepsSourceUntouched()
    { using var f = new Fixture(); var before = File.ReadAllBytes(f.Payload); var result = f.Run("prepare", "p"); Assert.Equal("staged", result.State); Assert.Equal(before, File.ReadAllBytes(f.Payload)); Assert.Equal(1, f.Device.Builds); Assert.True(result.Simulated); }
    [Fact] public void StaleManifestCannotPrepare()
    { using var f = new Fixture(); File.AppendAllText(f.ManifestPath, " "); Assert.Throws<InvalidDataException>(() => f.Run("prepare", "p")); Assert.Equal(0, f.Device.Builds); }
    [Fact] public void ChangedSourceFailsBeforeImageBuild()
    { using var f = new Fixture(); File.WriteAllText(f.Payload, "changed"); Assert.Throws<InvalidDataException>(() => f.Run("prepare", "p")); Assert.Equal(0, f.Device.Builds); }
    [Fact] public void LostCompletionResponseNeverBurnsAgain()
    { using var f = new Fixture(); f.Run("prepare", "p"); var first = f.Run("burn", "b"); var duplicate = f.Run("burn", "b"); Assert.Equal(first, duplicate); Assert.Equal(1, f.Device.Writes); Assert.Throws<InvalidOperationException>(() => f.Run("burn", "another")); }
    [Fact] public void DuplicateCommandCannotChangeArguments()
    { using var f = new Fixture(); f.Run("prepare", "p"); Assert.Throws<InvalidOperationException>(() => f.Engine.Execute("prepare", f.Cmd("p") with { ReserveBytes = 1 })); }
    [Fact] public void DisconnectRequiresReconciliationNeverRetry()
    { using var f = new Fixture(); f.Run("prepare", "p"); f.Device.FailWrite = true; Assert.Throws<IOException>(() => f.Run("burn", "b")); Assert.Equal("interrupted", f.Run("burn", "b").State); Assert.Throws<InvalidOperationException>(() => f.Run("burn", "b2")); Assert.Equal(1, f.Device.Writes); }
    [Fact] public void PowerLossWritingIsDurablyReconciled()
    {
        using var f = new Fixture(); f.Run("prepare", "p"); var c = f.Cmd("b"); var hash = Contract.Hash(JsonSerializer.SerializeToUtf8Bytes(new { operation = "burn", command = c }, Contract.Json));
        using (var j = new Journal(f.Config.StateRoot)) j.Append(new(1, "b", f.Disc, f.Hash, "drive", "writing", DateTime.UtcNow, RequestSha256: hash, Sequence: 3));
        Assert.Equal("interrupted", f.Run("burn", "b").State); Assert.Equal(0, f.Device.Writes);
    }
    [Fact] public void FrozenImageMutationPreventsWrite()
    { using var f = new Fixture(); f.Run("prepare", "p"); File.WriteAllBytes(Path.Combine(f.Config.StateRoot, f.Disc + ".udf.iso"), new byte[2048]); Assert.Throws<InvalidDataException>(() => f.Run("burn", "b")); Assert.Equal(0, f.Device.Writes); }
    [Fact] public void WrongDiscManifestFailsVerification()
    { using var f = new Fixture(); f.Run("prepare", "p"); File.WriteAllText(Path.Combine(f.Device.Root, Contract.ManifestName), "wrong"); Assert.Throws<InvalidDataException>(() => f.Run("verify", "v")); using var j = new Journal(f.Config.StateRoot); Assert.Equal("failed", j.Events(f.Disc).Last().State); }
    [Fact] public void FailedReverificationSupersedesSuccessAndPreservesHistory()
    { using var f = new Fixture(); f.Run("prepare", "p"); var ok = f.Run("verify", "v"); Assert.True(ok.ManifestMatched && ok.AllFilesVerified); File.WriteAllText(Path.Combine(f.Device.Root, "episode.mkv"), "bad"); Assert.Throws<InvalidDataException>(() => f.Run("verify", "v2")); using var j = new Journal(f.Config.StateRoot); var events = j.Events(f.Disc); Assert.Contains(events, e => e.State == "verified"); Assert.Equal("failed", events.Last().State); Assert.True(events.Last().Sequence > ok.Sequence); }
    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("stale-manifest")]
    [InlineData("missing-staging-root")]
    public void VerificationPreflightFailureSupersedesEarlierSuccess(string failure)
    {
        using var f = new Fixture(); f.Run("prepare", "p"); var verified = f.Run("verify", "v");
        if (failure == "missing-manifest") File.Delete(f.ManifestPath);
        else if (failure == "stale-manifest") File.AppendAllText(f.ManifestPath, " ");
        else Directory.Move(f.Config.StagingRoot, f.Config.StagingRoot + "-offline");
        Assert.ThrowsAny<Exception>(() => f.Run("verify", "v2"));
        using var journal = new Journal(f.Config.StateRoot); var history = journal.Events(f.Disc);
        Assert.Equal("verifying", history[^2].State); Assert.Equal("failed", history[^1].State);
        Assert.Equal("v2", history[^1].CommandId); Assert.True(history[^1].Sequence > verified.Sequence);
    }
    [Fact] public void WrongRequestedManifestFailsWithoutPoisoningPermanentIdentity()
    {
        using var f = new Fixture(); f.Run("prepare", "p"); f.Run("verify", "v");
        Assert.Throws<InvalidOperationException>(() => f.Engine.Execute("verify", f.Cmd("wrong") with { ManifestSha256 = new string('b', 64) }));
        using (var journal = new Journal(f.Config.StateRoot))
        {
            var history = journal.Events(f.Disc); Assert.Equal("failed", history[^1].State);
            Assert.All(history, e => Assert.Equal(f.Hash, e.ManifestSha256));
        }
        Assert.Equal("verified", f.Run("verify", "corrected").State);
    }
    [Fact] public void FallbackCannotClaimVerificationOrPermitSecondBurn()
    { using var f = new Fixture(); f.Run("prepare", "p"); f.Run("fallback", "f"); Assert.Equal("written-unverified", f.Run("report-external", "r").State); Assert.Throws<InvalidOperationException>(() => f.Run("burn", "b")); Assert.Equal(0, f.Device.Writes); }
    [Fact] public void JournalCanBeRestoredWithoutLosingEvidence()
    { using var f = new Fixture(); f.Run("prepare", "p"); f.Run("verify", "v"); var restore = Path.Combine(f.Temp, "restore"); Directory.CreateDirectory(restore); foreach (var p in Directory.GetFiles(f.Config.StateRoot, "*.event.json")) File.Copy(p, Path.Combine(restore, Path.GetFileName(p))); using var original = new Journal(f.Config.StateRoot); using var restored = new Journal(restore); Assert.Equal(original.Events(f.Disc), restored.Events(f.Disc)); }
    [Fact] public void NativeAdapterRefusesLinuxWithoutHardwareAccess()
    { if (!OperatingSystem.IsWindows()) Assert.Throws<PlatformNotSupportedException>(() => new NativeImapi()); }
    [Fact] public void OverlappingRootsRefusedBeforeAnyWrite()
    { using var f = new Fixture(); var nested = Path.Combine(f.Config.StagingRoot, "state"); Directory.CreateDirectory(nested); var engine = new Engine(f.Config with { StateRoot = nested }, f.Device); Assert.Throws<InvalidDataException>(() => engine.Execute("prepare", f.Cmd("p"))); Assert.Empty(Directory.GetFiles(nested)); }
    [Fact] public void OrphanJournalPublicationBlocksWrites()
    { using var f = new Fixture(); f.Run("prepare", "p"); File.WriteAllText(Path.Combine(f.Config.StateRoot, f.Disc + ".00000002.crash.partial"), "partial"); Assert.Throws<InvalidDataException>(() => f.Run("burn", "b")); Assert.Equal(0, f.Device.Writes); }
    [Fact] public void MissingJournalSequenceFailsClosed()
    { using var f = new Fixture(); f.Run("prepare", "p"); File.Move(Path.Combine(f.Config.StateRoot, f.Disc + ".00000001.event.json"), Path.Combine(f.Config.StateRoot, f.Disc + ".00000002.event.json")); Assert.Throws<InvalidDataException>(() => f.Run("burn", "b")); Assert.Equal(0, f.Device.Writes); }
    [Fact] public void SymlinkPayloadRefused()
    { using var f = new Fixture(); var outside = Path.Combine(f.Temp, "outside"); File.Move(f.Payload, outside); File.CreateSymbolicLink(f.Payload, outside); Assert.Throws<InvalidDataException>(() => f.Run("prepare", "p")); Assert.Equal(0, f.Device.Builds); }
    [Fact] public void StreamsCloneIndependentPositions()
    { using var memory = new MemoryStream([1,2,3]); var one = new ComStream(memory); one.Clone(out var two); var a = new byte[1]; var b = new byte[1]; one.Read(a, 1, IntPtr.Zero); one.Read(a, 1, IntPtr.Zero); two.Read(b, 1, IntPtr.Zero); Assert.Equal(2, a[0]); Assert.Equal(1, b[0]); }
    private sealed class Fixture : IDisposable
    {
        public string Temp = Path.Combine(Path.GetTempPath(), "library-companion-" + Guid.NewGuid().ToString("N")); public string Disc = "disc-synthetic-1";
        public CompanionConfig Config; public SimulatedDevice Device; public Engine Engine; public string Payload, ManifestPath, Hash;
        public Fixture()
        {
            Config = new(Path.Combine(Temp, "stage"), Path.Combine(Temp, "state")); Directory.CreateDirectory(Config.StateRoot); var stage = Path.Combine(Config.StagingRoot, Disc); Directory.CreateDirectory(stage);
            Payload = Path.Combine(stage, "episode.mkv"); File.WriteAllText(Payload, "synthetic-not-media"); var bytes = File.ReadAllBytes(Payload);
            var manifest = new FrozenManifest(1, Disc, "LIB-ABC-7", "backup-synthetic", 1, [new("episode.mkv", bytes.Length, Contract.Hash(bytes))]);
            ManifestPath = Path.Combine(stage, Contract.ManifestName); var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Contract.Json); File.WriteAllBytes(ManifestPath, json); Hash = Contract.Hash(json);
            Device = new(Path.Combine(Temp, "optical-simulation")); Directory.CreateDirectory(Device.Root); File.Copy(Payload, Path.Combine(Device.Root, "episode.mkv")); File.WriteAllBytes(Path.Combine(Device.Root, Contract.ManifestName), json); Engine = new(Config, Device);
        }
        public Command Cmd(string id) => new(id, Disc, Hash, "drive", 0);
        public Evidence Run(string action, string id) => Engine.Execute(action, Cmd(id));
        public void Dispose() => Directory.Delete(Temp, true);
    }
    private sealed class SimulatedDevice(string root) : IOpticalDevice
    {
        public string Root = root; public bool Simulated => true; public int Builds, Writes; public bool FailWrite;
        public Media Inspect(string id) => new(id, 18, true, true, 1000, 2048, [Root]);
        public IDisposable Acquire(string id) => new Empty();
        public void BuildImage(FrozenManifest m, string staging, byte[] bytes, Stream output) { Builds++; var image = new byte[4096]; bytes.CopyTo(image, 0); output.Write(image); }
        public void WriteImage(string id, Stream image, Action<string> progress) { Writes++; progress("finalizing"); if (FailWrite) throw new IOException("Simulated power loss"); }
        public string OpticalRoot(string id, string expectedLabel) => Root;
        public void Dispose() { }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
}
