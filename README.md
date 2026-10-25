# Library

Library is a local media catalog and preservation workspace. The web UI, API and
SQLite catalog run in Docker Desktop; a native Windows companion owns the file
and optical-device boundary.

**Preview: catalog and review mode only.** Production media processing, persistent
worker pairing and Silo HDR processing remain off. No physical disc, Plex device
or full-film quality qualification is claimed.

## Install and open

The delivery path uses a prebuilt container and a self-contained Windows package.
Normal use needs Windows x64 and an already running Docker Desktop with Linux
containers. It does not need Git, Node, Python, an SDK or a separate .NET runtime.

**No public release has been published yet.** The repository includes a manual
packaging workflow and local release qualification. Preview bundles are unsigned;
authenticated public distribution, licensing and signing need separate approval.
Use only a trusted bundle and verify its published ZIP and manifest hashes.

1. Extract the approved bundle into a new folder.
2. Choose a new installation directory beneath an existing private NTFS folder.
3. Run the bundle's manager in PowerShell, supplying the trusted manifest hash:

   ```powershell
   $bundle = 'C:\LibraryDownloads\library-0.2.0-preview.1'
   $install = 'C:\LibraryData\Library'
   $manager = Join-Path $bundle 'manager\Library.Manager.exe'
   $manifestHash = '<trusted lowercase SHA-256 of release.json>'
   & $manager install $install $bundle $manifestHash
   ```

4. Open **http://127.0.0.1:5080** after the manager reports success.

The manager verifies artifact hashes, loads the image, creates isolated catalog
storage and checks compatibility/readiness before enabling catalog changes.
It does not adopt an existing catalog, mount media, create credentials or install
a background service. The Windows companion is included but remains unpaired.

## Start, stop, back up and update

Keep the extracted bundle and use its manager:

```powershell
& $manager status $install
& $manager start $install
& $manager backup $install
& $manager stop $install
```

For an update, verify the next bundle, then run its manager with
`update <installation-directory> <next-bundle-directory> <trusted-manifest-hash>`.
The manager pauses changes, drains active work, backs up catalog/configuration and
journal, migrates a new catalog copy, checks readiness and resumes. Previous
artifacts and matching snapshots remain. Original media and saved presets are
outside this workflow.

If interrupted, use `status` then `recover`. Recovery can return to the previous
release before resume. After resume begins, it refuses an automatic history
rewind. Restore creates a separate paused installation and requires explicit
review before opening it. Same-machine snapshots are not off-machine backups.

See the [step-by-step operator guide](docs/deployment.md) for exact update,
restore and recovery commands, data locations and troubleshooting.

## Development and qualification

The detailed developer setup, automated tests, Wintermute-specific tooling and
retained RF20 playback checklist are in the [developer guide](docs/development.md).
They are not daily installation steps. The retained Godzilla sample requires
human motion/audio and real Plex/device checks; packet/decode checks do not prove
playback quality. Do not regenerate it merely to run deployment tests.

- [Deployment implementation and qualification](docs/deployment-qualification.md)
- [End-to-end catalog/archive/recovery evidence](docs/end-to-end-qualification.md)
- [Native execution boundary and remaining gates](docs/native-adapter.md)
- [Media and track-selection policy](docs/media-inspection.md)
- [Archive audit and offline catalog restore](docs/audits-restore.md)

No open-source license has been selected. Public release publication is gated.
