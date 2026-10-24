using System.Net;
using System.Text;
using Library.Execution;
using Xunit;
namespace Library.Companion.Tests;
public class QualificationClientTests
{
    static WorkerCommand Command()
    {
        var observed=new FileObservation(100,1,"fixture-id");
        return new(1,new string('a',32),new("source","invented.mkv",observed,new string('b',64)),"stage",new(new(1,"stage",3,["jpn"],[new(1,"jpn","main")],true,true),1,observed,[new(0,"video","h264","",[])],"fixture","fixture"));
    }
    [Fact] public async Task ClientRefusesServerPlanSubstitution()
    {
        var command=Command(); using var client=new QualificationClient(new Reply(_=>ExecutionContract.Serialize(command with { TargetRootId="different", Plan=command.Plan with { Request=command.Plan.Request with { TargetRootId="different" } } })));
        await Assert.ThrowsAsync<IOException>(()=>client.Fetch(command,new string('c',64),default));
    }
    [Fact] public async Task ClientUsesOnlyFixedLoopbackAndMetadataWithoutTokenInBody()
    {
        var command=Command(); var token=new string('c',64); string? body=null; Uri? url=null;
        using var client=new QualificationClient(new Reply(r=>{url=r.RequestUri;body=r.Content?.ReadAsStringAsync().Result;return "{}";}));
        var result=new WorkerResult(1,command.JobId,ExecutionContract.Digest(command),command.Source,"stage",ExecutionContract.Destination(command),new(100,2,"new-id"),new string('d',64),new("synthetic",NativeToolQualification.Detail));
        await client.Submit(command,result,token,default);
        Assert.Equal("127.0.0.1",url!.Host); Assert.Equal(5084,url.Port);
        Assert.DoesNotContain(token,body!); Assert.Contains("commandSha256",body!); Assert.DoesNotContain("bearer",body!,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task ClientRefusesRedirectResponse()
    {
        using var client=new QualificationClient(new Reply(_=>"redirect",HttpStatusCode.Redirect));
        await Assert.ThrowsAsync<IOException>(()=>client.Fetch(Command(),new string('c',64),default));
    }
    sealed class Reply(Func<HttpRequestMessage,string> respond,HttpStatusCode status=HttpStatusCode.OK):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(respond(request),Encoding.UTF8,"application/json")});
    }
}
