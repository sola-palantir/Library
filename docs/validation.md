# Validation evidence — 2026-10-12

All file operations used invented fixtures in the isolated Library checkout or temporary test directories. No live media, torrent client, metadata service, Windows host, optical drive or BurnAware process was accessed. The unrelated media-stack repository was not edited. No software was installed on the user's machine, no operational pairing key was created, and no merge or deployment occurred.

## Passed locally

| Check | Command / result |
| --- | --- |
| API and preservation suite | `dotnet test tests/Library.Tests -c Release --no-restore --nologo` — **85 passed** |
| Companion simulations | `dotnet test companion/Library.Companion.Tests -c Release --no-restore --nologo` — **38 passed** |
| Windows companion cross-build | `dotnet publish companion/Library.Companion -c Release -r win-x64 --self-contained false` — passed |
| TypeScript / production assets | `npm --prefix client run build` — passed |
| Formatting | `npm --prefix client run format:check` — passed |
| Browser suite | `LIBRARY_BASE_URL=http://127.0.0.1:5082 CHROMIUM_PATH=/usr/bin/chromium npm --prefix client run test:e2e` — **6 passed** against the locally published same-origin UI |
| Dependency audit | `npm --prefix client audit --omit=dev --audit-level=high` — zero reported vulnerabilities |
| Patch whitespace | `git diff --check` — passed |

Five browser workflows use the real API and synthetic filesystem roots. One isolated UI workflow mocks API responses to inject lost-response errors and exercise season scope/approval/mobile behavior. Neither approach constitutes optical-hardware evidence.

The real backup workflow copies two synthetic episodes, declares an expected three-episode season, packs two whole-episode discs, keeps the missing episode visible, approves an immutable backup, retries approval without creating another set, stages both batches, downloads their manifests and refuses unsigned receipt import. The catalog scan/copy/metadata/legacy staging workflow, qBittorrent synthetic contracts, keyboard navigation, reduced motion, mobile layout, cross-origin controls, XML entity refusal and SQLite backup download also pass.

## Preservation and review evidence

Independent review checkpoints covered copy publication, immutable backup records, staging, native companion boundaries, receipt import, audit/repair and restore. Findings were corrected before publication:

- Hash evidence is bound to the file observed during hashing. Adversarial replacements before publication, after publication, before registration and after registration cannot acquire an old hash. Failed recovery clears current verification while retaining history.
- Complete staging rechecks include the immutable manifest. Legacy folder checks can only produce content evidence; schema v4 downgrades old optical claims and retains/invalidates pre-v4 cached copy hashes.
- Permanent physical labels, frozen snapshots and evidence have uniqueness constraints and update/delete guards. Replacement blanks receive distinct identities. SQLite backup/restore retains those protections and history.
- The exact packing solver is checked against an independent exhaustive subset-DP oracle on **120 deterministic randomized fixtures**, each with 6–10 items, for minimum disc count and maximum single-disc fill. Bounded searches label unproved results rather than claiming optimality.
- Receipt signatures require a pinned public key, matching manifest/image, actual hardware mode and complete read-back evidence. Serialized transactional imports reject stale sequences and old success predating newer local adverse evidence. Duplicate imports cannot append/promote old success.
- Companion simulations cover wrong disc/volume label/manifest, stale staged or image data, measured-capacity boundaries, unsupported/nonblank media, no blind replay, lost completion response, interrupted writes, corrupt/orphan journal records, failed reverification and failed verification preflight. Windows write-ahead event publication is write-through; roots are distinct and scoped.
- Audits are disabled by default, bounded and read-only. Corruption/missing expectations create review evidence. Repairs produce approval-required independent copies to unused paths.
- Restore writes a durable incomplete marker before creating its new catalog. Faults before creation and after copying retain the marker. Startup refuses incomplete restores and requires explicit report-digest acknowledgement for completed restores. Standalone source backups are opened immutably without creating WAL/SHM sidecars.

## Actual UI screenshots

- [Backup planning with a missing episode](screenshots/backup-plan.png)
- [Permanent labels and staged-disc history](screenshots/backup-history.png)
- [Library and inspector](screenshots/library.png)
- [Copy preview](screenshots/copy-preview.png)
- [Settings and integrations](screenshots/integrations.png)
- [Mobile settings](screenshots/mobile-settings.png)

Screenshots were captured from the running UI and visually reviewed. All titles, paths and tiny file payloads are synthetic. Artwork remains a CSS placeholder.

## Boundaries and remaining qualification

- **Hardware:** Windows x64 output compiles, but native COM/device calls, actual BD-R/DL capacity, physical UDF burning/read-back, tray/exclusive locking, power loss, cache behavior, firmware and Windows filesystem semantics remain unqualified. Simulations never import as optical verification. Drive model and actual media are still setup prerequisites.
- **Companion workflow:** local CLI commands and signed receipt import are manual. Docker does not remotely start a burn or receive live progress. It cannot know about a later Windows failure until its receipt is imported. BurnAware uses the same frozen image but its GUI choices cannot be enforced. Identity is the embedded permanent ID/label; deliberately cloned identities cannot be distinguished.
- **Restore:** catalog and companion state must be reconciled together; restoring an older journal cannot establish that an already-attempted blank is unused. No restore erases media or rewinds physical activity.
- **Docker runtime:** previous MCR SDK image configuration/layer pulls returned **403 Forbidden**, although registry/manifest HEAD requests succeeded. Compose configuration passed previously; no image build/container runtime success is claimed. No permissions, registry credentials or network controls were changed.
- **Browser/tool downloads:** Playwright CDN downloads were blocked locally; the installed Chromium ran all browser checks. The SDK was obtained from Microsoft's supported direct download endpoint after installer fallback access failed.
- **Scale/integrations:** no 20–30 TB performance benchmark, Windows Docker Desktop bind-mount validation, live qBittorrent, Plex/Jellyfin or real metadata provider testing. Those integration limits remain in [integrations.md](integrations.md).
- **Remote CI:** the earlier foundation commit passed GitHub Actions. The expanded workflow runs API tests, companion simulations, cross-build, browser checks and a separate Windows compilation job. Exact final commit and CI result are reported in the delivery handoff; hardware actions are excluded from CI.
