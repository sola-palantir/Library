# Validation evidence

## Retained sample acceptance - 2026-10-25

[The complete sample record](color-and-pgs-qualification.md#retained-sample-technical-acceptance-and-visual-review---2026-10-25)
supersedes the earlier output-lifetime gap below. The qualification session now
retains one output until technical checks and an output-bound visual decision
finish; missing checks, changed output, stale hashes and premature disposal fail.
The native synthetic comparison preflight passed before one fresh real encode.

That encode revalidated the approved source/window SPS evidence and passed full
inventory/frame/packet/duration and video/audio decode checks while still retained:
**288 frames, 1,203 Japanese audio packets, four regular and six SDH English PGS
packets**, with all copied payloads and relative timing preserved. The complete
**11,237,343-byte** output SHA-256 was
`c921be9548f3f87065cf53e7db28c8534738c84ff3b1c192a6745ffa2cb88c24`.
It took **79.80 seconds** and peaked at **589,148,160 bytes** committed memory.

Actual inspection of three matched video frames/native crops and the two
subtitle renders found reduced grain/fine texture, no obvious gross framing or
brightness shift, and matching source/output subtitle appearance at the inspected
cue. Image hashes and the complete movie hash were rechecked before recording
`accepted-local-sample` and disposing the buffer. No worker/catalog receipt,
published movie or reservation was created. Local diagnostic images/evidence
remain ignored. This is limited static visual review, not motion, whole-film,
absolute-color, perceptual-parity or device qualification.

Local regressions passed **156 backend, 78 companion and 17 client-policy tests**
and the client build. Five new lifecycle tests cover retained acceptance and
failure paths. The actual Windows comparison preflight exercised EOF-draining
rendering and a terminal synthetic rejection. The unchanged heavy resource and
storage suites were not rerun. CI browser/Windows results are reported for the
new commit. Production processing, persistent pairing and Silo remain disabled.

## Approved Godzilla range/sample qualification - 2026-10-25

[Detailed results](color-and-pgs-qualification.md#earlier-approved-short-encodes-retrospective-evidence)
record the user's narrow range approval, complete inspected NAL coverage and
the distinction between reconciled technical evidence and a published asset.
All **23 observed SPS occurrences** matched the approved hash; **1,716 slices
across 429 pictures** resolved through verified PPS/SPS references.

Two sequential 12-second RAM encodes completed. The first stopped at an early
inventory-pipe close. The repeat fully decoded, then inventory refused absent
source versus discovered output PGS dimensions. Read-only source presentation
inspection resolved those fields from matching packet payloads. The unchanged
validators then accepted retained inventory/frame/packet/duration evidence:
**288 frames, 1,203 Japanese audio packets, 4 regular and 6 SDH English packets**.
An exact copied-packet count through the requested clip end also passed.

Video payload changed from **42,369,694 to 5,510,443 bytes (86.99% smaller)**.
The complete buffer was **11,237,343 bytes** and measured A/V duration **12.836
seconds**, including copied-audio pre-roll. Repeat encode time was **107.21
seconds**, with **589,041,664 bytes** peak committed memory under unchanged limits.

The disposed output has no retained whole-container hash, accepted worker
receipt or output visual/device review. No third encode was run, no media file
or reservation created, and no production gate changed. Source observations and
original presets remain unchanged; older failed residuals remain retained.

Local regressions passed **156 backend, 73 companion and 17 client-policy tests**
and the client build. New synthetic cases reject header conflicts, unresolved
PPS/SPS references, invalid/short windows, cross-source/window review reuse and
malformed PGS presentation data. CI browser/Windows results are reported for
the pushed commit. Production processing, pairing and Silo remain disabled.

## Read-only range, PGS rendering and HDR feasibility - 2026-10-25

The [source-header evidence](color-and-pgs-qualification.md#source-header-follow-up---2026-10-25)
now distinguishes Godzilla's absent container declaration and absent AVC range
flag from explicit limited/full-range declarations. The inspected SPS supports
H.264's limited-range default, but the production declared-range gate is unchanged.
The proposed per-title policy and remaining active-SPS qualification are recorded
in that report. No new real-film encode or remux was attempted.

Actual pixel inspection of one bounded in-memory image showed readable captions
from both English PGS source tracks. This is a source rendering check at one
moment, separate from the previous packet-verified remux; it is not output-device
playback or full-title sync qualification. No image/media file was saved.

The [Silo feasibility read](hdr-preservation-feasibility.md) found DV profile 8,
compatibility ID 1, and both Dolby Vision and HDR10+ side data in all 23 decoded
frames of a one-second probe. No Silo output was created. Its processing stays
blocked; a Dolby Vision option alone does not qualify dual-metadata preservation.

The final local suite passed **156 backend, 66 companion and 17 client-policy
tests**, plus the client production build. The bounded header reader was also
rerun on Windows after hardening and returned identical source evidence. The
unchanged heavy native encode/lifecycle suite was not repeated for this
qualification-only helper/docs change; its earlier results remain below.
Browser and Windows cross-build results are supplied by CI for the pushed commit.

Both inputs retained their recorded size/mtime/native identity; original preset
SHA-256 values were unchanged. Full source contents were not rehashed. Existing
failed reservations and the zero-byte partial remain retained. No catalog record,
credential, listener, persistent pairing, deployment or production activation
was created. Private metadata and scripts stay under ignored `.local/`.

## Color-range policy and PGS event qualification - 2026-10-24

[Detailed evidence and primary references](color-and-pgs-qualification.md)
separate codec signaling from actual sample-value conversion. All five synthetic
pixel-ramp cases passed. Unknown range now stops before encoding; no validator
exception or automatic limited-range assumption was added.

A synthetic seek test reproduced misleading nominal duration in streaming MKV.
Native output now omits the estimate through Matroska live mode and continues to
validate complete packet-derived duration at EOF with unchanged bounds. One
bounded repeat of the same supplied-copy PGS window passed all copied-payload,
inventory, timing and video/audio decode checks: **309 H.264, 1,203 Japanese DTS-HD
MA, four English regular PGS and six English SDH packets**. Actual start was
**163.830 seconds**, duration **12.887 seconds**, including keyframe pre-roll.

The single 50,955,215-byte RAM output was disposed; no real output file, reservation
or catalog record was created. Source identity/size/mtime and original presets
remain unchanged. This is stream-copy qualification, not a real-film encode,
subtitle-rendering or perceptual-quality claim. Production processing, persistent
pairing and Silo Dolby Vision remain blocked. Earlier sections below record the
preceding attempts and their limits.

Final local regressions passed **156 backend, 59 companion and 17 client-policy
tests**, plus the client build. The full actual Windows native suite passed again:
buffered input, five color-oracle cases, the two-mode seek-duration oracle,
resource/termination and owner-exit checks, synthetic remux/encode/recovery,
cancellation retention, and 1080p encoding. No real-media retry beyond the one
documented same-window repeat was performed.

## Native supplied-copy diagnosis - 2026-10-24

The README and native execution foundation were pushed as
[`54bc02a`](https://github.com/sola-palantir/Library/commit/54bc02add5002432edb3c17718f0b96b6e94b3c8).
Its [GitHub Actions run](https://github.com/sola-palantir/Library/actions/runs/40132095355)
passed both Linux tests/browser checks and Windows compilation. The following
diagnostic continued separately under authorization for supplied test copies.

- On the verified native FFmpeg 9.0.2 build, a ten-second null-output demux read
  **659,709,663 bytes with English PGS**, versus **33,800,355 bytes without PGS**.
  Demux committed memory remained about 29 MiB. A 15-second input boundary and
  bounded queue still read 645,592,211 bytes. These controls do not eliminate
  sparse-subtitle read-ahead. No media file was produced by these probes.
- One ten-second x265 CRF 22/Slow/8-bit encode completed into bounded memory:
  **5,433,491 bytes**, **596,799,488 bytes peak committed memory** (569 MiB),
  about 69 seconds. The 1 GiB job limit was unchanged. This buffer was disposed
  without independent validation. It established completion, not acceptance.
- One further encode retained its buffer for independent inspection and was
  **rejected by the existing color-range check**: the H.264 source has no declared
  `color_range`, while HEVC output declares `tv`. No assumption that unknown
  means limited range was made. Output was again 5,433,491 bytes, with 240 video
  packets. All **938 Japanese DTS-HD MA packet hashes/sizes and timestamps**
  matched the source. Both English regular/SDH PGS stream entries were retained,
  but there were **zero subtitle events** in the opening window. Frame and
  full-decode validation were not reached after inventory refusal.
- Both encodes ran sequentially, each with at most one bounded in-memory output.
  Buffers were disposed. **Zero new media files, reservations or catalog records**
  were created. The earlier failed zero-byte partial/reservation remains retained.
  Source size, modification time and native identity were unchanged; source
  content was opened read-only and was not fully rehashed for this diagnostic.
  Silo was not processed. No real media crossed a network endpoint.
- Anonymous validation-pipe qualification passed a 1 MiB byte-exact synthetic
  transfer, early-exit refusal and timeout termination. New regression coverage
  rejects an unknown source range silently becoming either TV or PC range.

Native completion does not identify whether FFmpeg version, process architecture
or another changed condition explains the earlier Linux memory failure. No
validated real-film output, perceptual-quality equivalence, active PGS payload
preservation or full-title processing is claimed. Production activation stays
disabled. The next bounded work is synthetic color-range handling and then a
sample window containing actual subtitle events, without relaxing acceptance.
Private logs/packet metadata are in ignored `.local/native-godzilla-diagnosis/`.

The follow-up regression run passed **152 backend, 58 companion and 17 client-policy
tests**, plus the client production build. Existing acceptance checks remain strict;
the added range cases exercise both TV and PC declarations against an unknown source.
The complete native adapter qualification also passed again: anonymous buffered
input, eight resource/lifetime groups, synthetic remux/encode and 1080p encode,
validation/recovery without repeat processing, and cancellation retaining its partial.

## Native processing adapter and process controls - 2026-10-24

The bounded Windows executor path is implemented and qualified with generated
fixtures. Production activation remains disabled; no network service or grant
was used for this milestone. See [architecture completion checklist](native-adapter.md).

- **150 backend, 57 companion and 17 client-policy tests passed.** Windows x64
  publish, client formatting and final service image build passed. The existing **15 native Windows lifecycle
  scenarios passed again**, including source/output hardlinks, junction rejection,
  no-overwrite collisions and independent process-exit recovery at validated and
  published checkpoints. Browser flows were unchanged and were not rerun here.
- **Eight native resource/lifetime scenario groups passed.** Timeout and caller
  cancellation stopped parent/child processes in about five seconds. A native
  Win32 768 MiB commit request under a 512 MiB Job Object cap failed with error
  1455. Stdout/stderr overflow killed the process and retained bounded data. The
  process-count cap was enforced, CPU hard-cap readback/accounting passed, and an
  independent owner exited without cleanup while its root/child were running;
  Windows killed both through the non-inherited kill-on-close Job Object.
- A synthetic remux and 320×180 x265 sample passed the shared inventory, packet,
  timing, disposition, decode and hash checks. Both recovered from a validated
  checkpoint using the default-disabled worker, with one processing run each.
  A cancelled encode retained one zero-byte partial and refused automatic retry.
- A separate **1920×1080, 24 fps, two-second x265 sample** preserved all 48 frames,
  dimensions and timestamps. Its selected Japanese PCM, English descriptive PCM
  and English regular/forced/SDH SRT packets matched byte-for-byte; confirmed
  commentary and the French subtitle were excluded. Video packet payload fell
  from 3,649,193 to 1,850,563 bytes (49.29%) for the same 48 synthetic frames.
  Output was 2,241,824 bytes; peak committed memory was 576,118,784 bytes
  (549.43 MiB), below the unchanged 1 GiB cap. This is technical fixture evidence,
  not a real-film compression prediction or perceptual-quality comparison.
- FFmpeg 9 rejected the legacy `-enc_time_base -1` spelling on the first invented
  encode. The native adapter now uses documented `demux` semantics. That failed
  partial remains retained; no input was changed or partial automatically deleted.
- Native output is a bounded streaming MKV. Duration is measured from every A/V
  packet at EOF and checked against the original sample bounds; copied source
  duration tags are not evidence. Full-title seekable mux finalization is still
  gated. The API/Linux validators share the same pure checks and retain their
  existing header-duration requirement.

The 1080p output is retained at
`.local/native-adapter-qualification/media-3226cf2fbe5d47dc808ddbad54a32b46/encode1080-stage/sample-9787017904164086bc296212b77cfadc.mkv`,
SHA-256 `bf521482ff8f3f5fbcee9faac65770e2a0f63c46ab28c31fd1c7b9a89d7ea6bd`.
Native process logs, exact per-job receipts, failed/cancelled fixture evidence
and regression results remain under ignored `.local/native-adapter-qualification/`.
No supplied film content was probed or encoded. No persistent pairing, service, production
catalog import, live migration, commit, push or deployment was performed.

The network-disabled image build missed its restore cache and failed. The normal
previously approved build used cached dependency layers and passed; image
`library-media:native-adapter` is
`sha256:0ebe65f0c3b30e29ba637424e84b704dfb042e594b9286a7b3eec5277eb64e06`.
It was built only, not started or deployed.

## Native tools and temporary worker protocol - 2026-10-24

After explicit approval, Gyan FFmpeg/FFprobe 9.0.2 Essentials was downloaded,
matched to the published SHA-256 and extracted into the ignored project tooling
folder with its license/docs. No system PATH or service changes were made.

- **150 backend, 53 companion and 17 client-policy tests passed.** Windows x64 publish and the
  final service image build passed. **One Windows control-plane browser test
  passed**, including HTTP 404 for the disabled qualification route. The other
  12 browser workflows passed in the preceding milestone; they were not rerun.
  Client formatting and `git diff --check` passed.
- One actual Windows synthetic job passed native probe/packet reads, byte-copy,
  no-overwrite publication, output hash verification and recovery without a second
  processing run. The only generated output is 803,603 bytes with the same
  SHA-256 as the invented source. This is not native encode/remux qualification.
- The isolated localhost API accepted one idempotent result. Wrong/expired token,
  job/plan mismatch, conflicting receipt and unknown payload fields were refused.
  One catalog job and one audit entry survived restart; there are zero assets,
  copies or derived assets. Instructions/results alone crossed the connection.
- The grant is cleared and owned test services are stopped. No plaintext token
  was saved, no persistent pairing was created, and no live catalog or real film
  was used. The production native processor remains unavailable.

See [native worker qualification](native-worker-qualification.md) for hashes,
protocol results, output location and remaining gates. Private evidence is in
ignored `.local/native-qualification/`. Earlier sections are historical results.

## Windows execution boundary - 2026-10-24

This refactor preserves schema v6 and all existing catalog/policy/manifest work.
It does not migrate a live catalog or enable a Windows worker connection.

- **146 backend, 50 companion, 17 client-policy tests passed.** The companion
  regression suite ran under Linux; actual Windows behavior was qualified
  separately using the compiled assemblies inside the installed PowerShell
  .NET 10 host. Native FFmpeg/FFprobe are not available on Windows PATH.
- **12 existing browser workflows passed**, followed by **one dedicated Windows
  control-plane browser test**. That test ran the final service image with no
  host mounts, verified the paused-job UI and usable imported review, and
  confirmed scan/processing/export refusal. Its screenshot was visually checked.
- **15 native Windows scenario groups passed** on local NTFS: source inspect/hash,
  publication/reopen, replay and changed-command rejection, one source reservation,
  case-insensitive collision, cancellation before/during output, stale source,
  concurrent writer/worker/parent replacement, changed result, torn journal,
  preserved source hardlinks, rejected multiply-linked output, root overlap,
  traversal and junction refusal. Two independent fixture processes exited at
  validated/published checkpoints; a fresh worker reconciled without processing.
- The restricted environment initially denied hardlink fixture creation with
  Win32 error 5. The approved isolated qualification outside that restriction
  passed. No permissions or security settings were changed.
- Frontend build/format, backend/companion compilation, Windows x64 publish,
  service image build and `git diff --check` passed. The final image is
  `library-media:windows-boundary`, SHA-256
  `357bee5a4a04a10c403ff5b4b7ed2fd5dfbb2212991c830a78c2a3ec8d08cd1c`.
- All test instances were stopped. No actual encodes, real-media output, native
  software install, credential, persistent service, deployment, commit or push
  occurred in this phase. Original preset hashes and both supplied media files'
  sizes/modification times remain unchanged. Earlier test evidence is retained.

The native processor is deliberately unavailable by default. The tested adapter
handles invented bytes and labels results synthetic; such receipts cannot become
verified catalog-media evidence. A native FFmpeg subprocess adapter, process
resource controls, transferred validation logic and authenticated API result
integration remain unqualified/unimplemented. No OS switch is claimed to solve
Godzilla's PGS read-ahead. See [Windows boundary and activation gates](windows-worker.md).
Private logs, native fixtures, screenshot and exact checkout/source observations
are retained under ignored `.local/worker-qualification/`. Earlier sections below
are historical milestones, not claims that the new Windows processing path is enabled.


## Bounded SDR x265 sample - 2026-10-24

The imported 1080p preset supplies the fixed CRF 22 / Slow / 8-bit starting
profile. Both original preset hashes are unchanged. The worker preserves source
dimensions/timing and copies retained audio/subtitles. Encoded samples have
distinct immutable lineage, cannot be promoted to complete library copies, and
cannot use the synthetic-only handoff route.

- **128 backend, 45 companion, 17 client-policy and 12 browser tests passed.**
  Backend coverage includes profile tampering, unknown/HDR/interlaced source
  refusal, frame count/timing/crop changes, copied audio/subtitle payloads,
  sample promotion/handoff refusal, separate opt-in and memory termination.
- The actual service image on the retained isolated ext4 volume encoded one
  invented 1920x1080, 24000/1001-fps three-second source interval. All 72 frame
  timestamps and dimensions matched. Video packet bytes fell from 8,076,893 to
  2,889,471 (64.23%); the MKV was 3,183,762 bytes. Japanese PCM and English
  regular/forced/SDH subtitle packets matched; commentary was excluded. Decode,
  full output SHA-256, atomic publication and second-output refusal passed.
- A separate invented 640x360 sparse-subtitle source also passed with 72 frames,
  copied Japanese PCM and an explicitly empty retained English subtitle track.
  Its sample was 594,355 bytes. This confirms boundary handling, not fidelity
  of subtitle events absent from that interval.
- The first x265 NUMA-pool attempt was refused because affinity calls failed
  under existing container restrictions. The supported no-pool setting passed;
  security settings and strict stderr checks were retained.
- An initial Godzilla attempt reached 6.9 GB and the container recorded an OOM
  kill before any sample bytes were written. Input read-ahead and mux queues
  were bounded, a 1 GiB sampled encoder-memory watchdog was added, and subsequent
  qualification uses a 2 GiB container ceiling. The same source reservation and
  owned output path are retained; no additional variant is created.
- With the new bounds, the same Godzilla job was safely terminated by the 1 GiB
  encoder watchdog after about 52 seconds. It remains failed with one zero-byte
  owned partial, no published file and no derived-asset record. Source size,
  mtime and file identity remained unchanged. **Godzilla/DTS/PGS execution is not
  qualified**, and there is no real-film compression or visual-quality result.
  The precise retained error is `Sample encoder exceeded its 1 GiB
  resident-memory budget; the source is retained and publication is refused.`
  No further real-media attempt or memory-limit increase was made. Sparse-stream
  buffering needs a separate bounded diagnosis before this title can proceed.

Evidence and exact settings are under ignored `.local/encoding-qualification/`;
browser results are under `.local/encoding-browser-final/`. Visual quality was
not assessed, whole-title savings are not predicted, and Silo DV remains blocked.
Test services were stopped and sample execution remains disabled by default.
See [processing contract](media-processing.md) for bounds and remaining limits.

## Approved local sample handoff - 2026-10-24

After explicit approval of the receiving process, localhost binding, single
invented clip, output directory and ten-minute job/hash-bound token, the narrowed
endpoint implementation was accepted and tested. The earlier rejection was not
bypassed. See [sample handoff](sample-handoff.md).

- **111 backend, 45 companion and 17 client-policy tests passed.** Server and
  companion builds, Windows publish, Docker image build, client production
  assets, formatting and whitespace checks passed.
- The actual Windows receiver and its `receive-sample` command route received
  exactly one invented 803,603-byte clip from the retained isolated ext4 volume.
  Independent NTFS hashing matched Linux. Repeat invocation reconciled the same
  output, and the immutable plan/receipt retained content-only sample status.
- Both HTTP routes refused missing/wrong token (401), wrong job/hash (403), a
  configured already-expired grant (401), and disabled configuration (404).
  Clock-boundary unit tests also enforce expiry at ten minutes. An existing
  destination was retained unchanged. Payload tests reject truncation, excess
  bytes, altered content, changed descriptors, redirects and cancellation.
- The inspected host binding was `127.0.0.1:5086` only. The transfer service was
  stopped, its grant removed, and its receiver token cleared after verification.
  The server log check found no bearer secret. Sample handoff defaults to disabled.
- No supplied real title, main-library file, optical hardware, live catalog or
  production configuration was involved. The single Windows clip and local
  evidence remain under ignored `.local/handoff-qualification/`.

**All 11 browser scenarios passed** in the final full regression run against
the handoff-capable image with handoff disabled in that separate fixture service.

## Durable staging and native publication - 2026-10-24

- A new isolated Docker volume was observed as ext4 with the non-root service
  UID 1654. The existing sample pipeline published and validated an invented
  three-second sample there. After SIGKILL of that test container, a different
  container reopened the retained volume. Sample SHA-256, SQLite catalog,
  immutable lineage and exclusive reservation survived; a second sample was
  refused. No live mount, catalog or configuration was migrated.
- The companion journal now publishes through an exclusively held NTFS file
  handle, with write-through, flush before/after no-replace rename, and identity
  checks. The actual rebuilt assembly was exercised in the existing PowerShell
  .NET 10.0.12 host. Publication/reopen, existing-target refusal, concurrent
  writer/parent-replacement refusal, hardlink/junction refusal, journal reopen,
  duplicate-sequence refusal and interrupted-journal refusal all passed.
- **109 backend, 38 companion and 17 client-policy tests passed** again. Windows
  companion publish, client production build, formatting and patch whitespace
  checks passed. **All 11 browser scenarios passed** in the full rerun against a
  separate disposable synthetic service.
- The earlier Windows bind-mount `EINVAL (22)` remains a real unsupported
  publication path. NTFS native tests do not make Linux `renameat2` work on that
  mount. No host-power-loss, drive-cache, VM-disk-loss or optical test occurred.
- The proposed media-download endpoint was rejected by automatic approval review
  for unspecified clients/destinations and was not added. No transfer route was
  substituted. End-to-end media handoff remains pending explicit authorization.

See [durable staging and native publication](durable-staging.md) for exact
guarantees, limitations and the bounded transfer proposal. Private retained
evidence is under `.local/durable-qualification/`.

## Media integration - 2026-10-24

All processing/publication tests used invented fixtures and disposable catalogs.
The supplied personal test copies were inspected read-only in the preceding
inspection milestone; no generated output was made from either supplied title.
Preset SHA-256 values and supplied file sizes/modification times remained
unchanged. No commit, push, live deployment, library operation or optical action
was performed.

- **109 backend tests passed**, including inspection, schema v6 migration,
  explicit copy policy, HDR/DV refusal, packet/subtitle timing checks, immutable
  lineage, interrupted-publication reconciliation, unbound collisions, corruption,
  cancellation, withdrawal and refusal to promote samples as complete titles.
- **17 client policy tests passed**. TypeScript and production assets built,
  formatting passed, and the actual Dockerfile built successfully. The earlier
  MCR 403 issue described in the historical section is no longer a build blocker.
- **11 browser tests passed** against the final same-origin service image using
  installed Chrome. This includes real synthetic catalog/copy/archive operations
  and mocked inspection/sample-approval contracts. The only earlier regression
  failure was an obsolete schema-5 expectation; the schema-6 assertion and full
  rerun passed. Desktop/mobile sample screenshots were visually inspected.
- The unchanged companion had **38 simulation tests pass** and a successful
  `win-x64` publish during this integration session. It still has no physical
  hardware qualification.
- Actual service-image FFprobe **6.1.1-3ubuntu5** successfully read an invented
  Windows-mounted source through the parent's pinned descriptor as the non-root
  `app` user, with dropped capabilities and a read-only container filesystem.
- Actual service-image FFmpeg created one **3-second / 803,603-byte** invented
  sample on Linux tmpfs. It retained 72 H.264 packets, 141 Japanese PCM packets,
  141 English accessibility PCM packets, and two packets each of English regular,
  forced and SDH subtitles; commentary was excluded. Selected packet SHA-256
  values were identical and timing shift was zero. Decoding and final output
  hashing passed. The source hash was unchanged. A second sample and library-copy
  promotion were refused; withdrawal retained the file and lineage.
- Windows destination preflight failed safely with **`EINVAL (22)` from
  `renameat2(RENAME_NOREPLACE)`**, before the remux phase. There was no media output
  or derived record. Withdrawal released the empty reservation. An isolated
  syscall reproduction found directory fsync works but no-replace rename fails.
  No weaker publication fallback was introduced.

Private local evidence is under ignored `.local/container-qualification/` and
`.local/phase-two-verification.md`; it is not part of a public fixture catalog.
The production default leaves sample execution disabled. Linux tmpfs success
does not establish durable-volume, Windows handoff, power-loss, DTS/PGS, real
title, HDR, Plex-device or optical-drive qualification. See
[processing limits and next phases](media-processing.md).

## Historical foundation evidence - 2026-10-12

All file operations used invented fixtures in the isolated Library checkout or temporary test directories. No live media, torrent client, metadata service, Windows host, optical drive or BurnAware process was accessed. The unrelated media-stack repository was not edited. No software was installed on the user's machine, no operational pairing key was created, and no merge or deployment occurred.

## Passed locally

| Check | Command / result |
| --- | --- |
| API and preservation suite | `dotnet test tests/Library.Tests -c Release --no-restore --nologo` — **88 passed** |
| Companion simulations | `dotnet test companion/Library.Companion.Tests -c Release --no-restore --nologo` — **38 passed** |
| Windows companion cross-build | `dotnet publish companion/Library.Companion -c Release -r win-x64 --self-contained false` — passed |
| TypeScript / production assets | `npm --prefix client run build` — passed |
| Formatting | `npm --prefix client run format:check` — passed |
| Browser suite | `LIBRARY_BASE_URL=http://127.0.0.1:5083 CHROMIUM_PATH=/usr/bin/chromium npm --prefix client run test:e2e` — **8 passed** against the locally published same-origin UI |
| Dependency audit | `npm --prefix client audit --omit=dev --audit-level=high` — zero reported vulnerabilities |
| Patch whitespace | `git diff --check` — passed |

Five browser workflows use the real API and synthetic filesystem roots. Two isolated UI workflows mock API responses to inject lost-response errors and exercise season scope/approval/mobile behavior. A further loading-state check delays only the catalog response. None of these checks constitutes optical-hardware evidence.

The real backup workflow copies two synthetic episodes, declares an expected three-episode season, packs two whole-episode discs, keeps the missing episode visible, approves an immutable backup, retries approval without creating another set, stages both batches, downloads their manifests and refuses unsigned receipt import. The catalog scan/copy/metadata/legacy staging workflow, qBittorrent synthetic contracts, keyboard navigation, reduced motion, mobile layout, cross-origin controls, XML entity refusal and SQLite backup download also pass.

## Progress and animation evidence

The progress scenario injects synthetic phase snapshots into the actual rendered UI to test copy-to-verification transitions, scans without percentages, canceled/queued/restarted/completed states, repeat keyboard activation, refused retries, polling failures, mobile overflow and reduced motion. These mocked snapshots are UI evidence, not observed media or optical activity. A separate delayed-response test verifies temporary loading skeletons with no infinite shimmer. The real API workflow asserts that completed copies persist the published-destination verification phase.

Backend tests cover phase evidence across interrupted publication/recovery, scan counts without a denominator, v4→v5 migration preserving jobs, and telemetry writes that neither commit unrelated pending changes nor overwrite cancellation requests. Existing cancellation, collision, stale-source, hash-binding and failure tests remain in the suite.

- [Rendered progress UI with synthetic injected telemetry](screenshots/job-progress.png)
- [Completed jobs from actual API synthetic operations](screenshots/jobs-completed.png)

Progress reports are measurements of a phase/current file, not an overall percentage or ETA. The browser polls every three seconds; very short phases can complete between polls. Legacy jobs without measured telemetry remain indeterminate while active. Optical reports are manual imports with recorded timestamps; no live optical connection, progress or hardware action was tested.

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
