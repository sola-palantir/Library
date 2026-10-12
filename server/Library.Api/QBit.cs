using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public sealed class IntegrationDocument
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public int Revision { get; set; }
}
public record QBitPathMapping(string RemotePrefix, string RootId, string RelativePrefix = "");
public record QBitConfiguration(string Mode, string BaseUrl, string Category, QBitPathMapping[] Mappings, int Revision = 0)
{
    public static QBitConfiguration Disabled => new("disabled", "", "", []);
}
public record QBitFile(string TorrentHash, int Index, string Name, long Bytes, double Progress, string TorrentState, long? CompletedAtUnix, long? SeedingSeconds, string? RootId, string? RelativePath, string Readiness, string Reason);
public record QBitSnapshot(string State, string Message, string? ApplicationVersion, string? ApiVersion, DateTime? ObservedUtc, QBitFile[] Files, bool Synthetic);
public record CompletionEvidence(string State, string Message, int ConfigurationRevision, bool Synthetic, DateTime? ObservedUtc);
public interface IQBitCopyGuard
{
    Task<CompletionEvidence> CheckAsync(PhysicalCopy copy, int? expectedRevision, CancellationToken ct);
}
public static partial class QBitPaths
{
    [GeneratedRegex("^[A-Za-z]:/")] private static partial Regex DrivePrefix();
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (!normalized.StartsWith('/') && !DrivePrefix().IsMatch(normalized)) throw new ArgumentException("qBittorrent mappings need absolute remote paths.");
        if (normalized.Contains('\0') || normalized.Split('/').Any(x => x is "." or "..")) throw new ArgumentException("Remote paths cannot contain traversal components.");
        return normalized;
    }
    public static (string? Root, string? Path, string? Error) Map(string savePath, string name, QBitPathMapping[] mappings)
    {
        try
        {
            var relative = name.Replace('\\', '/'); Storage.Components(relative);
            var full = Normalize(savePath) + "/" + relative;
            var matches = mappings.Where(m => full.StartsWith(Normalize(m.RemotePrefix) + "/", Windows(m.RemotePrefix) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).OrderByDescending(m => Normalize(m.RemotePrefix).Length).ToArray();
            if (matches.Length == 0) return (null, null, "No explicit source-root mapping covers this file.");
            var selected = matches[0];
            if (matches.Length > 1 && Normalize(matches[1].RemotePrefix).Length == Normalize(selected.RemotePrefix).Length) return (null, null, "Ambiguous remote path mappings.");
            var path = full[(Normalize(selected.RemotePrefix).Length + 1)..];
            if (selected.RelativePrefix.Length > 0) path = selected.RelativePrefix + "/" + path;
            Storage.Components(path); return (selected.RootId, path, null);
        }
        catch (ArgumentException ex) { return (null, null, ex.Message); }
    }
    private static bool Windows(string path) => DrivePrefix().IsMatch(path.Replace('\\', '/')) || path.StartsWith("\\\\") || path.StartsWith("//");
    public static void Validate(QBitConfiguration config, Storage storage)
    {
        if (config.Mode is not ("disabled" or "synthetic" or "live")) throw new ArgumentException("Choose disabled, synthetic or live mode.");
        if (config.Mode == "live" && (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)) throw new ArgumentException("Use an HTTP(S) WebUI base URL without credentials, query or fragment.");
        if (config.Mappings.Length > 20 || config.Category.Length > 200) throw new ArgumentException("Use at most 20 mappings and a category of at most 200 characters.");
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in config.Mappings)
        {
            var prefix = Normalize(mapping.RemotePrefix);
            if (!prefixes.Add(prefix)) throw new ArgumentException("Duplicate remote path mapping.");
            if (storage.Root(mapping.RootId).Role != "source") throw new ArgumentException("qBittorrent paths may map only to read-only source roots.");
            if (mapping.RelativePrefix.Length > 0) Storage.Components(mapping.RelativePrefix);
        }
        if (config.Mode != "disabled" && config.Mappings.Length == 0) throw new ArgumentException("Add at least one explicit source-root mapping.");
    }
}

