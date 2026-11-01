namespace Library.Execution;

// Provisioned offline after explicit approval. Never returned to the browser with its hash.
public record ReviewConnectionProfile(string AccessId, string Folder, string ApiOrigin, string TokenSha256, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc)
{
    public string RootId => "review-" + DerivedMedia.Digest(Folder.TrimEnd('\\').ToUpperInvariant())[..24];
    public void Validate()
    {
        if (!Guid.TryParseExact(AccessId, "N", out _) || Folder is null || Folder.Length is < 4 or > 240 || !char.IsAsciiLetter(Folder[0]) || !Folder.StartsWith(Folder[..1] + ":\\") || Folder.EndsWith('\\') || Folder[3..].Split('\\').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "/:<>\"|?*".Contains(c)))) throw new ArgumentException("An explicit bounded local Windows folder is required.");
        if (!Uri.TryCreate(ApiOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || ApiOrigin != uri.GetLeftPart(UriPartial.Authority)) throw new ArgumentException("Read-only access requires an exact localhost API origin.");
        ExecutionContract.Hash(TokenSha256);
        if (ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > TimeSpan.FromDays(7)) throw new ArgumentException("Remembered read-only access must expire within seven days.");
    }
    public bool Active(DateTimeOffset now) => now >= IssuedUtc && now < ExpiresUtc;
}
public record ReviewConnectionLease(string SessionId, string RootId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
