# Windows optical companion

The Docker service remains the catalog and planning UI. `companion/Library.Companion` is a local Windows CLI using native IMAPI COM interfaces; it opens no network listener and does not automate BurnAware. Its native code and Windows x64 output compile. **No physical drive, blank, burn or COM execution has been qualified.** The native NTFS publication/journal component has now passed isolated Windows tests; see [durable staging and publication](durable-staging.md). The actual drive model and BD-R media remain setup prerequisites. Filesystem tests and simulations are not device evidence.

## Preservation boundaries

- Only blank write-once BD-R (including supported dual-layer BD-R) is accepted. BD-RE, BDXL, nonblank, unsupported and ambiguous media fail closed. Because IMAPI type 18 also covers BDXL, the initial support envelope additionally rejects measured free capacity above 25,000,000 sectors (51.2 decimal GB); this is a conservative capacity guard, not a measurement of physical layer count. There is no erase, append-session, split, transcode or media-delete operation; IMAPI ForceOverwrite is explicitly false.
- IMAPI recorder exclusive access and a Windows tray-removal lock span capacity measurement and write/finalization. If these cannot be obtained, writing is refused.
- The final UDF 2.50 image includes the exact frozen manifest and only its selected files. Every staging file is SHA-256 checked, held open without write/delete sharing on Windows, and read into a new independent image. Extra staging files are not silently included. Source media is never opened for writing.
- Final image length must be a whole number of 2,048-byte blocks. `imageBytes + reserveBytes <= measuredFreeSectors * 2048` decides fit. A nominal 50 GB is 50,000,000,000 decimal bytes (about 46.57 GiB), not a fixed 45 GB payload. Filesystem overhead is already included in image length.
- Reparse points, junctions, alternate streams, traversal and Windows device names are refused. Staging/state are existing local directories; UNC shares and filesystem-root scopes are refused. Windows directory handles prevent ancestor replacement while in use. Linux path handling exists for simulations, not native production file access.
- A physical disc ID binds permanently to one exact manifest. Replacements require new physical IDs and labels and therefore a new embedded manifest/image. Failed or interrupted images and journal history are retained for review; no cleanup deletes media.

## Local setup — not performed by development

The optional `receive-sample` command is separate from all optical operations.
It receives only a specifically approved bounded sample from localhost with an
expiring bearer grant, using native NTFS publication and content verification.
It opens no listener, creates no pairing key and does not invoke IMAPI. See
[sample handoff](sample-handoff.md) for the actual synthetic test and limits.

On the Windows host, install the supported .NET 10 runtime and use a local, user-private state directory on durable NTFS storage with space for the complete image. Keep it separate from staging. The directory contains the journal, image and a signing key; back it up privately and do not publish it. This tool does not alter ACLs, install software, configure networking or create credentials automatically.

Build on a development host:

```text
dotnet publish companion/Library.Companion -c Release -r win-x64 --self-contained false
dotnet test companion/Library.Companion.Tests -c Release
```

Create a local configuration (these paths are fictional):

```json
{"stagingRoot":"D:\\LibraryStage","stateRoot":"D:\\LibraryCompanionState"}
```

A separately approved setup may run `Library.Companion pair config.json`. This explicitly creates one P-256 private signing key, refuses to overwrite an existing key, and prints its SPKI public PEM for manual pinning in Library. No key was created during implementation. Restrict the state directory to the intended Windows user before pairing. Never send the private key to Docker or put it in a repository.

`Library.Companion devices` lists IMAPI recorder identifiers. This invokes Windows COM and belongs to later hardware setup, not Linux testing.

## Frozen manifest contract

The staging directory is `<stagingRoot>/<discId>/`. It contains `LIBRARY-MANIFEST.json` and the selected relative file paths. The manifest digest is SHA-256 over its **exact UTF-8 bytes**, not a reserialized representation.

```json
{
  "schemaVersion": 1,
  "discId": "physical-disc-synthetic-1",
  "label": "LIB-ABC-7",
  "backupSetId": "backup-synthetic-1",
  "manifestRevision": 1,
  "files": [
    {"path":"Movies/Synthetic.mkv","bytes":1234,"sha256":"<64 hexadecimal characters>"}
  ]
}
```

Each command JSON contains `commandId`, `discId`, `manifestSha256`, `recorderId` and `reserveBytes` (default 1,000,000,000). A command ID is immutable across operation and arguments. Use a new ID for a new user intent, but never use a new ID to replay an uncertain burn. The manifest must remain available in staging for later operations; its payload files can be offline after image preparation.

```text
Library.Companion prepare config.json prepare-command.json
Library.Companion burn config.json burn-command.json
Library.Companion verify config.json verify-command.json
Library.Companion status config.json physical-disc-synthetic-1
```

