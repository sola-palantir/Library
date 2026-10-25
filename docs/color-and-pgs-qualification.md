# Color range and PGS sample qualification

## Range policy and evidence

Encoding now requires a declared `tv` or `pc` source range, in addition to the
existing SDR/pixel-format/profile restrictions. Missing, unknown or unspecified
range stops during source selection, before encoding. There is no override that
turns unknown into limited. Stream copy can retain unknown range unchanged.
Output inventory validation still requires identical range metadata.

This separates three facts that must not be conflated:

- The [H.264 specification, Annex E.2.1](https://www.itu.int/rec/dologin_pub.asp?id=T-REC-H.264-200305-S!!PDF-E&lang=e&type=items)
  defines an inferred zero value for an absent `video_full_range_flag`. That is
  a codec syntax rule; it does not make an absent FFprobe field universal evidence
  of a correctly tagged source or resolve conflicting container/bitstream data.
- [Matroska Range](https://www.matroska.org/technical/elements.html#Range) has a
  separate unspecified value. Library does not flatten unspecified container or
  probe metadata into a declared range, nor infer mastering intent from histograms.
- The pinned [FFmpeg 9.0.2 x265 wrapper](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavcodec/libx265.c#L310-L320)
  enables video-signal signaling and chooses a range even for unspecified input.
  That explains an output tag; it is not proof of sample-value conversion.
  [x265's range option](https://x265.readthedocs.io/en/master/cli.html#cmdoption-range)
  controls signaling. FFmpeg's [setparams filter](https://ffmpeg.org/ffmpeg-filters.html#setparams-1)
  changes frame properties without changing the pixels.

The native synthetic oracle uses one 64x64 YUV420 ramp spanning all code values.
Diagnostic lossless x265 encoding permits exact decoded-byte comparison; it is
not an added production encoding profile. Each output exists only in memory and
is disposed before the next case.

| Case | Output range | Decoded pixel bytes | First/last luma in ramp |
| --- | --- | --- | --- |
| Unknown input | TV | Identical | 0 / 255 |
| Declared TV input | TV | Identical | 0 / 255 |
| Declared PC input | PC | Identical | 0 / 255 |
| PC input, metadata-only relabel | TV | Identical | 0 / 255 |
| PC input, actual full-to-limited conversion | TV | Different | 16 / 235 |

The unchanged cases share SHA-256
`988ad1e27179852c841c332fd3faf59f04d4a5db5001600ccf9248d48a3542c7`.
The conversion control differs. This proves the diagnostic distinguishes
signaling from conversion; it does not prove the intended range of an untagged
film. The supplied Godzilla encode remains blocked. A bounded header trace hit
the existing log cap and stopped; the cap was not raised and incomplete trace
output was not accepted as range evidence.

### Source-header follow-up - 2026-10-25

A qualification-only reader now inspects a pinned, read-only prefix of at most
4 MiB, stopping its EBML search before the first media cluster. It distinguishes
an absent Colour/Range element from an explicit unspecified value, then reads
AVC configuration SPS fields through the VUI range/color fields. It has bounded
element counts, lengths and bit reads, and rejects unsupported profiles and
malformed syntax. It is not a full Matroska/SPS validator or a production range
resolver. Synthetic tests cover absent/explicit flags, High-profile scaling
lists, invalid syntax and truncated/oversized input.

For the supplied Godzilla copy, the actual header contains:

| Evidence | Observation |
| --- | --- |
| Matroska video track | No Colour element; no container Range declaration |
| AVC SPS | One configuration SPS, High profile 100, SPS ID 0 |
| VUI | Present; `video_signal_type_present_flag` is zero |
| Full-range/color fields | Absent, retained as null in evidence |
| AVC configuration SHA-256 | `8143177ede70c4c2d4f86a82bc60804345d307d9bd85a18e3e8f6c145c4ea176` |
| SPS SHA-256 | `a20397120a78c4505423f273169b81f53f70a593673d801f8b3270748d36b5ce` |

The configuration hash matches the independent FFprobe extradata hash. Native
source identity, size and modification time stayed unchanged. This establishes
a defensible **codec-default limited-range interpretation of the inspected SPS**
under H.264 Annex E.2.1, with no conflicting container declaration. It does not
establish mastering intent, inspect every later in-band SPS, or supply missing
primaries, transfer and matrix values. An absent FFprobe field alone still is
not sufficient evidence.

No encode gate or validator changed. The remaining policy decision is whether
to permit an explicitly reviewed, source-bound codec-default interpretation for
this title, recording that provenance and writing explicit limited-range
signaling without a range-conversion filter. The recommended next step is that
narrow policy, followed by checking active/in-band parameter sets for the chosen
sample and preserving the other unknown color fields. Until implemented and
qualified, this title remains stream-copy-only. Requiring an explicit source
declaration instead is a valid stricter policy and leaves the encode blocked.
Neither choice licenses a general unknown-to-limited fallback.

## Real PGS event window

A separate read-only packet inspection located events in both selected English
PGS tracks. The sample retained original H.264 video, Japanese DTS-HD MA audio
and English regular/SDH subtitles with stream copy; no color transformation or
video encoding occurred. Unknown range stayed unknown.

The requested seek was 164.664 seconds with a 12-second output limit. As described
by [FFmpeg's stream-copy seeking behavior](https://ffmpeg.org/ffmpeg.html#Main-options),
the preceding seek point is retained. Actual video begins at **163.830 seconds**;
measured packet duration is **12.887 seconds**. This is reported as pre-roll,
not exact frame-accurate trimming. Normal product plans still support only samples
from the beginning; this nonzero window was an isolated manual qualification.

The first attempt was correctly refused: the MKV header declared 12 seconds
while packets spanned 12.887. A synthetic seek oracle reproduced a 1-second
header over 1.499 seconds of packets. Native bounded stdout muxing now sets
Matroska live mode to omit that estimate. The same fixture then had no declared
duration and the same measured 1.499 seconds. Validators still require complete
finite packet timing at EOF, enforce sample bounds, and reject a conflicting
header when one exists. No threshold was relaxed.

One bounded repeat of the same real window passed:

- 309 H.264 packets and 1,203 Japanese DTS-HD MA packets copied exactly.
- Four English regular PGS packets and six English SDH packets copied exactly,
  covering all source packets for those tracks inside the measured window.
- Every selected packet hash, size and relative timestamp matched. All streams
  share a -163.830 second timestamp shift; languages, dispositions, inventory,
  extradata and color metadata passed unchanged.
- Full video/audio decoding passed. Output SHA-256 was
  `1742b9f90168c67ecc850b2333298ea359e256047b0424807208e7183fa23577`;
  size was 50,955,215 bytes. Remux peak committed memory was 36,028,416 bytes.
- The single RAM output was disposed. No real output file, new reservation or
  catalog record was created. Read-only source identity/size/mtime stayed equal;
  the full 25 GB source was not rehashed. Existing failed evidence was retained.

### Source subtitle rendering - 2026-10-25

Both selected English PGS streams were decoded from the unchanged input and
overlaid separately on generated black 1920x1080 canvases. One in-memory PNG
placed the regular track on the left and SDH on the right, each scaled to 960x540,
at the nominal selected time of 168.5 seconds. Actual pixel inspection showed
readable two-line captions in both panels, without clipped glyphs in that frame.
This independently establishes source PGS decoding/rendering at one moment;
the tracks have distinct cues and were not treated as interchangeable.

The single PNG was 39,508 bytes, SHA-256
`fc2ca7f10f5d4cb1222c4f71c388eb9b32eb70298dc8ef9d04ffa7b709bad034`.
Peak committed memory was 111,386,624 bytes under the existing limits. Its buffer
was disposed; no image or media file was saved. Source identity/size/mtime matched
before and after. The earlier packet-copy test and this source-render test are
separate: the disposed remux was not played or visually rendered in this step.
No whole-film subtitle synchronization, Plex/device playback, optical archive,
perceptual-quality or full-title encode qualification is implied.

[Silo's read-only feasibility check](hdr-preservation-feasibility.md) found both
Dolby Vision and HDR10+ metadata. Its processing, production activation,
persistent pairing and real-media network handoff remain disabled.

Run the synthetic oracles with `scripts/qualify-native-adapter.ps1 -Mode Color`
or `-Mode Window` and the same explicit root/assembly/tools arguments as the
main native qualification. `-Mode All` includes both. Private real-media scripts
and metadata remain under ignored `.local/native-godzilla-diagnosis/`.
