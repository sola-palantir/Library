using System.Text.Json;
using Library.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public class CatalogTimeTests
{
    [Fact] public void ExistingUtcTicksAreNotShiftedAndSerializeWithAnOffsetAfterSqliteRoundTrip()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<Catalog>().UseSqlite(connection).Options;
        var recorded = new DateTime(2026, 11, 2, 0, 32, 50, DateTimeKind.Unspecified).AddTicks(4986017);
        using (var db = new Catalog(options))
        {
            db.Database.EnsureCreated();
            db.Jobs.Add(new() { Id = "legacy-utc", CreatedUtc = recorded, UpdatedUtc = DateTime.SpecifyKind(recorded, DateTimeKind.Utc) });
            db.History.Add(new() { AtUtc = recorded }); db.SaveChanges();
        }
        using (var db = new Catalog(options))
        {
            var job = db.Jobs.Single();
            Assert.Equal(recorded.Ticks, job.CreatedUtc.Ticks); Assert.Equal(DateTimeKind.Utc, job.CreatedUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, job.UpdatedUtc.Kind); Assert.Equal(DateTimeKind.Utc, db.History.Single().AtUtc.Kind);
            Assert.Contains("2026-11-02T00:32:50.4986017Z", JsonSerializer.Serialize(job));
            var projected = db.Jobs.Select(j => new { j.CreatedUtc }).Single();
            Assert.Equal(DateTimeKind.Utc, projected.CreatedUtc.Kind);
        }
    }
}
