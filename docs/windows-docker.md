# Later installation on Windows Docker Desktop

This guide is for a later, user-controlled installation. Nothing has been installed on the user's Windows machine. Docker Desktop container startup and physical optical drives were not tested in the implementation environment.

1. Install Docker Desktop with Linux containers using its normal Windows setup. This repository does not alter Docker, WSL or networking permissions.
2. Clone the repository. First create three **distinct synthetic test folders**, for example `C:\LibraryFixture\downloads`, `C:\LibraryFixture\library` and `C:\LibraryFixture\staging`. Place only synthetic test files in downloads.
3. Copy `.env.example` to `.env` and edit the mappings with forward slashes. Keep `.env` private. The host directories must already exist. Nested or identical media roots are refused by the service, and should also be avoided at the Windows host level.
4. Review `compose.yaml`. Downloads mount read-only. Library and disc staging mount read-write. SQLite uses the named `library_catalog` local volume. The API/UI is bound to `127.0.0.1:5080`, with no privileged mode or Docker socket mount.
5. From PowerShell in the checkout:

   ```powershell
   docker compose config
   docker compose build
   docker compose up -d
   docker compose logs --tail 100 library
   ```

6. Open `http://127.0.0.1:5080`. In Settings, verify each Windows/container mapping. Scan synthetic downloads. Open Organize, preview a copy, approve it, and inspect Jobs. Test collision handling and compare source/destination hashes before considering a future live-media pilot.
7. Stop with `docker compose down`. **Do not add `-v`** unless you deliberately intend to discard the catalog volume. Back up the catalog from Settings before any upgrade.

The image runs as the .NET `app` user. If a Windows bind mount is not writable, the operation should fail visibly. Resolve ownership/sharing through your normal Docker Desktop administration; do not run the service privileged or relax source mount protection as a workaround. If Docker Desktop's filesystem does not support the required no-replace rename/fsync behavior, stop and report the failing job rather than bypassing integrity checks.

## BurnAware Free workflow

1. In Discs, select verified library copies, preview batches and save the plan.
2. Select **Stage files**. Wait for the staging job to complete. Each batch gets a folder named by its disc ID in the staging root, with `manifest.json` and the original organized paths.
3. In Windows, open BurnAware Free's data-disc GUI. Add the **contents** of that batch folder to a blank nominal 50 GB Blu-ray. Confirm actual available capacity and the burner settings. Burn externally.
4. Only after the GUI reports success, select **Report written** in Library. This does not assert read-back verification.
5. If an actual written disc can be exposed as a read-only folder to the Linux container, configure `DISC_READBACK_PATH` privately and start with the optional overlay:

   ```powershell
   docker compose -f compose.yaml -f compose.disc.yaml up -d
   ```

   Choose **Verify read-back**. This checks every planned media file against the catalog manifest. Docker Desktop optical-drive mounting support depends on the host configuration and remains untested. If no real disc mount is available, leave the status **reported written**. A future Windows verification helper can close this gap; no helper is bundled now.

The Free edition's GUI workflow is the integration boundary. BurnAware's dedicated console burner is associated with its Professional edition; this project promises no headless Free integration.

## Optional Windows optical companion

The browser/API remains in the Linux container. Optical devices are handled by the separate native Windows CLI, not USB passthrough into Docker. Follow [the companion guide](companion.md) only during a separately approved setup and hardware-validation step. Map the staging folder consistently between Windows and the container; keep companion state on a separate private, durable Windows directory. No listener, firewall changes or automatic software installation are required by the companion. Pairing is explicit and imports only the public key into the catalog. Burn commands, drive choice and receipt import are manual in this release; the browser does not remotely start a burn.

Only supported blank write-once BD-R/BD-R DL is accepted. Nominal 50 GB is approximately 46.57 GiB; measured available sectors and final UDF image size plus reserve determine fit. The actual burner model, firmware and media must be qualified before use. No hardware qualification was performed in development.
