# Validation evidence — 2026-10-12

All file operations used generated fixtures in the isolated `/workspace/Library` checkout or automatically created temporary test directories. The configured `media-stack` repository was not edited. No host installation, live media connection, remote push, merge or deployment was performed.

## Passed locally

| Check | Command / evidence | Result |
| --- | --- | --- |
| Backend integrity suite | `dotnet test tests/Library.Tests -c Release --no-restore --nologo` | 25 passed, 0 failed |
| TypeScript and production bundle | `npm --prefix client run build` | Passed; React assets generated |
| Frontend formatting | `npm --prefix client run format:check` | Passed |
| Chromium browser / API suite | `CHROMIUM_PATH=/usr/bin/chromium npm --prefix client run test:e2e` | 3 passed |
| Production artifact | `dotnet publish server/Library.Api -c Release --no-restore -o .local/publish /p:UseAppHost=false` plus React assets in `wwwroot` | Published and served locally |
| Same-origin production UI | `LIBRARY_BASE_URL=http://127.0.0.1:5080 CHROMIUM_PATH=/usr/bin/chromium npm --prefix client run test:e2e` | 3 passed against the locally published production artifact |
| Catalog backup | Download `/api/backup`; open with SQLite and run `PRAGMA integrity_check` | `ok` |
| Compose configuration | `docker compose config --quiet`, base and disc overlay, synthetic root mappings | Both passed |
| Production npm audit | `npm --prefix client audit --omit=dev --audit-level=high` | 0 reported vulnerabilities |
| Patch whitespace | `git diff --check` | Passed |

The integrity suite covers independent destination data, source/sidecar preservation, repeat scans without rehashing, offline catalog retention, unavailable roots, stale previews, same-length source replacement with restored mtime, exact/case collisions, cancellation before execution, partial-copy restart, publish-before-catalog recovery, recovery with an offline source, worker startup recovery, low free space via sparse synthetic fixtures, root/path restrictions, symlink rejection, preview nonmutation, NFO external-entity rejection and unknown-field preservation, manual metadata locks and revisions, decimal-capacity packing, oversized-file rejection, disc staging, positive and corrupt read-back, and Plex movie/TV naming.

Browser coverage exercises the real API: scan, inspector, copy preview and approval, completed verification, metadata before/after review, whole-file disc plan, staged files, keyboard search, no-result state, mobile navigation, reduced-motion mode, horizontal overflow check, cross-origin/incorrect-content-type rejection, XML entity rejection and SQLite backup download. Browser-side uncaught errors are asserted absent in the primary workflow.

## Screenshots of the running UI

- [Library and inspector](screenshots/library.png)
- [Copy approval preview](screenshots/copy-preview.png)
- [Mobile settings](screenshots/mobile-settings.png)

Artwork is generated with local CSS as a placeholder. File sizes are deliberately tiny synthetic payloads; they are not mock claims about a real collection.

## Blocked or not exercised

- **Docker image pull/build/runtime:** `docker pull mcr.microsoft.com/dotnet/sdk:10.0` failed after retries with `error pulling image configuration: download failed after attempts=6: Forbidden`. No container startup success is claimed. Compose syntax and the equivalent locally published .NET/frontend artifact are independently checked.
- **Downloaded Playwright Chromium:** CDN requests returned HTTP 403 `Domain forbidden`. The installed `/usr/bin/chromium` successfully ran the browser checks instead.
- **Default .NET installer:** its fallback `ci.dot.net` endpoint returned HTTP 403. Microsoft’s `builds.dotnet.microsoft.com` direct SDK endpoint worked, allowing the local .NET 10.0.401 build/test toolchain.
- **External integration:** no live qBittorrent, Plex, Jellyfin, Windows host, BurnAware process or physical Blu-ray was used. Read-back tests used a separate synthetic directory.
- **Scale and hardware:** no 20–30 TB performance run, Windows Docker Desktop bind-mount test, actual power-loss fault injection, optical capacity check or physical disc read-back. Cancellation during long real-media I/O and Windows filesystem semantics need a controlled pilot.
- **CI / publication:** GitHub Actions is prepared but not run remotely. Local commits are prepared for review; publication requires parent authorization.
