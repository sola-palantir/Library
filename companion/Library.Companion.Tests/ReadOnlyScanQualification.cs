using System.Diagnostics;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion.Tests;

// Explicit local qualification, not an automatically discovered test. Reads the
// supplied folder; writes metadata evidence only in a fresh isolated directory.
public static class ReadOnlyScanQualification
{
    public static async Task Run(string folder, string tools, string evidenceRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows required.");
        if (Directory.Exists(evidenceRoot) || File.Exists(evidenceRoot)) throw new IOException("Use a fresh evidence directory; no evidence is overwritten.");
        Directory.CreateDirectory(evidenceRoot);
        var before = Directory.GetFiles(folder).ToDictionary(p => Path.GetFileName(p)!, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        var now = DateTimeOffset.UtcNow; var configuration = new ReviewWorkerConfiguration("http://127.0.0.1:5090", Guid.NewGuid().ToString("N"), folder, tools, now, now.AddMinutes(10));
        var command = new ReviewScanCommand(Guid.NewGuid().ToString("N"), configuration.SessionId, configuration.RootId, now.AddMinutes(3));
        var watch = Stopwatch.StartNew();
        var inventory = await new ReadOnlyScanner(folder, tools).Scan(command, default);
        File.WriteAllText(Path.Combine(evidenceRoot, "inventory.json"), ExecutionContract.Serialize(inventory));
        foreach (var entry in before)
        {
            var file = Path.Combine(folder, entry.Key!);
            if ((new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)) != entry.Value) throw new IOException("Input stat changed during read-only qualification.");
        }
        var timer = Stopwatch.StartNew(); uint child = 0;
        try
        {
            using var output = new MemoryStream();
            await NativeProcess.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], null, output,
                new(512L*1024*1024, 2500, 2, TimeSpan.FromMilliseconds(800)), 1024, default, id => child = id);
            throw new IOException("Native timeout did not fire.");
        }
        catch (TimeoutException) { }
        if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new IOException("Native timeout did not terminate promptly.");
        try { using var process = Process.GetProcessById((int)child); if (!process.HasExited) throw new IOException("Timed-out child survived."); } catch (ArgumentException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new ReadOnlyScanner(folder, tools).Scan(command with { Id = Guid.NewGuid().ToString("N"), DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(3) }, cancelled.Token); throw new IOException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        var result = new { scan = "passed", files = inventory.Files.Select(f => new { f.RelativePath, f.Observation.Bytes, streams = f.Probe.GetProperty("streams").GetArrayLength() }), elapsedSeconds = watch.Elapsed.TotalSeconds, nativeTimeoutSeconds = timer.Elapsed.TotalSeconds, timedOutChildExited = true, cancellation = "passed", inputsStatUnchanged = true, contentHashesVerified = false, mediaWritten = false, networkAccess = false };
        File.WriteAllText(Path.Combine(evidenceRoot, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
