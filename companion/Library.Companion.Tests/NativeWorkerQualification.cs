using System.Text.Json;
using System.Runtime.InteropServices;
using Library.Execution;
namespace Library.Companion.Tests;

// Runs actual repository Windows code, without xunit/FFmpeg/COM/network dependencies.
// Every byte is invented under a new isolated directory; retained for evidence.
public static class NativeWorkerQualification
{
    public static void TerminateAt(string root, string checkpoint)
    {
        if (checkpoint is not ("validated" or "published")) throw new ArgumentException("Unknown crash boundary.");
        if (Directory.Exists(root)) throw new IOException("Crash fixture must be a new directory.");
        using var f = new Fixture(root, "process-crash");
        File.WriteAllText(Path.Combine(root, "command.json"), ExecutionContract.Serialize(f.Command));
        File.WriteAllText(Path.Combine(root, "config.json"), ExecutionContract.Serialize(f.Config));
        var worker = new WindowsMediaWorker(f.Config, f.Processor, at => { if (at == checkpoint) Environment.Exit(73); });
        worker.ExecuteAsync(f.Command, default).GetAwaiter().GetResult();
        throw new Exception("Crash checkpoint not reached.");
    }
    public static string RecoverAfterExit(string root)
    {
        var config = JsonSerializer.Deserialize<WindowsWorkerConfiguration>(File.ReadAllText(Path.Combine(root, "config.json")), ExecutionContract.Json)!;
        var command = JsonSerializer.Deserialize<WorkerCommand>(File.ReadAllText(Path.Combine(root, "command.json")), ExecutionContract.Json)!;
        // Default processor is unavailable: successful reconciliation cannot run an encode.
        var result = new WindowsMediaWorker(config).ReconcileAsync(command, default).GetAwaiter().GetResult();
        var report = ExecutionContract.Serialize(new { root, result.JobId, result.Sha256, processExitRecovered = true, reencoded = false });
        File.WriteAllText(Path.Combine(root, "recovery.json"), report); return report;
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Refused(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException) { return; } throw new Exception("Expected refusal did not occur."); }
    public static string Run(string parent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetFullPath(parent), "worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var results = new List<string>();
        using (var f = new Fixture(root, "complete"))
        {
            var r = f.Run(); Require(r.Validation.Kind == "synthetic", "Fixture evidence mislabeled.");
            Require(f.Run() == r && f.Processor.Runs == 1, "Duplicate delivery reprocessed.");
            Refused(() => ExecutionContract.Match(f.Command, r));
            Refused(() => f.Worker.ExecuteAsync(f.Command with { JobId = Guid.NewGuid().ToString("N") }, default).GetAwaiter().GetResult());
            Refused(() => f.Worker.ExecuteAsync(f.Command with { Plan = f.Command.Plan with { Request = f.Command.Plan.Request with { Seconds = 2 } } }, default).GetAwaiter().GetResult());
            Require(File.ReadAllBytes(f.Input).SequenceEqual(f.Payload), "Original changed.");
            results.Add("inspect/hash, publish/reopen, duplicate delivery, one source reservation, changed command and synthetic receipt rejection");
        }
        foreach (var stage in new[] { "validated", "published" })
        {
            using var f = new Fixture(root, "crash-" + stage);
            var failing = new WindowsMediaWorker(f.Config, f.Processor, at => { if (at == stage) throw new IOException("Invented process interruption."); });
            Refused(() => failing.ExecuteAsync(f.Command, default).GetAwaiter().GetResult());
            var recovered = f.Worker.ReconcileAsync(f.Command, default).GetAwaiter().GetResult();
            Require(f.Processor.Runs == 1 && File.Exists(Path.Combine(f.Stage, recovered.RelativePath)), "Recovery reran processor or lost output.");
            results.Add("interruption/reopen at " + stage + " reconciles without rerun");
        }
        using (var f = new Fixture(root, "collision"))
        {
            var path = Path.Combine(f.Stage, ExecutionContract.Destination(f.Command).ToUpperInvariant()); File.WriteAllText(path, "unrelated occupant");
            Refused(() => f.Run()); Require(File.ReadAllText(path) == "unrelated occupant" && f.Processor.Runs == 0, "Collision overwritten.");
            results.Add("case-insensitive destination collision preserved");
        }
        using (var f = new Fixture(root, "cancel"))
        {
            using var ct = new CancellationTokenSource(); f.Processor.During = () => ct.Cancel();
            Refused(() => f.Worker.ExecuteAsync(f.Command, ct.Token).GetAwaiter().GetResult());
            Require(File.Exists(Path.Combine(f.Stage, ExecutionContract.Destination(f.Command) + ".partial")), "Cancelled partial lost.");
            Refused(() => f.Run()); Require(f.Processor.Runs == 1, "Interrupted processing restarted without review.");
            results.Add("cancellation retains owned partial; retry cannot overwrite or reprocess");
        }
        using (var f = new Fixture(root, "cancel-before"))
        {
            using var ct = new CancellationTokenSource(); ct.Cancel();
            Refused(() => f.Worker.ExecuteAsync(f.Command, ct.Token).GetAwaiter().GetResult());
            Require(!Directory.EnumerateFiles(f.Stage).Any(), "Cancelled job created output.");
            results.Add("pre-cancellation creates no output");
        }
        using (var f = new Fixture(root, "stale"))
        {
            File.WriteAllText(f.Input, "replacement contents"); Refused(() => f.Run());
            Require(f.Processor.Runs == 0, "Stale source processed."); results.Add("stale source rejected before output");
        }
        using (var f = new Fixture(root, "exclusive"))
        {
            f.Processor.During = () => {
                Refused(() => { using var writer = new FileStream(f.Input, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
                Refused(() => f.Worker.ExecuteAsync(f.Command, default).GetAwaiter().GetResult());
                Refused(() => Directory.Move(f.Stage, f.Stage + "-moved"));
            };
            f.Run(); results.Add("source writer, concurrent worker and parent-directory replacement refused");
        }
        using (var f = new Fixture(root, "corrupt"))
        {
            var result = f.Run(); File.AppendAllText(Path.Combine(f.Stage, result.RelativePath), "alteration");
            Refused(() => f.Worker.ReconcileAsync(f.Command, default).GetAwaiter().GetResult());
            results.Add("modified completed output rejected on reconciliation");
        }
        using (var f = new Fixture(root, "journal-interruption"))
        {
            File.WriteAllText(Path.Combine(f.Config.StateRoot, "invented.journal.partial"), "torn write");
            Refused(() => f.Run()); Require(f.Processor.Runs == 0, "Torn journal accepted."); results.Add("interrupted journal blocks execution");
        }
        using (var f = new Fixture(root, "hardlinks"))
        {
            var alias = Path.Combine(Path.GetDirectoryName(f.Input)!, "seeded-alias.bin");
            if (!CreateHardLink(alias, f.Input, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create isolated NTFS hardlink fixture.");
            var result = f.Run();
            Require(File.ReadAllBytes(alias).SequenceEqual(f.Payload) && File.ReadAllBytes(f.Input).SequenceEqual(f.Payload), "Source hardlink contents changed.");
            if (!CreateHardLink(Path.Combine(f.Stage, "output-alias.bin"), Path.Combine(f.Stage, result.RelativePath), IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create output hardlink fixture.");
            Refused(() => f.Worker.ReconcileAsync(f.Command, default).GetAwaiter().GetResult());
            results.Add("seeding-style source hardlink preserved; multiply-linked output refused");
        }
        using (var f = new Fixture(root, "overlap"))
        {
            var unsafeWorker = new WindowsMediaWorker(f.Config with { StateRoot = f.Stage }, f.Processor);
            Refused(() => unsafeWorker.InspectAsync("source", "invented.bin", default).GetAwaiter().GetResult());
            Refused(() => f.Worker.InspectAsync("source", "../escape.bin", default).GetAwaiter().GetResult());
            results.Add("overlapping roots and traversal refused");
        }
        var report = JsonSerializer.Serialize(new { root, checks = results, realMedia = false, encodes = 0, filesDeleted = 0, powerLossQualified = false }, ExecutionContract.Json);
        File.WriteAllText(Path.Combine(root, "result.json"), report); return report;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string name, string existing, IntPtr security);
    sealed class Fixture : IDisposable
    {
        public string Input { get; } public string Stage { get; } public byte[] Payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        public WindowsWorkerConfiguration Config { get; } public SyntheticProcessor Processor { get; } = new(); public WindowsMediaWorker Worker { get; } public WorkerCommand Command { get; }
        public Fixture(string root, string name)
        {
            var home = Path.Combine(root, name); var source = Path.Combine(home, "source"); Stage = Path.Combine(home, "stage"); var state = Path.Combine(home, "state");
            foreach (var p in new[] { source, Stage, state }) Directory.CreateDirectory(p);
            Input = Path.Combine(source, "invented.bin"); File.WriteAllBytes(Input, Payload);
            Config = new(state, [new("source", source, "source"), new("stage", Stage, "staging")]); Worker = new(Config, Processor);
            var observed = Worker.InspectAsync("source", "invented.bin", default).GetAwaiter().GetResult();
            Command = new(1, Guid.NewGuid().ToString("N"), observed, "stage", new(new(1, "stage", 1, ["jpn"], [new(1, "jpn", "main")], true, true), 1, observed.Observation, [new(0, "video", "h264", "", []), new(1, "audio", "dts", "jpn", [])], "invented fixture", "invented signature"));
        }
        public WorkerResult Run() => Worker.ExecuteAsync(Command, default).GetAwaiter().GetResult();
        public void Dispose() { } // Preserve invented artifacts and journal evidence.
    }
    sealed class SyntheticProcessor : IStagedMediaProcessor
    {
        public int Runs; public Action? During; public string Qualification => "synthetic";
        public Task CheckSourceAsync(Stream input, SamplePlan plan, CancellationToken ct) => Task.CompletedTask;
        public async Task ProcessAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct) { Runs++; await input.CopyToAsync(output, ct); During?.Invoke(); }
        public Task<WorkerValidation> ValidateAsync(Stream input, Stream output, SamplePlan plan, CancellationToken ct) => Task.FromResult(new WorkerValidation("synthetic", "Invented bytes; no media or codec qualification."));
    }
}
