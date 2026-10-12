# Library

A local media catalog and preservation workspace for movies and television. React + TypeScript, ASP.NET Core, SQLite. Built for a Linux container on Windows Docker Desktop.

**Early implementation, using synthetic fixtures.** The six navigation areas connect to working APIs; this is not a static mockup. Hardware burning, live torrent clients and Plex/Jellyfin servers have not been connected or tested.

![Library with synthetic titles and inspector](docs/screenshots/library.png)

## Implemented

- **Library / Inbox:** paginated catalog, filtering, keyboard search, artwork placeholders, resizable inspector, read-only incremental scans. Separate title, edition/episode, asset and physical-copy records. Missing files remain cataloged as offline.
- **Organize:** two panes, drag/drop or keyboard-accessible selection to stage a preview; approve independent copies with Plex-compatible paths. Sources and sidecars remain untouched. SHA-256 source-stream hash is compared with a complete destination read-back before publication. No overwrite, hardlink or deletion operations.
- **Metadata:** bulk leave/set/clear changes, manual locks, optimistic revisions, old/new field and proposed-path previews. NFO import preview and export can preserve unknown XML fields when the original NFO is supplied. DTDs and external entities are prohibited.
- **Discs:** first-fit-decreasing whole-file batches; default 50,000,000,000-byte discs with a configurable 1,000,000,000-byte reserve. Staging copies and JSON manifests for BurnAware Free. Planned, reported-written and read-back-verified statuses are separate. Oversized files are rejected; seasons may span discs.
- **Jobs / Settings:** SQLite durable journal, one active background file job, queue limit of 100, cancellation/retry, restart recovery, explicit path mappings, configurable retention review (20 days by default), JSON export and consistent SQLite backup.

No open-source license has been selected. No credentials, personal media inventories or tracker URLs belong in this repository.

## Try locally with synthetic data

Prerequisites: **Linux**, .NET SDK **10.0.401**, Node **24**, Python 3. The service intentionally uses Linux filesystem primitives; on Windows, use Docker Desktop or WSL for development. Follow [Windows Docker Desktop setup](docs/windows-docker.md) for later installation.

```sh
python3 scripts/synthetic-fixtures.py
npm --prefix client ci
# Terminal 1
scripts/run-api.sh
# Terminal 2
npm --prefix client run dev
```

Open **http://127.0.0.1:5173** and select **Scan source**. The fixture files are small text payloads with media extensions, not playable movies. Their invented titles and paths can be shared publicly. Runtime data stays in ignored `.local/`.

If `dotnet` is installed outside PATH, use `DOTNET=/absolute/path/to/dotnet scripts/run-api.sh`. A development-only proxy translates the UI origin to the loopback API origin; production serves the UI from the same ASP.NET host.

## Validate

```sh
dotnet test tests/Library.Tests -c Release
npm --prefix client run build
# With the synthetic API and UI already running:
cd client
npx playwright install chromium
npm run test:e2e
# Or use an existing browser:
CHROMIUM_PATH=/usr/bin/chromium npm run test:e2e
```

The browser test performs real operations on the synthetic fixture roots. Use a dedicated fixture workspace. See [validation evidence](docs/validation.md), [architecture and safety boundaries](docs/architecture.md) and [interoperability references](docs/references.md).

## Current limits

This is a reviewable initial implementation, not a production release. Filename inference is deliberately conservative and needs review. There is no metadata-provider lookup, artwork download, qBittorrent API integration, direct burning, host helper, FileBot dependency, or automatic source cleanup. The retention clock starts when a source is first observed, not at torrent completion. NFO sidecars are downloaded/exported, never rewritten in torrent directories. Existing library files can be scanned, but currently become eligible for disc planning only after a verified independent-copy job. Full-scale 20–30 TB performance and Windows-mounted filesystem behavior remain to be validated.
