using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public sealed class Title
{
    public long Id { get; set; }
    public string Name { get; set; } = "Untitled";
    public string Kind { get; set; } = "movie";
    public int? Year { get; set; }
    public string? Plot { get; set; }
    public string? Genre { get; set; }
    public string LockedFields { get; set; } = "[]";
    public int Revision { get; set; }
}
public sealed class Edition
{
    public long Id { get; set; }
    public long TitleId { get; set; }
    public string Name { get; set; } = "Original";
    public int? Season { get; set; }
    public int? Episode { get; set; }
}
public sealed class Asset
{
    public long Id { get; set; }
    public long EditionId { get; set; }
    public string FileName { get; set; } = "";
    public long Bytes { get; set; }
    public string? Sha256 { get; set; }
}
public sealed class PhysicalCopy
{
    public long Id { get; set; }
    public long AssetId { get; set; }
    public string RootId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Role { get; set; } = "source";
    public string State { get; set; } = "online";
    public long Bytes { get; set; }
    public long ModifiedTicks { get; set; }
    public string? FileIdentity { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public string? ScanId { get; set; }
    public string? Sha256 { get; set; }
    public string? DiscId { get; set; }
}
public sealed class Operation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "scan";
    public string Status { get; set; } = "queued";
    public string Payload { get; set; } = "{}";
    public string? Error { get; set; }
    public long ProgressBytes { get; set; }
    public string ProgressJson { get; set; } = "{}";
    public bool CancelRequested { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}
public sealed class Disc
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Status { get; set; } = "planned";
    public long CapacityBytes { get; set; }
    public long ReserveBytes { get; set; }
    public string Manifest { get; set; } = "[]";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class Setting
{
    public string Id { get; set; } = "preferences";
    public int SeedRetentionDays { get; set; } = 20;
    public long DiscCapacityBytes { get; set; } = 50_000_000_000;
    public long DiscReserveBytes { get; set; } = 1_000_000_000;
}
public sealed class Catalog(DbContextOptions<Catalog> options) : DbContext(options)
{
    public DbSet<Title> Titles => Set<Title>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<PhysicalCopy> Copies => Set<PhysicalCopy>();
    public DbSet<Operation> Jobs => Set<Operation>();
    public DbSet<AuditEvent> History => Set<AuditEvent>();
    public DbSet<Disc> Discs => Set<Disc>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<IntegrationDocument> Integrations => Set<IntegrationDocument>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        BackupDomain.Configure(b);
        b.Entity<PhysicalCopy>().HasIndex(x => new { x.RootId, x.RelativePath }).IsUnique();
        b.Entity<Edition>().HasOne<Title>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Asset>().HasOne<Edition>().WithMany().HasForeignKey(x => x.EditionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PhysicalCopy>().HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Operation>().HasIndex(x => new { x.Status, x.CreatedUtc });
    }
    public void Audit(string action, string detail) => History.Add(new() { Action = action, Detail = detail });
}
public record StorageRoot(string Id, string Label, string ContainerPath, string WindowsPath, string Role);
public sealed class LibraryOptions
{
    public string DataPath { get; set; } = "/data";
    public List<StorageRoot> Roots { get; set; } = [];
}
public record CopyPlan(long SourceCopyId, string TargetRootId, string TargetPath, long Bytes, long ModifiedTicks, string? Hash = null, string? SourceIdentity = null, int? QBitRevision = null);
public record ManifestEntry(long AssetId, long CopyId, string Path, long Bytes, string Sha256);
public record FieldEdit(string Mode, string? Value);
public record MetadataChange(long TitleId, int Revision, Dictionary<string, FieldEdit> Fields, string[] LockedFields);
public record MetadataPlan(MetadataChange[] Changes, MetadataProvenance? Provenance = null);
