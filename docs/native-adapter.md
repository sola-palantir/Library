# Native media adapter and resource controls

The local Windows execution path now runs the verified project-local FFmpeg and
FFprobe binaries through the existing worker: inspect, freeze a reviewed plan,
process into a bounded owned partial, validate, journal, publish without overwrite,
reopen/hash and reconcile. Activation is restricted to explicit synthetic-fixture
SHA-256 values. The default processor, API media execution and production pairing
remain disabled. No persistent credential or connection is required by these tests.

## Implemented boundary

- `NativeMediaProcessor` pins the verified binaries and their directory chain for
  its lifetime. It accepts only immutable reviewed sample profiles and explicit
  stream maps. English/original-language main audio and accessibility audio are
  copied; confirmed commentary is excluded; English regular/forced/SDH subtitles
  stay separate. Unknown roles/languages still require review. HDR/HEVC/DV input
  remains blocked by the shared policy.
- `NativeProcess` creates a suspended process already associated with its Job
  Object using `PROC_THREAD_ATTRIBUTE_JOB_LIST`. Its explicit inheritance list
  contains a read-only duplicate of the pinned source plus stdout/stderr pipes.
  No source pathname is reopened. The `fd:` input can seek in the inherited disk
  handle. Child creation has no shell and uses a small environment without inherited
  report/credential variables. The child is resumed only after limits are installed.
- Native defaults are **1 GiB aggregate committed memory**, a **25% CPU hard cap**,
  **two active processes** and **two minutes per subprocess** inside the worker's
  five-minute overall deadline. Limit setup/readback failure blocks execution.
  Cancellation, timeout, root exit and owner death terminate the job tree. No
  breakaway flags are enabled. These do not cap physical working set or establish
  Windows as a security sandbox for untrusted executables.
- Stdout is drained with backpressure through the existing **256 MiB hard staging
  writer**. Stderr is limited to 64 KiB; probe/frame/packet JSON has separate bounded
  sizes and item counts. No child receives the output file's write handle, so it
  cannot bypass that byte limit or no-overwrite publication.
- `SampleValidation` contains the existing pure stream, packet and frame checks,
  now shared by API/Linux and Windows. Native validation checks selected inventory,
  codec/extradata, language/dispositions, copied packet hashes/sizes/timing, encoded
  frame count/dimensions/timing, decode success and final file hash. Chapters remain
  omitted; samples are not archive-eligible and do not imply visual-quality parity.

The bounded stdout mux creates a streaming MKV in Matroska live mode, omitting
estimated header duration and cues. Native validation measures duration from all A/V packet timestamps
and durations at EOF, rejects missing/nonfinite values and applies the existing
sample bounds. If a header duration exists it must agree with that measurement.
Copied source duration tags are not accepted as evidence. The Linux validator's
existing header-duration requirement remains unchanged. This is a deliberate
sample-container tradeoff; finalized seekable whole-title files remain gated.

The first native encode rejected the legacy `-enc_time_base -1` syntax. The native
adapter uses FFmpeg 9's documented `demux` value for the same input-timebase intent.
It does not force a frame rate, scale, crop or change the imported RF22/Slow/8-bit
profile. The failed invented input and zero-byte partial remain retained.

## Windows qualification

Run `scripts/qualify-native-adapter.ps1` with explicit isolated root, compiled test
assembly and verified tools root. Tests are manually invoked; normal unit tests
never launch an encoder. Each media case generates a new synthetic source and
at most one owned output. Only one media process/encode is active at a time.

The actual Windows checks include parent/child cancellation and timeout, output
and stderr overflow, process-count enforcement, memory-limit refusal, CPU limit
readback/accounting, and abrupt independent owner-process exit with no finally
cleanup. Both native process generations must be gone afterward.

Synthetic H.264/PCM/SRT fixtures contain Japanese main audio, English descriptive
audio, English commentary, English regular/forced/SDH subtitles and a French
subtitle. Successful outputs keep exactly the six selected streams. Both remux
and x265 processing reach validated checkpoints; a new default-disabled worker
reconciles them without rerunning FFmpeg. Cancellation keeps an unpublished
partial and refuses automatic retry. The existing native lifecycle suite also
covers collision, stale identity, source/output hardlinks, junctions and independent
process crashes on both sides of publication.

See [validation evidence](validation.md) and ignored
`.local/native-adapter-qualification/` for counts and measured output facts.

The internal `RunBufferedInput` validation helper feeds an already bounded RAM
sample through an anonymous pipe under the same Job Object and output limits.
It adds no listener or media-transfer endpoint. The production source adapter
still requires pinned seekable files. `-Mode BufferedInput` in the qualification
script tests byte-identical delivery, early consumer exit and timeout; no media
files are created. Real diagnostic data and scripts remain ignored and local.

A supplied-copy encode was rejected because undeclared source range became TV
range. Encoding now refuses unknown range during source selection; synthetic
pixel oracles distinguish metadata signaling from pixel conversion. A separate
stream-copy window preserved actual Japanese DTS-HD MA and both English PGS
event streams, with packet hashes, common timing and decode verified. Keyframe
pre-roll is measured and reported. These manual diagnostics do not activate
production jobs or add nonzero sample offsets to the product contract. See
[color and PGS evidence](color-and-pgs-qualification.md). A subsequent source-only
PGS frame render passed; whole-film timing/playback, sparse-PGS resource bounds,
full-title finalization and production real-film execution remain unqualified.
A separately approved source/window-bound default-range test now has reconciled
technical encode evidence; it produced no worker receipt or retained output.
The production unknown-range gate remains unchanged.
[Silo feasibility](hdr-preservation-feasibility.md) now records both DV and HDR10+
in decoded frames; its processing remains blocked.

## Architecture completion checklist

| Item | Implemented | Tested | Activation state |
| --- | --- | --- | --- |
| API owns catalog; worker owns local paths/I/O | Yes | Contract and Windows lifecycle | No live migration |
| Pinned native input, bounded output, no-overwrite journal/publication | Yes | Actual Windows NTFS | Synthetic-only |
| Native probe/remux/x265 adapter | Yes | Generated SDR fixtures and copied PCM/SRT | Real media blocked |
| Job tree, cancellation, deadlines, memory/CPU/process/log limits | Yes | Actual Windows processes | Required; no fallback |
| Shared inventory/packet/frame/decode/hash validation | Yes | Regression and native media checks | Synthetic receipts only |
| Command/result transport | Temporary protocol only | Prior one-job localhost test | Grant cleared; disabled |
| Persistent worker pairing and production catalog receipts | Not enabled | Not claimed | Separate approval and integration |
| Independent companion runtime packaging | Framework-dependent publish | Build passed | Runtime/self-contained packaging needed |
| Full-title finalized mux, sparse PGS, HDR/DV | Not qualified | Not claimed | Separate media qualification |
| Optical hardware and Plex device playback | Not qualified | Not claimed | Separate hardware/client testing |

This completes the bounded local executor foundation. It does not enable automatic
production processing, source deletion, a background service, or optical burning.

## Primary references

- [Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Process creation attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Job memory and lifetime limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
- [Job CPU-rate limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)
- [FFmpeg file-descriptor protocol](https://ffmpeg.org/ffmpeg-protocols.html#fd)
- [FFmpeg encoder time base](https://ffmpeg.org/ffmpeg.html)
