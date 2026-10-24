using Library.Api;
using System.Text.Json;
using Xunit;

namespace Library.Tests;

public sealed class MediaInspectionTests
{
    [Fact]
    public void ProbingIsDisabledUnlessAnExistingBinaryIsExplicitlyConfigured()
    {
        var options = new LibraryOptions();
        Assert.False(new MediaInspectionService(new Storage(options), options).Available);
        options.FfprobePath = "/no-such-library-probe-binary";
        Assert.False(new MediaInspectionService(new Storage(options), options).Available);
        options.FfprobePath = "ffprobe";
        Assert.False(new MediaInspectionService(new Storage(options), options).Available);
    }

    // Child-process contracts run on the supported Linux service platform; no media or FFprobe needed.
    [Fact]
    public async Task ChildOutputIsBoundedAndNonzeroExitCannotBecomeEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("fixture", await ProbeProcess.Run("/bin/sh", ["-c", "printf fixture"], 128, limit.Token));
        await Assert.ThrowsAsync<IOException>(() => ProbeProcess.Run("/bin/sh", ["-c", "printf oversized"], 2, limit.Token));
        await Assert.ThrowsAsync<IOException>(() => ProbeProcess.Run("/bin/sh", ["-c", "exit 7"], 128, limit.Token));
        await Assert.ThrowsAsync<IOException>(() => ProbeProcess.Run("/bin/sh", ["-c", "printf error >&2"], 128, limit.Token));
    }

    [Fact]
    public async Task CancellationTerminatesTheChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeProcess.Run("/bin/sh", ["-c", "sleep 10"], 128, limit.Token));
    }

    [Fact]
    public async Task InspectionReadsThePinnedDescriptorWithoutGrantingIntegrityEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new ProbeFixture();
        var before = File.ReadAllBytes(fixture.Input);
        var copyBefore = JsonSerializer.Serialize(fixture.Copy);
        var result = JsonSerializer.SerializeToElement(await fixture.Service.Inspect(fixture.Copy, CancellationToken.None));
        Assert.Equal("h264", result.GetProperty("probe").GetProperty("streams")[0].GetProperty("codec_name").GetString());
        Assert.False(result.GetProperty("evidence").GetProperty("contentVerified").GetBoolean());
        Assert.False(result.GetProperty("evidence").GetProperty("executable").GetBoolean());
        Assert.Equal("ffprobe synthetic contract fixture", result.GetProperty("evidence").GetProperty("toolVersion").GetString());
        Assert.Equal(before, File.ReadAllBytes(fixture.Input));
        Assert.Equal(copyBefore, JsonSerializer.Serialize(fixture.Copy));
    }

    [Fact]
    public async Task StaleCatalogSymlinkAndMalformedInventoryAreRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new ProbeFixture();
        fixture.Copy.Bytes++;
        Assert.Contains("stale", (await Assert.ThrowsAsync<IOException>(() => fixture.Service.Inspect(fixture.Copy, CancellationToken.None))).Message);
        fixture.Copy.Bytes--;
        File.CreateSymbolicLink(Path.Combine(fixture.Home, "link.mkv"), fixture.Input);
        fixture.Copy.RelativePath = "link.mkv";
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.Inspect(fixture.Copy, CancellationToken.None));
        fixture.Copy.RelativePath = "input.mkv";
        File.WriteAllText(fixture.Input, "{\"streams\":[]}");
        fixture.RefreshObservation();
        Assert.Contains("inventory", (await Assert.ThrowsAsync<IOException>(() => fixture.Service.Inspect(fixture.Copy, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task ReplacementDuringProbeIsRejectedAndInspectionSlotRecovers()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new ProbeFixture(waitForRelease: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = fixture.Service.Inspect(fixture.Copy, deadline.Token);
        while (!File.Exists(Path.Combine(fixture.Home, "ready"))) await Task.Delay(10, deadline.Token);
        Assert.Contains("already running", (await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.Inspect(fixture.Copy, deadline.Token))).Message);
        File.Move(fixture.Input, Path.Combine(fixture.Home, "old.mkv"));
        File.WriteAllText(fixture.Input, ProbeFixture.Inventory);
        File.WriteAllText(Path.Combine(fixture.Home, "release"), "release synthetic probe");
        Assert.Contains("changed", (await Assert.ThrowsAsync<IOException>(() => first)).Message);
        fixture.RefreshObservation();
        await fixture.Service.Inspect(fixture.Copy, deadline.Token);
    }

    // Only invented JSON files under a unique test directory; this executable is
    // a process-contract fixture, not FFprobe and never runs against user media.
    private sealed class ProbeFixture : IDisposable
    {
        public const string Inventory = "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\"}]}";
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "library-probe-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Home, "input.mkv");
        public PhysicalCopy Copy { get; } = new() { Id = 7, AssetId = 11, RootId = "source", RelativePath = "input.mkv", Role = "source", State = "online" };
        public MediaInspectionService Service { get; }
        private readonly Storage storage;
        public ProbeFixture(bool waitForRelease = false)
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(Input, Inventory);
            var executable = Path.Combine(Home, "synthetic-probe");
            var script = "#!/bin/sh\nset -eu\nif [ \"$1\" = '-version' ]; then printf 'ffprobe synthetic contract fixture\\n'; exit 0; fi\nfor argument do input=$argument; done\ncase $input in /proc/[0-9]*/fd/[0-9]*) ;; *) exit 9 ;; esac\n";
            if (waitForRelease) script += $"touch '{Home}/ready'\nwhile [ ! -f '{Home}/release' ]; do sleep 0.01; done\n";
            script += "cat \"$input\"\n";
            File.WriteAllText(executable, script);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new LibraryOptions { FfprobePath = executable, Roots = [new("source", "Synthetic source", Home, "", "source")] };
            storage = new Storage(options);
            Service = new MediaInspectionService(storage, options);
            RefreshObservation();
        }
        public void RefreshObservation()
        {
            var observed = storage.Observe("source", "input.mkv");
            Copy.Bytes = observed.Bytes; Copy.ModifiedTicks = observed.Ticks; Copy.FileIdentity = observed.Identity;
        }
        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
