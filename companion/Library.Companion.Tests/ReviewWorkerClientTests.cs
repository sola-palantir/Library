using System.Net;
using System.Text;
using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;

public class ReviewWorkerClientTests
{
    static ReviewWorkerConfiguration Configuration() => new("http://127.0.0.1:5090", Guid.NewGuid().ToString("N"), Path.GetFullPath("approved-samples"), Path.GetFullPath("approved-tools"), DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(9));
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request, ct); }
    [Fact] public void CommandCannotExpandLocallyApprovedFolderSessionOrDeadline()
    {
        var configuration = Configuration(); using var client = new ReviewWorkerClient(configuration, new string('a',64), new Handler((_,_) => throw new NotImplementedException()));
        var command = new ReviewScanCommand(Guid.NewGuid().ToString("N"), configuration.SessionId, configuration.RootId, DateTimeOffset.UtcNow.AddMinutes(2));
        client.ValidateCommand(command);
        Assert.Throws<IOException>(() => client.ValidateCommand(command with { RootId = "another-folder" }));
        Assert.Throws<IOException>(() => client.ValidateCommand(command with { SessionId = Guid.NewGuid().ToString("N") }));
        Assert.Throws<IOException>(() => client.ValidateCommand(command with { DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1) }));
        Assert.Throws<ArgumentException>(() => (configuration with { ApiOrigin = "http://evil.example:5090" }).Validate(DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => (configuration with { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) }).Validate(DateTimeOffset.UtcNow));
    }
    [Fact] public async Task CancellationInterruptsAStalledCommandFetch()
    {
        using var handler = new Handler(async (request, ct) => { Assert.Equal("127.0.0.1", request.RequestUri!.Host); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); });
        using var client = new ReviewWorkerClient(Configuration(), new string('a',64), handler); using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await client.Run(cancelled.Token).WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact] public async Task WrongGrantNeverLogsTokenOrServerResponse()
    {
        using var handler = new Handler((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("server body should not be logged", Encoding.UTF8) }));
        var token = new string('a',64); using var client = new ReviewWorkerClient(Configuration(), token, handler);
        var error = await Assert.ThrowsAsync<IOException>(() => client.Run(default));
        Assert.DoesNotContain(token, error.Message); Assert.DoesNotContain("server body", error.Message); Assert.Contains("401", error.Message);
    }
    [Fact] public async Task AStalledFetchHasAHardDeadlineWithoutCallerCancellation()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); });
        using var client = new ReviewWorkerClient(Configuration(), new string('a',64), handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Run(default).WaitAsync(TimeSpan.FromSeconds(7)));
    }
}
