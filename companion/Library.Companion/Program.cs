using System.Text.Json;
namespace Library.Companion;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--version" })) { Console.WriteLine(JsonSerializer.Serialize(Library.Execution.ReleaseInfo.Describe())); return 0; }
            if (args.Length == 1 && args[0] == "devices") { using var device = new NativeImapi(); Console.WriteLine(JsonSerializer.Serialize(device.ListRecorders(), Contract.Json)); return 0; }
            if (args.Length < 2) throw new ArgumentException("Usage: devices | pair <config.json> | status <config.json> <disc-id> | prepare|burn|fallback|report-external|verify <config.json> <command.json>");
            var config = JsonSerializer.Deserialize<CompanionConfig>(File.ReadAllText(args[1]), Contract.Json) ?? throw new InvalidDataException("Missing config.");
            config.Validate(requireStaging: args[0] != "verify");
            if (args[0] == "pair") { using var journal = new Journal(config.StateRoot); Console.WriteLine(Receipts.Pair(journal)); return 0; }
            if (args.Length != 3) throw new ArgumentException("Expected third argument.");
            if (args[0] == "status") { using var journal = new Journal(config.StateRoot); Console.WriteLine(JsonSerializer.Serialize(journal.Events(args[2]), Contract.Json)); return 0; }
            if (args[0] == "receive-sample")
            {
                if (new FileInfo(args[2]).Length > 8192) throw new InvalidDataException("Oversized sample request.");
                var request = JsonSerializer.Deserialize<SampleReceiveRequest>(File.ReadAllText(args[2]), Contract.Json) ?? throw new InvalidDataException("Missing sample request.");
                Console.WriteLine(JsonSerializer.Serialize(SampleReceiver.Receive(config, request).GetAwaiter().GetResult(), Contract.Json)); return 0;
            }
            if (args[0] is not ("prepare" or "burn" or "fallback" or "report-external" or "verify")) throw new ArgumentException("Unknown command.");
            var command = JsonSerializer.Deserialize<Command>(File.ReadAllText(args[2]), Contract.Json) ?? throw new InvalidDataException("Missing command.");
            using var optical = new NativeImapi(); Evidence result;
            try { result = new Engine(config, optical).Execute(args[0], command); }
            catch
            {
                using var failed = new Journal(config.StateRoot);
                var evidence = failed.Events(command.DiscId).LastOrDefault(e => e.CommandId == command.CommandId);
                if (evidence != null) Console.WriteLine(JsonSerializer.Serialize(Receipts.Sign(failed, evidence), Contract.Json));
                throw;
            }
            using var signer = new Journal(config.StateRoot); Console.WriteLine(JsonSerializer.Serialize(Receipts.Sign(signer, result), Contract.Json)); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = e.Message, hardwareQualified = false }, Contract.Json)); return 1; }
    }
}
