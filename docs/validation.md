# Validation evidence — 2026-10-12

All file operations used generated fixtures in the isolated `/workspace/Library` checkout or automatically created temporary test directories. The configured `media-stack` repository was not edited. No host installation, live media connection, remote push, merge or deployment was performed.

## Passed locally

| Check | Command / evidence | Result |
| --- | --- | --- |
| Backend integrity suite | `dotnet test tests/Library.Tests -c Release --no-restore --nologo` | 31 passed, 0 failed |
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


## Follow-up quality review

The production screenshot was visually inspected: the catalog table, inspector, verification badges, navigation and synthetic paths are legible. Artwork is a local decorative placeholder; it is not fetched movie artwork. Browser tests use the published same-origin UI and actual APIs, not mocked catalog responses.

The follow-up audit corrected two integrity gaps: (1) cached verification previously depended on size/mtime and could survive a replacement with restored timestamps; scans and disc-plan admission now also require device/inode/ctime identity, with identity checked on the open file handle during copy/verification; (2) an offline catalog entry could be silently reused as a destination for a different asset; catalog-reserved paths now reject new copy plans and execution. Canonical root, same-directory alias and declared Windows mapping checks were strengthened. A tested transactional v1→v2 schema upgrade preserves catalog/history while clearing legacy hashes without identity evidence.

Additional tests cover the two regressions, overlapping/symlink/Windows roots, repeated schema upgrade, and restoring catalog, copies and operation history from a SQLite online backup. A successful read-back status records filesystem hash evidence at the time of the read; the application cannot authenticate a physical Blu-ray drive from a configured folder. Source files remain unchanged by these workflows; active torrent seeding is not queried or asserted.

Registry investigation: a read-only HEAD request to `https://mcr.microsoft.com/v2/` and to the pinned SDK manifest both returned HTTP 200, while `docker pull mcr.microsoft.com/dotnet/sdk:10.0.401` again failed on image configuration/layer transfer with `download failed after attempts=6: Forbidden`. Microsoft's [container image documentation](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images) and [official docker repository](https://github.com/dotnet/dotnet-docker) identify MCR as the image source; the Docker Hub catalog links do not establish a separate official mirror. No verified alternate official registry was found. No permissions, proxy settings, network controls or registry credentials were changed.

### Implemented versus unconnected integrations

| Area | Implemented | Not implemented or untested |
| --- | --- | --- |
| Metadata | Catalog title/year/plot/genre editing; leave/set/clear; locks/revisions; before/after preview | Provider search/matching, artwork fetching, title merge/split, edition/episode editor |
| NFO | Manual XML import preview; export/download; unknown-field preservation when original XML is supplied; DTD/XXE rejection | Automatic torrent-sidecar writes; provider-specific full round trips; bulk folder import |
| Plex | Movie, edition and single-episode naming suggestions | Plex API connection, library refresh, playback, actual NFO-agent integration test; complex multi-episode naming |
| qBittorrent | Read-only source workflow; independent copies; configurable review window from first observation | Torrent API, completion/seed-time/state checks, torrent removal; no deletion occurs |
| Jellyfin | Separate catalog/copy model and NFO-based interoperability foundation | Server integration or validated Jellyfin round trip |
| BurnAware / discs | Whole-file packing, independently verified staging, manifests, explicit written/read-back states | BurnAware process control, Windows helper, physical burn or optical read-back test |
