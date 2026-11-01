# Windows sample-folder scan preview

The local implementation connects **Add sample folder → scan → review** to the
native Windows worker. This is an isolated development preview; the previously
installed release bundle is not upgraded by these commands.

The API remains the sole catalog writer. The worker reads the explicitly approved
local NTFS folder through pinned handles and runs only the checksum-pinned
FFprobe binary. No FFmpeg, media copy, remux, encoding, subtitle conversion, media
mount into Docker, optical operation or production pairing is enabled.

## Open the preview

Use the existing [developer toolchain](development.md). These are developer
qualification instructions, not a new end-user installer. PowerShell 7, Docker
Desktop, the .NET 10 SDK for building, and the already verified project-local
FFprobe build are required. Nothing here installs prerequisites or changes PATH.

1. Build the preview image and self-contained worker from this checkout:

   ```powershell
   docker build -t library-readonly-review:local .
   dotnet publish companion/Library.Companion -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o .local/scan-review/worker
   ```

   On Wintermute, the existing SDK is Linux-based under `.dotnet`; use it through
   WSL as described in the developer guide. The checked local preview artifacts
   already exist under `.local/scan-review`; rebuilding is unnecessary merely to
   view their saved observations. FFprobe is expected at
   `<ToolsRoot>/bin/ffprobe.exe`, with the hash pinned in
   `NativeMediaProcessor.FfprobeSha256`. Do not substitute an unverified binary.

2. Manually start one temporary session, choosing a small folder you authorize
   the worker to read. Replace example paths with your own:

   ```powershell
   ./scripts/start-review-preview.ps1 `
     -SampleFolder 'C:\LibrarySamples' `
     -PreviewRoot (Join-Path $PWD '.local/scan-review') `
     -ToolsRoot 'C:\LibraryTools\ffmpeg\9.0.2-essentials' `
     -Port 5090
   ```

   This creates a distinct named catalog volume and container, binds only
   `127.0.0.1`, and starts one native worker. It refuses an occupied port. The
   printed `state.json` identifies exactly that preview. It does not touch the
   installed pilot, other containers, existing catalogs or the sample bytes.

   The ten-minute bearer token is generated locally, passed only in the worker's
   process environment and removed from that environment before FFprobe starts.
   Only its hash and expiry are written in API configuration; no token is printed
   or saved. There is no grant-creation endpoint, auto-renewal, Windows listener,
   background service, login task or permanent pairing. No off-machine endpoint
   is supported. Starting a session is a local approval of its exact folder.

3. Open the printed URL, normally **http://127.0.0.1:5090**. Choose **Add sample
   folder**, verify the displayed Windows folder, then **Add folder and scan
   read-only**. The worker must show connected. A session cannot be expanded by
   entering another path. At most eight top-level MKV/MP4/M4V/WebM files are scanned;
   subfolders are not traversed and folders exceeding 128 entries are refused.

4. Wait for **completed**, select a **Catalog file**, then **Inspect read-only**.
   The files also appear in **Inbox** as source observations. The technical view
   uses the saved, time-labelled scan; it is not a fresh probe each time you open
   it. No SHA-256 content verification or processing eligibility is asserted.
   Rescan to refresh an observation.

5. Review language and role tags. Confirm a foreign film's original language and
   manually resolve unknown track roles. English and confirmed original audio
   are preserved in the preview; confirmed commentary is excluded, accessibility
   audio retained, and English regular/forced/SDH subtitles remain separate.
   These are review choices only: no source tracks are removed or rewritten.

6. **Cancel scan** stops a queued or running scan. **Retry scan** creates a fresh
   job ID bound to the same approved folder. The Jobs view uses the same rules.
   Failed/cancelled scans register no partial inventory. Previously completed
   observations remain available after cancellation, restart or session expiry.

7. Stop the preview with the exact state file printed at startup:

   ```powershell
   ./scripts/stop-review-preview.ps1 -StateFile '<printed session directory>\state.json'
   ```

   This stops only the identified preview and its matching worker; it retains the
   catalog volume, configuration and evidence. It deletes no files or volumes.
   Session expiry stops worker access automatically; it does not delete the
   catalog or stop the review UI. Another session requires a new manual launch;
   there is no persistent connection or automatic credential reuse.

## Bounds, failure handling and verification

- Worker HTTP requests: **5 seconds**, including response bodies; no redirects,
  proxies or default Windows credentials. Responses are capped at 64 KiB.
- Scan: **3 minutes**, also capped by session expiry. FFprobe: **45 seconds**,
  512 MiB aggregate committed memory, 25% CPU rate and one subprocess. Native Job
  Objects terminate descendants on cancellation, timeout or owner exit.
- Metadata: at most 4 MiB per probe and 8 MiB per result. The API never accepts
  media bytes or instructions to run arbitrary commands. Source handles deny
  writes/deletion while read; reparse points are refused. Read-only inspection
  does not sever hardlinks or modify seeding files.
- Registration is one transaction. Late, cancelled, wrong-folder/session or
  conflicting results are refused. Duplicate identical receipts are idempotent.
  API restart fails pending scans with an explicit retry message, never silently
  resumes or treats an incomplete inventory as complete.
- The browser displays errors and uses bounded requests. Disconnection or expiry
  disables starting another scan; imported metadata and saved reviews still work.

Automated tests:

```powershell
dotnet test tests/Library.Tests -c Release
dotnet test companion/Library.Companion.Tests -c Release
npm --prefix client run test:media
npm --prefix client run build
dotnet publish tests/Library.ScanQualification -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o .local/scan-review/qualification
```

The native harness takes exactly three arguments: approved sample folder,
verified tools root, and a **new** metadata-evidence directory. It reads sources,
checks their size/mtime, exercises process timeout/termination and cancellation,
and writes metadata evidence only. It does not calculate full movie hashes.

The optional `client/e2e/review-scan.spec.ts` uses `LIBRARY_BASE_URL` and
`LIBRARY_REVIEW_TEST_FOLDER`. It is skipped without explicit local configuration;
its current fixture assertion targets the two supplied qualification samples and
an initially empty isolated catalog. It performs real worker-backed inspection,
cancellation/retry and browser review. `scripts/qualify-review-resilience.ps1`
additionally tests active cancellation, unauthorized requests and API stop/restart
against **only** the preview named by its state file. Do not use it on a live catalog.

See [qualification results](read-only-scan-results.md). Neither metadata inspection
nor browser review proves playback, perceptual quality, subtitle rendering, HDR
preservation or device compatibility. Silo encoding and all production processing
remain disabled. The historical shell stall's cause remains unknown; current
bounded commands and explicit process deadlines replace indefinite waiting.
