using System.Text.Json;
using Library.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class BackupTests
{
    static BackupBundle Bundle(int id, long bytes, int? episode = null, long sidecar = 0) => new($"b{id}", $"Synthetic {id}", sidecar == 0 ? [new(id, $"file{id}.mkv", bytes, new string('a',64), $"identity{id}")] : [new(id, $"file{id}.mkv", bytes, new string('a',64), $"identity{id}"), new(id+1000, $"file{id}.nfo", sidecar, new string('b',64), $"sidecar{id}",true)], episode);
    [Fact] public void FillOneUsesExactSizesAndKeepsSidecars()
    {
        var preview = BackupDomain.Preview(new("Movies", [Bundle(1, 8), Bundle(2, 6), Bundle(3, 3, sidecar: 1)], 10, 0, "fill-one"));
        Assert.Equal(10, preview.Packing.Discs[0].Sum(BackupDomain.Size));
        Assert.True(preview.Packing.OptimalityProven);
        Assert.Equal(["b1"], preview.Packing.UnselectedBundleIds);
        Assert.Equal(2, preview.Packing.Discs[0].Single(x => x.Id == "b3").Files.Length);
    }
    [Fact] public void CapacityBoundaryAndOversizeDoNotSplit()
    {
        Assert.Single(BackupDomain.Preview(new("Exact", [Bundle(1,9)],10,1)).Packing.Discs);
        Assert.Throws<ArgumentException>(() => BackupDomain.Preview(new("Over",[Bundle(1,9,sidecar:1)],10,1)));
    }
    [Fact] public void BudgetDoesNotClaimOptimality()
    {
        var p = BackupDomain.Preview(new("Bounded", [Bundle(1,8),Bundle(2,6),Bundle(3,4)],11,0,"fill-one",SearchBudget:1));
        Assert.False(p.Packing.OptimalityProven);
        Assert.NotEmpty(p.Packing.Discs[0]);
    }
    [Fact] public void MinimizeFindsBetterThanFirstFit()
    {
        var p = BackupDomain.Preview(new("Movies",[Bundle(1,6),Bundle(2,5),Bundle(3,3),Bundle(4,2),Bundle(5,2),Bundle(6,2)],10,0));
        Assert.Equal(2,p.Packing.Discs.Length); Assert.True(p.Packing.OptimalityProven);
    }
    [Fact] public void PartialSeasonCannotBecomeCompleteAndRemainsOrdered()
    {
        var p=BackupDomain.Preview(new("Season",[Bundle(3,6,3),Bundle(1,6,1)],10,0,Season:new("Show",1,[1,2,3,4],[4])));
        Assert.Equal([2],p.Coverage!.Missing); Assert.Equal([4],p.Coverage.Excluded); Assert.False(p.Coverage.EntireSeasonIncluded);
        Assert.Equal([1,3],p.Packing.Discs.SelectMany(x=>x).Select(x=>x.Episode!.Value));
    }
    [Fact] public void DuplicateOrExcludedEpisodeRejected()
    {
        Assert.Throws<ArgumentException>(()=>BackupDomain.Preview(new("Season",[Bundle(1,1,1),Bundle(2,1,1)],10,0,Season:new("Show",1,[1],[]))));
        Assert.Throws<ArgumentException>(()=>BackupDomain.Preview(new("Season",[Bundle(1,1,1)],10,0,Season:new("Show",1,[1],[1]))));
    }
    [Fact] public void PreviewDetachedAndDigestChangesWithSnapshot()
    {
        var bundles=new[]{Bundle(1,1)}; var p=BackupDomain.Preview(new("Original",bundles,10,0));
        bundles[0]=Bundle(2,2); Assert.Equal("b1",p.Request.Bundles[0].Id);
        Assert.NotEqual(p.Sha256,BackupDomain.Preview(p.Request with {Name="Changed"}).Sha256);
    }
    [Fact] public void LabelsAreUniqueReadableAndPermanentSchemaRejectsMutation()
    {
        Assert.Equal(1000,Enumerable.Range(0,1000).Select(_=>BackupDomain.Label()).Distinct().Count());
        using var c=new SqliteConnection("Data Source=:memory:"); c.Open();
        using var cmd=c.CreateCommand(); cmd.CommandText=BackupDomain.SchemaSql;cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT INTO BackupSets VALUES ('s','command','Synthetic','{}','digest','2026-01-01'); INSERT INTO BackupPhysicalDiscs VALUES ('d','s','LB-ABC',1,'{}','digest','2026-01-01');";cmd.ExecuteNonQuery();
        foreach(var sql in new[]{"UPDATE BackupSets SET Name='changed'","DELETE FROM BackupSets","UPDATE BackupPhysicalDiscs SET Label='new'","DELETE FROM BackupPhysicalDiscs","INSERT INTO BackupPhysicalDiscs VALUES ('d2','s','LB-ABC',2,'{}','digest','2026-01-01')"}) {cmd.CommandText=sql;Assert.Throws<SqliteException>(()=>cmd.ExecuteNonQuery());}
    }
    [Fact] public async Task PublicEvidenceCannotAssertVerification()
    {
        using var db=new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite("Data Source=:memory:").Options);
        var service=new BackupService(db);
        foreach(var state in new[]{"verified","verifying","writing","finalizing"}) await Assert.ThrowsAsync<ArgumentException>(()=>service.RecordAsync(new("disc","cmd","attempt",state)));
    }
    [Fact] public async Task ApprovalEvidenceReplacementAndRestoreKeepImmutableHistory()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options);
        CatalogSchema.Initialize(db);
        var service = new BackupService(db);
        var request = new BackupRequest("Synthetic movie", [Bundle(1, 6)], 10, 0);
        var preview = service.Preview(request);
        var set = await service.ApproveAsync(request, preview.Sha256, "approve");
        Assert.Equal(set.Id, (await service.ApproveAsync(request, preview.Sha256, "approve")).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(request with { Name = "Changed" }, preview.Sha256, "approve2"));
        var disc = await db.Set<BackupPhysicalDisc>().SingleAsync();
        var failure = new BackupAttemptRequest(disc.Id,"failed-command","first-attempt","failed","{\"reason\":\"synthetic interruption\"}");
        var evidence = await service.RecordAsync(failure);
        Assert.Equal(evidence.Id,(await service.RecordAsync(failure)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(failure with { State = "written-unverified" }));
        var replacement = await service.CreateReplacementAsync(disc.Id,"replacement-command");
        Assert.NotEqual(disc.Id,replacement.Id); Assert.NotEqual(disc.Label,replacement.Label);
        Assert.Equal(replacement.Id,(await service.CreateReplacementAsync(disc.Id,"replacement-command")).Id);
        Assert.NotEqual(disc.Sha256,replacement.Sha256);
        Assert.Equal(3,await db.Set<BackupEvidence>().CountAsync());
        using var restored = new SqliteConnection("Data Source=:memory:"); restored.Open(); connection.BackupDatabase(restored);
        using var restoredDb = new Catalog(new DbContextOptionsBuilder<Catalog>().UseSqlite(restored).Options);
        CatalogSchema.Initialize(restoredDb);
        Assert.Equal(set.SnapshotJson,(await restoredDb.Set<BackupSetRecord>().SingleAsync()).SnapshotJson);
        Assert.Equal(2,await restoredDb.Set<BackupPhysicalDisc>().CountAsync());
        Assert.Equal(3,await restoredDb.Set<BackupEvidence>().CountAsync());
        await Assert.ThrowsAsync<SqliteException>(()=>restoredDb.Database.ExecuteSqlRawAsync("DELETE FROM BackupEvidence"));
    }

    [Fact] public void PackingMatchesIndependentSubsetDynamicProgrammingOnSmallRandomInputs()
    {
        var random = new Random(17012026);
        for (var sample = 0; sample < 120; sample++)
        {
            var count = random.Next(6, 11); var capacity = random.Next(8, 22);
            var sizes = Enumerable.Range(0, count).Select(_ => random.Next(1, capacity + 1)).ToArray();
            var combinations = 1 << count;
            var weights = new int[combinations];
            for (var mask = 1; mask < combinations; mask++)
                for (var bit = 0; bit < count; bit++) if ((mask & (1 << bit)) != 0) weights[mask] += sizes[bit];
            // Independent exhaustive subset DP: choose every fitting subset containing the
            // lowest selected bit, then solve the remainder. No branch-and-bound incumbent.
            var optimum = Enumerable.Repeat(count + 1, combinations).ToArray(); optimum[0] = 0;
            for (var mask = 1; mask < combinations; mask++)
            {
                var first = mask & -mask;
                for (var subset = mask; subset != 0; subset = (subset - 1) & mask)
                    if ((subset & first) != 0 && weights[subset] <= capacity)
                        optimum[mask] = Math.Min(optimum[mask], 1 + optimum[mask ^ subset]);
            }
            var bundles = sizes.Select((size, index) => Bundle(index + 1, size)).ToArray();
            var minimize = BackupDomain.Preview(new("Random synthetic", bundles, capacity, 0, SearchBudget:1000000));
            Assert.True(minimize.Packing.OptimalityProven);
            Assert.Equal(optimum[^1], minimize.Packing.Discs.Length);
            Assert.All(minimize.Packing.Discs, disc => Assert.InRange(disc.Sum(BackupDomain.Size), 0, capacity));
            Assert.Equal(count, minimize.Packing.Discs.SelectMany(x=>x).Select(x=>x.Id).Distinct().Count());
            var fill = BackupDomain.Preview(new("Random fill", bundles, capacity, 0, "fill-one", SearchBudget:1000000));
            Assert.True(fill.Packing.OptimalityProven);
            Assert.Equal(weights.Where(weight=>weight<=capacity).Max(), fill.Packing.Discs.Single().Sum(BackupDomain.Size));
        }
    }

}
