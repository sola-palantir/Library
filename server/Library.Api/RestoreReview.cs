using System.Text.Json;

namespace Library.Api;

public static class RestoreReview
{
    public static void Validate(string dataPath, string? acknowledgement)
    {
        var report = Path.Combine(dataPath, "restore-report.json");
        if (!File.Exists(report)) return;
        var text = File.ReadAllText(report);
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "restored-offline")
            throw new InvalidOperationException("Restore did not complete. Preserve this directory and restore the backup to a new directory; no jobs have started.");
        if (acknowledgement != BackupDomain.Digest(text))
            throw new InvalidOperationException("Restored catalog is paused. Review root mappings, pending operations, audit policy and companion state, then acknowledge the SHA-256 of restore-report.json with LIBRARY_RESTORE_REVIEWED_SHA256. No jobs have started.");
    }
}
