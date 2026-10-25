# Operator delivery and recovery

This preview runs the catalog, UI and imported reviews on Docker Desktop with a
self-contained Windows management tool and companion. **Production processing,
persistent pairing and Silo processing stay disabled.** The companion is packaged
and versioned, but does not run as a service or receive production jobs.

The repository contains the packaging workflow. No public release, container
registry publication or code-signing identity is assumed. Locally generated
previews are explicitly **unsigned**. A hash checks integrity, not publisher
authenticity: obtain the ZIP and manifest SHA-256 from a trusted release channel.
Do not execute a download from an untrusted source just because it supplies its
own checksum. Public publication, licensing and signing remain release gates.

## Install once

Prerequisites: Windows x64, a local NTFS installation parent owned by your user,
Docker Desktop already installed/running in Linux-container mode, and sufficient
free space for the image archive, retained releases and multiple catalog copies.
No end-user Git, Node, SDK, Python or system .NET runtime is needed. The manager
does not install prerequisites, elevate, change ACLs/firewalls/PATH, register a
service or create credentials. Keep the installation directory private because
catalog backups may contain private metadata.

1. Download the approved preview ZIP, its ZIP SHA-256, and the release manifest
   SHA-256. Compare the ZIP with `Get-FileHash -Algorithm SHA256`. Extract the ZIP
   into a new folder. Do not extract over a previous release.
2. In PowerShell set paths to **your** extracted bundle and a new installation
   directory. The parent of the installation directory must already exist:

   ```powershell
   $bundle = 'C:\LibraryDownloads\library-0.2.0-preview.1'
   $install = 'C:\LibraryData\Library'
   $manifestHash = '<trusted lowercase SHA-256 of release.json>'
   $manager = Join-Path $bundle 'manager\Library.Manager.exe'
   & $manager install $install $bundle $manifestHash
   if ($LASTEXITCODE -ne 0) { throw 'Installation failed; retain the evidence.' }
   ```

3. Open **http://127.0.0.1:5080**. The manager loads the prebuilt image, creates an
   isolated named volume, starts the API paused, verifies version/schema/protocol
   and catalog integrity, then enables catalog/review changes. It exposes no
   off-machine listener and no Windows listener. Existing media/catalogs are not
   adopted or migrated by installation. Native worker pairing is unavailable. The Windows components include pinned .NET 10.0.13 and its original licensing notices.

`install` accepts an optional final localhost port for a separate test instance.
Do not point the installation at an existing checkout, media folder or another
application's data directory. The manager refuses an existing destination.

## Daily use

Keep the extracted bundle. Use its manager, or the matching manager under the
installation's retained `releases` folder, in PowerShell:

```powershell
& $manager status $install
& $manager start $install
& $manager backup $install
& $manager stop $install
```

`start` verifies retained artifact hashes and readiness before allowing changes.
`backup` pauses changes, waits for active work to drain, stops the managed API,
creates a verified catalog snapshot and configuration/journal backup, then
restarts the same release. It requires a running, healthy managed API.
`stop` pauses and drains first. Docker containers have no automatic restart policy
in this preview; after a reboot, explicitly use `start`. No background updater or
companion is installed. Never start managed containers manually around the manager.

## Update

Download and verify the next approved bundle as in installation. Run its manager:

```powershell
$nextBundle = 'C:\LibraryDownloads\library-0.2.0-preview.2'
$nextManager = Join-Path $nextBundle 'manager\Library.Manager.exe'
$nextManifestHash = '<trusted lowercase SHA-256 of next release.json>'
& $nextManager update $install $nextBundle $nextManifestHash
if ($LASTEXITCODE -ne 0) { throw 'Update paused; inspect status before recovery.' }
```

The manager verifies every listed artifact, image content ID and runtime version.
It checks deployment protocol, worker protocol, schema and disabled production mode.
It quiesces new API mutations and waits up to 60 seconds for active jobs/audits and
processing leases to clear; it does not cancel jobs or assume a stale lease is safe.
There is no native production worker to drain in this preview. Integrating one
requires its shared maintenance lease and queue protocol before activation.

