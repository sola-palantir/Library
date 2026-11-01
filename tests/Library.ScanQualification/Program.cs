using Library.Companion.Tests;
if (args.Length != 3) throw new ArgumentException("Supply the approved sample folder, verified tools directory, and a new metadata-only evidence directory.");
await ReadOnlyScanQualification.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
