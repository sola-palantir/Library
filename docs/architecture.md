# Architecture and integrity model

## Versions and deployment

- .NET 10 LTS; SDK 10.0.401, ASP.NET runtime and EF Core SQLite 10.0.12. .NET 8/9 are close to their November 2026 support endpoints, so .NET 10 is the appropriate new-service baseline.
- React 19.3.0, TypeScript 5.9.3, Vite 8.3.4, Node 24. Exact resolved frontend dependencies are in `client/package-lock.json`.
- Single ASP.NET process serves the production React build and API. SQLite and its WAL reside on a **local persistent Docker volume**, not a Windows media bind mount or SMB share. One process/replica is supported.
- Schema v5 adds optional persisted phase telemetry to the job journal with an additive, idempotent migration. Existing jobs retain their state and default to no measured progress; telemetry is never verification evidence.
- Schema v4 adds immutable backup sets, permanent physical-disc identities and append-only evidence, and downgrades legacy folder-based optical claims to content-only evidence. Pre-v4 copy hashes are retained in history but cleared as current evidence because their old publication path did not bind verification tightly enough; create a fresh verified independent copy before new planning. Schema v3 adds non-secret integration configuration documents; the transactional v2→v3 upgrade is additive. Schema v2 adds a persisted file identity (device/inode/ctime). An idempotent transactional v1→v2 upgrade preserves catalog/history and clears legacy verification caches that lack identity evidence. SQLite `user_version` records the version and newer schemas are refused. The upgrade has regression coverage; back up before upgrades and introduce explicit migrations for subsequent changes.

## Catalog

`Title → Edition → Asset → PhysicalCopy` separates descriptive identity from files and locations. Editions carry season/episode identifiers for TV. The initial scanner groups exact inferred title/kind/year and episode matches; persistent provider identities on titles, title merge/split and edition editing are future work (selected metadata-match provenance is stored in job history). Assets have physical source, library and disc copies. A disc copy does not disappear because its disc is offline.

Scans stream directory entries, ignore symlinks, inspect size/mtime/device/inode/ctime and update per-file observations. Hashes and verified status are invalidated when any fingerprint component changes, including same-size replacements with restored modification times. Unchanged files are not read or hashed. Each file observation is transactional; tracking is cleared every 100 entries to bound memory. Only a fully successful scan marks unobserved copies offline. An unavailable root fails its job and preserves observations. A successful hash records evidence at the time of reading; no monitor can guarantee a file stays unchanged afterward. Scans still enumerate directory entries: filesystem watch-based incremental discovery, mount identity tracking and benchmarks on 20–30 TB are future work.

Library/Inbox API results are paginated (50 default, 100 maximum); the browser renders a page at a time. Jobs and scans use a single bounded worker. Queue admission is serialized and limited to 100 pending/active operations. This favors predictable disk pressure over maximum throughput.

## Filesystem safety

The service runs only on Linux. `openat` walks every directory component with `O_NOFOLLOW`; anchored directory handles prevent symlink swaps from redirecting reads/writes. Final files are opened without following links, and `statx` rejects nonregular files. Root paths must be absolute, nonoverlapping and separate from the database. Canonical filesystem-root paths, observed aliases to the same directory and overlapping declared Windows mappings are refused. Windows-incompatible components, traversal, device names, alternate separators and case-insensitive destination collisions are rejected. Root IDs and container-relative paths are the operational authority; Windows paths supply display mappings and an additional overlap check; the configured container roots remain the operational authority. Host mappings must be truthful; arbitrary bind-mount ancestry cannot be inferred from separate container paths.

Read-only source and disc bind mounts provide an additional OS boundary. Library and staging roots alone permit creation. Temporary files are exclusively created (0600), flushed, verified and published with `renameat2(RENAME_NOREPLACE)` followed by a directory fsync. There is no hardlink code, media deletion endpoint, torrent deletion endpoint or overwrite operation. Cleanup is limited to a job's own `.library-<job-id>.partial` files and generated catalog-backup downloads.

Threat boundary: roots must be controlled by the user and mounted as configured. Another process with write access to library/staging can still change data after verification. Linux case checks cannot make a case-sensitive filesystem fully emulate Windows under hostile concurrent writers. Use dedicated destinations and a single service instance. Source downloads should be complete and quiescent before copying; qBittorrent readiness is queried at preview and copy time when the read-only adapter is explicitly configured; otherwise completion remains manual. The adapter has only mocked contract coverage. Root paths reused for a different drive require operator review. Cross-origin browser mutations are rejected, JSON is required, and deployment binds to loopback. The service has no login or TLS and is not intended for network exposure.

## Copy journal and recovery

1. Preview validates a library destination without creating filesystem directories. It records source copy ID, size, modification ticks, device/inode/ctime identity and target path in a durable **draft**. Approval queues the immutable plan ID.
2. Worker rejects changed sources, existing destinations and catalog-reserved offline destinations, checks available capacity with a 64 MiB reserve, creates a new temporary destination and streams source bytes into it with SHA-256.
3. Flush to disk; recheck source fingerprint/identity; independently hash the destination; compare checksums. Cancellation is checked at least each 16 MiB of streaming or verification.
4. Persist the verified hash in the journal **before** atomic no-replace publication. Register the verified physical copy afterward.
5. Restart requeues running jobs. An incomplete copy restarts from byte zero after removing only its own partial file. An already-published destination is reconciled only if its length and full SHA-256 match the journal checkpoint. Collisions and stale previews fail visibly. Media is never rolled back by deleting it.