After the API stops, SQLite's backup API creates a separate frozen Docker volume.
The manager copies that verified snapshot to a new candidate volume. Only the
candidate is migrated on startup. The previous catalog volume, image, companion,
configuration, journal and snapshot are retained. Checks must pass while the
candidate is still paused. The durable `ResumeIntent` record is written **before**
changes are allowed. This is the irreversible rollback boundary.

The current compatibility envelope is deployment protocol 1, worker protocol 1,
catalog schema 6 and `catalog-only`. An incompatible future release needs a
deliberate migration/update of this contract; silently accepting it is prohibited.
No files under existing source/preset/media folders are read or changed.

## Interrupted update and rollback

```powershell
& $nextManager status $install
& $nextManager recover $install
```

Before `ResumeIntent`, recovery starts the retained previous release against its
unmodified catalog. Partial candidate volumes and files remain for diagnosis.
At or after `ResumeIntent`, recovery is forward-only: it verifies/restarts the
candidate. `rollback` explicitly refuses to rewind history after that boundary,
even when a resume response was lost. A torn journal is a manual reconciliation
case; it is never silently discarded. A failed initial install has no previous
catalog: retain it and use a fresh installation directory after fixing the cause.

Newer schemas are refused rather than downgraded. If health checks or snapshot
verification fail, do not manually remove pause markers or edit journal records.
Do not remove Docker volumes to get past a failure. No cleanup command is provided.

## Restore a backup into a separate, paused installation

Each successful `backup` or pre-update snapshot has a directory under
`<installation>\backups\<backup-id>` containing `restore-point.json`, `files.json`
and the configuration/worker-state copy. The catalog is in the Docker volume
named by the restore point. Preserve **both** the installation tree and Docker
volumes. These same-machine snapshots are not an off-machine disaster backup.
Use Settings' SQLite export for a separately stored catalog backup; complete
off-machine image/configuration/journal transport is not automated here.

```powershell
$restored = 'C:\LibraryData\LibraryRestored'
$backupId = '<completed backup directory name>'
& $manager restore $install $restored $backupId 5081
Get-Content (Join-Path $restored 'restore-review.json')
```

Restore checks the frozen catalog hash, schema, SQLite integrity and backed-up
file hashes, and writes to a new volume/directory. It starts nothing. Configuration
and companion journals are retained in `restored-state` for review, never blindly
activated: newer journal history may describe physical actions that cannot be undone.
Review pending work, roots and newer media/disc/companion history. Only after that
review, acknowledge the exact report:

```powershell
$reviewHash = (Get-FileHash (Join-Path $restored 'restore-review.json') -Algorithm SHA256).Hash.ToLowerInvariant()
& $manager approve-restore $restored $reviewHash
```

This starts only the restored catalog/review UI on the chosen localhost port.
It does not restore media, perform optical recovery, enable pairing or replay
native operations. Active/unreconciled work still blocks resume.

## Storage, failure evidence and troubleshooting

- `installation.json`: installation identity and localhost port. Do not edit.
- `events`: append-only, hash-linked update records; NTFS publication is flushed
  and refuses overwrite. Partial records fail closed.
- `releases`: immutable, hash-verified delivery bundles; previous versions stay.
- `backups`: verified configuration/journal files and catalog volume references.
- `worker-state`: reserved private native state; no key is created by setup.
- Docker volumes: live catalog, frozen snapshots and candidates; names/ownership
  are recorded in update history. Only owned containers are stopped; volumes are
  never deleted. A same-host backup does not survive loss of Docker's data disk.

If Docker is unavailable or port 5080 is occupied, fix that prerequisite without
changing other services. For a separate test installation choose a different
port explicitly. Bad hashes/partial downloads require a fresh trusted download;
never override the manifest. Drain timeout means work/leases need reconciliation,
not a larger timeout or an automatic cancellation. Local backup failures and
failed readiness leave the update recorded and paused. Review `status` and the
exception, then use `recover` where permitted. Docker operations have a ten-minute
deadline and bounded captured logs; an uncertain outcome must be recovered first.

No signing is claimed. Authenticated public distribution, a production worker
connection, native/optical qualification and automatic off-machine backups remain
separate gates. See [developer instructions](development.md) and
[qualification history](end-to-end-qualification.md).
