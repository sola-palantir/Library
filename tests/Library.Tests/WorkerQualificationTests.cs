using System.Text.Json;
using Library.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Library.Tests;
public class WorkerQualificationTests
{
    static WorkerCommand Command()
    {
        var observed = new FileObservation(100, 1, "native-fixture-id");
        return new(1, new string('a',32), new("fixture", "invented.mkv", observed, new string('b',64)), "stage", new(new(1,"stage",3,["jpn"],[new(1,"jpn","main")],true,true),1,observed,[new(0,"video","h264","",[])],"fixture","fixture"));
    }
    [Fact] public void GrantIsDisabledByDefaultAndBindsHostJobPlanTokenAndTime()
    {
        var command=Command(); var token=new string('c',64); var now=DateTimeOffset.UtcNow;
        var options=new LibraryOptions { ExecutionBackend=ExecutionMode.Windows, WorkerQualification=new(ExecutionContract.Serialize(command),DerivedMedia.Digest(token),now,now.AddMinutes(10)) };
        int Auth(string header,string job,string plan,DateTimeOffset time,string host="127.0.0.1:5084") => WorkerQualification.Authorize(options,host,header,job,plan,time);
        var digest=ExecutionContract.Digest(command);
        Assert.Equal(200,Auth("Bearer "+token,command.JobId,digest,now));
        Assert.Equal(401,Auth("",command.JobId,digest,now));
        Assert.Equal(401,Auth("Bearer "+new string('d',64),command.JobId,digest,now));
        Assert.Equal(401,Auth("Bearer "+token,command.JobId,digest,now.AddMinutes(10)));
        Assert.Equal(401,Auth("Bearer "+token,command.JobId,digest,now.AddTicks(-1)));
        Assert.Equal(403,Auth("Bearer "+token,new string('e',32),digest,now));
        Assert.Equal(403,Auth("Bearer "+token,command.JobId,new string('f',64),now));
        Assert.Equal(404,Auth("Bearer "+token,command.JobId,digest,now,"localhost:5084"));
        options.WorkerQualification=options.WorkerQualification with { ExpiresUtc=now.AddMinutes(11) };
        Assert.Equal(404,Auth("Bearer "+token,command.JobId,digest,now));
        options.WorkerQualification=null;
        Assert.Equal(404,Auth("Bearer "+token,command.JobId,digest,now));
    }
    [Fact] public async Task ReceiptReconciliationIsIdempotentAndCannotPromoteSyntheticMedia()
    {
        using var connection=new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db=new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options); db.Database.EnsureCreated();
        var command=Command(); var now=DateTimeOffset.UtcNow; var grant=new WorkerQualificationGrant(ExecutionContract.Serialize(command),new string('a',64),now,now.AddMinutes(10));
        await WorkerQualification.Command(db,grant,default); Assert.Empty(db.Jobs);
        var receipt=new WorkerResult(1,command.JobId,ExecutionContract.Digest(command),command.Source,"stage",ExecutionContract.Destination(command),new(100,2,"output"),new string('d',64),new("synthetic",WorkerQualification.ValidationDetail));
        var first=JsonSerializer.SerializeToElement(await WorkerQualification.Accept(db,grant,receipt,default)); Assert.False(first.GetProperty("duplicate").GetBoolean());
        var again=JsonSerializer.SerializeToElement(await WorkerQualification.Accept(db,grant,receipt,default)); Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Single(db.Jobs); Assert.Single(db.History); Assert.Empty(db.Assets); Assert.Empty(db.Copies); Assert.Empty(db.DerivedAssets);
        await Assert.ThrowsAsync<IOException>(()=>WorkerQualification.Accept(db,grant,receipt with { Sha256=new string('e',64) },default));
        await Assert.ThrowsAsync<IOException>(()=>WorkerQualification.Accept(db,grant,receipt with { Validation=new("media-validated",WorkerQualification.ValidationDetail) },default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerQualification.Accept(db,grant,receipt with { CommandSha256=new string('e',64) },default));
        Assert.Single(db.History);
    }
    [Fact] public async Task ExistingCatalogOperationCannotBeReusedForQualification()
    {
        using var connection=new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db=new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options); db.Database.EnsureCreated();
        var c=Command(); db.Jobs.Add(new(){Id=c.JobId,Type="copy"}); db.SaveChanges();
        var now=DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<IOException>(()=>WorkerQualification.Command(db,new(ExecutionContract.Serialize(c),new string('c',64),now,now.AddMinutes(10)),default));
        Assert.Equal("copy",db.Jobs.Single().Type);
    }
    [Fact] public void AppSettingsRoundTripPreservesTheExactOfflineCommandDigest()
    {
        var command=Command();var now=DateTimeOffset.UtcNow;
        var json=JsonSerializer.Serialize(new { Library=new LibraryOptions { ExecutionBackend=ExecutionMode.Windows,WorkerQualification=new(ExecutionContract.Serialize(command),new string('c',64),now,now.AddMinutes(10)) } });
        using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration=new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options=configuration.GetSection("Library").Get<LibraryOptions>()!;
        Assert.Equal(ExecutionContract.Digest(command),ExecutionContract.Digest(options.WorkerQualification!.Command));
        Assert.Empty(options.WorkerQualification.Command.Plan.Streams[0].Dispositions);
        Assert.Null(options.WorkerQualification.Command.Plan.Encoding);
    }
}