`prepare` creates `<stateRoot>/<discId>.udf.iso` with exclusive create and appends `planned` then `staged` with image hash/bytes. It never overwrites an existing image. Failed preparation retains the partial image and failed history; review and allocate a replacement physical ID rather than silently rebuilding under the same ID.

`burn` rehashes the held image, acquires the drive, measures inserted blank media and appends `writing` before the one IMAPI write call. Progress appends `finalizing`; success means only `written-unverified`. Errors after writing starts become `interrupted` because the physical outcome is uncertain. Repeated identical command IDs return durable results. An incomplete duplicate becomes `interrupted`; a different command cannot initiate another write for that disc ID. A crash between final image creation and its staged event also requires review, not an overwrite.

Eject/reinsert the written disc before readback as part of hardware qualification. `verify` requires the selected IMAPI recorder's mounted CD-ROM drive, written BD-R media, locked tray, exact on-disc manifest digest (which binds physical ID and label), and the size/SHA-256 of every selected file. It cannot verify a caller-supplied arbitrary directory. Failure appends `failed` after `verifying`, preserving earlier evidence without leaving a current successful state. The companion does not promise immutable manufacturer serial identification: identity is the permanently assigned embedded manifest/label. A byte-for-byte duplicate disc with the same embedded identity is indistinguishable; never intentionally duplicate identities.

## BurnAware fallback

After preparing the image, run `fallback` with a fresh command ID. It rehashes the same frozen `.udf.iso`, measures and validates the inserted blank, and durably reserves this physical ID as an external attempt. Use BurnAware Free's **Burn ISO** GUI with that exact image, not a newly assembled data project. Do not change or erase media. The companion cannot control BurnAware's later media selection or prevent the user changing GUI settings; its preflight is not a guarantee of the external operation.

`report-external` records only `written-unverified`. It never yields verification. Run the same full optical `verify` command afterwards. An uncertain or failed fallback attempt must not be replayed under the same physical identity. Allocate a replacement and preserve the original attempt history. There is no headless BurnAware promise or Professional Console Burner dependency.

## Durable signed receipts

Journal events are immutable, individually published JSON files with monotonic `sequence` per disc, explicit `commandId`, identity/digest, image digest/size, recorder, state and timestamps. Corrupt/missing sequence records fail closed. One exclusive state-directory lease serializes companion processes. On Windows, an exclusively held write-through file handle spans event writing, flush, no-replace rename and a final flush, under pinned parent directories. Native publication requires local NTFS. Sudden power failure still requires filesystem/backup qualification, and the journal cannot turn uncertain hardware completion into a retry permission.

CLI operations print this envelope:

```json
{"payload":"<exact JSON text>","signature":"<base64 ECDSA signature>"}
```

The signature covers UTF-8 `payload`, SHA-256, ECDSA P-256, IEEE P1363 fixed 64-byte format. Verify only against an independently pinned public key, never a key supplied in the receipt. Payload is `Evidence` in `Contracts.cs`, with `schemaVersion:1`, monotonic `sequence`, `mode:"hardware"|"simulation"`, `simulated`, `manifestMatched`, `allFilesVerified` and `opticalRoot`. A verified import must bind current disc/manifest/image, require hardware mode and all verification evidence, and reject older sequences. A newer failed/reverification event must supersede earlier success. Signature is evidence of the paired local program's report, not independent hardware attestation.

Errors that occur after a durable failed/interrupted event print that signed event to stdout and an error to stderr with a nonzero exit. A missing signing identity can leave a completed operation in the durable journal: repeat the same command after restoring the original identity to obtain its receipt; never issue a fresh burn command. `status` exports unsigned full history for diagnostics/backup, not a way to assert optical verification.

Back up the entire private state directory together with the catalog and image retention policy. Restoring a journal older than physical activity requires reconciliation; the API sequence gate must reject stale receipts. Never rewind or discard attempts to make an old disc ID writable again.

## Validation and remaining hardware gates

Linux simulation tests exercise measured-capacity boundaries, path refusal, source/manifest/image changes, duplicated commands, lost responses, interrupted writes, wrong manifests, failed reverification, fallback restrictions and journal restore. They use an injected device with `simulated:true`; production verification must reject those receipts. The Windows native adapter is cross-compiled, not executed here.

Before production, independently qualify IMAPI COM marshaling/events, UDF 2.50 readability, exclusive/tray locks, actual drive firmware and BD-R/DL media, media removal/reinsertion, cache behavior, Windows power failure/restart and BurnAware image compatibility. No unattended burn queue is offered. Cancellation during a native write is intentionally not exposed; forced termination makes the attempt uncertain and requires reconciliation.

References: [IMAPI media types](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type), [image result stream](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-createresultimage), [IMAPI write actions](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_format2_data_write_action), [BurnAware editions](https://www.burnaware.com/compare-editions.html).
