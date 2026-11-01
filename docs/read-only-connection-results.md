# Read-only worker connection qualification - 2026-11-01

The preceding supplied-folder milestone was published as
[`f76d3b8f6ec6876691a341c26e5a77b6792270fc`](https://github.com/sola-palantir/Library/commit/f76d3b8f6ec6876691a341c26e5a77b6792270fc).
Its [exact-commit CI run](https://github.com/sola-palantir/Library/actions/runs/41350058677)
passed both Linux validation and Windows compilation/lifecycle jobs.

## Implemented boundary

The [connection flow](read-only-connection.md) has explicit folder scope,
connection state, session/authorization expiry, Connect, Disconnect/cancel and
Forget access. Reconnect creates a fresh session without extending authorization.
API restart disconnects; revocation is stored in the existing catalog table and
survives restart. No schema migration or automatic grant creation/renewal exists.

Offline authorization is implemented but **not activated for any user folder**.
It requires an exact foreground confirmation before writing a current-user DPAPI
credential. The API receives only a hash and scope. No service, login task,
listener, production pairing or media-processing capability is installed/enabled.

## Actual qualification

The final local browser used a fresh headless Chrome profile against a Linux API
at a separate localhost port, an isolated catalog, and the native Windows worker.
It used one tiny invented FFV1 Matroska input, not Godzilla, Silo or RF20.
The fixture's public test token expired within four minutes; no persistent
credential was created. These were actual HTTP/UI/native FFprobe operations,
not mocked stream metadata.

Passed:

- Connect through the rendered confirmation controls; connected state and scope.
- Native read-only scan and atomic registration of one **observed**, unhashed file.
- Disconnect during another scan; cancellation without partial registration.
- Reconnect with a different session ID and unchanged authorization expiry.
- Wrong/missing token, wrong session ID, stale session and wrong-folder rejection.
- Forget access through the rendered confirmation; native worker exits.
- Saved native technical review opens in the UI after revocation (FFV1, 64 x 64).
- Actual isolated API stop/restart retains the revoked state, rejects the same
  test token with HTTP 401, and preserves the completed catalog observation.
- Synthetic source SHA-256 unchanged; production, pairing and encoding off.
- Current-user Windows DPAPI round trip and damaged-blob rejection, using only
  public synthetic bytes in memory; no credential file is written by this test.

The final rendered/native browser case passed in **27.4 seconds**. The initial
case also passed before additional negative and saved-review assertions were
added. Metadata evidence and rendered screenshot remain in ignored
`.local/connection-qualification/`; no media, binaries, API configurations or
credential/session artifacts are included in the source commit.

Backend regression: **201 passed**. Companion regression: **92 passed** (the
Windows-only DPAPI path is also exercised by the actual Windows harness).
Client track/preset policy: **17 passed**. Production build, formatting and
PowerShell parsing passed. The full Linux synthetic browser suite passed
**14 cases**, with three environment-specific cases skipped, in **5.3 minutes**.
The configured native connection case passed separately as described above.
The default Windows-mode browser run passed **two cases** (disconnected control
plane and approval explanation), with its configured-native case skipped.
CI terminal results are reported with the exact pushed commit.

## Existing preview and activation gate

The original pilot and the supplied-folder preview were not upgraded or
restarted. The previous ten-minute grant stayed expired. Saved supplied-file
reviews remain in that prior preview; the new controls require the new build.
No supplied media, saved preset, retained RF20 sample or main catalog was changed.
Final read-only checks found the prior preview healthy with two saved files,
the old grant expired/disconnected, and encoding/remux/HDR disabled. Both supplied
source sizes and modification timestamps still match the earlier native evidence.

Before real remembered access, specific approval is still needed for the exact
Windows user/device, sample directory, localhost API, credential directory,
duration and revocation controls, plus updating the isolated preview to this
build. This is a pending authorization boundary, not a failed tool operation.
Do not infer approval from synthetic qualification or from permission to push
source code.

Human playback/motion/audio, subtitle rendering, Plex/devices, Silo dual-HDR,
optical hardware, production processing and recovery of an activated remembered
profile were not qualified. Restored catalogs must keep access disabled pending
review because an old backup can predate a revocation.
