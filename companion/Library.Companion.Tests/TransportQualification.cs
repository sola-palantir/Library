using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Library.Execution;
namespace Library.Companion.Tests;

public static class TransportQualification
{
    static void Require(bool value,string message){if(!value)throw new IOException(message);}
    static T Read<T>(string path)=>JsonSerializer.Deserialize<T>(File.ReadAllText(path),ExecutionContract.Json)!;
    public static string Prepare(string root,string toolsRoot,string sourceRoot)
    {
        if(File.Exists(Path.Combine(root,"command.json")))return File.ReadAllText(Path.Combine(root,"command.json"));
        if(Directory.Exists(root) && (Directory.EnumerateFiles(root).Any() || Directory.Exists(Path.Combine(root,"stage")) && Directory.EnumerateFileSystemEntries(Path.Combine(root,"stage")).Any() || Directory.Exists(Path.Combine(root,"state")) && Directory.EnumerateFiles(Path.Combine(root,"state")).Any(p=>Path.GetFileName(p)!="media-worker.lock")))throw new IOException("Existing qualification evidence requires review; no new job will be created.");
        Directory.CreateDirectory(root);var state=Path.Combine(root,"state");var stage=Path.Combine(root,"stage");Directory.CreateDirectory(state);Directory.CreateDirectory(stage);
        var config=new WindowsWorkerConfiguration(state,[new("synthetic",sourceRoot,"source"),new("stage",stage,"staging")]);
        var worker=new WindowsMediaWorker(config);var source=worker.InspectAsync("synthetic","sample-e6e77a0e63ad4fada0b646d701889102.mkv",default).GetAwaiter().GetResult();
        Require(source.Sha256==NativeToolQualification.FixtureHash,"Not the approved retained invented clip.");
        using var processor=new NativeToolQualification(toolsRoot);using var scope=new Scope(sourceRoot);using var file=scope.Read(source.RelativePath);
        var probe=processor.Probe(file,default).GetAwaiter().GetResult();
        var request=new SampleRequest(1,"stage",3,["jpn"],[new(1,"jpn","main"),new(2,"eng","descriptive"),new(3,"eng","subtitle"),new(4,"eng","subtitle"),new(5,"eng","subtitle")],true,true);
        var plan=new SamplePlan(request,1,source.Observation,DerivedMedia.Select(probe,request),"ffprobe 9.0.2 native synthetic qualification",DerivedMedia.ProbeSignature(probe));
        var command=new WorkerCommand(1,Guid.NewGuid().ToString("N"),source,"stage",plan);ExecutionContract.Validate(command);
        File.WriteAllText(Path.Combine(root,"command.json"),ExecutionContract.Serialize(command));File.WriteAllText(Path.Combine(root,"worker.json"),ExecutionContract.Serialize(config));
        return ExecutionContract.Serialize(command);
    }
    static async Task<int> Status(HttpMethod method,string suffix,WorkerCommand command,string token,string? job=null,string? digest=null,object? body=null)
    {
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(10)};
        using var request=new HttpRequestMessage(method,$"http://127.0.0.1:5084/api/worker-qualification/{job??command.JobId}/{suffix}");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Add("X-Library-Plan-Sha256",digest??ExecutionContract.Digest(command));
        if(body!=null)request.Content=new StringContent(JsonSerializer.Serialize(body,ExecutionContract.Json),Encoding.UTF8,"application/json");
        using var response=await client.SendAsync(request);return (int)response.StatusCode;
    }
    public static int GrantStatus(string root,string token)=>Status(HttpMethod.Get,"command",Read<WorkerCommand>(Path.Combine(root,"command.json")),token).GetAwaiter().GetResult();
    public static string Run(string root,string toolsRoot,string token)
    {
        var command=Read<WorkerCommand>(Path.Combine(root,"command.json"));var config=Read<WindowsWorkerConfiguration>(Path.Combine(root,"worker.json"));
        var checks=new Dictionary<string,int>();
        checks["wrongToken"]=Status(HttpMethod.Get,"command",command,new string('f',64)).GetAwaiter().GetResult();Require(checks["wrongToken"]==401,"Wrong token accepted.");
        checks["wrongJob"]=Status(HttpMethod.Get,"command",command,token,job:new string('f',32)).GetAwaiter().GetResult();Require(checks["wrongJob"]==403,"Wrong job accepted.");
        checks["wrongPlan"]=Status(HttpMethod.Get,"command",command,token,digest:new string('f',64)).GetAwaiter().GetResult();Require(checks["wrongPlan"]==403,"Wrong plan accepted.");
        using var processor=new NativeToolQualification(toolsRoot);using var client=new QualificationClient();
        var target=config.Roots.Single(r=>r.Role=="staging").Path;var destination=Path.Combine(target,ExecutionContract.Destination(command));
        // Invent an unrelated destination occupant and preserve it as evidence.
        using(var file=new FileStream(destination,FileMode.CreateNew,FileAccess.Write))file.Write(Encoding.UTF8.GetBytes("invented collision occupant"));
        var collided=false;try{client.Run(command,token,new WindowsMediaWorker(config,processor),default).GetAwaiter().GetResult();}catch(IOException e) when(e.Message.Contains("Existing output")){collided=true;}
        Require(collided && processor.ProcessRuns==0 && File.ReadAllText(destination)=="invented collision occupant","Collision was not retained.");
        var collisionEvidence=Path.Combine(target,"collision-native-evidence.bin");File.Move(destination,collisionEvidence,false);
        var interrupted=false;var crashWorker=new WindowsMediaWorker(config,processor,at=>{if(at=="published")throw new IOException("Invented interruption before result submission.");});
        try{client.Run(command,token,crashWorker,default).GetAwaiter().GetResult();}catch(IOException e) when(e.Message.Contains("Invented interruption")){interrupted=true;}
        Require(interrupted && processor.ProcessRuns==1 && File.Exists(destination),"Native publication checkpoint was not reached.");
        // No processor is available on retry; reconciliation must use the exact checkpoint.
        var result=client.Run(command,token,new WindowsMediaWorker(config),default).GetAwaiter().GetResult();
        var duplicate=client.Submit(command,result,token,default).GetAwaiter().GetResult();
        Require(JsonDocument.Parse(duplicate).RootElement.GetProperty("duplicate").GetBoolean(),"Duplicate catalog receipt was not idempotent.");
        checks["receiptConflict"]=Status(HttpMethod.Post,"result",command,token,body:result with{Sha256=new string('e',64)}).GetAwaiter().GetResult();Require(checks["receiptConflict"]==409,"Conflicting receipt accepted.");
        checks["receiptPlanMismatch"]=Status(HttpMethod.Post,"result",command,token,body:result with{CommandSha256=new string('e',64)}).GetAwaiter().GetResult();Require(checks["receiptPlanMismatch"]==409,"Mismatched receipt plan accepted.");
        checks["unknownMediaField"]=Status(HttpMethod.Post,"result",command,token,body:new{mediaBytes="forbidden-field"}).GetAwaiter().GetResult();Require(checks["unknownMediaField"]==409,"Unknown body fields accepted.");
        using var hashFile=File.OpenRead(destination);Require(Convert.ToHexStringLower(SHA256.HashData(hashFile))==NativeToolQualification.FixtureHash,"Published synthetic output changed.");
        var report=ExecutionContract.Serialize(new{command.JobId,result,checks,processor.ProcessRuns,collisionPreserved=true,recoveredWithoutProcessing=true,duplicateReceipt=true,mediaTransferred=false,realMedia=false,encodes=0});
        File.WriteAllText(Path.Combine(root,"result.json"),report);return report;
    }
}