The initial restart design intentionally re-copies an interrupted partial file instead of maintaining chunk hashes. This costs additional I/O after interruption but keeps recovery auditable. A cancelled or failed disc-staging operation can leave owned partial files; retry replaces only those partials. Complete staged files are rehashed and reused, not overwritten. Shutdown leaves running jobs recoverable; a process that cannot finish inside the grace period is handled by the same recovery path.

## Metadata and interoperability

Bulk changes use explicit leave/set/clear modes and a title revision. Locked fields reject changes; unlock them in a separate reviewed transaction. Preview includes before/after fields, locks and Plex naming suggestions. Applying metadata updates catalog rows only. Physical copying always needs a separate plan. The initial mapped fields are title, year, genre and plot; episode and edition information comes from filename inference.

NFO import returns a metadata preview, not an immediate write. Export accepts an original document so unknown tags/attributes survive; XML DTDs and external entities are prohibited, with a 2 MB document limit. Original sidecars remain untouched. For bulk unknown-field-preserving round trips, callers must supply each original document when exporting. Provider-specific IDs, multi-valued tags, artwork, metadata provider matching and full Jellyfin round-trip compatibility require additional fixtures. Plex's current supported NFO agents are acknowledged; agent configuration must be tested on the user's version. FileBot is neither bundled nor copied.

## Archive discs

The primary workspace creates immutable backup-set snapshots and physical-disc manifests. SQL triggers prevent updates/deletes of snapshots, labels and backup evidence; unique constraints protect labels and command IDs. Replacements retain the same content scope with a new permanent physical identity and newly frozen manifest. Historical titles, selected files, sidecars and expected/missing/excluded season episodes remain in the set snapshot. API export and SQLite backup include these tables.

Packing uses exact bytes per selected media/sidecar bundle. Fill-one performs bounded subset optimization; minimize-discs starts from first-fit decreasing and searches for fewer discs. The result exposes objective, search method and whether optimality is proved. Seasons use optimal contiguous ordered packing, preserve complete files and declare missing/excluded episodes. Selected-scope verification is distinct from entire-season verification. Oversized bundles are refused. Planning capacity is an estimate; the complete UDF image plus configured reserve must fit the Windows companion's measured free sectors.

Stage jobs bind the immutable manifest digest, validate frozen source identities, create independent files, and rehash all final files plus the exact manifest. Restart reuses only matching content and never overwrites completed files. Companion image preparation hashes/locks the staging inputs and makes a new frozen image; native IMAPI and manual BurnAware fallback use that same image. See [Windows companion](companion.md) for the signed receipt protocol, device requirements, retry/reconciliation semantics and hardware limits.

Selected sidecar previews are bounded to 64 MiB per file and 256 MiB total, with 500 media bundles per request; larger batches require separate plans.

The Docker API accepts optical verification only from the explicitly pinned companion public key. Signatures cover exact receipt text, disc/manifest/image identity, hardware-mode evidence and a monotonic sequence. Imports are serialized and transactional. Older sequence receipts, simulated results, mismatched images and success predating a locally recorded problem cannot become current verification. Offline import is manual: Docker cannot know about a failed Windows attempt whose receipt has not been imported. The UI displays evidence history; it is not live device status.

Legacy disc batches remain visible for compatibility. Their mounted-folder checks require the exact embedded manifest and every file hash but produce only `content-checked`, never optical verification. Schema upgrade downgrades prior folder-based `read-back-verified` claims with a retained audit reason.

## Backup and recovery

Settings offers SQLite online-backup API output and a versioned JSON catalog/history export. SQLite backup captures a consistent point including the journal while WAL mode is active. JSON export reads all tables inside a database transaction for a consistent logical snapshot; it is not yet an import format. For restore, stop the service and retain the existing catalog volume unchanged. `scripts/restore-catalog.py` validates and clones a standalone backup into a new data directory, preserving evidence and triggers. The service refuses to start jobs from that restored directory until root mappings, pending jobs, schedules and companion history are reviewed and the restore report digest is acknowledged. See [audit/restore guide](audits-restore.md). Backups contain local inventory and paths: store them privately; never commit them.

## Read-only client and metadata-provider adapters

See [integrations.md](integrations.md) for enforced protocol versions, bounded snapshots, path mapping, copy-time readiness, credential handling, synthetic provider matching and explicitly deferred Plex status/refresh. The worker retains the immutable configuration revision in each new copy plan. Snapshot checks never replace filesystem fingerprints or destination hashes.

## Job progress and motion

Workers persist measured phase snapshots (`ProgressJson`) separately from pending catalog mutations. Copy bytes, temporary verification, recovered-destination verification and published-destination verification are distinct phases. Backup staging reports the current file and final batch file checks (including the embedded manifest); preservation audits report hash bytes. Scan file counts have no reliable total and remain indeterminate. Legacy jobs without measured telemetry remain indeterminate while active. A phase reaching 100% never substitutes for the journal's completed state or successful hash/identity checks.

Within phases, telemetry is throttled to at most twice per second, with explicit phase boundaries/final measurements. The browser polls every three seconds, so short phases may finish between polls. Queued retries hide old progress until the worker resets it. Network failures freeze activity animation and retain the last received measurement; there are no estimated ETAs, pause controls or fabricated totals. Native optical work is not polled: the UI labels manually imported evidence and its catalog timestamp, with no live drive connection.

Progress bars expose accessible names and measured values only when a denominator exists. Status changes are polite announcements; byte updates are not a live-region stream. Drawer/dialog, selection, notification and completion transitions are brief. Loading skeletons appear only while waiting for catalog data and have no continuous shimmer. `prefers-reduced-motion` disables all animation and transitions. Repeated journal actions are guarded while a request is pending.
