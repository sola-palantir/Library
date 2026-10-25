using Library.Tests;
if (args.Length == 3 && args[0] == "--verify-restored")
{
    Console.WriteLine(QualificationCatalogFlow.VerifyOfficialRestore(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]))); return;
}
if (args.Length != 2) throw new ArgumentException("Supply a new isolated catalog directory and completed-checkpoint JSON, --verify and an existing qualification directory, or --verify-restored with source and restored directories.");
// Offline qualification only: no listener, credentials, worker activation or media I/O.
if (args[0] == "--verify") Console.WriteLine(QualificationCatalogFlow.Verify(Path.GetFullPath(args[1])));
else
{
    var root = Path.GetFullPath(args[0]);
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Qualification destination already exists.");
    var evidence = File.ReadAllText(Path.GetFullPath(args[1]));
    Directory.CreateDirectory(root);
    Console.WriteLine(await QualificationCatalogFlow.Run(root, evidence));
}
