using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Library.Execution;

namespace Library.Companion;

// Manually invoked, one offline-approved command. No listener or credential store.
public sealed class QualificationClient : IDisposable
{
    readonly HttpClient client;
    static readonly JsonSerializerOptions StrictJson = new(ExecutionContract.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public QualificationClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false }) { }
    internal QualificationClient(HttpMessageHandler handler)
    { client = new(handler) { BaseAddress = new("http://127.0.0.1:5084/"), Timeout = TimeSpan.FromSeconds(15) }; }
    static HttpRequestMessage Request(HttpMethod method, string suffix, WorkerCommand command, string token)
    {
        ExecutionContract.Validate(command);
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid ephemeral grant.");
        var request = new HttpRequestMessage(method, $"api/worker-qualification/{command.JobId}/{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Library-Plan-Sha256", ExecutionContract.Digest(command)); return request;
    }
    async Task<string> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15)); ct = deadline.Token;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"Qualification API refused request ({(int)response.StatusCode}); no secret or response body is logged.");
        using var input = await response.Content.ReadAsStreamAsync(ct); using var result = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) { if (result.Length + read > 131072) throw new IOException("Oversized qualification response."); result.Write(buffer, 0, read); }
        return Encoding.UTF8.GetString(result.ToArray());
    }
    public async Task<WorkerCommand> Fetch(WorkerCommand approved, string token, CancellationToken ct)
    {
        // Freeze the caller's expected plan before network I/O.
        var expected = ExecutionContract.Digest(approved);
        using var request = Request(HttpMethod.Get, "command", approved, token);
        var returned = JsonSerializer.Deserialize<WorkerCommand>(await Send(request, ct), StrictJson) ?? throw new IOException("Missing command.");
        ExecutionContract.Validate(returned);
        if (ExecutionContract.Digest(returned) != expected) throw new IOException("Server command differs from the offline-approved plan.");
        return returned;
    }
    public async Task<string> Submit(WorkerCommand command, WorkerResult result, string token, CancellationToken ct)
    {
        ExecutionContract.Match(command, result, allowSynthetic: true);
        if (result.Validation.Kind != "synthetic") throw new IOException("This client is restricted to synthetic qualification.");
        using var request = Request(HttpMethod.Post, "result", command, token);
        request.Content = new StringContent(ExecutionContract.Serialize(result), Encoding.UTF8, "application/json");
        return await Send(request, ct);
    }
    public async Task<WorkerResult> Run(WorkerCommand approved, string token, IMediaExecutor worker, CancellationToken ct)
    {
        var command = await Fetch(approved, token, ct);
        var result = await worker.ExecuteAsync(command, ct);
        await Submit(command, result, token, ct); return result;
    }
    public void Dispose() => client.Dispose();
}
