using System.Text.Json;
using System.Xml;
using Library.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("LIBRARY_CONFIG") is { } configPath) builder.Configuration.AddJsonFile(configPath, optional: false);
var options = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
Directory.CreateDirectory(options.DataPath);
var databasePath = Path.Combine(options.DataPath, "catalog.db");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<Storage>();
builder.Services.AddDbContextFactory<Catalog>(b => b.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
builder.Services.AddHostedService<JobRunner>();
var app = builder.Build();
var storage = app.Services.GetRequiredService<Storage>(); storage.ValidateConfiguration();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Catalog>();
    CatalogSchema.Initialize(db); db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); db.Database.ExecuteSqlRaw("PRAGMA synchronous=FULL;");
    if (!db.Settings.Any()) { db.Settings.Add(new Setting()); db.SaveChanges(); }
}
var mutations = new SemaphoreSlim(1);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" || (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}"))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Cross-origin mutation refused." }); return; }
        if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; await context.Response.WriteAsJsonAsync(new { error = "JSON required." }); return; }
    }
    var isMutation = context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD");
    if (isMutation) await mutations.WaitAsync(context.RequestAborted);
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or XmlException or JsonException or DbUpdateException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = ex is DbUpdateException ? "Catalog conflict; refresh and retry." : ex.Message });
    }
    finally { if (isMutation) mutations.Release(); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/health", () => new { status = "ok", version = "0.1.0" });
app.MapGet("/api/settings", async (Catalog db) => new { preferences = await db.Settings.SingleAsync(), roots = storage.Roots.Select(r => new { r.Id, r.Label, r.ContainerPath, r.WindowsPath, r.Role, online = Directory.Exists(r.ContainerPath) }) });
app.MapPut("/api/settings", async (Setting value, Catalog db) =>
{
    if (value.SeedRetentionDays is < 0 or > 3650) throw new ArgumentException("Retention must be 0–3650 days.");
    Packing.Pack([], value.DiscCapacityBytes, value.DiscReserveBytes);
    var setting = await db.Settings.SingleAsync(); setting.SeedRetentionDays = value.SeedRetentionDays; setting.DiscCapacityBytes = value.DiscCapacityBytes; setting.DiscReserveBytes = value.DiscReserveBytes;
    db.Audit("settings", JobRunner.Encode(value)); await db.SaveChangesAsync(); return Results.Ok(setting);
});
app.MapGet("/api/catalog", async (Catalog db, string? q, string? kind, string? role, int? page, int? pageSize) =>
{
    var query = from copy in db.Copies join asset in db.Assets on copy.AssetId equals asset.Id join edition in db.Editions on asset.EditionId equals edition.Id join title in db.Titles on edition.TitleId equals title.Id select new { copy, asset, edition, title };
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.title.Name.Contains(q) || x.copy.RelativePath.Contains(q));
    if (kind is "movie" or "tv") query = query.Where(x => x.title.Kind == kind);
    if (role is "source" or "library" or "disc") query = query.Where(x => x.copy.Role == role);
    var total = await query.CountAsync();
    var items = await query.OrderBy(x => x.title.Name).ThenBy(x => x.copy.Id).Skip(Math.Max(0, page ?? 0) * Math.Clamp(pageSize ?? 50, 1, 100)).Take(Math.Clamp(pageSize ?? 50, 1, 100)).ToListAsync();
    var days = (await db.Settings.SingleAsync()).SeedRetentionDays;
    return Results.Ok(new { items = items.Select(x => new { x.copy, x.asset, x.edition, x.title, reviewEligible = x.copy.Role == "source" && x.copy.FirstSeenUtc.AddDays(days) <= DateTime.UtcNow }), total });
});
app.MapGet("/api/summary", async (Catalog db) => new { titles = await db.Titles.CountAsync(), copies = await db.Copies.CountAsync(), libraryBytes = await db.Copies.Where(x => x.Role == "library").SumAsync(x => x.Bytes), sourceCopies = await db.Copies.CountAsync(x => x.Role == "source"), discs = await db.Discs.CountAsync(), activeJobs = await db.Jobs.CountAsync(x => x.Status == "running" || x.Status == "queued") });
app.MapPost("/api/scan", async (RootRequest request, Catalog db) =>
{
    if (storage.Root(request.RootId).Role is not ("source" or "library")) throw new ArgumentException("Scan a source or library root.");
    await LimitQueue(db);
    var job = new Operation { Type = "scan", Payload = JobRunner.Encode(request.RootId) }; db.Jobs.Add(job); db.Audit("scan-queued", job.Id); await db.SaveChangesAsync(); return Results.Ok(job);
});
app.MapGet("/api/jobs", async (Catalog db) => await db.Jobs.OrderByDescending(x => x.CreatedUtc).Take(100).ToListAsync());
app.MapPost("/api/jobs/{id}/cancel", async (string id, Catalog db) =>
{
    var changed = await db.Jobs.Where(x => x.Id == id && (x.Status == "queued" || x.Status == "running")).ExecuteUpdateAsync(x => x.SetProperty(j => j.CancelRequested, true));
    if (changed == 0) throw new ArgumentException("Job is no longer cancellable.");
    db.Audit("cancel-requested", id); await db.SaveChangesAsync(); return Results.Ok();
});
app.MapPost("/api/jobs/{id}/retry", async (string id, Catalog db) =>
{
    await LimitQueue(db);
    var changed = await db.Jobs.Where(x => x.Id == id && (x.Status == "failed" || x.Status == "cancelled")).ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, "queued").SetProperty(j => j.CancelRequested, false));
    if (changed == 0) throw new ArgumentException("Job is not retryable.");
    db.Audit("retry-requested", id); await db.SaveChangesAsync(); return Results.Ok();
});
app.MapPost("/api/copy/preview", async (CopyRequest request, Catalog db) =>
{
    if (storage.Root(request.TargetRootId).Role != "library") throw new ArgumentException("Choose a library destination.");
    var copy = await db.Copies.SingleAsync(x => x.Id == request.SourceCopyId);
    if (copy.Role is not ("source" or "library")) throw new ArgumentException("An online source or library copy is required.");
    var asset = await db.Assets.SingleAsync(x => x.Id == copy.AssetId);
    var edition = await db.Editions.SingleAsync(x => x.Id == asset.EditionId);
    var title = await db.Titles.SingleAsync(x => x.Id == edition.TitleId);
    var path = string.IsNullOrWhiteSpace(request.TargetPath) ? Naming.Plex(title, edition, asset) : request.TargetPath;
    Storage.Components(path);
    if (await db.Copies.AnyAsync(x => x.RootId == request.TargetRootId && x.RelativePath.ToUpper() == path.ToUpper())) throw new IOException("The catalog already records a copy at this destination, possibly offline. Choose a new path.");
    if (storage.Exists(request.TargetRootId, path)) throw new IOException("Destination collision. Choose a different path.");
    var fingerprint = storage.Observe(copy.RootId, copy.RelativePath);
    var plan = new CopyPlan(copy.Id, request.TargetRootId, path, fingerprint.Bytes, fingerprint.Ticks, SourceIdentity: fingerprint.Identity);
    var job = new Operation { Type = "copy", Status = "draft", Payload = JobRunner.Encode(plan) };
    db.Jobs.Add(job); db.Audit("copy-preview", job.Id); await db.SaveChangesAsync();
    return Results.Ok(new { job, plan, sourcePath = copy.RelativePath, freeBytes = storage.FreeBytes(request.TargetRootId) });
});
app.MapPost("/api/copy/{id}/commit", async (string id, Catalog db) =>
{
    await LimitQueue(db);
    var updated = await db.Jobs.Where(x => x.Id == id && x.Type == "copy" && x.Status == "draft").ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, "queued"));
    if (updated != 1) throw new ArgumentException("Preview has already been committed or is unavailable.");
    db.Audit("copy-approved", id); await db.SaveChangesAsync(); return Results.Ok();
});
app.MapPost("/api/metadata/preview", async (MetadataPlan plan, Catalog db) =>
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
    return Results.Ok(new { job, rows });
});
app.MapPost("/api/metadata/{id}/commit", async (string id, Catalog db) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    var job = await db.Jobs.SingleAsync(x => x.Id == id && x.Type == "metadata");
    if (job.Status != "draft") throw new ArgumentException("Preview already committed.");
    foreach (var change in JobRunner.Decode<MetadataPlan>(job.Payload).Changes)
    {
        var title = await db.Titles.SingleAsync(x => x.Id == change.TitleId); var updated = Metadata.Apply(title, change);
        db.Entry(title).CurrentValues.SetValues(updated);
    }
    job.Status = "completed"; db.Audit("metadata-approved", job.Payload); await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok();
});
app.MapPost("/api/nfo/import-preview", async (NfoRequest request, Catalog db) =>
{
    var title = await db.Titles.SingleAsync(x => x.Id == request.TitleId);
    var xml = Metadata.ReadNfo(request.Xml ?? throw new ArgumentException("NFO XML is required."));
    var fields = new Dictionary<string, FieldEdit>();
    foreach (var field in Metadata.Fields)
    {
        var element = xml.Root!.Element(field == "name" ? "title" : field);
        fields[field] = element == null ? new("leave", null) : new(string.IsNullOrWhiteSpace(element.Value) ? "clear" : "set", element.Value);
    }
    return Results.Ok(new MetadataChange(title.Id, title.Revision, fields, JobRunner.Decode<string[]>(title.LockedFields)));
});
app.MapPost("/api/nfo/export", async (NfoRequest request, Catalog db) =>
{
    var title = await db.Titles.SingleAsync(x => x.Id == request.TitleId);
    var edition = request.EditionId is { } editionId ? await db.Editions.SingleAsync(x => x.Id == editionId && x.TitleId == title.Id) : await db.Editions.FirstAsync(x => x.TitleId == title.Id);
    return Results.Text(Metadata.ExportNfo(title, edition, request.Xml), "application/xml");
});
app.MapPost("/api/discs/preview", async (DiscRequest request, Catalog db) =>
{
    if (request.CopyIds.Length is < 1 or > 1000) throw new ArgumentException("Choose 1–1000 copies.");
    var entries = new List<ManifestEntry>();
    foreach (var id in request.CopyIds.Distinct())
    {
        var copy = await db.Copies.SingleAsync(x => x.Id == id);
        if (copy.Role != "library" || copy.Sha256 == null || copy.State == "offline") throw new ArgumentException("Disc plans require verified library copies.");
        if (storage.Observe(copy.RootId, copy.RelativePath) != new FileObservation(copy.Bytes, copy.ModifiedTicks, copy.FileIdentity ?? "")) throw new IOException("Library file changed; verify a fresh copy first.");
        entries.Add(new(copy.AssetId, copy.Id, copy.RelativePath, copy.Bytes, copy.Sha256));
    }
    if (entries.Select(x => x.AssetId).Distinct().Count() != entries.Count || entries.Select(x => x.Path.ToUpperInvariant()).Distinct().Count() != entries.Count) throw new ArgumentException("Select one distinct copy and path per asset.");
    var bins = Packing.Pack(entries, request.CapacityBytes, request.ReserveBytes);
    var plan = new DiscPlan(request.CapacityBytes, request.ReserveBytes, bins);
    var job = new Operation { Type = "disc-plan", Status = "draft", Payload = JobRunner.Encode(plan) }; db.Jobs.Add(job); await db.SaveChangesAsync();
    return Results.Ok(new { job, plan });
});
app.MapPost("/api/discs/{id}/commit", async (string id, Catalog db) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var job = await db.Jobs.SingleAsync(x => x.Id == id && x.Type == "disc-plan");
    if (job.Status != "draft") throw new ArgumentException("Plan already committed.");
    var plan = JobRunner.Decode<DiscPlan>(job.Payload);
    var number = await db.Discs.CountAsync();
    foreach (var bin in plan.Bins) db.Discs.Add(new() { Label = $"LIB-{++number:0000}", CapacityBytes = plan.CapacityBytes, ReserveBytes = plan.ReserveBytes, Manifest = JobRunner.Encode(bin) });
    job.Status = "completed"; db.Audit("disc-plan-approved", job.Id); await db.SaveChangesAsync(); await tx.CommitAsync(); return Results.Ok();
});
app.MapGet("/api/discs", async (Catalog db) => await db.Discs.OrderByDescending(x => x.CreatedUtc).ToListAsync());
app.MapGet("/api/discs/{id}/manifest", async (string id, Catalog db) =>
{
    var disc = await db.Discs.SingleAsync(x => x.Id == id);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(JobRunner.Encode(new { version = 1, disc.Id, disc.Label, disc.CapacityBytes, disc.ReserveBytes, files = JobRunner.Decode<ManifestEntry[]>(disc.Manifest) })), "application/json", disc.Label + ".json");
});
app.MapPost("/api/discs/{id}/stage", async (string id, RootRequest request, Catalog db) =>
{
    if (storage.Root(request.RootId).Role != "staging") throw new ArgumentException("Choose a staging root.");
    if (!await db.Discs.AnyAsync(x => x.Id == id)) throw new ArgumentException("Unknown disc.");
    await LimitQueue(db); var job = new Operation { Type = "disc-stage", Payload = JobRunner.Encode(new DiscJob(id, request.RootId)) }; db.Jobs.Add(job); await db.SaveChangesAsync(); return Results.Ok(job);
});
app.MapPost("/api/discs/{id}/written", async (string id, Catalog db) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var disc = await db.Discs.SingleAsync(x => x.Id == id);
    if (disc.Status != "planned") throw new ArgumentException("Only planned discs can be reported written.");
    disc.Status = "reported-written";
    foreach (var entry in JobRunner.Decode<ManifestEntry[]>(disc.Manifest)) db.Copies.Add(new() { AssetId = entry.AssetId, RootId = "disc:" + disc.Id, RelativePath = entry.Path, Role = "disc", State = "reported-written", Bytes = entry.Bytes, Sha256 = entry.Sha256, DiscId = disc.Id });
    db.Audit("disc-reported-written", id); await db.SaveChangesAsync(); await tx.CommitAsync(); return Results.Ok();
});
app.MapPost("/api/discs/{id}/verify", async (string id, RootRequest request, Catalog db) =>
{
    if (storage.Root(request.RootId).Role != "disc") throw new ArgumentException("Choose a read-only disc mount.");
    await LimitQueue(db); var job = new Operation { Type = "disc-verify", Payload = JobRunner.Encode(new DiscJob(id, request.RootId)) }; db.Jobs.Add(job); await db.SaveChangesAsync(); return Results.Ok(job);
});
app.MapGet("/api/export", async (Catalog db) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    var bytes = System.Text.Encoding.UTF8.GetBytes(JobRunner.Encode(new { schemaVersion = CatalogSchema.Version, exportedUtc = DateTime.UtcNow, titles = await db.Titles.ToListAsync(), editions = await db.Editions.ToListAsync(), assets = await db.Assets.ToListAsync(), copies = await db.Copies.ToListAsync(), jobs = await db.Jobs.ToListAsync(), discs = await db.Discs.ToListAsync(), history = await db.History.ToListAsync(), settings = await db.Settings.ToListAsync() }));
    await transaction.CommitAsync();
    return Results.File(bytes, "application/json", "library-export.json");
});
app.MapGet("/api/backup", async (HttpContext context) =>
{
    var path = Path.Combine(options.DataPath, $"backup-{Guid.NewGuid():N}.db");
    try
    {
        using (var source = new SqliteConnection($"Data Source={databasePath}"))
        using (var target = new SqliteConnection($"Data Source={path}")) { source.Open(); target.Open(); source.BackupDatabase(target); }
        context.Response.ContentType = "application/vnd.sqlite3";
        context.Response.Headers.ContentDisposition = "attachment; filename=library-backup.db";
        await context.Response.SendFileAsync(path);
    }
    finally { File.Delete(path); } // only the generated catalog backup, never a media file
});
app.MapFallbackToFile("index.html");
app.Run();
static async Task LimitQueue(Catalog db)
{
    if (await db.Jobs.CountAsync(x => x.Status == "queued" || x.Status == "running") >= 100) throw new ArgumentException("Job queue is full (100). Wait for work to finish.");
}
public record RootRequest(string RootId);
public record CopyRequest(long SourceCopyId, string TargetRootId, string? TargetPath);
public record NfoRequest(long TitleId, long? EditionId, string? Xml);
public record DiscRequest(long[] CopyIds, long CapacityBytes, long ReserveBytes);
public record DiscPlan(long CapacityBytes, long ReserveBytes, List<List<ManifestEntry>> Bins);
public partial class Program;
