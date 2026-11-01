using System.Text;
using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;
public class ReviewAccessTests
{
    [Fact] public void ConnectionCannotExpandScopeLifetimeOrExpiry()
    {
        var now=DateTimeOffset.UtcNow; var profile=new ReviewConnectionProfile(Guid.NewGuid().ToString("N"),@"C:\synthetic\samples","http://127.0.0.1:5095",DerivedMedia.Digest(new string('a',64)),now.AddSeconds(-1),now.AddDays(1));
        var bundle=new ReviewAccessBundle(profile,Path.GetFullPath("tools"),new string('a',64));
        var lease=new ReviewConnectionLease(Guid.NewGuid().ToString("N"),profile.RootId,now,now.AddMinutes(59));
        // Configuration path validation is host-native; contract scope is Windows.
        if (OperatingSystem.IsWindows()) ReviewConnectionClient.Configuration(bundle,lease);
        Assert.Throws<IOException>(()=>ReviewConnectionClient.Configuration(bundle,lease with { RootId="wrong" }));
        Assert.Throws<IOException>(()=>ReviewConnectionClient.Configuration(bundle,lease with { ExpiresUtc=now.AddDays(2) }));
        Assert.Throws<ArgumentException>(()=>ReviewConnectionClient.Configuration(bundle,lease with { ExpiresUtc=now.AddHours(2) }));
    }
    [Fact] public void CurrentUserProtectionRoundTripsOnlySyntheticBytesAndRejectsDamage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bytes=Encoding.UTF8.GetBytes("public synthetic test bytes; not a credential");
        var encrypted=ReviewAccess.Protect(bytes); Assert.NotEqual(bytes,encrypted); Assert.Equal(bytes,ReviewAccess.Protect(encrypted,true));
        encrypted[0]^=255; Assert.ThrowsAny<Exception>(()=>ReviewAccess.Protect(encrypted,true));
    }
}
