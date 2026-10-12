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

## Windows optical companion workflow

The browser/API remains in the Linux container. Optical devices are handled by the included native Windows CLI, not USB passthrough into Docker. Follow [the companion guide](companion.md) during a separately approved setup and hardware-validation step. Map the staging folder consistently between Windows and the container; keep companion state on a separate private, durable Windows directory. No listener, firewall changes or automatic software installation are required. Pairing is explicit and imports only the public key into the catalog. Commands, drive choice and receipt import are manual; the browser does not remotely start a burn.

1. In Discs, select verified library copies and use **Preview whole-file packing** in the backup workspace. Review capacity, reserve, selected files and any season gaps, then choose **Record this backup plan**.
2. For each recorded disc, choose **Stage verified files** and wait for its job to complete in Jobs. The staging directory is `<stagingRoot>/<discId>/`, containing the exact frozen `LIBRARY-MANIFEST.json` and selected relative paths. Preserve the manifest for later verification.
3. Download **Companion command**. During Windows setup, use `Library.Companion devices` to identify the recorder and replace the downloaded `recorderId` placeholder. Keep the disc ID, manifest digest and reserve bound to this plan. Use a distinct command ID/file for each new operation; retain the same command and arguments when retrieving an existing result. Never create a fresh command to replay an uncertain burn.
4. Run `Library.Companion prepare config.json prepare-command.json` to create the independent frozen `<stateRoot>/<discId>.udf.iso`. For native burning, run `Library.Companion burn config.json burn-command.json`. Successful writing records only `written-unverified`.
5. Eject/reinsert the written disc as part of hardware qualification, then run `Library.Companion verify config.json verify-command.json`. Verification uses the selected recorder's mounted CD-ROM, written BD-R media, the exact embedded manifest and every selected file's size/SHA-256. It cannot verify a caller-supplied arbitrary directory.
6. In Discs, open **Import signed companion evidence** and use **Validate and import evidence** to import the signed JSON envelopes, including failures or interruptions, in sequence. Only accepted full optical read-back evidence establishes `verified`; a writing report alone does not. The catalog cannot learn of later companion failures until their receipts are imported.

Only supported blank write-once BD-R/BD-R DL is accepted. Nominal 50 GB is approximately 46.57 GiB; measured available sectors and final UDF image size plus reserve determine fit. The actual burner model, firmware and media must be qualified before use. No hardware qualification was performed in development. Consult the companion guide for capacity guards, command identity, interrupted attempts and private state backups.

## BurnAware Free fallback

After preparing the frozen image, run `Library.Companion fallback config.json fallback-command.json` with a fresh command ID. This checks the same image, validates the inserted blank and records an external attempt against the permanent physical disc ID.

Use BurnAware Free's **Burn ISO** GUI with that exact `.udf.iso`, rather than assembling a new data project from the staging folder. The companion cannot control BurnAware's later media selection or GUI settings; its preflight is not proof of a successful external write. Only after successful external writing, run `Library.Companion report-external config.json report-command.json`; this records `written-unverified`. Perform the same companion `verify` and signed receipt import described above. Failed or uncertain attempts require review and a replacement physical ID, not another write under the old identity.

BurnAware Free remains a manual GUI fallback. Its dedicated console burner is associated with the Professional edition; this project promises no headless Free integration. BurnAware image compatibility remains part of hardware qualification.

## Legacy folder content checks

Older disc batches remain available for compatibility. Their **Stage files** action produces a batch folder with `manifest.json`; this is the legacy format, not the companion's `LIBRARY-MANIFEST.json` contract. **Report written** records only `reported-written`.

For an explicitly mounted legacy disc directory, `DISC_READBACK_PATH` and the optional overlay can expose its contents read-only:

```powershell
docker compose -f compose.yaml -f compose.disc.yaml up -d
```

**Check legacy content** compares the exact legacy manifest and all planned files and records only `content-checked`. A container-visible directory cannot establish that those bytes were read from the intended physical optical disc, even if the user describes it as a disc mount. This path never establishes physical-disc verification and is not a substitute for recorder-bound companion evidence. The overlay is unnecessary for companion verification; Docker Desktop optical mounting remains untested.
