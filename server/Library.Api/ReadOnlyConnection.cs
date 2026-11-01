using System.Security.Cryptography;
using System.Text;
using Library.Execution;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

// Durable revocation and session state share the existing catalog transaction boundary.
// No registration/credential-minting endpoint. A missing profile is the default.
public sealed class ReadOnlyConnection(LibraryOptions options, IDbContextFactory<Catalog> factory)
{
    const string StateId = "read-only-worker-connection";
    readonly SemaphoreSlim gate = new(1, 1);
    State state = new("", null, false);
    record State(string AccessId, ReviewConnectionLease? Lease, bool Revoked);
    public ReviewConnectionProfile? Profile => options.ReadOnlyConnection;
    public bool Revoked => state.Revoked;
    public bool Configured => Profile != null;
    public ReviewConnectionLease? Lease => Profile?.Active(DateTimeOffset.UtcNow) == true && !state.Revoked && state.AccessId == Profile.AccessId && state.Lease?.ExpiresUtc > DateTimeOffset.UtcNow ? state.Lease : null;
    public ReviewSessionOptions? Grant => Lease is { } lease && Profile is { } profile ? new(lease.SessionId, profile.Folder, profile.ApiOrigin, profile.TokenSha256, lease.IssuedUtc, lease.ExpiresUtc) : null;
    async Task Change(Func<State, State> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Integrations.SingleOrDefaultAsync(d => d.Id == StateId, ct);
            var previous = row == null ? new State(Profile?.AccessId ?? "", null, false) : JobRunner.Decode<State>(row.Payload);
            // A different offline authorization requires a new explicit connection.
            if (previous.AccessId != Profile?.AccessId) previous = new(Profile?.AccessId ?? "", null, false);
            var next = change(previous);
            state = previous with { Lease = null }; // fail closed if persistence fails
            if (row == null) { row = new() { Id = StateId }; db.Integrations.Add(row); }
            row.Payload = ExecutionContract.Serialize(next); row.Revision++;
            db.Audit("read-only-worker-connection", next.Revoked ? "access revoked" : next.Lease == null ? "disconnected" : "connected; read-only; bounded folder; one-hour maximum");
            await db.SaveChangesAsync(ct); state = next;
        }
        finally { gate.Release(); }
    }
    public Task Initialize(CancellationToken ct) => Configured ? Change(s => s with { Lease = null }, ct) : Task.CompletedTask;
    public Task Connect(string folder, CancellationToken ct)
    {
        if (Profile is not { } profile || !profile.Active(DateTimeOffset.UtcNow)) throw new InvalidOperationException("No approved, unexpired Windows worker access is configured. Connect cannot create or renew credentials.");
        if (!string.Equals(folder, profile.Folder, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Connect is restricted to the approved folder shown here.");
        return Change(s => {
            if (s.Revoked) throw new InvalidOperationException("Read-only access was revoked. A new local authorization is required.");
            if (s.Lease?.ExpiresUtc > DateTimeOffset.UtcNow) throw new InvalidOperationException("Disconnect the existing session before reconnecting.");
            var now = DateTimeOffset.UtcNow; var expires = now.AddHours(1); if (expires > profile.ExpiresUtc) expires = profile.ExpiresUtc;
            return s with { Lease = new(Guid.NewGuid().ToString("N"), profile.RootId, now, expires) };
        }, ct);
    }
    public Task Disconnect(bool revoke, CancellationToken ct) => Configured ? Change(s => s with { Lease = null, Revoked = s.Revoked || revoke }, ct) : Task.CompletedTask;
    public bool Authorize(string origin, string header)
    {
        if (Profile is not { } p || !p.Active(DateTimeOffset.UtcNow) || state.AccessId != p.AccessId || state.Revoked || origin != p.ApiOrigin || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var token = header[7..];
        return token.Length == 64 && token.All(char.IsAsciiHexDigit) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(token)), Convert.FromHexString(p.TokenSha256));
    }
    public object Status(bool workerConnected) => new {
        configured = Configured, state = !Configured ? "not-configured" : Revoked ? "revoked" : Profile!.Active(DateTimeOffset.UtcNow) == false ? "authorization-expired" : state.Lease?.ExpiresUtc <= DateTimeOffset.UtcNow ? "session-expired" : Lease == null ? "disconnected" : workerConnected ? "connected" : "waiting-for-worker",
        folder = Profile?.Folder, apiOrigin = Profile?.ApiOrigin, accessExpiresUtc = Profile?.ExpiresUtc, sessionExpiresUtc = Lease?.ExpiresUtc,
        canConnect = Configured && !Revoked && Profile!.Active(DateTimeOffset.UtcNow) && Lease == null,
        canDisconnect = Lease != null, rememberedAccess = Configured && !Revoked, encoding = false, maxFiles = ReviewScan.MaxFiles, recursive = false,
        credentialStorage = "Windows DPAPI for the authorizing Windows account; API stores a hash only", sessionMinutes = 60
    };
}
