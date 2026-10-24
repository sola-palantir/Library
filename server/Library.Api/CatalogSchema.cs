using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public static class CatalogSchema
{
    public const int Version = 6;
    public static void Initialize(Catalog db)
    {
        db.Database.EnsureCreated();
        db.Database.OpenConnection();
        try
        {
            using var versionCommand = db.Database.GetDbConnection().CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version > Version) throw new InvalidOperationException("Catalog was created by a newer Library version; refusing to open it.");
            var columns = new List<string>();
            using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "PRAGMA table_info('Copies')";
                using var reader = command.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            var jobColumns = new List<string>();
            using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "PRAGMA table_info('Jobs')";
                using var reader = command.ExecuteReader();
                while (reader.Read()) jobColumns.Add(reader.GetString(1));
            }
            using var transaction = db.Database.BeginTransaction();
            if (!jobColumns.Contains("ProgressJson"))
                db.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN ProgressJson TEXT NOT NULL DEFAULT '{{}}';");
            if (!columns.Contains("FileIdentity"))
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE Copies ADD COLUMN FileIdentity TEXT NULL;");
                // Old hashes remain in history/manifests, but are not current verification evidence.
                db.Database.ExecuteSqlRaw("UPDATE Copies SET Sha256 = NULL, State = CASE WHEN Role = 'library' AND State = 'verified' THEN 'online' ELSE State END WHERE Role IN ('library', 'source');");
                db.Database.ExecuteSqlRaw("UPDATE Assets SET Sha256 = NULL;");
            }
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Integrations (Id TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL, Revision INTEGER NOT NULL);");
            if (version < 4)
            {
                db.Database.ExecuteSqlRaw("INSERT INTO History (AtUtc, Action, Detail) SELECT datetime('now'), 'legacy-copy-evidence-retained', json_object('copyId', Id, 'sha256', Sha256, 'fileIdentity', FileIdentity) FROM Copies WHERE Role IN ('library','source') AND Sha256 IS NOT NULL;");
                db.Database.ExecuteSqlRaw("UPDATE Copies SET Sha256 = NULL, State = CASE WHEN State = 'verified' THEN 'online' ELSE State END WHERE Role IN ('library','source');");
                db.Database.ExecuteSqlRaw("UPDATE Assets SET Sha256 = NULL;");
                db.Database.ExecuteSqlRaw("INSERT INTO History (AtUtc, Action, Detail) SELECT datetime('now'), 'legacy-optical-claim-downgraded', Id FROM Discs WHERE Status = 'read-back-verified';");
                db.Database.ExecuteSqlRaw("UPDATE Discs SET Status = 'content-checked' WHERE Status = 'read-back-verified';");
                db.Database.ExecuteSqlRaw("UPDATE Copies SET State = 'content-checked' WHERE Role = 'disc' AND State = 'read-back-verified';");
            }
            db.Database.ExecuteSqlRaw(BackupDomain.SchemaSql);
            db.Database.ExecuteSqlRaw(PreservationService.SchemaSql);
            db.Database.ExecuteSqlRaw(DerivedCatalog.SchemaSql);
            db.Database.ExecuteSqlRaw("PRAGMA user_version = 6;");
            transaction.Commit();
        }
        finally { db.Database.CloseConnection(); }
    }
}
