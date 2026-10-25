using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Library.Manager;

public record Ready(bool ReadyState, string Version, int Protocol, int DeploymentProtocol, int CatalogSchema, string Mode, bool Paused, int ActiveJobs, bool Production, bool Pairing, string Backend);
public record SnapshotReceipt(int Schema, string Sha256, long Bytes);
public interface IRuntime
{
    void Load(string bundle, Manifest manifest);
    void CreateVolume(string name);
    void Start(Release release);
    void Stop(Release release);
    SnapshotReceipt Snapshot(Release source, Release target, string? expectedHash = null, string? sourceOwner = null);
    Ready Status();
    void Pause();
    void Resume();
}

public sealed class DockerRuntime(Installation installation) : IRuntime
{
    const string Label = "com.library.install";
    readonly HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{installation.Port}"), Timeout = TimeSpan.FromSeconds(10) };
    public static string Run(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Docker CLI failed to start.");
        var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
        if (!process.WaitForExit(600_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException("Docker operation timed out; run recover before any further update."); }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException($"Docker failed ({process.ExitCode}): {stderr.Result}");
        return stdout.Result.Trim();
    }
    static async Task<string> Capture(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
    public void Load(string bundle, Manifest m)
    {
        foreach (var relative in new[] { "manager/Library.Manager.exe", "companion/Library.Companion.exe" })
        {
            var start = new ProcessStartInfo(Path.Combine(bundle, relative)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start) ?? throw new IOException("Packaged component failed to start.");
            var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
            if (!process.WaitForExit(30_000)) { process.Kill(true); throw new IOException("Component version check timed out."); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException("Packaged component failed version check: " + stderr.Result);
            var component = JsonDocument.Parse(stdout.Result).RootElement;
            if (component.GetProperty("version").GetString() != m.Version || component.GetProperty("protocol").GetInt32() != m.Protocol || component.GetProperty("deploymentProtocol").GetInt32() != m.DeploymentProtocol || component.GetProperty("catalogSchema").GetInt32() != m.CatalogSchema || component.GetProperty("production").GetBoolean() || component.GetProperty("pairing").GetBoolean())
                throw new IOException("Packaged component/manifest compatibility mismatch.");
        }
        Run("load", "--input", Path.Combine(bundle, m.ImageFile));
        if (Run("image", "inspect", "--format", "{{.Id}}", m.ImageId) != m.ImageId) throw new IOException("Loaded image identity mismatch.");
        var version = JsonDocument.Parse(Run("run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", m.ImageId, "--deployment", "version")).RootElement;
        if (version.GetProperty("version").GetString() != m.Version || version.GetProperty("catalogSchema").GetInt32() != m.CatalogSchema || version.GetProperty("protocol").GetInt32() != m.Protocol || version.GetProperty("mode").GetString() != m.Mode)
            throw new IOException("Image and release manifest are incompatible.");
    }
    void Own(string kind, string name, string? owner = null)
    {
        var value = Run(kind, "inspect", "--format", kind == "container" ? "{{index .Config.Labels \"com.library.install\"}}" : "{{index .Labels \"com.library.install\"}}", name);
        if (value != (owner ?? installation.Id)) throw new IOException("Refusing an unowned Docker resource.");
    }
    public void CreateVolume(string name)
    {
        // A random name is generated once and journaled before this operation. Existing
        // resources are never adopted; retained volumes are never deleted by this tool.
        if (Run("volume", "ls", "--format", "{{.Name}}").Split('\n').Contains(name)) throw new IOException("Volume collision; existing volume retained.");
        Run("volume", "create", "--label", $"{Label}={installation.Id}", name); Own("volume", name);
    }
    public void Start(Release release)
    {
        Own("volume", release.Volume);
        if (Run("container", "ls", "--all", "--format", "{{.Names}}").Split('\n').Contains(release.Container))
        {
            Own("container", release.Container);
            var container = JsonDocument.Parse(Run("container", "inspect", release.Container)).RootElement[0];
            var bindings = container.GetProperty("HostConfig").GetProperty("PortBindings");
            var mounts = container.GetProperty("Mounts").EnumerateArray().Where(m => m.GetProperty("Type").GetString() != "tmpfs").ToArray();
            if (container.GetProperty("Image").GetString() != release.ImageId || mounts.Length != 1 || mounts[0].GetProperty("Name").GetString() != release.Volume || mounts[0].GetProperty("Destination").GetString() != "/data" ||
                bindings.EnumerateObject().Count() != 1 || !bindings.TryGetProperty("8080/tcp", out var ports) || ports.GetArrayLength() != 1 || ports[0].GetProperty("HostIp").GetString() != "127.0.0.1" || ports[0].GetProperty("HostPort").GetString() != installation.Port.ToString() ||
                !container.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean()) throw new IOException("Container image/storage/binding configuration changed; reconcile manually.");
            Run("start", release.Container); return;
        }
        Run("run", "--detach", "--name", release.Container, "--label", $"{Label}={installation.Id}", "--restart", "no", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp",
            "--publish", $"127.0.0.1:{installation.Port}:8080", "--mount", $"type=volume,src={release.Volume},dst=/data",
            "--env", "Library__DataPath=/data", "--env", "Library__ExecutionBackend=windows-worker", "--env", "LIBRARY_MANAGED_START_PAUSED=1", release.ImageId);
    }
    public void Stop(Release release)
    {
        if (!Run("container", "ls", "--all", "--format", "{{.Names}}").Split('\n').Contains(release.Container)) return;
        Own("container", release.Container); Run("stop", "--time", "30", release.Container);
    }
    public SnapshotReceipt Snapshot(Release source, Release target, string? expectedHash = null, string? sourceOwner = null)
    {
        Own("volume", source.Volume, sourceOwner); Own("volume", target.Volume);
        if (Run("container", "ls", "--filter", $"volume={source.Volume}", "--format", "{{.ID}}").Length != 0)
            throw new IOException("Snapshot source volume is in use by a running container.");
        var arguments = new List<string> { "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp",
            "--mount", $"type=volume,src={source.Volume},dst=/source,readonly", "--mount", $"type=volume,src={target.Volume},dst=/data",
            source.ImageId, "--deployment", "snapshot", "/source", "/data" };
        if (expectedHash != null) arguments.Add(expectedHash);
        var result = Run(arguments.ToArray());
        var receipt = JsonDocument.Parse(result).RootElement;
        if (!Bundle.Digest(receipt.GetProperty("sha256").GetString()!) || receipt.GetProperty("schema").GetInt32() != source.Schema) throw new IOException("Snapshot verification failed.");
        return new(source.Schema, receipt.GetProperty("sha256").GetString()!, receipt.GetProperty("bytes").GetInt64());
    }
    public Ready Status()
    {
        var root = JsonDocument.Parse(client.GetStringAsync("/api/deployment/readiness").GetAwaiter().GetResult()).RootElement;
        return new(root.GetProperty("ready").GetBoolean(), root.GetProperty("version").GetString()!, root.GetProperty("protocol").GetInt32(), root.GetProperty("deploymentProtocol").GetInt32(), root.GetProperty("catalogSchema").GetInt32(), root.GetProperty("mode").GetString()!, root.GetProperty("paused").GetBoolean(), root.GetProperty("activeJobs").GetInt32(), root.GetProperty("production").GetBoolean(), root.GetProperty("pairing").GetBoolean(), root.GetProperty("backend").GetString()!);
    }
    void Post(string action) { using var response = client.PostAsJsonAsync("/api/deployment/" + action, new { }).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode(); }
    public void Pause() => Post("quiesce");
    public void Resume() => Post("resume");
}
