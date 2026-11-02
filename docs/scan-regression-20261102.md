# Read-only scan and timestamp regression — 2026-11-02

Local corrections based on `32ab3d34c43a89a6cc19d8353cf3ee34fcf3eaf1`.
This is an isolated private-preview repair, not a released bundle or production
processing qualification. No new credential, media transfer or encoding is involved.

## Observed failure

Both supplied screenshots were materialized locally and inspected. The terminal
showed `API command differs from the locally scoped read-only session.` The
worker had exited; the launcher was waiting for Enter. The job subsequently
failed at its three-minute deadline.
Live inspection found zero active jobs and no worker or FFprobe process. No
partial inventory was registered. The original failed job remains in history.

The old worker combined six guards into one error and did not report a rejection
back to the API. Its exact historical failed guard cannot be recovered. Persisted
job/folder identity matched the approved scope. A diagnostic retry using the same
grant and unchanged guards completed in approximately 1.6 seconds. Current Windows
and container clocks differed by about 50–70 ms; this is not proof of their
difference at the original failure.

A reproducible timing defect exists: a command issued for three minutes by a
container clock 100 ms ahead of Windows was rejected as out of scope. Validation
now allows at most one second of clock tolerance, while the worker and scanner
independently cap execution at **three minutes**, shortened by an earlier deadline.
The original session-expiry guard is unchanged. Expired commands, deadlines beyond
the session, larger discrepancies and wrong job/session/folder identities fail
closed. Rejections have specific diagnostics. A rejected command within the current
scope is immediately reported as failed, rather than appearing active for three
minutes. Wrong-scope commands are never scanned or acknowledged.

The first updated-preview UI check exposed a second clock boundary: native session
validation exited before queueing a scan with the former generic lifetime error.
An immediately issued session can be slightly ahead of the other OS clock. The
worker now waits for its start time (at most a one-second discrepancy, rounded up
for timer precision), then performs the original strict validity checks. It never
starts access early or moves the expiry. Larger discrepancies fail with specific
diagnostics. That unsuccessful connection-only check was disconnected and its
evidence retained; it produced no duplicate scan.

## Timestamps

SQLite round-trips discarded `DateTime.Kind` on catalog UTC columns. Consequently
the API returned `2026-11-02T00:32:50.4986017` without an offset, and the browser
treated it as local time. A regression test reproduces that storage boundary.
The fix restores UTC kind for the existing UTC columns without shifting their
ticks or migrating the database. JSON now carries `Z`. This follows the
[EF Core UTC value-converter pattern](https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions#specify-the-datetimekind-when-reading-dates).

Jobs, saved-review observations and connection expiry display in the viewer's
local timezone with an abbreviation. The reported job time should display
**November 1, 2026, 6:32:50 PM CST (UTC−06)**, corresponding to November 2,
00:32:50 UTC. Browser coverage includes both occurrences of 1:30 AM across the
Central daylight-saving transition: CDT before, CST after. No operating-system
clock or timezone setting is changed.

## Verification

- Backend: 202 tests passed, including legacy SQLite ticks, projection and JSON UTC serialization.
- Worker: 96 tests passed, including rejection reporting, wrong scope, bounded session-start waiting, clock tolerance,
  strict session expiry, cancellation and hard request deadlines.
- Media policy: 17 tests passed; frontend production build and formatting passed.
- Browser: 15 tests passed, including Central DST timestamps; three environment-specific
  native/Windows scenarios were skipped in the synthetic Linux API suite.
- Final private-preview native/UI verification passed on `0.2.0-preview.10-local`:
  Connect through the rendered confirmation, scan, inspect both saved reviews,
  Disconnect, then reopen both reviews. The native scan completed in **1.669 seconds**. The catalog
  still contained two files, with unchanged source identities, sizes and mtimes;
  Godzilla had seven streams and Silo 44. No browser errors occurred. The browser's
  actual timezone was `America/Chicago`, offset 360 minutes, and the original job
  displayed the expected **6:32:50 PM CST**. No timezone override was used for this
  live check.

The isolated preview was left healthy, disconnected and ready for a manual
connection. Its encrypted credential and previously approved expiry were unchanged.
Scope remains at most eight
top-level files in the previously approved Test Files folder, with at most
one-hour connections. Production, encoding, media transfer and production pairing
remain disabled. Verification workers and the isolated synthetic test API stopped.

The previous container/catalog and a verified snapshot were retained before the
update. Snapshot identities and hashes remain in the private local evidence.
The existing manual launcher now selects the corrected native worker, and its
readiness-only check passed. Close the old error window, run the existing
**Start Library Review.cmd**, leave its worker window open, and use **Media review
→ Connect Windows worker**. The files are already cataloged, so **Inspect read-only**
can open their saved reviews without another scan.

These source/docs/test fixes are maintained on the feature branch. Publishing
the source corrections does not publish a release bundle or deploy production.

Private screenshots, command logs, hashes, retained snapshots and live verification
results stay under ignored `.local/scan-regression-20261102/`. They are not release
artifacts. Saved presets and supplied source files are preserved. No playback,
encoding, Silo HDR processing, Plex or optical-device qualification is claimed.
