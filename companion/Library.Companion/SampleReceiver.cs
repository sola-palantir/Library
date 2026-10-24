using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Library.Companion;

public sealed record SampleReceiveRequest(string BaseUrl, string JobId, long Bytes, string Sha256, string BearerToken)
{
    public override string ToString() => "SampleReceiveRequest (credential redacted)";
}
public sealed record SampleDescriptor(int SchemaVersion, string JobId, long SourceAssetId, long OutputAssetId, long Bytes, string Sha256, string LineageSha256, string Kind, string DownloadPath, DateTimeOffset ExpiresUtc);
public sealed record SampleReceivePlan(string Origin, string JobId, long SourceAssetId, long OutputAssetId, long Bytes, string Sha256, string LineageSha256, string FileName);
public sealed record SampleReceiveResult(string JobId, string OutputPath, long Bytes, string Sha256, string FileIdentity, string State, bool Reconciled, bool SampleOnly = true, bool ArchiveEligible = false);

// Manually invoked Windows receiver. No listener, proxy, redirect, pairing or media encoder.
public static class SampleReceiver
{
    public static Uri ValidateRequest(SampleReceiveRequest request)
    {
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) throw new InvalidDataException("Sample handoff requires an explicit HTTP 127.0.0.1 origin with no path, credentials or redirects.");
        if (request.JobId is not { Length: 32 } || !request.JobId.All(char.IsAsciiHexDigit) || request.Bytes is <= 0 or > 268435456) throw new InvalidDataException("Invalid bounded sample approval.");
        Contract.Digest(request.Sha256); Contract.Digest(request.BearerToken);
        return uri;
    }
    public static SampleReceivePlan ValidateDescriptor(SampleReceiveRequest request, SampleDescriptor descriptor, DateTimeOffset now)
    {
        var origin = ValidateRequest(request).GetLeftPart(UriPartial.Authority);
        Contract.Digest(descriptor.LineageSha256);
        if (descriptor.SchemaVersion != 1 || descriptor.JobId != request.JobId || descriptor.Bytes != request.Bytes || descriptor.Sha256 != request.Sha256 || descriptor.Kind != "stream-copy-sample" || descriptor.SourceAssetId <= 0 || descriptor.OutputAssetId <= 0 || descriptor.OutputAssetId == descriptor.SourceAssetId || descriptor.DownloadPath != $"/api/media/samples/{request.JobId}/content" || descriptor.ExpiresUtc <= now || descriptor.ExpiresUtc - now > TimeSpan.FromMinutes(10)) throw new InvalidDataException("Descriptor does not match the bounded offline sample approval.");
        return new(origin, descriptor.JobId, descriptor.SourceAssetId, descriptor.OutputAssetId, descriptor.Bytes, descriptor.Sha256, descriptor.LineageSha256, $"sample-{descriptor.JobId}.mkv");
    }
    public static async Task CopyAndVerify(Stream input, Stream output, long expectedBytes, string expectedHash, CancellationToken ct)
    {
        if (expectedBytes is <= 0 or > 268435456) throw new InvalidDataException("Invalid sample size.");
        Contract.Digest(expectedHash);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > expectedBytes) throw new InvalidDataException("Transfer exceeds the approved sample size; partial retained.");
            hash.AppendData(buffer.AsSpan(0, count)); await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != expectedBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash) throw new InvalidDataException("Transferred sample length or SHA-256 differs; partial retained, publication refused.");
    }
    private static async Task<byte[]> BoundedJson(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Oversized handoff descriptor.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0) { if (output.Length + count > 65536) throw new InvalidDataException("Oversized handoff descriptor."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static void RequireOk(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"Local sample handoff refused (HTTP {(int)response.StatusCode}).");
    }
    private static void Verify(FileStream file, SampleReceivePlan plan)
    {
        file.Position = 0;
        if (file.Length != plan.Bytes || Contract.Hash(file) != plan.Sha256) throw new InvalidDataException("Existing sample or partial differs from its immutable plan; no overwrite or deletion is permitted.");
        WindowsPublication.Identity(file.SafeFileHandle);
    }
    private static void SaveJson<T>(Scope scope, string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json);
        if (File.Exists(scope.Resolve(name)))
        {
            using var existing = scope.Read(name);
            if (existing.Length != bytes.Length || Contract.Hash(existing) != Contract.Hash(bytes)) throw new InvalidDataException("Immutable handoff journal conflict.");
            return;
        }
        var partial = name + ".partial";
        using var file = WindowsPublication.Open(scope, partial, existing: File.Exists(scope.Resolve(partial)));
        if (file.Length != 0)
        {
            if (file.Length != bytes.Length || Contract.Hash(file) != Contract.Hash(bytes)) throw new InvalidDataException("Incomplete handoff journal retained for review.");
        }
        else { file.Write(bytes); file.Flush(true); }
        WindowsPublication.Publish(scope, file, name);
    }
    public static async Task<SampleReceiveResult> Receive(CompanionConfig config, SampleReceiveRequest request, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sample receiving uses the qualified Windows NTFS publisher.");
        var origin = ValidateRequest(request); config.Validate();
        using var destination = new Scope(config.StagingRoot); using var state = new Scope(config.StateRoot);
        var leaseName = "sample-receive.lock";
        using var lease = WindowsPublication.Open(state, leaseName, existing: File.Exists(state.Resolve(leaseName)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { BaseAddress = origin, Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);
        client.DefaultRequestHeaders.Add("X-Library-Sample-Sha256", request.Sha256);
        using var metadata = await client.GetAsync($"/api/media/samples/{request.JobId}/handoff", HttpCompletionOption.ResponseHeadersRead, timeout.Token); RequireOk(metadata);
        var descriptor = JsonSerializer.Deserialize<SampleDescriptor>(await BoundedJson(metadata, timeout.Token), Contract.Json) ?? throw new InvalidDataException("Missing sample descriptor.");
        var plan = ValidateDescriptor(request, descriptor, DateTimeOffset.UtcNow);
        var remaining = descriptor.ExpiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new IOException("Sample grant expired.");
        if (remaining < TimeSpan.FromMinutes(3)) timeout.CancelAfter(remaining);
        var planName = $"sample-{request.JobId}.plan.json";
        var finalPath = destination.Resolve(plan.FileName);
        if ((File.Exists(finalPath) || Directory.Exists(finalPath)) && !File.Exists(state.Resolve(planName))) throw new IOException("Unbound destination collision; existing file retained.");
        SaveJson(state, planName, plan); // Immutable, flushed before any media write; contains no credential.
        var reconciled = File.Exists(finalPath); string identity;
        if (!reconciled)
        {
            if (new DriveInfo(Path.GetPathRoot(finalPath)!).AvailableFreeSpace < plan.Bytes + 64L * 1024 * 1024) throw new IOException("Insufficient NTFS staging space.");
            var partialName = plan.FileName + ".handoff.partial";
            var resumed = File.Exists(destination.Resolve(partialName));
            using var output = WindowsPublication.Open(destination, partialName, existing: resumed);
            if (resumed) Verify(output, plan); // Complete partials only; never truncate or delete an incomplete one.
            else
            {
                using var response = await client.GetAsync(descriptor.DownloadPath, HttpCompletionOption.ResponseHeadersRead, timeout.Token); RequireOk(response);
                if (response.Content.Headers.ContentLength != plan.Bytes) throw new InvalidDataException("Transfer length header differs from approval.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await CopyAndVerify(input, output, plan.Bytes, plan.Sha256, timeout.Token); output.Flush(true); Verify(output, plan);
            }
            timeout.Token.ThrowIfCancellationRequested();
            identity = WindowsPublication.Identity(output.SafeFileHandle);
            WindowsPublication.Publish(destination, output, plan.FileName);
            Verify(output, plan);
        }
        else
        {
            using var known = destination.Read(plan.FileName); Verify(known, plan); identity = WindowsPublication.Identity(known.SafeFileHandle);
        }
        using var published = destination.Read(plan.FileName); Verify(published, plan);
        if (WindowsPublication.Identity(published.SafeFileHandle) != identity) throw new IOException("Published sample identity changed before receipt.");
        var result = new SampleReceiveResult(plan.JobId, finalPath, plan.Bytes, plan.Sha256, identity, "content-verified-sample", false);
        SaveJson(state, $"sample-{request.JobId}.receipt.json", result);
        return result with { Reconciled = reconciled };
    }
}
