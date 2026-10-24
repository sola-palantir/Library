# Windows Docker control plane and native companion

The source configuration has changed; no live deployment was performed.
`compose.yaml` now mounts only the catalog volume and selects `windows-worker`.
The UI/API can run without access to media directories. Worker transport remains
unconfigured, so media jobs are paused rather than routed through Linux staging
or an export endpoint. Imported probe review, preset previews, metadata and
catalog operations remain available. See [the worker boundary and migration
gates](windows-worker.md) before any later installation.

Do not apply this configuration to an existing catalog without backing it up
and reviewing pending jobs. No automatic job/catalog migration is implemented.
The Docker API stays bound to `127.0.0.1:5080`; no native listener is created.
No Docker permissions, mounts or services were reconfigured during qualification.

`compose.linux.yaml` retains the earlier local Linux executor configuration for
qualified Linux filesystems. The tested Windows bind mount rejects required
`renameat2(RENAME_NOREPLACE)` with `EINVAL (22)`. Do not select that backend for
Windows media as a workaround. Isolated ext4 and the narrowly approved synthetic
handoff test remain documented in [durable-staging.md](durable-staging.md).

The workflow below describes retained optical functionality and is pending
integration with worker-owned native archive staging. It is not presently an
end-to-end Windows deployment procedure. No physical drive has been qualified.

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
