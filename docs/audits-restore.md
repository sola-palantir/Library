# Preservation audits and offline catalog restore

Scheduled audits are **disabled by default**. Settings are exposed at `GET/PUT /api/preservation/policy`: enabled, intervalHours (1–8760), maxFiles (1–100), and maxBytes (up to 10 TB per batch). The persistent cursor resumes through copies across restarts; oversized files generate visible budget-skipped history rather than being silently omitted. An audit opens the configured root safely, reads all content for SHA-256, compares expected content, and checks both the open handle and path identity. An unchanged size/timestamp does not bypass hashing. Its result is a historical observation, not everlasting proof or optical-disc verification.

`POST /api/preservation/audit/{copyId}` requests a manual audit. `GET /api/preservation/history` lists recent results and review items. A mismatch, replacement or read failure invalidates matching cached evidence, creates review-required history, and changes no media. An interrupted operation records no successful evidence. Existing hashes in manifests/history remain historical expectations. There is no automatic repair, deletion, torrent change, or retirement action.

`POST /api/preservation/repair-preview` accepts `healthyCopyId`, `targetRootId`, and a new `targetPath`. It requires a fresh successful audit against independently recorded expected content, checks the qBittorrent guard when applicable, and creates a regular **draft** copy operation. Review and approve through the existing copy workflow. The destination must be unused in the filesystem and catalog. The damaged copy remains untouched. This tool does not decide which conflicting copies are authoritative.

## Offline restore

Export a standalone SQLite backup from Settings. Stop the service before restoring; do not pass a live catalog with WAL/SHM files. Keep the exported backup immutable throughout restore; the tool opens it in immutable read-only mode so even WAL/SHM sidecars are not created beside it.

```sh
python3 scripts/restore-catalog.py exported-catalog.db /new/nonexistent/data-directory
```

The parent directory must exist; the target itself must not exist, even if empty. The tool validates SQLite integrity, foreign keys, schema version and required tables before destination creation. It copies the entire database through SQLite's backup API, preserving all history, frozen manifests, labels, attempts and triggers, and verifies destination integrity/schema. It never migrates, modifies or deletes the source backup. A flushed `restore-incomplete` marker is durably written before any destination catalog is created. Only a fully validated, flushed catalog gets an atomically published `restored-offline` report. Failed output and its incomplete marker are retained for review; never start the service from a failed restore. A new restore attempt must use another new directory.

Restore only recreates the catalog, not media, optical hardware, companion credentials or deployment configuration. Review root mappings and disable scheduling before starting; pending file and burn commands require their normal restart reconciliation. A report is saved next to the restored database. Past verification evidence remains historical and must not be interpreted as a fresh hardware check.

## Restored-catalog startup gate

The service detects `restore-report.json` and refuses startup before any worker or audit scheduler starts. After separately reviewing root mappings, queued/running jobs, scheduling policy and the companion's newer physical activity, an operator can explicitly acknowledge the report by setting `LIBRARY_RESTORE_REVIEWED_SHA256` to the SHA-256 of the exact UTF-8 report file. This is a review acknowledgement, not a password. Do not remove the report to bypass review. Restore does not undo physical burns; never use an older companion journal to authorize another write for an attempted disc.
