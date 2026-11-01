# Read-only sample scan qualification — 2026-11-01

Local working-tree implementation on `feat/library-foundation`, based on
`908de6ec4a4a5de874bc955fa578185a80e796ea`, reviewed for feature-branch publication.
No release publication, production activation or installed-pilot update was performed.

## Verified behavior

The final native Windows worker and Docker API/UI preview completed **Add sample
folder → cancel → retry → scan → review** using the two supplied files in the
dedicated test folder. The UI test used a fresh headless Chrome profile and the
actual API and native worker, not mocked stream metadata. Both files were
registered atomically as **observed**, with null content hashes and no processing
eligibility. Saved reviews remain available without an active worker.

| Supplied input | Bytes observed | Streams |
|---|---:|---:|
| Godzilla vs. SpaceGodzilla (1994).mkv | 25,807,542,388 | 7 |
| S01E01 - Freedom Day.mkv | 11,416,177,778 | 44 |

The Godzilla review exposed Japanese audio and both English PGS streams. The
original-language override can be reviewed, while unknown audio roles still
require explicit confirmation. The Silo file's HEVC inventory was displayed.
Neither file was encoded, copied, remuxed, renamed, deleted or rewritten.

The native inspection/cancellation/timeout harness completed in **2.16 seconds**.
Its deliberately stalled child was terminated in **0.84 seconds**, with child
exit verified. Input sizes and modification times matched before/after. This was
not a full source-content hash audit. The verified, pinned native FFprobe 9.0.2
build was used with the existing Windows Job Object limits.
The original 1080p preset and retained RF20 sample also matched their previously
recorded SHA-256 hashes. Both exported preset files retained their sizes and
modification times; neither was edited.

Additional actual preview checks passed:

- Cancellation while a scan was **running**, preserving the previous two entries.
- API stop/restart during another scan: the interrupted job became **failed**
  with an explicit restart/retry explanation; no automatic continuation or
  partial catalog registration occurred. Completed observations persisted.
- Requests without a bearer grant, with a wrong grant, or for a different folder
  were rejected. Worker access remained localhost-only, temporary and read-only.
- Encoding, remux, HDR processing, media transfer and persistent pairing stayed
  disabled. The original installed pilot remained stopped and unchanged.
- After expiry at 20:27:44 UTC, the worker exited and capabilities reported
  disconnected/disabled. At 20:37:49 UTC, actual rendered controls reopened both
  saved reviews: Godzilla (H.264, 1920 x 1080, 7 streams) and Silo (HEVC,
  3840 x 1920, 44 streams). Both inspection GETs returned 200; scan/retry controls
  were disabled, with zero mutation requests or page errors. Earlier locator
  failures were test-harness errors, not a failed saved-review UI.

## Regression results

| Check | Final result |
|---|---|
| Backend tests | 196 passed |
| Companion tests | 90 passed |
| Track/preset policy tests | 17 passed |
| Browser suite against isolated Linux synthetic fixtures | 13 passed; 2 environment-specific cases skipped |
| Disconnected Windows control-plane browser case | 1 passed separately |
| Native supplied-folder browser case, final build | 1 passed separately |
| Client production build and formatting | Passed |
| PowerShell script parsing and exercised preview start/stop/restart paths | Passed |
| Git whitespace check | Passed |

The complete browser run initially exposed an invalid mocked scan-history
response that could crash the new panel. Response validation, a dedicated
malformed-response regression test and the older fixture were corrected. The
complete final run passed. A startup recovery race was also found and fixed:
reconciliation now finishes before API startup accepts work. The native harness
was corrected to expect the process runner's `TimeoutException` and was rerun
successfully. These intermediate failures are retained in local evidence.

Private local evidence, excluded from Git, is beneath `.local/scan-review/`:

- `native-evidence-02/result.json` and `inventory.json`.
- `linux-browser-final.log` and `linux-browser-final/`.
- `windows-default-final.log` and `windows-default-final/`.
- `native-browser-final.log` and `native-browser-final/`, including a rendered
  screenshot of the actual Godzilla technical review.
- Session directories contain configuration hashes/expiry, `state.json` and the
  exercised resilience evidence. Bearer secrets are never saved there.

## Scope and remaining gates

This is the isolated development preview described in the
[launch/stop instructions](read-only-scan.md), not a newly distributed package.
Its manually launched worker session expires after ten minutes; there is no
permanent pairing or background worker installation. The API/catalog can remain
available for reviewing saved observations after expiry. A new scan requires a
new explicitly scoped session.

Human motion, heard audio, subtitle rendering, Plex/device playback, optical
hardware, HDR preservation, full-title encodes and production processing were
**not run**. Silo encoding remains blocked. Metadata/packet facts must not be
presented as playback or visual-quality qualification.

The historical shell stall was not reproduced: bounded executor checks completed
promptly. Its original cause remains unknown. Explicit child-process, HTTP and
test-command deadlines now give failures terminal outcomes instead of indefinite
waiting. Publication review also added regression coverage for filename case-only
changes and null inventory entries; preview cleanup preserves the original
startup failure if stopping its container also fails. GitHub CI is verified
separately for the published commit.
