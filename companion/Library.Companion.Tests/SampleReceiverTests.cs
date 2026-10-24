using System.Text;
using System.Text.Json;
using Library.Companion;
using Xunit;

public sealed class SampleReceiverTests
{
    private static SampleReceiveRequest Request() => new("http://127.0.0.1:5086", new string('1', 32), 4, Contract.Hash(Encoding.UTF8.GetBytes("test")), new string('a', 64));
    private static SampleDescriptor Descriptor(DateTimeOffset now) { var r = Request(); return new(1, r.JobId, 1, 2, r.Bytes, r.Sha256, new string('b', 64), "stream-copy-sample", $"/api/media/samples/{r.JobId}/content", now.AddMinutes(5)); }
    [Theory]
    [InlineData("http://example.com:5086")]
    [InlineData("http://localhost:5086")]
    [InlineData("http://127.0.0.1:5086/path")]
    [InlineData("http://user:secret@127.0.0.1:5086")]
    [InlineData("http://127.0.0.1:5086/?redirect=external")]
    public void ReceiverRefusesNonliteralLoopbackAndAmbiguousOrigins(string origin)
    {
        Assert.Throws<InvalidDataException>(() => SampleReceiver.ValidateRequest(Request() with { BaseUrl = origin }));
    }
    [Fact]
    public void DescriptorCannotRedirectOrChangeTheApprovedAssetAndPlansContainNoToken()
    {
        var now = DateTimeOffset.UtcNow; var r = Request(); var d = Descriptor(now);
        var plan = SampleReceiver.ValidateDescriptor(r, d, now);
        Assert.DoesNotContain(r.BearerToken, JsonSerializer.Serialize(plan)); Assert.DoesNotContain(r.BearerToken, r.ToString());
        foreach (var bad in new[] { d with { JobId = new string('2', 32) }, d with { Sha256 = new string('c', 64) }, d with { Bytes = 5 }, d with { OutputAssetId = 1 }, d with { Kind = "original" }, d with { DownloadPath = "http://example.com/private" }, d with { ExpiresUtc = now }, d with { ExpiresUtc = now.AddMinutes(11) } })
            Assert.Throws<InvalidDataException>(() => SampleReceiver.ValidateDescriptor(r, bad, now));
    }
    [Fact]
    public async Task TransferRequiresExactLengthAndHashBeforePublicationCanBeReached()
    {
        var r = Request(); using var good = new MemoryStream();
        await SampleReceiver.CopyAndVerify(new MemoryStream(Encoding.UTF8.GetBytes("test")), good, r.Bytes, r.Sha256, CancellationToken.None);
        Assert.Equal("test", Encoding.UTF8.GetString(good.ToArray()));
        foreach (var text in new[] { "tes", "tests", "bad!" })
        {
            using var partial = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleReceiver.CopyAndVerify(new MemoryStream(Encoding.UTF8.GetBytes(text)), partial, r.Bytes, r.Sha256, CancellationToken.None));
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SampleReceiver.CopyAndVerify(new MemoryStream(Encoding.UTF8.GetBytes("test")), new MemoryStream(), r.Bytes, r.Sha256, stop.Token));
    }
}
