using Library.Api;
using Xunit;

namespace Library.Tests;

public sealed class SamplePipelineTests
{
    [Fact]
    public async Task BinaryPipeDrainsAndReportsTransferredBytes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await SamplePipeline.Run("/bin/sh", ["-c", "printf 'invented-window'"], ["-c", "cat"], deadline.Token);
        Assert.Equal(15, result.Bytes); Assert.Equal("invented-window", result.ConsumerOutput);
        Assert.Equal("copy-window-pipe-v1", result.Pipeline);
    }
    [Theory]
    [InlineData("printf '123456789'", "cat", 4)]
    [InlineData("printf 'partial'; exit 3", "cat", 100)]
    [InlineData("printf 'partial'", "cat; exit 4", 100)]
    [InlineData("printf 'error' >&2; printf 'partial'", "cat", 100)]
    public async Task BoundsAndEitherChildFailureRefuseThePipeline(string producer, string consumer, int limit)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => SamplePipeline.Run("/bin/sh", ["-c", producer], ["-c", consumer], deadline.Token, limit));
    }
    [Fact]
    public async Task CancellationStopsBothChildrenEvenWithABlockedPipe()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SamplePipeline.Run("/bin/sh", ["-c", "sleep 30"], ["-c", "cat"], deadline.Token));
    }
    [Fact]
    public async Task CombinedMemoryBudgetKillsBothChildren()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<IOException>(() => SamplePipeline.Run("/bin/sh", ["-c", "sleep 30"], ["-c", "cat"], deadline.Token, memoryLimit: 1));
        Assert.Contains("resident-memory budget", error.Message);
    }
}
