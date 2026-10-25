# Deployment qualification

The release coordinator and packaging scripts are implemented for a disabled-production Windows control plane. Qualification on 2026-10-25 used isolated Windows NTFS installation directories and Docker Desktop named volumes. It did not adopt a live catalog, modify media/presets, create credentials, enable pairing or burn discs.

The delivery bundle has a prebuilt API/UI image archive, self-contained Windows manager and companion, version/protocol/schema manifest and per-artifact SHA-256. It is explicitly unsigned. The manual packaging workflow uploads CI artifacts only and contains no registry push, public release, installation or deployment step.

Production worker connection/pairing, background services, real-media processing, optical hardware and off-machine disaster recovery remain unqualified and disabled. No release workflow has been dispatched or public package published by this work.

## Passed checks

- **190 backend tests**, including **26 deployment cases**: incompatible schema/protocol/mode, corrupt/partial artifacts, path collisions, competing manager locks, altered retained components, journal chaining/partial records, journal-to-manifest identity, active work, backup failure, failed readiness, interruptions at each pre-resume phase, lost resume response, guarded rollback, restore-review mismatch, corrupted backups, SQLite foreign-key damage and newer-schema refusal.
- **86 companion tests**, **17 client policy tests**, and frontend production build.
- **12 Linux fixture browser workflows** and **one packaged Windows control-plane browser workflow** passed with the already-installed Chrome browser. The Windows check confirmed paused media controls and unavailable production worker/transfer routes.
- The final packaged workflow passed **18 checks** with actual self-contained Windows executables and Docker images: versions/capabilities, bad manifest refusal, installation/readiness, mutation refusal while paused, manual snapshot, stop/start, coordinated update, retained settings, a snapshot created by the updated release, rollback refusal after resume, offline restore, quarantined native journal byte equality, review gating and disabled pairing.
- The first packaged backup exposed read-only SQLite WAL-header behavior. After the fix, cold snapshots and restoration passed. Recovery of that original failed test reopened its retained original catalog with its synthetic settings intact, then stopped normally.
- ZIP SHA-256, manifest SHA-256 and per-file hashes were verified. Windows runtime **10.0.13** is explicitly selected; its original `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are present in each Windows component. No code signature is claimed.

The packaged update changed release versions with **schema 6 on both sides**. Startup migration ordering is exercised, but this is not qualification of an invented future schema migration. Every future schema change needs its own upgrade/restore fixtures. Crash boundaries are injected contract tests, plus actual recovery from a real backup failure; no physical power-cut test is claimed.

## Retained local evidence

All generated artifacts and catalogs remain ignored, outside source control:

- `.local/delivery-qualification/bundle-h.zip`: terminal unsigned local preview `0.2.0-preview.8` (qualification identifier, not a published release).
- ZIP SHA-256: `db142a6d605dc56311293dae85a767d0f1ef4f135f811f3f5bc6b90a27e840d5`.
- Manifest SHA-256: `29120a77bdc52127161da96de27e444dec652533524a765a857b05b133c72cf5`.
- Image content ID: `sha256:94ba0f5b72cea9c7eaebd42ebf998b80990c25a0bc0bc4b97c396cd9135f5db3`.
- `.local/delivery-qualification/run-d/result.json`: terminal 18-check packaged result; `manager.log`, installation journal and restore report are alongside it.
- `.local/delivery-qualification/recovered-first-failure.log`: actual recovery of the initial failed backup.
- `.local/delivery-backend-terminal.log`, `.local/delivery-companion-final.log`, and `.local/delivery-browser/`: regression evidence.

Snapshots live in dedicated labeled Docker volumes referenced by the local journals. No volume cleanup is automatic. These same-machine snapshots do not protect against loss of Docker's data disk. The earlier failed/partial package attempts and test evidence were retained.

All test APIs and the Linux fixture API were stopped after qualification. The supplied movie/episode, original presets and retained RF20 output were not processed or changed during this milestone.

## Safety boundary and remaining release gates

The manager operates a new, owned installation only. It pauses catalog mutations, drains API job/audit/lease counts, stops its API, creates a frozen SQLite snapshot, clones it into a candidate volume, and checks readiness before resume. It never migrates the previous volume in place. A flushed `ResumeIntent` record makes subsequent recovery forward-only. Restoration targets a fresh volume/home; older worker journals are quarantined and cannot authorize repeated physical operations.

The source catalog must have no running container and no WAL/SHM/journal sidecar. The helper mounts it read-only, checks its hash before/after, and uses SQLite's immutable URI only for that cold source. This follows [SQLite's immutable-file contract](https://www.sqlite.org/uri.html) and [Microsoft.Data.Sqlite URI filename support](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings). It is not permission to treat a live WAL database as immutable.

Production worker queue draining is **not** claimed: the worker is deliberately disconnected. Its maintenance lease/queue coordination must be integrated and qualified before production activation. Restored native state remains quarantined for the same reason.

Public delivery requires explicit approval of the GitHub Release/registry destination and visibility, a Library licensing/distribution decision, and a trusted checksum delivery channel. Signing requires an available approved signing identity; none was used or created. The manual packaging workflow has read-only repository permissions and uploads build artifacts only when explicitly dispatched. It has no registry push, GitHub Release creation or deployment step.

## Developer reproduction

On a Linux build host with the documented SDK, Docker and Python 3.10+, use fresh output directories:

```sh
python3 scripts/package-release.py --version 0.2.0-preview.1 --output .local/release/first
python3 scripts/package-release.py --version 0.2.0-preview.2 --output .local/release/second
```

On Windows, with Docker Desktop already running and the two built directories locally available:

```powershell
./scripts/qualify-deployment.ps1 -BundleA .local/release/first -BundleB .local/release/second -EvidenceRoot .local/release/qualification
```

The qualification script stops only its labeled test containers in `finally`, retains volumes/evidence and uses invented metadata. These are developer checks, not operator installation prerequisites. See the [operator guide](deployment.md) for normal use.
