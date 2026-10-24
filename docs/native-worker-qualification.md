# Native worker qualification — 2026-10-24

The approved Windows binary and temporary command/result checks are complete.
Production media processing and pairing remain disabled. The API owns SQLite;
the worker owns Windows file handles and publication. No movie bytes crossed
the HTTP connection, and no supplied film was processed in this milestone.

## Installed tools

[FFmpeg's download page](https://ffmpeg.org/download.html) links to the
[Gyan Windows builds](https://www.gyan.dev/ffmpeg/builds/). The release metadata
and the resolved versioned archive both identified **9.0.2 Essentials** before
execution. The 114,768,076-byte ZIP matched its
[published SHA-256](https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip.sha256):

```text
60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba
```

The archive is retained in `.local/tools/ffmpeg/`; binaries and license/docs are
under `.local/tools/ffmpeg/9.0.2-essentials/`. Extraction checked entry paths,
duplicates, symlink attributes and sizes and used create-new writes. Only the
needed FFmpeg/FFprobe binaries were extracted alongside the documentation.

| Binary | SHA-256 |
| --- | --- |
| `bin/ffmpeg.exe` | `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec` |
| `bin/ffprobe.exe` | `f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52` |

Both reported `9.0.2-essentials_build-www.gyan.dev`. The libx265 capability listing
includes 8/10/12-bit YUV formats; no encoding was performed. No PATH, registry,
service, permissions or security setting changed.

## One native synthetic job

Job `2ea8768133d7451a9c18f9cc6c7c3f5d` used the previously invented 803,603-byte,
3.008-second MKV: H.264 320×180 at 24 fps, Japanese PCM, English descriptive PCM,
and English regular/forced/SDH SRT tracks. Native FFprobe read the six streams.
Native FFmpeg packet-copied the input to the null muxer. The qualification-only
processor then made a byte-identical copy through pinned streams, validation,
native no-overwrite publication, reopen/hash and journal reconciliation.

The single retained output is:

```text
.local/native-qualification/transport/stage/sample-2ea8768133d7451a9c18f9cc6c7c3f5d.mkv
SHA-256: 5b47da5641e1c960e56282fa26bb0799852b64dd35f7ba9f50a42a5106df34f1
Bytes: 803603
```

A collision occupant was preserved. An interruption after native publication
was recovered without processing again; `ProcessRuns` remained one. Native
subprocess input was limited to this exact fixture/hash, with 15-second timeout,
process-tree cancellation and bounded metadata/stderr reads. These test bounds
do not establish safe resource limits or seekable handle integration for movies.
This was a copy/read qualification, **not an encode or remux qualification**.

## Temporary local protocol

The manually invoked Windows test client connected only to `127.0.0.1:5084`.
The isolated API had a named catalog volume and read-only test configuration,
with no media mounts. Only instructions and result metadata crossed HTTP.
An offline-issued random bearer grant was bound to the job and immutable plan
digest for ten minutes. No grant-creation endpoint, Windows listener, persistent
credential, pairing or background service was added. The client refuses
redirects and ignores proxies; secrets were not logged or saved.

| Check | Observed result |
| --- | --- |
| Valid command, native completion and receipt | Passed |
| Wrong bearer | HTTP 401 |
| Wrong job or plan header | HTTP 403 |
| Conflicting receipt or receipt-plan mismatch | HTTP 409 |
| Unknown media field in result | HTTP 409 |
| Expired grant | HTTP 401 |
| Cleared grant | HTTP 404 |
| Duplicate identical receipt | Idempotent; one job and one audit record |
| API restart | Same catalog result retained |

The expiry test moved the same grant's whole ten-minute validity window into
the past; it did not wait for wall-clock expiry. Exact reviewed command JSON is
stored inside the temporary configuration so configuration binding cannot lose
empty arrays and change the plan digest. A regression test covers that round trip.

The catalog contains zero titles, assets, copies or derived assets. Synthetic
evidence cannot become verified media evidence. The temporary grant is cleared
and test services are stopped. The named test volume and output remain available
for inspection. No live catalog was used or migrated.

## Verification and remaining gates

- 150 backend, 53 companion and 17 client-policy regression tests passed. The .NET test suites
  ran under Linux; the synthetic worker/binary/protocol qualification ran on
  Windows NTFS through compiled assemblies inside PowerShell's .NET 10 host.
- The Windows control-plane browser test passed against the final service image,
  including disabled qualification access. Windows x64 framework-dependent
  publish, client formatting, service image build and `git diff --check` passed. The 12 other browser workflows passed
  in the preceding milestone and were not rerun for this protocol-only change.
- Logs and exact results are retained under ignored `.local/native-qualification/`.
  Production processing still uses `UnavailableWindowsProcessor`.

Next gates are a production native subprocess adapter with seekable pinned
handles, OS resource controls and existing media validators, followed by approved
production pairing and transactional derived-asset receipt integration. A Windows
.NET 10 runtime or a self-contained publish is needed for independent companion
launch. The sparse-PGS buffering issue, Silo Dolby Vision and physical optical/Plex
device qualification remain separate. This milestone does not resolve them.
