# Safe media processing milestone

The execution feature produces a **1-30 second sample from the beginning of a
reviewed SDR H.264 8-bit Matroska source**. Stream-copy mode preserves compressed
video. The separately enabled x265 sample mode compresses progressive video up
to 1920x1080 using CRF 22, Slow, and 8-bit output. Both are disabled by default
(`Library__EnableSampleRemux=false`, `Library__EnableSampleEncode=false`). Neither
creates complete optimized titles, processes HDR/HEVC/Dolby Vision inputs,
replaces originals or qualifies optical discs/Plex clients.

## Bounded x265 calibration

The fixed profile references the unchanged `1080p-preset.json` SHA-256
`2b53699a614ea5b260d844a37e4f79425c8ae40b2abca21f677dc63f33e2b383`.
Its x265 RF 22 / Slow / 8-bit settings are a starting point for FFmpeg libx265
CRF 22 / Slow / yuv420p, not a promise of bit-identical HandBrake output or visual
equivalence. The exported extra video options are empty. Saved crop values,
HandBrake's audio fallback and subtitle burn rules are not applied. No filter,
crop, scale, forced frame rate, tune or user-supplied command string is accepted.
Audio, English subtitle tracks and attachments use stream copy.

`-fps_mode passthrough` and the demuxer time base preserve source timing. Decoded
frames must have matching counts, timestamps (within the same 2 ms muxer
rounding allowance used by copied streams), dimensions, pixel format and sample
aspect ratio. Interlaced, unknown field order, rotated/stereo video, rates above
60 fps, odd dimensions and HDR/DV are refused. Actual decoded frames must also be
progressive. The output must be HEVC Main, with unchanged color metadata. Copied
audio/subtitle payload hashes, sizes, order, language, flags and timing must
still match, using the encoded-video timestamp shift as their reference.

The isolated container refused NUMA thread-affinity requests. The fixed profile
therefore uses `pools=none:frame-threads=2:log-level=error`, retaining its existing
security restrictions and strict stderr gate. This disables x265 worker pools
and WPP, trades speed for bounded resource use, and is recorded in immutable
evidence. It is another reason not to assume identical HandBrake output.

Evidence compares video packet bytes for the same decoded frame interval, not
a short output against an entire movie. It also records output bytes/hash and
copied packet counts. Negative savings are allowed and reported. No SSIM, PSNR,
VMAF or subjective quality claim is inferred; an opening sample is not a
representative whole-title compression forecast. Empty subtitle packet counts
cannot establish subtitle-event fidelity for later scenes.

An input window of sample duration plus five seconds bounds read-ahead; a separate
output duration keeps the requested clip boundary. Decoder threads and input/
mux packet queues are bounded. An encoder watchdog polls resident memory every
100 ms and terminates the process above 1 GiB. This is a sampled application
limit, not a kernel memory ceiling; an isolated container resource limit should
also protect the host. Qualification uses a 2 GiB container ceiling. A previous
Godzilla attempt without these guards reached 6.9 GB and was OOM-killed before
writing any output. This failure is retained as evidence; it is not a successful
compression measurement.

Encoded samples have `video-encode-sample` lineage and use the same source lease,
single worker, durable staging, no-overwrite publication, cancellation and
recovery paths. They cannot become complete library/archive copies. The
synthetic-only HTTP handoff deliberately refuses this kind, even with a grant.

