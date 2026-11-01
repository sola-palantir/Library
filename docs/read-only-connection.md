# Windows worker Connect / Disconnect

This is the feature-branch read-only connection flow, not an installed pilot
upgrade or production activation. It does not renew a ten-minute preview grant.
**No remembered user authorization is enabled by default.** Encoding, remux,
media transfer, recursive scans, optical operations and production pairing remain
off. Existing saved reviews can be opened with no worker or authorization.

## Daily use after a separately approved setup

1. Manually open the authorized companion with
   `Library.Companion.exe review-connect <approved-access-directory>`.
   Leave its window open. No login startup, service or Windows listener is added.
2. Open Library, choose **Add sample folder** / **Media review**, then
   **Connect Windows worker**. Review the exact folder, localhost API and expiry;
   check **Connect within this approved folder and expiry**, then
   **Connect read-only**.
3. Wait for **Connected for read-only scanning**. Choose **Add folder and scan
   read-only**. Only the approved folder's top-level eight MKV/MP4/M4V/WebM files
   can be inspected. A different folder requires a new local authorization.
4. When completed, select a catalog file and **Inspect read-only**. This is saved
   metadata, not live probing, a content hash or playback qualification.
5. **Cancel scan** cancels one job. **Disconnect and cancel scans** ends the
   session and cancels queued/running scans. Late results cannot register files.
   Saved reviews remain. The manually opened companion waits without reading
   files; close its window or use Ctrl+C to stop it completely.
6. To reconnect, manually reopen a stopped companion, choose Connect again and
   confirm the scope. A fresh session ID prevents old results being accepted.
   An already active session must be disconnected first. API restart also
   disconnects; it never resumes scans automatically.
7. Each connection lasts at most **one hour**, capped by authorization expiry.
   The remembered authorization lasts at most **seven days**. Neither polling,
   reconnecting nor restarting extends it. After authorization expiry, new local
   approval is required. If contact is lost, scanning is disabled; disconnect,
   resolve/reopen the companion, then connect again.
8. **Forget access**, followed by its explicit confirmation, cancels work and
   stores revocation in the catalog. That authorization cannot reconnect even
   after API restart. Its encrypted local file is retained but rejected. A
   replacement authorization requires a new directory and access identity.

## Setup requires specific approval before activation

First agree the Windows account/device, exact folder, exact localhost API,
credential-storage directory, duration and revocation controls. The general
implementation approval is **not** permission to run the following setup.
There is no unauthenticated credential-registration endpoint or browser secret.

Once approved, the operator supplies a small non-secret request JSON:

```json
{
  "folder": "C:\\LibrarySamples",
  "apiOrigin": "http://127.0.0.1:5090",
  "toolsRoot": "C:\\LibraryTools\\ffmpeg\\9.0.2-essentials",
  "days": 7
}
```

Use real approved paths, an existing empty private NTFS access directory, and
the already verified tools. Run the built/package companion:

```powershell
& $companion review-authorize .\read-only-request.json $approvedAccessDirectory
```

The foreground prompt repeats scope, storage, duration and revocation. Only
typing `ALLOW READ ONLY` generates a random credential. The companion writes:

- `worker-access.dpapi`: encrypted credential and immutable scope, protected for
  the authorizing Windows account using current-user DPAPI (not machine scope).
- `api-profile.json`: scope, expiry and SHA-256 token hash only; no bearer secret.

It refuses existing contents and never installs/starts anything. The operator
must separately configure **only the approved isolated API**, setting
`Library.ReadOnlyConnection` to the exported profile object, removing any old
`Library.ReviewSession`, and keeping the Windows execution backend, empty media
mounts and all processing flags off. A reviewed isolated restart is required to
load that configuration. The existing installed pilot is not automatically
modified. Never paste the encrypted file/token into chat, Git or browser storage.

The browser does not launch native programs; the explicit companion command in
step 1 is still needed. No packaged shortcut/update for this connection flow has
been installed. A next trusted bundle must include the matching API/UI/companion.

[Microsoft's DPAPI documentation](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
describes current-user protection. This protects storage, not against programs
already running as that Windows account. The bearer travels only over the exact
loopback HTTP origin; redirects, proxies and default Windows credentials are
disabled. API responses never expose its hash to the browser. No credentials
are logged. The API's existing same-origin mutation checks apply.

Revocation/session state uses the existing catalog integration-document table;
there is no schema migration. Backups contain hash-only API configuration and
revocation state, not the DPAPI file. Restoring an older catalog could restore
older authorization state: keep `ReadOnlyConnection` absent during restore and
review/revoke or replace authorization before reconnecting. Do not automatically
carry active access into a recovered installation. The release remains gated.

## Bounds and failure behavior

- Native requests: five-second hard deadline including response body; bounded
  response sizes, no redirect/proxy fallback. Lost contact stops active probing.
- Job: three minutes capped by connection expiry. FFprobe: 45 seconds, existing
  Job Object limits, rooted/pinned NTFS handles and checksum-pinned executable.
- Disconnect/revocation invalidates the session immediately. The worker's
  watchdog cancels on its next check (500 ms plus a maximum five-second request).
  Source handles and child processes are released; partial inventory is rejected.
- Restart disconnects and reconciles unfinished scans before accepting new work.
  Revocation survives restart; a changed access ID requires explicit reconnect.
- A UI timeout is an **uncertain request outcome**. Inspect refreshed status
  before retrying. Status fetch failures disable scanning until status recovers.
- No grant renewal, new folder, arbitrary executable, output path or media bytes
  can be requested through the connection protocol.

## Developer verification

Use [existing development prerequisites](development.md); these commands do not
install them. Backend/companion/client regression commands remain unchanged.
The Windows DPAPI unit test uses public synthetic bytes in memory only.

```powershell
dotnet test tests/Library.Tests -c Release
dotnet test companion/Library.Companion.Tests -c Release
dotnet publish server/Library.Api -c Release -o .local/connection-qualification/api
dotnet publish tests/Library.ConnectionQualification -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o .local/connection-qualification/worker
npm --prefix client run build
```

On Wintermute, build the Linux API through the existing project-local SDK in WSL.
`scripts/qualify-review-connection.ps1 -ToolsRoot <verified-tools-root>` expects
that Linux API and SDK, Windows harness, existing Chrome/Node and PowerShell 7.
It uses a distinct synthetic folder/catalog and localhost port (default 5096),
checks binary hashes, creates one tiny synthetic clip and uses a **public test
token** expiring after four minutes. No remembered credential file is created.
The API has a 160-second hard lifetime; process/browser deadlines are bounded.
Existing services and user samples are untouched. Evidence is retained under
ignored `.local/connection-qualification/`.

The actual browser/native case exercises Connect, native inspection,
Disconnect/cancellation, reconnect and revocation; it is skipped in ordinary CI
unless `LIBRARY_CONNECTION_TEST` explicitly points at this isolated fixture.
CI runs the unconfigured UI, backend lifecycle tests and Windows synthetic DPAPI
checks. See [connection results](read-only-connection-results.md) for actual
observed passes and remaining activation/device gates.
