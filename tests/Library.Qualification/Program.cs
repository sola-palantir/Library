using Library.Tests;
if (args.Length != 2) throw new ArgumentException("Supply a new isolated catalog directory and completed-checkpoint JSON, or --verify and an existing qualification directory.");
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
