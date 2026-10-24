using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;
public class WorkerContractTests
{
    [Fact] public async Task OutputBudgetRefusesOversizedWriteBeforeWriting()
    {
        using var bytes = new MemoryStream(); using var output = new BoundedOutput(bytes, 3, CancellationToken.None);
        output.Write([1, 2]);
        await Assert.ThrowsAsync<IOException>(() => output.WriteAsync(new byte[] { 3, 4 }).AsTask());
        Assert.Equal(new byte[] { 1, 2 }, bytes.ToArray());
    }
    [Fact] public void OutputCannotSeekOrTruncate()
    {
        using var bytes = new MemoryStream(); using var output = new BoundedOutput(bytes, 3, CancellationToken.None);
        Assert.Throws<NotSupportedException>(() => output.SetLength(0));
        Assert.Throws<NotSupportedException>(() => output.Seek(0, SeekOrigin.Begin));
    }
    [Fact] public async Task DefaultProcessorNeverRunsAnEncode()
    {
        var p = new UnavailableWindowsProcessor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.ProcessAsync(Stream.Null, Stream.Null, null!, CancellationToken.None));
    }
    [Fact] public async Task WindowsExecutorHasNoLinuxFallback()
    {
        if (OperatingSystem.IsWindows()) return;
        var worker = new WindowsMediaWorker(new("/tmp/state", [new("source", "/tmp/source", "source")]));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => worker.InspectAsync("source", "invented.bin", CancellationToken.None));
    }
    [Fact] public void NativeWorkerLifecycle()
    {
        if (!OperatingSystem.IsWindows()) return; // Native qualification is also run explicitly by the PowerShell harness.
        NativeWorkerQualification.Run(Path.Combine(Path.GetTempPath(), "library-worker-tests"));
    }
}
