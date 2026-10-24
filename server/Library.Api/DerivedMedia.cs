using Microsoft.EntityFrameworkCore;
namespace Library.Api;
public sealed class DerivedAssetRecord
{
    public long OutputAssetId { get; set; }
    public long SourceAssetId { get; set; }
    public long SourceCopyId { get; set; }
    public string JobId { get; set; } = "";
    public string Kind { get; set; } = "stream-copy-sample";
    public string Evidence { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class ProcessingLease
{
    public long SourceAssetId { get; set; }
    public string JobId { get; set; } = "";
}
public static class DerivedCatalog
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<DerivedAssetRecord>().ToTable("DerivedAssets", t => t.HasCheckConstraint("CK_DerivedDistinct", "OutputAssetId <> SourceAssetId")).HasKey(x => x.OutputAssetId);
        b.Entity<DerivedAssetRecord>().HasIndex(x => x.JobId).IsUnique();
        b.Entity<DerivedAssetRecord>().HasOne<Asset>().WithMany().HasForeignKey(x => x.OutputAssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DerivedAssetRecord>().HasOne<Asset>().WithMany().HasForeignKey(x => x.SourceAssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DerivedAssetRecord>().HasOne<PhysicalCopy>().WithMany().HasForeignKey(x => x.SourceCopyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProcessingLease>().HasKey(x => x.SourceAssetId);
        b.Entity<ProcessingLease>().HasIndex(x => x.JobId).IsUnique();
        b.Entity<ProcessingLease>().HasOne<Asset>().WithMany().HasForeignKey(x => x.SourceAssetId).OnDelete(DeleteBehavior.Restrict);
    }
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS DerivedAssets (
          OutputAssetId INTEGER NOT NULL PRIMARY KEY REFERENCES Assets(Id),
          SourceAssetId INTEGER NOT NULL REFERENCES Assets(Id),
          SourceCopyId INTEGER NOT NULL REFERENCES Copies(Id), JobId TEXT NOT NULL UNIQUE,
          Kind TEXT NOT NULL, Evidence TEXT NOT NULL, CreatedUtc TEXT NOT NULL,
          CHECK (OutputAssetId <> SourceAssetId));
        CREATE TABLE IF NOT EXISTS ProcessingLeases (SourceAssetId INTEGER NOT NULL PRIMARY KEY REFERENCES Assets(Id), JobId TEXT NOT NULL UNIQUE);
        CREATE TRIGGER IF NOT EXISTS derived_no_update BEFORE UPDATE ON DerivedAssets BEGIN SELECT RAISE(ABORT, 'Derived lineage is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS derived_no_delete BEFORE DELETE ON DerivedAssets BEGIN SELECT RAISE(ABORT, 'Derived lineage is immutable'); END;
        """;
}
