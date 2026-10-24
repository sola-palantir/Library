using Library.Api;
using Xunit;

namespace Library.Tests;
public sealed class ExecutionBoundaryTests
{
    [Fact] public void WindowsModeStartsWithoutMountsButCannotOpenLinuxStorage()
    {
        var options = new LibraryOptions { ExecutionBackend = ExecutionMode.Windows, Roots = [new("source", "Windows source", "", "C:/not-mounted", "source")] };
        var storage = new Storage(options); storage.ValidateConfiguration();
        Assert.Throws<InvalidOperationException>(() => storage.Read("source", "movie.mkv"));
        Assert.Throws<InvalidOperationException>(() => storage.Create("source", "movie.mkv"));
        Assert.Throws<InvalidOperationException>(() => storage.Publish("source", "a", "b"));
        Assert.False(new MediaInspectionService(storage, options).Available);
    }
    [Theory]
    [InlineData("other")][InlineData("")][InlineData("Windows")]
    public void UnknownBackendRefused(string backend) => Assert.Throws<ArgumentException>(() => ExecutionMode.Validate(new() { ExecutionBackend = backend }));
    [Theory]
    [InlineData(true, false, false)][InlineData(false, true, false)][InlineData(false, false, true)]
    public void WindowsModeCannotEnableLinuxProcessingOrExport(bool remux, bool encode, bool handoff) => Assert.Throws<ArgumentException>(() => ExecutionMode.Validate(new() { ExecutionBackend = ExecutionMode.Windows, EnableSampleRemux = remux, EnableSampleEncode = encode, EnableSampleHandoff = handoff }));
    static WorkerCommand Command()
    {
        var observation = new FileObservation(123, 1, "windows-file-identity");
        return new(1, new string('a', 32), new("source", "input.mkv", observation, new string('b', 64)), "stage",
            new(new(1, "stage", 3, ["jpn"], [new(1, "jpn", "main")], true, true), 1, observation, [new(0, "video", "h264", "", []), new(1, "audio", "dts", "jpn", [])], "fixture", "probe-signature"));
    }
    static WorkerResult Receipt(WorkerCommand c) => new(1, c.JobId, ExecutionContract.Digest(c), c.Source, c.TargetRootId, ExecutionContract.Destination(c), new(120, 2, "new-file"), new string('c', 64), new("synthetic", "test only"));
    [Fact] public void SyntheticEvidenceCannotEnterCatalogAsVerifiedMedia()
    {
        var command = Command(); var receipt = Receipt(command);
        Assert.Throws<InvalidDataException>(() => ExecutionContract.Match(command, receipt));
        ExecutionContract.Match(command, receipt, true);
    }
    [Fact] public void ReceiptBindsEveryCommandAndOutputField()
    {
        var c = Command(); var result = Receipt(c);
        foreach (var wrong in new[] { result with { JobId = new string('d', 32) }, result with { RelativePath = "other.mkv" }, result with { TargetRootId = "library" }, result with { CommandSha256 = new string('d', 64) }, result with { Output = result.Output with { Bytes = 0 } }, result with { Source = c.Source with { RelativePath = "different.mkv" } } })
            Assert.Throws<InvalidDataException>(() => ExecutionContract.Match(c, wrong, true));
        Assert.NotEqual(ExecutionContract.Digest(c), ExecutionContract.Digest(c with { Plan = c.Plan with { Request = c.Plan.Request with { OriginalLanguages = ["eng"] } } }));
    }
    [Fact] public void StaleSourceOrChangedProfileCannotCrossBoundary()
    {
        var c = Command();
        Assert.Throws<ArgumentException>(() => ExecutionContract.Validate(c with { Source = c.Source with { Observation = c.Source.Observation with { Bytes = 124 } } }));
        Assert.Throws<ArgumentException>(() => ExecutionContract.Validate(c with { Plan = c.Plan with { Encoding = DerivedMedia.SdrEncoding } }));
    }
}
