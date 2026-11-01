using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion;

public record ReviewAccessRequest(string Folder, string ApiOrigin, string ToolsRoot, int Days = 7);
public record ReviewAccessBundle(ReviewConnectionProfile Profile, string ToolsRoot, string Token);

// DPAPI current-user scope (never LOCAL_MACHINE); no credential manager, service,
// browser secret, registration endpoint, registry setting or network listener.
public static class ReviewAccess
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] bytes, bool decrypt = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembered access requires Windows current-user DPAPI.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, "Library read-only worker", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect/unprotect this user's read-only access.");
            if (output.Length is <= 0 or > 32768) throw new IOException("Invalid protected access length.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    public static void Authorize(ReviewAccessRequest request, string destination)
    {
        if (!OperatingSystem.IsWindows() || request.Days is < 1 or > 7) throw new ArgumentException("Windows authorization lasts one to seven days.");
        using var folder = new Scope(request.Folder); using var tools = new Scope(request.ToolsRoot);
        using var binary = tools.Read("bin/ffprobe.exe");
        if (Convert.ToHexStringLower(SHA256.HashData(binary)) != NativeMediaProcessor.FfprobeSha256) throw new IOException("Verified FFprobe required before authorizing access.");
        using var destinationScope = new Scope(destination);
        if (Directory.EnumerateFileSystemEntries(destinationScope.Root).Any()) throw new IOException("Authorization destination must be empty; existing access is never replaced.");
        var now = DateTimeOffset.UtcNow;
        // Validate all non-secret scope before asking for action-time approval.
        var profile = new ReviewConnectionProfile(Guid.NewGuid().ToString("N"), folder.Root, request.ApiOrigin, new string('0',64), now, now.AddDays(request.Days)); profile.Validate();
        Console.WriteLine($"Remember read-only access for this Windows account until {profile.ExpiresUtc:O}?\nFolder: {profile.Folder}\nAPI: {profile.ApiOrigin}\nStorage: {destinationScope.Root} (DPAPI encrypted credential; API export contains hash only).\nAt most eight top-level media files; metadata only. No encoding, uploads, recursion or background startup.\nConnect is limited to one hour. Disconnect cancels work; Forget access revokes it in the catalog.\nType ALLOW READ ONLY to create this authorization; anything else cancels.");
        if (Console.ReadLine() != "ALLOW READ ONLY") throw new OperationCanceledException("Authorization cancelled; no credential created.");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); profile = profile with { TokenSha256 = DerivedMedia.Digest(token) };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new ReviewAccessBundle(profile, tools.Root, token), ExecutionContract.Json);
        byte[] encrypted;
        try { encrypted = Protect(plaintext); } finally { CryptographicOperations.ZeroMemory(plaintext); }
        using (var output = WindowsPublication.Open(destinationScope, "worker-access.partial")) { output.Write(encrypted); WindowsPublication.Publish(destinationScope, output, "worker-access.dpapi"); }
        using (var output = WindowsPublication.Open(destinationScope, "api-profile.partial")) { JsonSerializer.Serialize(output, profile, ExecutionContract.Json); WindowsPublication.Publish(destinationScope, output, "api-profile.json"); }
        Console.WriteLine("Authorization files created. Nothing was connected or installed. Configure only the selected isolated API after reviewing api-profile.json.");
    }
    internal static ReviewAccessBundle Read(string directory)
    {
        using var scope = new Scope(directory); using var input = scope.Read("worker-access.dpapi");
        if (input.Length is <= 0 or > 32768) throw new IOException("Invalid protected profile size.");
        var encrypted = new byte[input.Length]; input.ReadExactly(encrypted); var plaintext = Protect(encrypted, true);
        try
        {
            var bundle = JsonSerializer.Deserialize<ReviewAccessBundle>(plaintext, ExecutionContract.Json) ?? throw new IOException("Missing protected profile.");
            bundle.Profile.Validate();
            if (!bundle.Profile.Active(DateTimeOffset.UtcNow) || bundle.Token.Length != 64 || !bundle.Token.All(char.IsAsciiHexDigit) || DerivedMedia.Digest(bundle.Token) != bundle.Profile.TokenSha256) throw new IOException("Read-only authorization expired or differs from its protected identity; no automatic renewal.");
            return bundle;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static async Task Run(string directory, CancellationToken ct)
    {
        using var scope = new Scope(directory);
        using var lease = new FileStream(scope.Resolve("worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var bundle = Read(directory);
        Console.WriteLine($"Read-only worker ready for {bundle.Profile.Folder}. Open {bundle.Profile.ApiOrigin} and choose Connect Windows worker. Close this window or press Ctrl+C to stop. Authorization expires {bundle.Profile.ExpiresUtc:O}.");
        await new ReviewConnectionClient(bundle).Run(ct);
    }
}

public sealed class ReviewConnectionClient(ReviewAccessBundle bundle)
{
    internal static ReviewWorkerConfiguration Configuration(ReviewAccessBundle bundle, ReviewConnectionLease lease)
    {
        bundle.Profile.Validate();
        if (!bundle.Profile.Active(DateTimeOffset.UtcNow) || !Guid.TryParseExact(lease.SessionId,"N",out _) || lease.RootId != bundle.Profile.RootId || lease.IssuedUtc < bundle.Profile.IssuedUtc || lease.ExpiresUtc > bundle.Profile.ExpiresUtc) throw new IOException("API connection differs from the locally approved scope.");
        var config = new ReviewWorkerConfiguration(bundle.Profile.ApiOrigin, lease.SessionId, bundle.Profile.Folder, bundle.ToolsRoot, lease.IssuedUtc, lease.ExpiresUtc, true); config.Validate(DateTimeOffset.UtcNow); return config;
    }
    public async Task Run(CancellationToken cancellation)
    {
        bundle.Profile.Validate();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation); session.CancelAfter(bundle.Profile.ExpiresUtc - DateTimeOffset.UtcNow);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var ct = session.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, bundle.Profile.ApiOrigin + "/api/review-connection/worker"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bundle.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) throw new IOException($"Read-only connection refused ({(int)response.StatusCode}). Relaunch only after resolving the connection; no credentials or response body logged.");
                using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var memory = new MemoryStream(); var buffer = new byte[1024]; int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0) { if (memory.Length + count > 8192) throw new IOException("Oversized connection response."); memory.Write(buffer, 0, count); }
                var connection = memory.Length == 0 ? null : JsonSerializer.Deserialize<ReviewConnectionLease>(memory.ToArray(), ExecutionContract.Json);
                if (connection != null)
                {
                    using var worker = new ReviewWorkerClient(Configuration(bundle, connection), bundle.Token);
                    try { await worker.Run(ct); }
                    catch (ReviewSessionEndedException) { /* Disconnect invalidates the session. Recheck profile/lease; never renew it. */ }
                }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
    }
}
