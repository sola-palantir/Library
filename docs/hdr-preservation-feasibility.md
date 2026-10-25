# Silo HDR preservation feasibility

## Read-only observation - 2026-10-25

The verified native FFprobe 9.0.2 binary read a one-second interval from the
supplied test copy through a pinned read-only handle. It returned 23 video
packets and 23 decoded frames. No Silo remux or encode was attempted and no media
output was created. Native source identity, size and modification time matched
before and after. Peak committed memory was 197,455,872 bytes, within the existing
1 GiB process-job limit. This interval is not a full-title scan.

| Field | Observed value |
| --- | --- |
| Video | HEVC Main 10, 3840x1920, YUV420P10LE |
| Color | Limited range, BT.2020 primaries, BT.2020 nonconstant matrix, PQ transfer |
| Dolby Vision configuration | Version 1.0, profile 8, level 6, compatibility ID 1 |
| Layers | Base layer and RPU present; enhancement layer absent |
| Each of 23 decoded frames | Dolby Vision RPU and parsed metadata; HDR10+ SMPTE ST 2094-40; mastering-display and content-light metadata |

The required preservation scope therefore includes **both Dolby Vision and
HDR10+**, alongside static HDR metadata. A Dolby Vision configuration record by
itself is not proof that the dynamic metadata survived. Source dimensions must
remain 3840x1920; a generic 2160-line target or saved crop is not appropriate.

## Feasible candidates and unqualified behavior

Keeping the existing file is the current supported preservation choice. An
explicit stream-copy remux is a future candidate: it could preserve the original
HEVC payload, but must prove selected packet identity, timestamps, configuration,
RPU and HDR10+ metadata after muxing. No such Silo output was produced here.
Neither container acceptance nor a successful base-layer decode proves that a
particular Plex client will display dynamic HDR.

The installed encoder help exposes `libx265` Dolby Vision support. The pinned
[FFmpeg 9.0.2 wrapper](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavcodec/libx265.c#L781-L798)
can generate RPU data from frame-side Dolby Vision metadata, with version guards
and errors for missing required frame data. Its examined frame path does not
automatically forward HDR10+ frame-side data. This is source-code evidence, not
a tested Silo encoding result. Enabling Dolby Vision alone is insufficient.

[x265 documents a separate HDR10+ JSON input](https://x265.readthedocs.io/en/master/cli.html#cmdoption-dhdr10-info).
That indicates a possible integration route, not a qualified configuration in
Library. Extraction, frame association, trim/reorder handling, build support and
round-trip comparison would all need qualification. Library currently has none
of that production path. No metadata was stripped, converted, regenerated or
assumed preserved for this source.

The sensible default remains **no re-encode for this title**. Future work should
first qualify a packet-preserving remux and per-frame checks for both dynamic
formats, then assess any lossy path separately. Timing, dimensions, bit depth,
color properties, audio/subtitle copy and source safety remain acceptance gates.
Perceptual review and playback on the actual iPad, Apple TV/audio output chain,
Windows Plex client and Samsung device are separate checks; no device playback
is claimed. Production processing, persistent pairing and media transfer stay off.

Private probe JSON and local harness evidence are retained in ignored `.local/`;
no personal file paths, media bytes or credentials are published with this report.
