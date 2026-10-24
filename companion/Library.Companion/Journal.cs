using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
namespace Library.Companion;

public sealed class Journal : IDisposable
{
    private readonly Scope scope;
    private readonly FileStream lease;
    public Journal(string root) { scope = new(root); lease = new(scope.Resolve("companion.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    public List<Evidence> Events(string discId)
    {
        Contract.Id(discId);
        if (Directory.EnumerateFiles(scope.Root, discId + ".*.partial").Any()) throw new InvalidDataException("Interrupted journal publication exists. Do not write this disc; preserve evidence and reconcile from backup.");
        var list = new List<Evidence>();
        foreach (var path in Directory.EnumerateFiles(scope.Root, discId + ".*.event.json").Order(StringComparer.Ordinal))
        {
            using var f = scope.Read(Path.GetFileName(path)); var record = JsonSerializer.Deserialize<Evidence>(f, Contract.Json) ?? throw new InvalidDataException("Journal record invalid.");
            if (Path.GetFileName(path) != $"{discId}.{list.Count:D8}.event.json" || record.DiscId != discId || record.Sequence != list.Count + 1) throw new InvalidDataException("Journal identity or sequence is corrupt; restore before continuing.");
            list.Add(record);
        }
        return list;
    }
    public void Append(Evidence e)
    {
        var count = Events(e.DiscId).Count; if (e.Sequence != count + 1) throw new InvalidOperationException("Journal sequence must increase by one."); var destinationName = $"{e.DiscId}.{count:D8}.event.json";
        var partialName = $"{e.DiscId}.{count:D8}.{Guid.NewGuid():N}.partial";
        if (OperatingSystem.IsWindows())
        {
            using var f = WindowsPublication.Open(scope, partialName);
            JsonSerializer.Serialize(f, e, Contract.Json);
            WindowsPublication.Publish(scope, f, destinationName);
        }
        else // Simulation only; NativeImapi refuses non-Windows.
        {
            var partial = scope.Resolve(partialName);
            using (var f = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(f, e, Contract.Json); f.Flush(true); }
            File.Move(partial, scope.Resolve(destinationName), false);
        }
    }
    public string PathFor(string name) => scope.Resolve(name);
    public FileStream Read(string name) => scope.Read(name);
    public void Dispose() { lease.Dispose(); scope.Dispose(); }
}
public record Receipt(string Payload, string Signature);
public static class Receipts
{
    public static string Pair(Journal journal)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Explicit setup only. Never overwrite an existing trust identity.
        using (var f = new FileStream(journal.PathFor("identity.private.pem"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { var bytes = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()); f.Write(bytes); f.Flush(true); }
        return key.ExportSubjectPublicKeyInfoPem();
    }
    public static Receipt Sign(Journal journal, Evidence e)
    {
        using var source = journal.Read("identity.private.pem"); using var reader = new StreamReader(source); using var key = ECDsa.Create(); key.ImportFromPem(reader.ReadToEnd());
        var payload = JsonSerializer.Serialize(e, Contract.Json);
        return new(payload, Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }
}
