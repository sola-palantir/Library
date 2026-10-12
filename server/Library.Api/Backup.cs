using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public sealed class BackupSetRecord
{
    public string Id { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
public sealed class BackupPhysicalDisc
{
    public string Id { get; set; } = "";
    public string SetId { get; set; } = "";
    public string Label { get; set; } = "";
    public int Ordinal { get; set; }
    public string ManifestJson { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
public sealed class BackupEvidence
{
    public long Id { get; set; }
    public string DiscId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string State { get; set; } = "planned";
    public string DetailJson { get; set; } = "{}";
    public DateTime AtUtc { get; set; }
}
public record BackupFile(long CopyId, string Path, long Bytes, string Sha256, string FileIdentity, bool Sidecar = false, string? SourceRootId = null, string? SourcePath = null);
public record BackupBundle(string Id, string Title, BackupFile[] Files, int? Episode = null);
public record SeasonScope(string Title, int Season, int[] ExpectedEpisodes, int[] ExcludedEpisodes);
public record BackupRequest(string Name, BackupBundle[] Bundles, long CapacityBytes, long ReserveBytes, string Objective = "minimize-discs", SeasonScope? Season = null, int SearchBudget = 100000);
public record BackupPacking(BackupBundle[][] Discs, string[] UnselectedBundleIds, bool OptimalityProven, int Nodes, string Method);
public record BackupCoverage(int[] Expected, int[] Selected, int[] Missing, int[] Excluded, bool EntireSeasonIncluded);
public record BackupPreview(BackupRequest Request, BackupPacking Packing, BackupCoverage? Coverage, string Sha256);
public record BackupAttemptRequest(string DiscId, string CommandId, string AttemptId, string State, string DetailJson = "{}");

public static class BackupDomain
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static void Configure(ModelBuilder b)
    {
        b.Entity<BackupSetRecord>().ToTable("BackupSets").HasIndex(x => x.CommandId).IsUnique();
        b.Entity<BackupPhysicalDisc>().ToTable("BackupPhysicalDiscs").HasIndex(x => x.Label).IsUnique();
        b.Entity<BackupPhysicalDisc>().HasOne<BackupSetRecord>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupEvidence>().ToTable("BackupEvidence").HasIndex(x => x.CommandId).IsUnique();
        b.Entity<BackupEvidence>().HasOne<BackupPhysicalDisc>().WithMany().HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Restrict);
    }
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS BackupSets (Id TEXT PRIMARY KEY NOT NULL, CommandId TEXT NOT NULL UNIQUE, Name TEXT NOT NULL, SnapshotJson TEXT NOT NULL, Sha256 TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS BackupPhysicalDiscs (Id TEXT PRIMARY KEY NOT NULL, SetId TEXT NOT NULL REFERENCES BackupSets(Id), Label TEXT NOT NULL UNIQUE, Ordinal INTEGER NOT NULL, ManifestJson TEXT NOT NULL, Sha256 TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS BackupEvidence (Id INTEGER PRIMARY KEY AUTOINCREMENT, DiscId TEXT NOT NULL REFERENCES BackupPhysicalDiscs(Id), CommandId TEXT NOT NULL UNIQUE, AttemptId TEXT NOT NULL, State TEXT NOT NULL, DetailJson TEXT NOT NULL, AtUtc TEXT NOT NULL);
        CREATE TRIGGER IF NOT EXISTS BackupSetsImmutableUpdate BEFORE UPDATE ON BackupSets BEGIN SELECT RAISE(ABORT,'Backup snapshots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS BackupSetsImmutableDelete BEFORE DELETE ON BackupSets BEGIN SELECT RAISE(ABORT,'Backup history cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS BackupDiscsImmutableUpdate BEFORE UPDATE ON BackupPhysicalDiscs BEGIN SELECT RAISE(ABORT,'Physical disc manifests are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS BackupDiscsImmutableDelete BEFORE DELETE ON BackupPhysicalDiscs BEGIN SELECT RAISE(ABORT,'Physical disc labels cannot be reused'); END;
        CREATE TRIGGER IF NOT EXISTS BackupEvidenceImmutableUpdate BEFORE UPDATE ON BackupEvidence BEGIN SELECT RAISE(ABORT,'Evidence is append only'); END;
        CREATE TRIGGER IF NOT EXISTS BackupEvidenceImmutableDelete BEFORE DELETE ON BackupEvidence BEGIN SELECT RAISE(ABORT,'Evidence is append only'); END;
        """;
    // 80 random bits with an unambiguous alphabet and a transcription check symbol.
    public static string Label()
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        var bytes = RandomNumberGenerator.GetBytes(10);
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[17]; var check = 0;
        for (var i = 0; i < 17; i++) { chars[i] = alphabet[(int)(value % alphabet.Length)]; value /= alphabet.Length; check = (check * 7 + alphabet.IndexOf(chars[i])) % alphabet.Length; }
        return $"LB-{new string(chars, 0, 5)}-{new string(chars, 5, 6)}-{new string(chars, 11, 6)}-{alphabet[check]}";
    }
    public static BackupPreview Preview(BackupRequest request)
    {
        // Detach caller-owned mutable arrays before freezing a snapshot.
        request = JsonSerializer.Deserialize<BackupRequest>(JsonSerializer.Serialize(request))!;
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.Bundles.Length is < 1 or > 500)
            throw new ArgumentException("Name and 1–500 bundles are required.");
        if (request.CapacityBytes <= 0 || request.ReserveBytes < 0 || request.ReserveBytes >= request.CapacityBytes || request.SearchBudget is < 1 or > 1000000)
            throw new ArgumentException("Invalid capacity, reserve or search budget.");
        if (request.Objective is not ("fill-one" or "minimize-discs")) throw new ArgumentException("Unknown packing objective.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var copies = new HashSet<long>(); var ids = new HashSet<string>();
        foreach (var bundle in request.Bundles)
        {
            if (string.IsNullOrWhiteSpace(bundle.Id) || !ids.Add(bundle.Id) || bundle.Files.Length == 0 || bundle.Files.All(f => f.Sidecar)) throw new ArgumentException("Each unique bundle needs media; selected sidecars stay with it.");
            foreach (var file in bundle.Files)
            {
                Storage.Components(file.Path);
                if ((file.Path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("LIBRARY-MANIFEST.json", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("manifest.json is reserved for disc identity.");
                if (file.Bytes < 0 || file.CopyId <= 0 || (!file.Sidecar && !copies.Add(file.CopyId)) || !paths.Add(file.Path) || string.IsNullOrWhiteSpace(file.FileIdentity) || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                    throw new ArgumentException("Files require unique copies/paths, exact sizes, identities and SHA-256 evidence.");
            }
            if (Size(bundle) > request.CapacityBytes - request.ReserveBytes) throw new ArgumentException($"Bundle {bundle.Id} exceeds payload capacity; no files were split.");
        }
        BackupCoverage? coverage = null;
        if (request.Season is { } season)
        {
            var expected = season.ExpectedEpisodes.Order().ToArray(); var excluded = season.ExcludedEpisodes.Order().ToArray();
            if (season.Season < 0 || expected.Length == 0 || expected.Any(x => x < 1) || expected.Distinct().Count() != expected.Length || excluded.Distinct().Count() != excluded.Length || excluded.Except(expected).Any()) throw new ArgumentException("Declare a unique expected episode scope and exclusions within it.");
            var selected = request.Bundles.Select(x => x.Episode ?? throw new ArgumentException("Every season bundle needs an episode number.")).Order().ToArray();
            if (selected.Distinct().Count() != selected.Length || selected.Except(expected).Any() || selected.Intersect(excluded).Any()) throw new ArgumentException("Selected episodes must be unique, in scope and not excluded.");
            var missing = expected.Except(selected).Except(excluded).ToArray();
            coverage = new(expected, selected, missing, excluded, missing.Length == 0 && excluded.Length == 0);
            if (request.Objective == "fill-one") throw new ArgumentException("Season backups preserve ordered scope across discs; use minimize-discs.");
        }
        var packing = Pack(request);
        var digest = Digest(JsonSerializer.Serialize(new { request, packing, coverage }));
        return new(request, packing, coverage, digest);
    }
    public static long Size(BackupBundle bundle) => bundle.Files.Aggregate(0L, (sum, f) => checked(sum + f.Bytes));
    static BackupPacking Pack(BackupRequest r)
    {
        var capacity = r.CapacityBytes - r.ReserveBytes; var nodes = 0; var exhausted = false;
        var items = (r.Season == null ? r.Bundles.OrderByDescending(Size).ThenBy(x => x.Id, StringComparer.Ordinal) : r.Bundles.OrderBy(x => x.Episode).ThenBy(x => x.Id, StringComparer.Ordinal)).ToArray();
        if (r.Season != null)
        {
            var ordered = new List<List<BackupBundle>>(); long used = 0;
            foreach (var item in items) { if (ordered.Count == 0 || Size(item) > capacity - used) { ordered.Add([]); used = 0; } ordered[^1].Add(item); used = checked(used + Size(item)); }
            return new(ordered.Select(x => x.ToArray()).ToArray(), [], true, items.Length, "optimal contiguous episode order");
        }
        if (r.Objective == "fill-one")
        {
            long best = 0; var chosen = new List<BackupBundle>();
            var incumbent = new List<BackupBundle>();
            foreach (var item in items) if (Size(item) <= capacity - best) { incumbent.Add(item); best += Size(item); }
            BackupBundle[] winner = incumbent.ToArray();
            void Search(int index, long used)
            {
                if (exhausted) return;
            if (++nodes > r.SearchBudget) { exhausted = true; return; }
                if (used > best) { best = used; winner = chosen.ToArray(); }
                if (best == capacity || index == items.Length) return;
                var size = Size(items[index]);
                if (size <= capacity - used) { chosen.Add(items[index]); Search(index + 1, used + size); chosen.RemoveAt(chosen.Count - 1); }
                Search(index + 1, used);
            }
            Search(0, 0);
            return new([winner], items.Except(winner).Select(x => x.Id).ToArray(), !exhausted || best == capacity, nodes, "bounded exact subset search");
        }
        var bestBins = new List<List<BackupBundle>>(); var bestUsed = new List<long>();
        foreach (var item in items) { var index = bestUsed.FindIndex(x => Size(item) <= capacity - x); if (index < 0) { bestBins.Add([]); bestUsed.Add(0); index = bestBins.Count - 1; } bestBins[index].Add(item); bestUsed[index] += Size(item); }
        var total = items.Aggregate(System.Numerics.BigInteger.Zero, (s, x) => s + Size(x));
        var lower = (int)((total + capacity - 1) / capacity); lower = Math.Max(1, lower);
        var bins = new List<List<BackupBundle>>(); var usedBins = new List<long>();
        void Assign(int at)
        {
            if (exhausted) return;
            if (++nodes > r.SearchBudget) { exhausted = true; return; }
            if (bestBins.Count == lower) return;
            if (bins.Count >= bestBins.Count) return;
            if (at == items.Length)
            {
                if (bins.Count < bestBins.Count) bestBins = bins.Select(x => x.ToList()).ToList();
                return;
            }
            var size = Size(items[at]); var seen = new HashSet<long>();
            for (var i = 0; i < bins.Count; i++) if (seen.Add(usedBins[i]) && size <= capacity - usedBins[i]) { bins[i].Add(items[at]); usedBins[i] += size; Assign(at + 1); usedBins[i] -= size; bins[i].RemoveAt(bins[i].Count - 1); }
            if (bins.Count + 1 < bestBins.Count) { bins.Add([items[at]]); usedBins.Add(size); Assign(at + 1); bins.RemoveAt(bins.Count - 1); usedBins.RemoveAt(usedBins.Count - 1); }
        }
        Assign(0);
        return new(bestBins.Select(x => x.ToArray()).ToArray(), [], !exhausted || bestBins.Count == lower, nodes, "bounded exact bin search; first-fit decreasing incumbent");
    }
}

public sealed class BackupService(Catalog db)
{
    public BackupPreview Preview(BackupRequest request) => BackupDomain.Preview(request);
    public async Task<BackupSetRecord> ApproveAsync(BackupRequest request, string expectedDigest, string commandId)
    {
        ValidateCommand(commandId);
        var preview = Preview(request);
        if (preview.Sha256 != expectedDigest) throw new ArgumentException("Backup preview changed; review again.");
        var previous = await db.Set<BackupSetRecord>().SingleOrDefaultAsync(x => x.CommandId == commandId);
        if (previous != null)
        {
            var snapshot = JsonSerializer.Deserialize<BackupPreview>(previous.SnapshotJson) ?? throw new ArgumentException("Stored backup snapshot is invalid.");
            if (JsonSerializer.Serialize(Preview(snapshot.Request)) != previous.SnapshotJson || snapshot.Sha256 != previous.Sha256) throw new ArgumentException("Stored backup snapshot digest mismatch; review required.");
            return previous.Sha256 == preview.Sha256 ? previous : throw new ArgumentException("Command was already used for a different snapshot.");
        }
        using var tx = await db.Database.BeginTransactionAsync();
        var set = new BackupSetRecord { Id = Guid.NewGuid().ToString("N"), CommandId = commandId, Name = request.Name, Sha256 = preview.Sha256, SnapshotJson = JsonSerializer.Serialize(preview), CreatedUtc = DateTime.UtcNow };
        db.Add(set);
        for (var i = 0; i < preview.Packing.Discs.Length; i++)
        {
            var id = Guid.NewGuid().ToString("N"); string label;
            do { label = BackupDomain.Label(); } while (await db.Set<BackupPhysicalDisc>().AnyAsync(x => x.Label == label) || db.Set<BackupPhysicalDisc>().Local.Any(x => x.Label == label));
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 1, discId = id, label, backupSetId = set.Id, manifestRevision = 1, files = preview.Packing.Discs[i].SelectMany(x => x.Files).Select(x => new { path = x.Path, bytes = x.Bytes, sha256 = x.Sha256.ToLowerInvariant() }).ToArray() });
            var disc = new BackupPhysicalDisc { Id = id, SetId = set.Id, Label = label, Ordinal = i + 1, ManifestJson = manifest, Sha256 = BackupDomain.Digest(manifest), CreatedUtc = set.CreatedUtc };
            db.Add(disc); db.Add(new BackupEvidence { DiscId = id, CommandId = $"{commandId}:planned:{i}", AttemptId = id, State = "planned", DetailJson = "{}", AtUtc = set.CreatedUtc });
        }
        await db.SaveChangesAsync(); await tx.CommitAsync(); return set;
    }
    public async Task<BackupPhysicalDisc> CreateReplacementAsync(string originalDiscId, string commandId)
    {
        ValidateCommand(commandId);
        var prior = await db.Set<BackupEvidence>().SingleOrDefaultAsync(x => x.CommandId == commandId);
        if (prior != null)
        {
            using var previous = JsonDocument.Parse(prior.DetailJson);
            if (prior.State != "planned" || !previous.RootElement.TryGetProperty("replacesDiscId", out var replaces) || replaces.GetString() != originalDiscId)
                throw new ArgumentException("Command was used for another request.");
            return await db.Set<BackupPhysicalDisc>().SingleAsync(x => x.Id == prior.DiscId);
        }
        var original = await db.Set<BackupPhysicalDisc>().SingleOrDefaultAsync(x => x.Id == originalDiscId) ?? throw new ArgumentException("Unknown original disc.");
        using var tx = await db.Database.BeginTransactionAsync();
        var id = Guid.NewGuid().ToString("N"); string label;
        do { label = BackupDomain.Label(); } while (await db.Set<BackupPhysicalDisc>().AnyAsync(x => x.Label == label));
        if (BackupDomain.Digest(original.ManifestJson) != original.Sha256) throw new ArgumentException("Stored manifest digest mismatch; review required.");
        using var document = JsonDocument.Parse(original.ManifestJson);
        var manifest = JsonSerializer.Serialize(new { schemaVersion = 1, discId = id, label, backupSetId = original.SetId, manifestRevision = 1, files = document.RootElement.GetProperty("files") });
        var replacement = new BackupPhysicalDisc { Id = id, SetId = original.SetId, Label = label, Ordinal = original.Ordinal, ManifestJson = manifest, Sha256 = BackupDomain.Digest(manifest), CreatedUtc = DateTime.UtcNow };
        db.Add(replacement);
        db.Add(new BackupEvidence { DiscId = id, CommandId = commandId, AttemptId = id, State = "planned", DetailJson = JsonSerializer.Serialize(new { replacesDiscId = original.Id }), AtUtc = replacement.CreatedUtc });
        await db.SaveChangesAsync(); await tx.CommitAsync(); return replacement;
    }
    static void ValidateCommand(string command) { if (string.IsNullOrWhiteSpace(command) || command.Length > 200) throw new ArgumentException("An idempotency command ID is required (maximum 200 characters)."); }
    // User-reported evidence can never grant verified status. Trusted companion verification is integrated separately.
    public async Task<BackupEvidence> RecordAsync(BackupAttemptRequest request)
    {
        ValidateCommand(request.CommandId); ValidateCommand(request.AttemptId);
        if (request.State is not ("staged" or "written-unverified" or "failed" or "interrupted" or "review-required" or "retired")) throw new ArgumentException("This endpoint cannot assert device activity or verification.");
        if (request.DetailJson.Length > 32000) throw new ArgumentException("Evidence detail is too large.");
        using var detail = JsonDocument.Parse(request.DetailJson);
        var existing = await db.Set<BackupEvidence>().SingleOrDefaultAsync(x => x.CommandId == request.CommandId);
        if (existing != null) return existing.DiscId == request.DiscId && existing.AttemptId == request.AttemptId && existing.State == request.State && existing.DetailJson == request.DetailJson ? existing : throw new ArgumentException("Command ID already records different evidence.");
        if (!await db.Set<BackupPhysicalDisc>().AnyAsync(x => x.Id == request.DiscId)) throw new ArgumentException("Unknown physical disc.");
        if (await db.Set<BackupEvidence>().AnyAsync(x => x.DiscId == request.DiscId && x.State == "retired")) throw new ArgumentException("Retired physical disc history is closed; create a replacement.");
        var evidence = new BackupEvidence { DiscId = request.DiscId, CommandId = request.CommandId, AttemptId = request.AttemptId, State = request.State, DetailJson = request.DetailJson, AtUtc = DateTime.UtcNow };
        db.Add(evidence); await db.SaveChangesAsync(); return evidence;
    }
    public async Task<object> HistoryAsync(string? search = null)
    {
        search = search?.Trim();
        var query = db.Set<BackupPhysicalDisc>().AsNoTracking();
        if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.Label.Contains(search) || x.ManifestJson.Contains(search));
        var discs = await query.OrderByDescending(x => x.CreatedUtc).Take(200).ToArrayAsync();
        var ids = discs.Select(x => x.Id).ToArray();
        var evidence = await db.Set<BackupEvidence>().AsNoTracking().Where(x => ids.Contains(x.DiscId)).OrderBy(x => x.Id).ToArrayAsync();
        return new { discs, evidence, limit = 200 };
    }
}
