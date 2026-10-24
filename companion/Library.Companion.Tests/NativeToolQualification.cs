using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Library.Execution;

namespace Library.Companion.Tests;

// Qualification-only adapter for the one previously invented 803603-byte clip.
// It checks native demux/probe through bounded pipes, then makes a byte-identical
// staged fixture copy. It is NOT a production encoder/remux adapter.
public sealed class NativeToolQualification : IStagedMediaProcessor, IDisposable
{
    public const string FixtureHash="5b47da5641e1c960e56282fa26bb0799852b64dd35f7ba9f50a42a5106df34f1";
    public const string Detail="Synthetic fixture; not media verification.";
    readonly Scope toolsScope; readonly FileStream ffmpegHandle,ffprobeHandle;
    readonly string ffmpeg,ffprobe;
    public int ProcessRuns {get;private set;}
    public string Qualification=>"synthetic";
    public NativeToolQualification(string toolsRoot)
    {
        toolsScope=new(toolsRoot); ffmpegHandle=toolsScope.Read("bin/ffmpeg.exe"); ffprobeHandle=toolsScope.Read("bin/ffprobe.exe");
        if(Convert.ToHexStringLower(SHA256.HashData(ffmpegHandle))!="3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec" || Convert.ToHexStringLower(SHA256.HashData(ffprobeHandle))!="f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52") throw new IOException("Native tool differs from the approved installed binary.");
        ffmpeg=toolsScope.Resolve("bin/ffmpeg.exe"); ffprobe=toolsScope.Resolve("bin/ffprobe.exe");
    }
    public async Task<JsonElement> Probe(Stream input,CancellationToken ct)
    {
        input.Position=0;
        var json=await Run(ffprobe,["-v","error","-protocol_whitelist","file,pipe","-format_whitelist","matroska,webm","-show_streams","-show_format","-count_packets","-show_data_hash","sha256","-of","json","pipe:0"],input,ct);
        using var document=JsonDocument.Parse(json); input.Position=0; return document.RootElement.Clone();
    }
    public async Task CheckSourceAsync(Stream input,SamplePlan plan,CancellationToken ct)
    {
        if(input.Length!=803603 || plan.Encoding!=null) throw new IOException("Only the approved invented fixture and copy qualification are accepted.");
        input.Position=0; if(Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))!=FixtureHash) throw new IOException("Not the approved synthetic fixture.");
        var probe=await Probe(input,ct);
        if(ExecutionContract.Serialize(DerivedMedia.Select(probe,plan.Request))!=ExecutionContract.Serialize(plan.Streams) || DerivedMedia.ProbeSignature(probe)!=plan.ProbeSignature) throw new IOException("Reviewed synthetic inventory changed.");
    }
    public async Task ProcessAsync(Stream input,Stream output,SamplePlan plan,CancellationToken ct)
    {
        ProcessRuns++; input.Position=0;
        // Read/packet-copy validation to null only: no encoding or media output.
        await Run(ffmpeg,["-nostdin","-hide_banner","-v","error","-xerror","-threads","1","-protocol_whitelist","file,pipe","-format_whitelist","matroska,webm","-i","pipe:0","-map","0","-c","copy","-f","null","-"],input,ct);
        input.Position=0; await input.CopyToAsync(output,65536,ct);
    }
    public async Task<WorkerValidation> ValidateAsync(Stream input,Stream output,SamplePlan plan,CancellationToken ct)
    {
        input.Position=0; output.Position=0;
        var original=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct));
        var copied=Convert.ToHexStringLower(await SHA256.HashDataAsync(output,ct));
        if(original!=FixtureHash || copied!=original || output.Length!=803603) throw new IOException("Synthetic copy changed bytes.");
        var probe=await Probe(output,ct);
        if(DerivedMedia.ProbeSignature(probe)!=plan.ProbeSignature) throw new IOException("Synthetic output inventory changed.");
        return new("synthetic",Detail);
    }
    static async Task<string> Run(string executable,string[] args,Stream input,CancellationToken caller)
    {
        if(input.Length>1048576) throw new IOException("Synthetic pipe input exceeds 1 MiB.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(TimeSpan.FromSeconds(15)); var ct=deadline.Token;
        using var process=new Process { StartInfo=new(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true} };
        foreach(var arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start()) throw new IOException("Cannot start approved native tool.");
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        async Task<byte[]> Read(Stream stream,int limit) { using var bytes=new MemoryStream();var buffer=new byte[8192];int read;while((read=await stream.ReadAsync(buffer,ct))>0){if(bytes.Length+read>limit){deadline.Cancel();throw new IOException("Native tool output limit exceeded.");} bytes.Write(buffer,0,read);}return bytes.ToArray(); }
        async Task Feed(){try{await input.CopyToAsync(process.StandardInput.BaseStream,65536,ct);}finally{process.StandardInput.Close();}}
        try
        {
            var stdout=Read(process.StandardOutput.BaseStream,1048576);var stderr=Read(process.StandardError.BaseStream,65536);
            await Task.WhenAll(Feed(),stdout,stderr,process.WaitForExitAsync(ct));
            if(process.ExitCode!=0 || stderr.Result.Length!=0)throw new IOException($"Native synthetic tool check failed (exit {process.ExitCode}); no secret or media payload logged.");
            return System.Text.Encoding.UTF8.GetString(stdout.Result);
        }
        finally { if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);} }
    }
    public void Dispose(){ffmpegHandle.Dispose();ffprobeHandle.Dispose();toolsScope.Dispose();}
}