Official references: [FFmpeg libx265 options](https://ffmpeg.org/ffmpeg-codecs.html#libx265),
[FFmpeg frame timing](https://ffmpeg.org/ffmpeg.html#Advanced-Video-options), and
[x265 performance options](https://x265.readthedocs.io/en/stable/cli.html#performance-options).

The service image includes distribution FFmpeg/FFprobe. An operator can enable
samples only after qualifying isolated staging storage and reviewing root
mappings. The October 24 test used the real Dockerfile, a non-root user, a
read-only container filesystem, no capabilities, no-new-privileges, a read-only
Windows synthetic source mount and disposable Linux tmpfs destinations. It was
an isolated test, not a live installation.

## Destination blocker

The tested Windows Docker Desktop bind mount supports the pinned source read and
directory fsync, but `renameat2(RENAME_NOREPLACE)` fails with `EINVAL (22)`.
Therefore **this Windows destination is not qualified** for Library publication.
The worker tests publication with empty owned files before starting FFmpeg and
refuses that destination without producing a media sample. There is no plain
rename, overwrite, elevated-user or permission-relaxation fallback.

Linux tmpfs passed the initial lifecycle test. A later isolated ext4 Docker
volume test retained the sample/catalog across abrupt termination and a new
container, and native Windows NTFS publication passed separate filesystem tests.
The separately approved [localhost sample handoff](sample-handoff.md) also passed
an invented-clip transfer with an expiring grant. It remains disabled by default;
none of these results establishes power-loss or optical qualification. See
[durable staging findings](durable-staging.md).
Leave sample execution disabled on the currently failing Windows bind mount.

## Review and execution contract

1. Live inspection binds the actual catalog copy, file size, timestamps and file
   identity. Local probe JSON and invented examples cannot authorize processing.
2. Review every audio/subtitle language and audio role. Keep English and confirmed
   foreign-film original main soundtracks, retain descriptive/accessibility
   soundtracks, exclude confirmed commentary only, and retain separate English
   regular/forced/SDH subtitles and attachments. Unknown tags or roles block a
   plan. A confirmed main soundtrack is required.
3. Confirm that the source is complete/quiescent and reviewed as SDR. Metadata
   indicating HDR, HDR10+, Dolby Vision, HEVC, non-8-bit video, multiple primary
   videos or unsupported stream types still blocks the request. Confirmations
   cannot override these guards. When configured, the existing read-only torrent
   guard also rechecks readiness and its configuration revision.
4. Preview records an immutable job payload with explicit selected stream indexes
   and generated `samples/<job-id>.mkv` destination. It starts no FFmpeg operation
   and creates no output file. Approval acquires one persistent reservation per
   source Asset; a second sample is refused, including from another physical copy
   of that same Asset. The single existing file worker serializes execution.
5. Recheck source identity and technical inventory. Test atomic no-overwrite
   publication, reserve 320 MiB free space, and exclusively create one job-owned
   partial through the existing anchored Storage boundary. FFmpeg receives pinned
   file descriptors, fixed allowlists, explicit `-map` values, and `-c copy` for
   all retained streams, with only the video override from the fixed x265 profile
   when selected. No audio fallback, filters or arbitrary encoder arguments are
   accepted. Sample chapters are intentionally omitted.
6. Bound the run to five minutes and 256 MiB. Probe output is bounded. Require the
   expected stream inventory, codec/extradata, video/color/audio properties,
   languages and dispositions; compare copied packets' SHA-256, size, order and
   timing; reject missing early packets. Encoded video instead requires the
   decoded-frame proof described above.
   Decode retained video/audio to the null sink, then hash the entire staged
   output. A short sample cannot establish whole-title quality or integrity.
7. Persist validation plus output SHA-256 before atomic publication. Reopen and
   hash the published file, check identities again, then register a **new Asset**,
   its staging PhysicalCopy and immutable lineage to the source Asset/copy/job.
   Source content hashes are not invented from a probe or partial sample.

The `-y` switch is confined to an already exclusively created, pinned owned
partial descriptor; it never addresses an existing media pathname. Final
publication always uses the existing no-replace Storage primitive.

## Recovery and withdrawal

An interrupted partial can be restarted only as that job's own partial. A final
file already present is accepted only when the persisted validation checkpoint,
length and full SHA-256 agree. An unbound collision or corrupted destination fails
without deleting anything. Reconciliation registers lineage at most once.

Cancellation terminates the child and retains the source and any published
file. Failed validation invalidates current output hash/state while retaining
immutable historical evidence. Withdrawal removes only the job's incomplete
partial and marks a completed copy `withdrawn`; it retains published files,
lineage and the source reservation. An empty reservation is released only when no
published file or derived record exists. There is deliberately no automatic
delete-and-replace path for generating further variants.

Samples have `staging` role and `sample-validated` state. The copy preview and
worker refuse to promote either sample kind into the library, so a
short sample cannot become a complete-title archive through Organize. Current
disc planning requires verified library copies. Sample validation is neither
optical verification nor full-title optimization evidence.

## Qualification and next phases

- Automated synthetic contracts cover policy, HDR refusal, explicit maps,
  packet/timing alteration, schema migration, immutable lineage, collision,
  recovery, corruption, cancellation, withdrawal and sample-promotion refusal.
- Actual FFmpeg in the service image passed one invented three-second H.264,
  Japanese PCM + English accessibility PCM and English regular/forced/SDH SRT
  sample. English commentary was excluded. Packet payloads matched and decoding
  passed. This does not qualify DTS, PGS, font attachments, real films or all
  container metadata combinations.
- Browser coverage exercises live-inspection gating, separate approval,
  explicit selections, withdrawal and mobile layout using mocked process results;
  the independent container test is the execution evidence.
- Next: representative per-title calibration and full-title no-op/remux
  decisions with complete chapters/metadata and crash fault tests. Durable Linux
  staging and a narrow synthetic-only Windows handoff have separate evidence.
  Keep source dimensions/frame rate, avoid global stored crop values, and treat
  CPU x265 RF and GPU encoder CQ as separate controls requiring calibration.
- HDR10+/Dolby Vision need encoder/profile-specific metadata and playback checks.
  The supplied DV profile 8 episode remains blocked from processing. Objective
  quality scores can aid comparisons but cannot establish correct HDR, grain,
  subtitle readability or subjective quality without visual/device review.
- Final optimized assets, not samples, are the intended disc contents. Archive
  manifests must bind the optimized asset hash and original lineage; physical
  read-back and restore testing remain necessary. Archiving never authorizes
  deleting the original or altering a torrent/hardlinked payload.

Official behavior references: [FFmpeg stream selection and streamcopy](https://ffmpeg.org/ffmpeg.html),
[FFprobe packet and extradata hashing](https://ffmpeg.org/ffprobe.html).
See [validation evidence](validation.md) for observed scope and remaining limits.
