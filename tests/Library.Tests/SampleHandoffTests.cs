using Library.Api;
using Xunit;

namespace Library.Tests;

public sealed class SampleHandoffTests
{
    [Fact]
    public void OfflineGrantIsDisabledByDefaultAndBindsTokenJobHashAndTenMinuteWindow()
    {
        var now = DateTimeOffset.UtcNow; var token = new string('a', 64); var id = new string('1', 32); var hash = new string('b', 64);
        var options = new LibraryOptions { SampleHandoffGrant = new(id, 100, hash, DerivedMedia.Digest(token), now, now.AddMinutes(10)) };
        int Auth(string authorization, string job, string digest, DateTimeOffset at) => SampleHandoff.Authorize(options, authorization, job, digest, at);
        Assert.Equal(404, Auth("Bearer " + token, id, hash, now));
        options.EnableSampleHandoff = true;
        Assert.Equal(200, Auth("Bearer " + token, id, hash, now));
        Assert.Equal(401, Auth("", id, hash, now));
        Assert.Equal(401, Auth("Bearer " + new string('c', 64), id, hash, now));
        Assert.Equal(403, Auth("Bearer " + token, new string('2', 32), hash, now));
        Assert.Equal(403, Auth("Bearer " + token, id, new string('c', 64), now));
        Assert.Equal(401, Auth("Bearer " + token, id, hash, now.AddMinutes(10)));
        Assert.Equal(401, Auth("Bearer " + token, id, hash, now.AddTicks(-1)));
        options.SampleHandoffGrant = options.SampleHandoffGrant with { ExpiresUtc = now.AddMinutes(11) };
        Assert.Equal(404, Auth("Bearer " + token, id, hash, now));
        options.SampleHandoffGrant = options.SampleHandoffGrant with { ExpiresUtc = now.AddMinutes(10), TokenSha256 = "invalid" };
        Assert.Equal(404, Auth("Bearer " + token, id, hash, now));
    }
}