// No arbitrary endpoint method: the only POST authenticates a session. All torrent
// requests are a fixed GET allowlist. No torrent mutation operation exists here.
public sealed class QBitReadOnlyAdapter(HttpClient client)
{
    private static readonly HashSet<string> ReadableStates = ["uploading", "stalledUP", "queuedUP", "forcedUP", "pausedUP", "stoppedUP", "downloading", "stalledDL", "queuedDL", "forcedDL", "pausedDL", "stoppedDL"];
    public async Task<QBitSnapshot> ReadAsync(QBitConfiguration config, string username, string password, CancellationToken ct = default)
    {
        if (client.BaseAddress == null) throw new ArgumentException("qBittorrent base URL is missing.");
        using (var login = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login"))
        {
            login.Headers.Referrer = client.BaseAddress;
            login.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password });
            using var response = await client.SendAsync(login, ct);
            if (response.StatusCode != HttpStatusCode.OK || (await response.Content.ReadAsStringAsync(ct)).Trim() != "Ok.") throw new InvalidOperationException("qBittorrent authentication failed. Check credentials and WebUI access; no retry was attempted.");
        }
        var application = (await Get("app/version", ct)).Trim();
        var api = (await Get("app/webapiVersion", ct)).Trim();
        ValidateVersion(application, api);
        var query = "torrents/info?limit=201" + (config.Category.Length > 0 ? "&category=" + Uri.EscapeDataString(config.Category) : "");
        using var torrents = JsonDocument.Parse(await Get(query, ct));
        if (torrents.RootElement.ValueKind != JsonValueKind.Array || torrents.RootElement.GetArrayLength() > 200) throw new InvalidOperationException("Snapshot exceeds the 200-torrent limit. Narrow the configured category; no partial result is trusted.");
        var output = new List<QBitFile>();
        foreach (var torrent in torrents.RootElement.EnumerateArray())
        {
            var hash = Text(torrent, "hash");
            if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit)) throw new InvalidOperationException("qBittorrent returned an invalid torrent identifier.");
            var state = Text(torrent, "state"); var savePath = Text(torrent, "save_path");
            using var files = JsonDocument.Parse(await Get("torrents/files?hash=" + Uri.EscapeDataString(hash), ct));
            if (files.RootElement.ValueKind != JsonValueKind.Array || files.RootElement.GetArrayLength() == 0) throw new InvalidOperationException("Torrent contents were unavailable; the snapshot is incomplete.");
            foreach (var file in files.RootElement.EnumerateArray())
            {
                if (output.Count >= 20_000) throw new InvalidOperationException("Snapshot exceeds 20,000 files. Narrow the configured category.");
                var index = file.GetProperty("index").GetInt32(); var name = Text(file, "name"); var size = file.GetProperty("size").GetInt64(); var progress = file.GetProperty("progress").GetDouble();
                if (index < 0 || size < 0 || !double.IsFinite(progress) || progress < 0 || progress > 1) throw new InvalidOperationException("qBittorrent returned invalid file completion data.");
                var mapped = QBitPaths.Map(savePath, name, config.Mappings);
                var readiness = mapped.Error != null ? "unmapped" : !ReadableStates.Contains(state) ? "blocked" : progress == 1 ? "complete" : "incomplete";
                var reason = mapped.Error ?? (readiness == "complete" ? "File is reported complete; copying still requires filesystem and SHA-256 verification." : readiness == "incomplete" ? "This file is not fully downloaded." : "Torrent is moving, checking, unavailable or in an unrecognized state.");
                output.Add(new(hash, index, name, size, progress, state, OptionalLong(torrent, "completion_on") is > 0 ? OptionalLong(torrent, "completion_on") : null, OptionalLong(torrent, "seeding_time"), mapped.Root, mapped.Path, readiness, reason));
            }
        }
        return new("supported", "Read-only snapshot; completion is observed per file, not inferred from torrent-wide progress.", application, api, DateTime.UtcNow, output.ToArray(), false);
    }
    public static void ValidateVersion(string application, string api)
    {
        if (!Version.TryParse(application.TrimStart('v').Split('-')[0], out var appVersion) || !(appVersion.Major == 5 || appVersion.Major == 4 && appVersion.Minor >= 5) || !Version.TryParse(api, out var apiVersion) || apiVersion.Major != 2 || apiVersion < new Version(2, 8, 2)) throw new InvalidOperationException("Unsupported qBittorrent version. This adapter targets application 4.5+/5.x with Web API 2.8.2–2.x; other versions are refused.");
    }
    private async Task<string> Get(string endpoint, CancellationToken ct)
    {
        if (!(endpoint is "app/version" or "app/webapiVersion" || endpoint.StartsWith("torrents/info?") || endpoint.StartsWith("torrents/files?"))) throw new InvalidOperationException("Endpoint is outside the read-only allowlist.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/" + endpoint); request.Headers.Referrer = client.BaseAddress;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"qBittorrent read failed (HTTP {(int)response.StatusCode}). No torrent was changed.");
        // Cap response size even when Content-Length is absent.
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (output.Length + read > 8_000_000) throw new InvalidOperationException("qBittorrent response exceeds 8 MB."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString() ?? throw new InvalidOperationException("Missing qBittorrent field: " + key);
    private static long? OptionalLong(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
public sealed class SyntheticQBitHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/login" => "Ok.",
            "/api/v2/app/version" => "v5.0.4",
            "/api/v2/app/webapiVersion" => "2.11.0",
            "/api/v2/torrents/info" => "[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"Synthetic demonstration\",\"state\":\"downloading\",\"save_path\":\"X:/LibraryFixture/source\",\"progress\":0.6,\"completion_on\":-1,\"seeding_time\":0}]",
            "/api/v2/torrents/files" => "[{\"index\":0,\"name\":\"Northlight.2024.1080p.mkv\",\"size\":384000,\"progress\":1},{\"index\":1,\"name\":\"Paper.Moons.2021.mkv\",\"size\":514800,\"progress\":0.42}]",
            _ => throw new InvalidOperationException("Unexpected synthetic qBittorrent endpoint.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }
}
public sealed class QBitIntegration(IDbContextFactory<Catalog> factory, Storage storage) : IQBitCopyGuard
{
    private readonly SemaphoreSlim gate = new(1);
    private QBitSnapshot? last;
    public static bool CredentialsAvailable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBRARY_QBIT_USERNAME")) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBRARY_QBIT_PASSWORD"));
    public async Task<QBitConfiguration> Configuration(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var row = await db.Integrations.SingleOrDefaultAsync(x => x.Id == "qbit", ct);
        return row == null ? QBitConfiguration.Disabled : JobRunner.Decode<QBitConfiguration>(row.Payload) with { Revision = row.Revision };
    }
    public async Task Save(QBitConfiguration config, CancellationToken ct)
    {
        QBitPaths.Validate(config, storage);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct); var row = await db.Integrations.SingleOrDefaultAsync(x => x.Id == "qbit", ct);
            if ((row?.Revision ?? 0) != config.Revision) throw new ArgumentException("qBittorrent configuration changed. Reload before saving.");
            if (row == null) { row = new() { Id = "qbit" }; db.Integrations.Add(row); }
            row.Revision++; row.Payload = JobRunner.Encode(config with { Revision = row.Revision }); last = null;
            db.Audit("qbit-configuration", $"Mode {config.Mode}; revision {row.Revision}; no credentials stored and no connection attempted."); await db.SaveChangesAsync(ct);
        }
        finally { gate.Release(); }
    }
    public async Task<object> Status(CancellationToken ct = default)
    {
        var config = await Configuration(ct);
        return new { configuration = config, credentialsAvailable = CredentialsAvailable, snapshot = last ?? Empty(config), plex = new { state = "deferred", message = "Plex naming is implemented. Server status and library refresh are not connected or implemented." } };
    }
    private static QBitSnapshot Empty(QBitConfiguration config) => new(config.Mode == "disabled" ? "disabled" : config.Mode == "live" && !CredentialsAvailable ? "unconfigured" : "not-checked", config.Mode == "disabled" ? "Manual source workflow. Torrent completion has not been checked." : config.Mode == "live" && !CredentialsAvailable ? "Set LIBRARY_QBIT_USERNAME and LIBRARY_QBIT_PASSWORD outside the UI before an explicit read-only check." : "Configuration saved; no connection has been made. Refresh explicitly.", null, null, null, [], config.Mode == "synthetic");
    public async Task<QBitSnapshot> Refresh(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var config = await Configuration(ct);
            if (config.Mode == "disabled" || config.Mode == "live" && !CredentialsAvailable) return last = Empty(config);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new HttpClient(config.Mode == "synthetic" ? new SyntheticQBitHandler() : new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(config.Mode == "synthetic" ? "https://synthetic.invalid/" : config.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                var snapshot = await new QBitReadOnlyAdapter(client).ReadAsync(config, config.Mode == "synthetic" ? "synthetic" : Environment.GetEnvironmentVariable("LIBRARY_QBIT_USERNAME")!, config.Mode == "synthetic" ? "synthetic" : Environment.GetEnvironmentVariable("LIBRARY_QBIT_PASSWORD")!, timeout.Token);
                return last = snapshot with { Synthetic = config.Mode == "synthetic", State = config.Mode == "synthetic" ? "synthetic" : snapshot.State };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            {
                ct.ThrowIfCancellationRequested();
                return last = new("unavailable", ex is InvalidOperationException ? ex.Message : "qBittorrent could not provide a valid complete snapshot. Check configuration and connectivity; no torrent was changed.", null, null, DateTime.UtcNow, [], config.Mode == "synthetic");
            }
        }
        finally { gate.Release(); }
    }
    public async Task<CompletionEvidence> CheckAsync(PhysicalCopy copy, int? expectedRevision, CancellationToken ct)
    {
        var config = await Configuration(ct);
        if (expectedRevision != null && config.Revision != expectedRevision) throw new IOException("qBittorrent configuration changed after preview. Preview again.");
        if (copy.Role != "source" || config.Mode == "disabled" || !config.Mappings.Any(x => x.RootId == copy.RootId)) return new("manual", "Torrent completion is not checked for this source. Confirm the file is complete before approval.", config.Revision, false, null);
        var snapshot = await Refresh(ct);
        if ((await Configuration(ct)).Revision != config.Revision) throw new IOException("qBittorrent configuration changed during the check.");
        if (snapshot.State is not ("supported" or "synthetic")) throw new IOException(snapshot.Message);
        var matches = snapshot.Files.Where(x => x.RootId == copy.RootId && string.Equals(x.RelativePath, copy.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("Multiple torrent files map to this source path; resolve the ambiguity before copying.");
        if (matches.Length == 0) return new("manual", "Not present in the configured torrent/category snapshot. Manual completion review is required.", config.Revision, snapshot.Synthetic, snapshot.ObservedUtc);
        var match = matches[0];
        if (match.Readiness != "complete" || storage.Observe(copy.RootId, copy.RelativePath).Bytes != match.Bytes) throw new IOException("qBittorrent file is not ready: " + (match.Readiness == "complete" ? "reported size differs from the source." : match.Reason));
        return new("complete", "qBittorrent reports this file complete. This is not a claim that the torrent is actively seeding.", config.Revision, snapshot.Synthetic, snapshot.ObservedUtc);
    }
}
