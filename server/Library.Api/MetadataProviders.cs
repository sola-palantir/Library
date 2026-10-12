using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public record MetadataCandidate(string Id, string ProviderId, string Name, string Kind, int? Year, string? Plot, string? Genre, bool Synthetic);
public record MetadataProviderStatus(string Id, string Label, string State, string Message, bool RequiresApiKey);
public interface IMetadataProvider
{
    MetadataProviderStatus Status { get; }
    Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string query, string kind, CancellationToken ct);
    Task<MetadataCandidate?> GetAsync(string id, CancellationToken ct);
}
public sealed class SyntheticMetadataProvider : IMetadataProvider
{
    public MetadataProviderStatus Status => new("synthetic", "Synthetic demonstration", "synthetic", "Invented titles and descriptions. No external lookup or artwork download.", false);
    private static readonly MetadataCandidate[] Candidates =
    [
        new("northlight-2024", "synthetic", "Northlight", "movie", 2024, "A cartographer follows an unfamiliar constellation across a silent northern landscape.", "Adventure", true),
        new("northlight-1998", "synthetic", "Northlight", "movie", 1998, "An invented alternative match about a remote coastal weather station.", "Drama", true),
        new("paper-moons-2021", "synthetic", "Paper Moons", "movie", 2021, "An illustrator turns a small town into a canvas of impossible places.", "Animation", true),
        new("signal-coast-2023", "synthetic", "Signal Coast", "tv", 2023, "A forgotten radio station begins broadcasting from the coast.", "Mystery", true),
        new("last-observatory-2022", "synthetic", "The Last Observatory", "movie", 2022, "Two astronomers keep watch through the last winter at a remote observatory.", "Drama", true)
    ];
    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string query, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MetadataCandidate>>(Candidates.Where(x => x.Kind == kind && x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(20).ToArray());
    }
    public Task<MetadataCandidate?> GetAsync(string id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Candidates.SingleOrDefault(x => x.Id == id)); }
}
public sealed class UnconfiguredMetadataProvider : IMetadataProvider
{
    public MetadataProviderStatus Status => new("tmdb", "TMDB (planned)", "unconfigured", "Live lookup is not implemented. A provider adapter, user-supplied API credential and integration validation are required. Manual editing and NFO import remain available.", true);
    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string query, string kind, CancellationToken ct) => throw new InvalidOperationException(Status.Message);
    public Task<MetadataCandidate?> GetAsync(string id, CancellationToken ct) => throw new InvalidOperationException(Status.Message);
}
public record MetadataProvenance(string ProviderId, string CandidateId, bool Synthetic);
public record MatchRequest(long TitleId, int Revision, string ProviderId, string CandidateId);
public record MetadataSearchRequest(long TitleId, string ProviderId, string Query);
public sealed class MetadataProviders(IEnumerable<IMetadataProvider> providers)
{
    public MetadataProviderStatus[] Statuses => providers.Select(x => x.Status).ToArray();
    public IMetadataProvider Get(string id) => providers.SingleOrDefault(x => x.Status.Id == id) ?? throw new ArgumentException("Unknown metadata provider.");
    public async Task<MetadataPlan> Match(Title title, MatchRequest request, CancellationToken ct)
    {
        if (request.TitleId != title.Id || request.Revision != title.Revision) throw new ArgumentException("Title changed after selection. Refresh before matching.");
        var candidate = await Get(request.ProviderId).GetAsync(request.CandidateId, ct) ?? throw new ArgumentException("Candidate no longer exists.");
        if (candidate.Kind != title.Kind) throw new ArgumentException("Movie and TV matches cannot be interchanged.");
        var locked = JobRunner.Decode<string[]>(title.LockedFields);
        var values = new Dictionary<string, string?> { ["name"] = candidate.Name, ["year"] = candidate.Year?.ToString(), ["plot"] = candidate.Plot, ["genre"] = candidate.Genre };
        var edits = values.ToDictionary(x => x.Key, x => locked.Contains(x.Key) || x.Value == null ? new FieldEdit("leave", null) : new FieldEdit("set", x.Value));
        return new([new(title.Id, title.Revision, edits, locked)], new(candidate.ProviderId, candidate.Id, candidate.Synthetic));
    }
}
public static class MetadataPreviews
{
    public static async Task<object> Create(MetadataPlan plan, Catalog db)
    {
        if (plan.Changes.Length is < 1 or > 100 || plan.Changes.Select(x => x.TitleId).Distinct().Count() != plan.Changes.Length) throw new ArgumentException("Select 1–100 distinct titles.");
        var rows = new List<object>();
        foreach (var change in plan.Changes)
        {
            var original = await db.Titles.SingleAsync(x => x.Id == change.TitleId);
            var updated = Metadata.Apply(original, change);
            var assets = await (from e in db.Editions join a in db.Assets on e.Id equals a.EditionId where e.TitleId == original.Id select new { edition = e, asset = a }).ToListAsync();
            rows.Add(new { before = original, after = updated, paths = assets.Select(x => new { oldPath = Naming.Plex(original, x.edition, x.asset), newPath = Naming.Plex(updated, x.edition, x.asset) }) });
        }
        var job = new Operation { Type = "metadata", Status = "draft", Payload = JobRunner.Encode(plan) }; db.Jobs.Add(job); await db.SaveChangesAsync();
        return new { job, rows, provenance = plan.Provenance };
    }
}
