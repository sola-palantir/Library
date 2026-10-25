using System.Text.Json;
using Library.Execution;

namespace Library.Manager;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--version" })) { Console.WriteLine(JsonSerializer.Serialize(ReleaseInfo.Describe())); return 0; }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Operator management runs on Windows; Linux uses contract tests only.");
            if (args.Length < 2) throw new ArgumentException("Usage: install <new-home> <bundle> <manifest-sha256> [port] | update <home> <bundle> <manifest-sha256> | start|stop|status|recover|rollback <home> | restore <old-home> <new-home> <backup-id> [port] | approve-restore <home> <report-sha256>");
            var action = args[0];
            if (action == "restore")
            {
                if (args.Length is not (4 or 5)) throw new ArgumentException("Restore needs old home, new home, backup ID and optional localhost port.");
                using var source = new StateStore(args[1]); using var destination = new StateStore(args[2], true, args.Length == 5 ? int.Parse(args[4]) : 5080);
                new Coordinator(destination, new DockerRuntime(destination.Installation)).Restore(source, args[3]);
                Console.WriteLine($"Restored offline. Review {Path.Combine(destination.Home, "restore-review.json")} and restored-state before approve-restore. Nothing was started."); return 0;
            }
            if (action is not ("install" or "update" or "start" or "stop" or "status" or "recover" or "rollback" or "backup" or "approve-restore")) throw new ArgumentException("Unknown action.");
            if (action is "install" or "update" && args.Length < 4) throw new ArgumentException("Bundle directory and trusted manifest hash required.");
            using var store = new StateStore(args[1], action == "install", args.Length == 5 ? int.Parse(args[4]) : 5080);
            var runtime = new DockerRuntime(store.Installation); var coordinator = new Coordinator(store, runtime, checkpoint: s => Console.WriteLine($"{s.Phase}: {s.Candidate.Version}"));
            switch (action)
            {
                case "install": case "update": coordinator.Apply(args[2], args[3]); break;
                case "start": coordinator.Start(); break;
                case "stop": coordinator.Stop(); break;
                case "recover": coordinator.Recover(); break;
                case "rollback": coordinator.Rollback(); break;
                case "backup": coordinator.Backup(); break;
                case "approve-restore": if (args.Length != 3) throw new ArgumentException("Exact restore report SHA-256 required."); coordinator.ApproveRestore(args[2]); break;
                case "status":
                    object api;
                    try { api = runtime.Status(); } catch (Exception e) { api = new { available = false, detail = e.Message }; }
                    Console.WriteLine(JsonSerializer.Serialize(new { update = store.Read(), api }, Bundle.Json)); break;
            }
            Console.WriteLine($"Library: http://127.0.0.1:{store.Installation.Port} — catalog/review mode, production and pairing off."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
    }
}
