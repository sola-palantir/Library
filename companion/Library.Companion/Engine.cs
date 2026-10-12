using System.Text.Json;
namespace Library.Companion;

public sealed class Engine(CompanionConfig config, IOpticalDevice optical)
{
    public Evidence Execute(string operation, Command command)
    {
        config.Validate(requireStaging: operation != "verify");
        Contract.Id(command.CommandId); Contract.Id(command.DiscId); Contract.Digest(command.ManifestSha256);
        if (command.ReserveBytes < 0) throw new InvalidDataException("Reserve cannot be negative.");
        using var journal = new Journal(config.StateRoot);
        var requestHash = Contract.Hash(JsonSerializer.SerializeToUtf8Bytes(new { operation, command }, Contract.Json));
        var history = journal.Events(command.DiscId);
        var duplicate = history.LastOrDefault(e => e.CommandId == command.CommandId);
        if (duplicate != null)
        {
            if (duplicate.RequestSha256 != requestHash) throw new InvalidOperationException("Command ID was already used with different arguments.");
            if (duplicate.State is "writing" or "finalizing" or "verifying" or "planned")
            { var interrupted = duplicate with { State = "interrupted", Sequence = history.Count + 1, AtUtc = DateTime.UtcNow, Detail = "Previous command did not finish. Verify existing disc or retire its ID; never replay a write." }; journal.Append(interrupted); return interrupted; }
            return duplicate; // Lost completion response: return durable result, never replay.
        }
        var boundManifestSha256 = history.FirstOrDefault()?.ManifestSha256 ?? command.ManifestSha256;
        Evidence Event(string state, string? detail = null, string? imageHash = null, long? bytes = null, long? free = null, string? root = null, bool matched = false, bool files = false)
        {
            var e = new Evidence(1, command.CommandId, command.DiscId, boundManifestSha256, command.RecorderId, state, DateTime.UtcNow, imageHash, bytes, free, detail, optical.Simulated, optical.Simulated ? "simulation" : "hardware", root, matched, files, requestHash, journal.Events(command.DiscId).Count + 1);
            journal.Append(e); return e;
        }
        var staged = history.LastOrDefault(e => e.State == "staged");
        if (operation == "verify") Event("verifying", imageHash: staged?.ImageSha256, bytes: staged?.ImageBytes);
        try
        {
            if (history.Any(e => e.ManifestSha256 != command.ManifestSha256)) throw new InvalidOperationException("Disc identity is permanently bound to another manifest.");
            using var staging = new Scope(Path.Combine(config.StagingRoot, command.DiscId));
            using var manifestStream = staging.Read(Contract.ManifestName);
            if (manifestStream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
            using var memory = new MemoryStream(); manifestStream.CopyTo(memory); var manifestBytes = memory.ToArray();
            if (!Contract.Hash(manifestBytes).Equals(command.ManifestSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Stale manifest.");
            var manifest = JsonSerializer.Deserialize<FrozenManifest>(manifestBytes, Contract.Json) ?? throw new InvalidDataException("Missing manifest."); Contract.Validate(manifest);
            if (manifest.DiscId != command.DiscId) throw new InvalidDataException("Wrong disc manifest.");
            var imageName = command.DiscId + ".udf.iso";
            if (operation == "prepare")
            {
                if (staged != null || history.Count != 0) throw new InvalidOperationException("This permanent disc ID has already been prepared or attempted. Inspect its history; do not replace its image.");
                Event("planned");
                var locks = new List<FileStream>();
                try
                {
                    foreach (var file in manifest.Files) { var f = staging.Read(file.Path); locks.Add(f); if (f.Length != file.Bytes || Contract.Hash(f) != file.Sha256.ToLowerInvariant()) throw new InvalidDataException("Staged file does not match frozen manifest: " + file.Path); }
                    using var image = new FileStream(journal.PathFor(imageName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    optical.BuildImage(manifest, staging.Root, manifestBytes, image); image.Flush(true);
                    if (image.Length <= 0 || image.Length % 2048 != 0) throw new InvalidDataException("Invalid final image block length.");
                    image.Position = 0; return Event("staged", imageHash: Contract.Hash(image), bytes: image.Length);
                }
                catch (Exception e) { Event("failed", e.Message); throw; }
                finally { foreach (var f in locks) f.Dispose(); }
            }
            if (staged == null) throw new InvalidOperationException("Prepare the frozen image first.");
            if (operation == "report-external")
            {
                if (!history.Any(e => e.State == "writing" && e.Detail == "External BurnAware attempt authorized; writing outcome unknown.")) throw new InvalidOperationException("No external attempt is pending.");
                return Event("written-unverified", "User reported BurnAware completion; full optical readback still required.", staged.ImageSha256, staged.ImageBytes);
            }
            if (operation is "burn" or "fallback")
            {
                if (history.Any(e => e.State is "writing" or "finalizing" or "written-unverified" or "verified" or "interrupted")) throw new InvalidOperationException("Physical disc has a write attempt. Reconcile by verifying or retire it; replacements need new IDs and labels.");
                using var deviceLease = optical.Acquire(command.RecorderId);
                using var image = journal.Read(imageName);
                if (image.Length != staged.ImageBytes || Contract.Hash(image) != staged.ImageSha256) throw new InvalidDataException("Frozen image changed.");
                var media = optical.Inspect(command.RecorderId); Contract.Fit(media, image.Length, command.ReserveBytes);
                if (operation == "fallback") return Event("writing", "External BurnAware attempt authorized; writing outcome unknown.", staged.ImageSha256, image.Length, media.FreeSectors);
                Event("writing", imageHash: staged.ImageSha256, bytes: image.Length, free: media.FreeSectors);
                try
                {
                    // The tray/exclusive lock remains held from measurement through write/finalization.
                    optical.WriteImage(command.RecorderId, image, state => { if (state == "finalizing") Event(state, imageHash: staged.ImageSha256, bytes: image.Length, free: media.FreeSectors); });
                    return Event("written-unverified", "IMAPI returned success; eject/reinsert and full optical readback required.", staged.ImageSha256, image.Length, media.FreeSectors);
                }
                catch (Exception e) { Event("interrupted", "Write outcome uncertain: " + e.Message, staged.ImageSha256, image.Length, media.FreeSectors); throw; }
            }
            if (operation == "verify")
            {
                using var deviceLease = optical.Acquire(command.RecorderId); var root = optical.OpticalRoot(command.RecorderId, manifest.Label);
                // Native adapter rejects ordinary folders and verifies recorder -> CD-ROM volume mapping.
                using var readback = new Scope(root, opticalRoot: true);
                using var onDisc = readback.Read(Contract.ManifestName);
                if (onDisc.Length != manifestBytes.Length || Contract.Hash(onDisc) != command.ManifestSha256.ToLowerInvariant()) throw new InvalidDataException("Wrong physical disc or embedded manifest mismatch.");
                foreach (var f in manifest.Files) { using var input = readback.Read(f.Path); if (input.Length != f.Bytes || Contract.Hash(input) != f.Sha256.ToLowerInvariant()) throw new InvalidDataException("Readback failed: " + f.Path); }
                return Event("verified", "Embedded physical-disc ID/label and every manifest file read and verified.", staged.ImageSha256, staged.ImageBytes, root: root, matched: true, files: true);
            }
            throw new InvalidOperationException("Unknown operation.");
        }
        catch (Exception e) when (operation == "verify")
        { Event("failed", "Verification preflight/readback failed: " + e.Message, staged?.ImageSha256, staged?.ImageBytes); throw; }
    }
}
