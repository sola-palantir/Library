# Durable staging and Windows publication qualification

The October 24 qualification establishes the two filesystem components and a
separately approved, optional localhost handoff of one invented sample. It does
not change live storage mappings or enable full-title processing.

| Approach | Observed result | Current status |
| --- | --- | --- |
| Linux service publishes directly to the tested Windows bind mount | `renameat2(RENAME_NOREPLACE)` returns `EINVAL (22)` | Refused; no weaker fallback |
| Linux service publishes to an isolated Docker volume | ext4, non-root operation; sample, SQLite catalog and reservation survive abrupt container termination and a new container | Qualified for this bounded synthetic process-restart test |
| Windows companion publishes locally by its held file handle | NTFS, no replacement, file flush, identity retained, competing writer/parent replacement refused | Native filesystem component qualified with invented files |
| Linux-to-Windows sample transfer | One invented sample transferred with an expiring job/hash-bound credential; native publication and matching SHA-256 verified | Bounded test passed; feature left disabled |

## Persistent Linux test

The test created a uniquely named, labelled Docker volume for synthetic data.
It did not reuse a live volume or change an existing mount. The service ran as
`app` (UID 1654), with a read-only container filesystem, dropped capabilities and
no-new-privileges. `/data` was a persistent ext4 filesystem, not tmpfs. Synthetic
source media remained a read-only Windows bind mount; catalog, library and
staging were separate directories within the test volume.

The existing sample pipeline published a three-second / 803,603-byte sample with
its packet, decode and SHA-256 checks. The container was terminated with SIGKILL.
A different container reopened the same volume: the completed job, immutable
derived lineage, sample hash and source reservation remained intact. A new
sample for the same source was refused. Independent hashing in the reopened
container matched the recorded output; the synthetic source hash was unchanged.

This tests persistent filesystem and process/container restart behavior. It does
not simulate a host power failure, Docker VM disk loss, a drive that lies about
flush completion, or a mid-publication storage fault. Existing synthetic worker
tests cover interrupted checkpoint reconciliation separately. Production volume
capacity, backups, mount identity and host failure recovery still need review.

## Native Windows publication

`WindowsPublication` now provides the companion journal's native publish path:

1. `Scope` pins each existing parent directory and refuses reparse points.
   Failed scope construction releases already acquired handles.
2. Create the owned partial exclusively on local NTFS with read/write/delete
   access, no sharing, `FILE_FLAG_OPEN_REPARSE_POINT` and
   `FILE_FLAG_WRITE_THROUGH`. Refuse directories, reparse points and multiple
   hardlinks. The original staged file handle stays open throughout publication.
3. Flush file data, then use `SetFileInformationByHandle(FileRenameInfo)` with
   `ReplaceIfExists=false`, within the pinned scope. No copy-across-volume,
   delete, overwrite or path-based rename fallback is permitted.
4. Flush again and confirm the volume/file identity is unchanged before closing
   the handle. The published journal record is durable at the Windows API
   acknowledgement boundary; physical power-loss guarantees are not inferred.

Actual NTFS tests used the rebuilt Windows companion assembly loaded into the
existing PowerShell .NET 10 host. They confirmed same-handle publication, flush,
reopened content hash, error 183 on an existing destination with both files
retained, rejection of concurrent writing and parent replacement, hardlink and
junction refusal, journal reopen, duplicate-sequence refusal and refusal to use
a journal with an interrupted partial. No recorder, COM adapter, pairing key,
burn or media deletion was involved. The standalone CLI and optical hardware
still require their own setup/qualification.

The reusable test creates a unique subdirectory and deliberately retains all
invented payloads, hardlinks, junctions and journal evidence:

```powershell
pwsh -NoProfile -File scripts/qualify-windows-publication.ps1 `
  -TestRoot C:\IsolatedLibraryTest `
  -CompanionAssembly C:\Build\Library.Companion.dll
```

The paths are examples. Supply an existing isolated directory. PowerShell must
have a runtime compatible with the assembly (the actual test used .NET 10.0.12).
Omit `CompanionAssembly` to compile and test just the repository's native storage
primitive with the existing PowerShell compiler; that mode omits journal checks.

## Approved bounded transfer

Automatic approval review initially rejected the HTTP content endpoint because
media-serving clients/destinations had not been explicitly authorized. That
patch was not applied and no alternate transfer route was used. The user later
approved a precise localhost, single-job, ten-minute-token test to an isolated
Windows directory. The narrowed implementation and test then succeeded.

The optional [sample handoff](sample-handoff.md) uses the isolated service's host
loopback binding and authenticates the manually invoked local Windows companion
with an offline-issued random grant bound to one job and output SHA-256, expiring
within ten minutes. It provides no unauthenticated grant-minting route,
arbitrary-path API, remote listener, long-lived pairing, background sync, archive
promotion or original deletion. The recipient verifies the entire transferred
hash before native publication. The bearer token stayed out of logs and was
cleared after the test; the grant was removed and the service stopped.

The observed transfer was a synthetic bounded sample only. It does not make the
existing Windows bind mount support Linux publication, qualify real titles, or
authorize a production configuration change.

Official contracts: [Docker volume lifecycle](https://docs.docker.com/engine/storage/volumes/),
[FILE_RENAME_INFO no-replacement behavior](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info),
[CreateFile write-through and metadata behavior](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew),
[FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers).
