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

No PGS rendering, Plex client playback, optical archive, perceptual-quality or
full-title encode qualification is implied. Silo Dolby Vision, production
processing, persistent pairing and real-media network handoff remain disabled.

Run the synthetic oracles with `scripts/qualify-native-adapter.ps1 -Mode Color`
or `-Mode Window` and the same explicit root/assembly/tools arguments as the
main native qualification. `-Mode All` includes both. Private real-media scripts
and metadata remain under ignored `.local/native-godzilla-diagnosis/`.
