using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion;

internal sealed class ReviewSessionEndedException() : IOException("Read-only session ended or was disconnected.");

public record ReviewWorkerConfiguration(string ApiOrigin, string SessionId, string Folder, string ToolsRoot, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, bool RememberedAccess = false)
{
    public string RootId => "review-" + DerivedMedia.Digest(Folder.TrimEnd('\\').ToUpperInvariant())[..24];
    public void Validate(DateTimeOffset now)
    {
        if (!Guid.TryParseExact(SessionId, "N", out _) || !Path.IsPathFullyQualified(Folder) || Folder.StartsWith("\\\\") || Path.GetPathRoot(Folder) == Folder || !Path.IsPathFullyQualified(ToolsRoot)) throw new ArgumentException("Explicit local review folder and verified tools required.");
        if (!Uri.TryCreate(ApiOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || ApiOrigin != uri.GetLeftPart(UriPartial.Authority)) throw new ArgumentException("Read-only worker connects only to the exact localhost API origin.");
        if (ExpiresUtc <= now || now < IssuedUtc || ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > TimeSpan.FromMinutes(RememberedAccess ? 60 : 10)) throw new ArgumentException("Read-only session is expired or exceeds its bounded lifetime.");
    }
}

// Manually launched, temporary process. No listener, pairing store or encoder.
public sealed class ReviewWorkerClient : IDisposable
{
    readonly HttpClient client;
    readonly ReviewWorkerConfiguration configuration;
    readonly string token;
    public ReviewWorkerClient(ReviewWorkerConfiguration configuration, string token) : this(configuration, token,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false }) { }
    internal ReviewWorkerClient(ReviewWorkerConfiguration configuration, string token, HttpMessageHandler handler)
    {
        configuration.Validate(DateTimeOffset.UtcNow);
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit)) throw new ArgumentException("Missing ephemeral review grant; no token is logged or stored.");
        this.configuration = configuration; this.token = token;
        client = new(handler) { BaseAddress = new(configuration.ApiOrigin + "/api/review-worker/"), Timeout = TimeSpan.FromSeconds(5) };
    }
    async Task<T?> Send<T>(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Library-Review-Session", configuration.SessionId);
        if (body != null) request.Content = new StringContent(ExecutionContract.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && configuration.RememberedAccess) throw new ReviewSessionEndedException();
        if (!response.IsSuccessStatusCode) throw new IOException($"Read-only API refused the request ({(int)response.StatusCode}); no credential or response body is logged.");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        using var stream = await response.Content.ReadAsStreamAsync(ct); using var result = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (result.Length + count > 65536) throw new IOException("Oversized read-only command response."); result.Write(buffer, 0, count); }
        return result.Length == 0 ? default : JsonSerializer.Deserialize<T>(result.ToArray(), ExecutionContract.Json);
    }
    internal void ValidateCommand(ReviewScanCommand command)
    {
        if (!Guid.TryParseExact(command.Id, "N", out _) || command.SessionId != configuration.SessionId || command.RootId != configuration.RootId || command.DeadlineUtc > configuration.ExpiresUtc || command.DeadlineUtc <= DateTimeOffset.UtcNow || command.DeadlineUtc > DateTimeOffset.UtcNow.AddMinutes(3))
            throw new IOException("API command differs from the locally scoped read-only session.");
    }
    public async Task Run(CancellationToken cancellation)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation); session.CancelAfter(configuration.ExpiresUtc - DateTimeOffset.UtcNow);
        var ct = session.Token; var scanner = new ReadOnlyScanner(configuration.Folder, configuration.ToolsRoot);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var command = await Send<ReviewScanCommand>(HttpMethod.Get, "command", null, ct);
                if (command == null) { await Task.Delay(500, ct); continue; }
                ValidateCommand(command);
                using var job = CancellationTokenSource.CreateLinkedTokenSource(ct); job.CancelAfter(command.DeadlineUtc - DateTimeOffset.UtcNow);
                async Task Watch()
                {
                    try
                    {
                        while (!job.IsCancellationRequested)
                        {
                            await Task.Delay(500, job.Token);
                            if (!await Send<bool>(HttpMethod.Get, $"jobs/{command.Id}/running", null, job.Token)) { job.Cancel(); return; }
                        }
                    }
                    catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException) { job.Cancel(); }
                }
                var watch = Watch();
                try
                {
                    var result = await scanner.Scan(command, job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    await Send<JsonElement>(HttpMethod.Post, "result", result, job.Token);
                }
                catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        var error = e is OperationCanceledException ? "Scan cancelled, disconnected or timed out. No partial inventory was registered." : e.Message;
                        await Send<JsonElement>(HttpMethod.Post, $"jobs/{command.Id}/failure", new { error }, ct);
                    }
                }
                finally { job.Cancel(); await watch; }
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
    }
    public void Dispose() => client.Dispose();
}
