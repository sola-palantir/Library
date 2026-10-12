using Microsoft.EntityFrameworkCore;

namespace Library.Api;

public static class CatalogSchema
{
    public const int Version = 3;
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
            using var transaction = db.Database.BeginTransaction();
            if (!columns.Contains("FileIdentity"))
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE Copies ADD COLUMN FileIdentity TEXT NULL;");
                // Old hashes remain in history/manifests, but are not current verification evidence.
                db.Database.ExecuteSqlRaw("UPDATE Copies SET Sha256 = NULL, State = CASE WHEN Role = 'library' AND State = 'verified' THEN 'online' ELSE State END WHERE Role IN ('library', 'source');");
                db.Database.ExecuteSqlRaw("UPDATE Assets SET Sha256 = NULL;");
            }
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Integrations (Id TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL, Revision INTEGER NOT NULL);");
            db.Database.ExecuteSqlRaw("PRAGMA user_version = 3;");
            transaction.Commit();
        }
        finally { db.Database.CloseConnection(); }
    }
}
