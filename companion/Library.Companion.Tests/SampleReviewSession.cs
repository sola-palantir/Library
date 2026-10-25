using System.Security.Cryptography;
namespace Library.Companion.Tests;

// Owns exactly one local qualification output until a terminal decision.
// This is not a catalog/worker receipt and has no transport or production route.
public sealed class SampleReviewSession : IDisposable
{
    readonly MemoryStream output = new();
    bool sealedOutput, technical, disposed;
    string? visualHash, visualNotes; bool? visualAccepted;
    public string? OutputSha256 { get; private set; }
    public string? Decision { get; private set; }
    public Stream Output { get { Alive(); if (sealedOutput) throw new IOException("Output is sealed."); return output; } }
    public long Bytes { get { Alive(); return output.Length; } }
    void Alive() { if (disposed) throw new ObjectDisposedException(nameof(SampleReviewSession)); }
    string Hash() => Convert.ToHexStringLower(SHA256.HashData(output.GetBuffer().AsSpan(0, checked((int)output.Length))));
    public string Seal()
    {
        Alive(); if (sealedOutput || output.Length is < 1 or > 268435456) throw new IOException("One bounded completed output required.");
        OutputSha256 = Hash(); sealedOutput = true; return OutputSha256;
    }
    public ReadOnlyMemory<byte> Read()
    { Alive(); if (!sealedOutput) throw new IOException("Output is incomplete."); return output.GetBuffer().AsMemory(0, checked((int)output.Length)); }
    public void TechnicalPassed(string hash)
    { Alive(); if (!sealedOutput || hash != OutputSha256 || Hash() != hash || Decision != null) throw new IOException("Technical evidence is not bound to this output."); technical = true; }
    public void VisualReview(string outputHash, string imageHash, bool accepted, string notes)
    {
        Alive(); if (!technical || outputHash != OutputSha256 || Decision != null || visualAccepted != null || string.IsNullOrWhiteSpace(notes)) throw new IOException("Bound technical and visual review required.");
        Library.Execution.ExecutionContract.Hash(imageHash); visualHash = imageHash; visualNotes = notes; visualAccepted = accepted;
    }
    public object Complete()
    {
        Alive(); if (!technical || visualAccepted == null || Hash() != OutputSha256 || Decision != null) throw new IOException("Cannot accept incomplete or changed output.");
        Decision = visualAccepted.Value ? "accepted-local-sample" : "rejected-local-sample";
        return new { decision = Decision, bytes = output.Length, outputSha256 = OutputSha256, visualEvidenceSha256 = visualHash, visualNotes, technicalPassed = technical, workerReceipt = false, productionActivation = false };
    }
    public void Dispose()
    { if (disposed) return; Decision ??= "rejected-incomplete-session"; output.Dispose(); disposed = true; }
}
