using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Library.Execution;

namespace Library.Companion.Tests;

// Explicitly invoked on Windows, never discovered as an ordinary cross-platform
// unit test. All media is generated here; there are no movie paths or HTTP calls.
public static class NativeAdapterQualification
{
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
    static void Gone(int pid) { try { using var p = Process.GetProcessById(pid); Require(p.HasExited, $"Owned fixture process {pid} survived."); } catch (ArgumentException) { } }
    public static int NativeMemoryCheck()
    {
        // An actual Win32 commit request, independent of the managed GC budget.
        var memory = VirtualAlloc(IntPtr.Zero, (nuint)805306368, 0x3000, 4);
        if (memory == IntPtr.Zero) return Marshal.GetLastWin32Error();
        VirtualFree(memory, 0, 0x8000); return 0;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAlloc(IntPtr address, nuint bytes, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFree(IntPtr address, nuint bytes, uint operation);
    // Called only in an independent hidden harness process. Exiting deliberately
    // skips NativeProcess's finally block and tests OS kill-on-last-job-handle-close.
    public static void ExitOwner(string root, string powershell)
    {
        uint pid = 0;
        using var output = new ExitOnChildOutput(root, () => pid);
        var script = "$p=[Diagnostics.ProcessStartInfo]::new([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName);$p.UseShellExecute=$false;$p.CreateNoWindow=$true;foreach($a in @('-NoProfile','-NonInteractive','-Command','[Threading.Thread]::Sleep(60000)')){$p.ArgumentList.Add($a)};$c=[Diagnostics.Process]::Start($p);[Console]::WriteLine($c.Id);[Console]::Out.Flush();[Threading.Thread]::Sleep(60000)";
        NativeProcess.Run(powershell, ["-NoLogo","-NoProfile","-NonInteractive","-Command",script], null, output,
            new(512L * 1024 * 1024, 1000, 2, TimeSpan.FromSeconds(15)), 65536, default, started => pid = started).GetAwaiter().GetResult();
        throw new IOException("Owner-exit checkpoint not reached.");
    }
    sealed class ExitOnChildOutput(string root, Func<uint> parent) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            base.Write(buffer.Span);
            var text = Encoding.UTF8.GetString(ToArray());
            if (text.Contains('\n') && int.TryParse(text.Trim(), out var child))
            {
                File.WriteAllText(Path.Combine(root, "processes.json"), ExecutionContract.Serialize(new { rootProcess = parent(), childProcess = child }));
                Environment.Exit(79);
            }
            return ValueTask.CompletedTask;
        }
    }
    public static string Resources(string parent, string powershell)
    {
        var home = Path.Combine(parent, "resources-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        var checks = new List<object>(); var limits = new NativeProcessLimits(512L * 1024 * 1024, 1000, 2, TimeSpan.FromSeconds(20));
        async Task<NativeProcessEvidence> Run(string script, MemoryStream bytes, NativeProcessLimits policy, CancellationToken ct = default, Action<uint>? started = null) =>
            await NativeProcess.Run(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], null, bytes, policy, 65536, ct, started);
        var childScript = "$p=[Diagnostics.ProcessStartInfo]::new([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName);$p.UseShellExecute=$false;$p.CreateNoWindow=$true;foreach($a in @('-NoLogo','-NoProfile','-NonInteractive','-Command','[Threading.Thread]::Sleep(60000)')){$p.ArgumentList.Add($a)};$c=[Diagnostics.Process]::Start($p);[Console]::WriteLine($c.Id);[Console]::Out.Flush();[Threading.Thread]::Sleep(60000)";
        foreach (var mode in new[] { "timeout", "cancellation" })
        {
            using var output = new MemoryStream(); using var cancel = new CancellationTokenSource(); uint root = 0; bool refused = false;
            if (mode == "cancellation") cancel.CancelAfter(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            try { Run(childScript, output, limits with { Timeout = TimeSpan.FromSeconds(mode == "timeout" ? 5 : 15) }, cancel.Token, pid => root = pid).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { refused = true; }
            var text = Encoding.UTF8.GetString(output.ToArray()).Trim(); Require(int.TryParse(text, out var child), "Child fixture did not start before cancellation: " + text);
            Gone((int)root); Gone(child); Require(refused && watch.Elapsed < TimeSpan.FromSeconds(15), "Process tree was not stopped within the bounded wait.");
            checks.Add(new { mode, root, child, seconds = watch.Elapsed.TotalSeconds, rootAndChildTerminated = true });
        }
        using (var output = new MemoryStream())
        {
            var assembly = typeof(NativeAdapterQualification).Assembly.Location.Replace("'", "''");
            var script = "[Reflection.Assembly]::LoadFrom('" + assembly + "')|Out-Null;[Console]::WriteLine([Library.Companion.Tests.NativeAdapterQualification]::NativeMemoryCheck())";
            var result = Run(script, output, limits).GetAwaiter().GetResult();
            Require(result.ExitCode == 0 && int.TryParse(Encoding.UTF8.GetString(output.ToArray()).Trim(), out _), "Native memory fixture failed.");
            var nativeError = int.Parse(Encoding.UTF8.GetString(output.ToArray()).Trim());
            Require(nativeError is 8 or 1455 or 1816 && result.PeakCommittedBytes <= limits.MemoryBytes, "OS memory limit was not enforced.");
            checks.Add(new { memoryAllocationRefused = true, nativeError, requestedCommitBytes = 805306368, result.PeakCommittedBytes, limit = limits.MemoryBytes });
        }
        foreach (var kind in new[] { "stdout", "stderr" })
        {
            using var output = new MemoryStream(); uint root = 0; bool refused = false;
            var script = kind == "stdout" ? "while($true){[Console]::Out.Write([string]::new('x',8192))}" : "while($true){[Console]::Error.Write([string]::new('x',8192))}";
            try { Run(script, output, limits, started: pid => root = pid).GetAwaiter().GetResult(); }
            catch (IOException e) when (e.Message.Contains("limit exceeded")) { refused = true; }
            Require(refused && output.Length <= 65536, "Output limit failed."); Gone((int)root); checks.Add(new { kind, bounded = true, processTerminated = true });
        }
        using (var output = new MemoryStream())
        {
            var script = childScript[..childScript.IndexOf("$c=[Diagnostics.Process]::Start", StringComparison.Ordinal)] + "try{$c=[Diagnostics.Process]::Start($p);[Console]::WriteLine('ESCAPED');exit 9}catch{[Console]::WriteLine('PROCESS-REFUSED');exit 0}";
            var result = Run(script, output, limits with { Processes = 1 }).GetAwaiter().GetResult();
            Require(result.ExitCode == 0 && Encoding.UTF8.GetString(output.ToArray()).Contains("PROCESS-REFUSED"), "Active-process limit failed."); checks.Add(new { activeProcessLimit = "enforced" });
        }
        using (var output = new MemoryStream())
        {
            // Sustained work is accounted by Windows. Querying back the hard CPU
            // cap is enforced by the runner; elapsed/CPU measurements are evidence,
            // not a claim about scheduling precision on every host.
            var result = Run("$s=[Diagnostics.Stopwatch]::StartNew();while($s.Elapsed.TotalSeconds -lt 3){$v=[Math]::Sqrt(1234567)}", output, limits with { CpuRate = 100 }).GetAwaiter().GetResult();
            Require(result.ExitCode == 0, "CPU-bound fixture failed."); checks.Add(new { cpuHardCapPercent = 1, result.UserTimeTicks, cpuLimitReadBack = true });
        }
        var report = ExecutionContract.Serialize(new { home, checks, memoryLimitMeaning = "committed bytes across the job", persistentServices = false, network = false });
        File.WriteAllText(Path.Combine(home, "evidence.json"), report); return report;
    }
    static async Task<string> Generate(string home, string toolsRoot, string name)
    {
        var directory = Path.Combine(home, name); Directory.CreateDirectory(directory);
        var subtitle = Path.Combine(directory, "invented.srt");
        File.WriteAllText(subtitle, "1\n00:00:00,200 --> 00:00:01,200\nInvented qualification subtitle.\n\n2\n00:00:02,200 --> 00:00:03,200\nOutside the two-second sample.\n");
        using var scope = new Scope(toolsRoot); using var pinned = scope.Read("bin/ffmpeg.exe");
        Require(Convert.ToHexStringLower(SHA256.HashData(pinned)) == NativeMediaProcessor.FfmpegSha256, "Fixture generator binary changed.");
        var source = Path.Combine(directory, "invented.mkv");
        using var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var size = name == "encode1080" ? "1920x1080" : "320x180";
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=24:duration=4", "-f", "lavfi", "-i", "sine=frequency=401:sample_rate=48000:duration=4", "-f", "srt", "-i", subtitle,
            "-map", "0:v", "-map", "1:a", "-map", "1:a", "-map", "1:a", "-map", "2:0", "-map", "2:0", "-map", "2:0", "-map", "2:0", "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-threads", "2", "-c:a", "pcm_s16le", "-c:s", "srt", "-metadata", "fixture=" + name };
        foreach (var (index, language, disposition) in new[] { (0,"","default"), (1,"jpn","default"), (2,"eng","visual_impaired"), (3,"eng","comment"), (4,"eng","0"), (5,"eng","forced"), (6,"eng","hearing_impaired"), (7,"fra","0") })
        { args.AddRange([$"-disposition:{index}", disposition]); if (language.Length > 0) args.AddRange([$"-metadata:s:{index}", "language=" + language]); }
        args.AddRange(["-f", "matroska", "pipe:1"]);
        var result = await NativeProcess.Run(scope.Resolve("bin/ffmpeg.exe"), args.ToArray(), null, output, NativeProcessLimits.Sample, 32 * 1024 * 1024, default);
        Require(result.ExitCode == 0 && result.Stderr.Length == 0, "Invented source generation failed: " + result.Stderr); output.Flush(true); return source;
    }
    public static string Media(string parent, string toolsRoot) => MediaCases(parent, toolsRoot, ["remux", "encode", "cancel"]);
    public static string Media1080(string parent, string toolsRoot) => MediaCases(parent, toolsRoot, ["encode1080"]);
    static string MediaCases(string parent, string toolsRoot, string[] modes)
    {
        var home = Path.Combine(parent, "media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home); var checks = new List<object>();
        foreach (var mode in modes)
        {
            var source = Generate(home, toolsRoot, mode).GetAwaiter().GetResult(); var fixtureHash = Hash(source);
            var stage = Path.Combine(home, mode + "-stage"); var state = Path.Combine(home, mode + "-state"); Directory.CreateDirectory(stage); Directory.CreateDirectory(state);
            var config = new WindowsWorkerConfiguration(state, [new("source", Path.GetDirectoryName(source)!, "source"), new("stage", stage, "staging")]);
            using var processor = new NativeMediaProcessor(toolsRoot, [fixtureHash]);
            var worker = new WindowsMediaWorker(config, processor);
            var observed = worker.InspectAsync("source", "invented.mkv", default).GetAwaiter().GetResult();
            using var sourceScope = new Scope(Path.GetDirectoryName(source)!);
            JsonElement probe; using (var pinned = sourceScope.Read("invented.mkv")) probe = processor.InspectAsync(pinned, default).GetAwaiter().GetResult();
            var request = new SampleRequest(1, "stage", 2, ["jpn"], [new(1,"jpn","main"),new(2,"eng","descriptive"),new(3,"eng","commentary"),new(4,"eng","subtitle"),new(5,"eng","subtitle"),new(6,"eng","subtitle"),new(7,"fra","subtitle")], true, true, mode == "remux" ? "stream-copy" : DerivedMedia.EncodeMode);
            var plan = new SamplePlan(request, 1, observed.Observation, DerivedMedia.Select(probe, request), "9.0.2-essentials_build-www.gyan.dev", DerivedMedia.ProbeSignature(probe), Encoding: mode == "remux" ? null : DerivedMedia.SdrEncoding);
            var command = new WorkerCommand(1, Guid.NewGuid().ToString("N"), observed, "stage", plan);
            File.WriteAllText(Path.Combine(home, mode + "-command.json"), ExecutionContract.Serialize(command));
            if (mode == "cancel")
            {
                using var cancellation = new CancellationTokenSource();
                var task = worker.ExecuteAsync(command, cancellation.Token);
                var waiter = Task.Run(async () => { while (processor.ProcessRuns == 0 && !task.IsCompleted) await Task.Delay(5); cancellation.Cancel(); });
                bool refused = false; try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { refused = true; }
                waiter.GetAwaiter().GetResult(); Require(refused, "Native encode cancellation did not stop execution.");
                Require(!File.Exists(Path.Combine(stage, ExecutionContract.Destination(command))) && File.Exists(Path.Combine(stage, ExecutionContract.Destination(command) + ".partial")), "Cancelled output was published or lost.");
                bool retryRefused = false; try { worker.ExecuteAsync(command, default).GetAwaiter().GetResult(); } catch (IOException) { retryRefused = true; }
                Require(retryRefused && processor.ProcessRuns == 1, "Unvalidated partial was retried automatically."); checks.Add(new { mode, partialRetained = true, retryRefused, processor.ProcessRuns });
            }
            else
            {
                // Complete real native validators, then interrupt before publication.
                var interrupted = new WindowsMediaWorker(config, processor, at => { if (at == "validated") throw new IOException("Invented validated-checkpoint interruption."); });
                bool interruptedAtCheckpoint = false;
                try { interrupted.ExecuteAsync(command, default).GetAwaiter().GetResult(); }
                catch (IOException e) when (e.Message.Contains("Invented validated-checkpoint")) { interruptedAtCheckpoint = true; }
                Require(interruptedAtCheckpoint, "Native media validation did not reach the checkpoint.");
                var recovered = new WindowsMediaWorker(config).ReconcileAsync(command, default).GetAwaiter().GetResult();
                Require(new WindowsMediaWorker(config).ExecuteAsync(command, default).GetAwaiter().GetResult() == recovered && processor.ProcessRuns == 1, "Recovery reran FFmpeg.");
                Require(Hash(Path.Combine(stage, recovered.RelativePath)) == recovered.Sha256 && recovered.Validation.Kind == "synthetic", "Output hash/evidence mismatch.");
                Require(plan.Streams.Select(s => s.Index).SequenceEqual(new[] { 0, 1, 2, 4, 5, 6 }), "Incorrect commentary/language/accessibility selection.");
                checks.Add(new { mode, result = recovered, processor.ProcessRuns, recoveredWithoutProcessing = true, processes = processor.Processes });
            }
            Require(Hash(source) == fixtureHash, "Invented input changed.");
        }
        var report = ExecutionContract.Serialize(new { home, checks, realMedia = false, transport = false, archiveEligible = false }); File.WriteAllText(Path.Combine(home, "evidence.json"), report); return report;
    }
}
