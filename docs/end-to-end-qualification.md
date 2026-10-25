# Offline RF20 workflow qualification - 2026-10-25

This is an isolated qualification path, not an enabled production workflow.
The native test harness handles media; the offline API-domain runner alone writes
its new test catalog. Only checkpoint/manifest metadata crosses that boundary.
No listener, persistent grant, media-transfer endpoint, live catalog or physical
disc is involved. Production processing, pairing and Silo processing stay off.

## Retained sample

The first 12-second attempt exceeded the unchanged two-minute subprocess limit.
The process tree was terminated and its incomplete RAM output discarded, with
no staged or published file. The reason for the slower run is not established.
One eight-second retry then completed under the same limits; no further encode
was needed. Both attempts used the supplied Godzilla copy without modifying it.

The retry uses source time 164.664 through 172.664 seconds, libx265 CRF 20,
Slow, 8-bit, original dimensions/timing, no crop, and copy-only Japanese audio
and both English PGS tracks. Source identity, complete source SHA-256 and active
SPS/PPS evidence were rechecked. The reviewed limited-range interpretation stays
bound to this source/window; production unknown-range rejection is unchanged.

| Measurement | Observed result |
| --- | --- |
| Video | 192 frames, 1920 x 1080, original timing retained |
| Copied packets | 828 Japanese audio; 2 English regular PGS; 4 English SDH PGS |
| Copied payloads/timing | Exact packet hashes and common timestamp shift verified |
| Whole output | 9,826,780 bytes |
| Video payload | 5,927,627 versus 27,949,239 source bytes; 78.79% reduction |
| Encode | 90.54 seconds; 589,094,912 bytes peak committed memory |
| Duration | 8.832 seconds measured from packets, including audio preroll |
| Output SHA-256 | `edfa73c2771d094cfe28c58ff98b845db3ea8ae2f295b96a983005614da479d1` |

Full video/audio decode and inventory/frame/packet/duration validation passed
before acceptance and staging. Source/output overviews, native 640 x 480 crops
at frames 12/120/180, and both subtitle renders were actually inspected. Framing
and broad brightness match; grain and distant foliage are smoother in the output.
The English cue images match their corresponding source images. These are static
observations, not viewed motion or perceptual parity. The window contains a dim
walking shot, a grainy exterior and a cut to a door; it is too short to qualify
whole-film motion, dark-scene quality or savings. The prior same-window 12-second
RF20/RF22 comparison remains a separate measurement.

One retained media file is under the checkout:
`.local/playback-qualification/Library Qualification - Godzilla RF20 (8-second sample).mkv`.
It is streaming MKV with packet-measured duration; finalized duration/cues and
device-specific seeking are not implied. Media, diagnostic images, checkpoints,
catalogs and source hashes remain ignored local evidence, not repository assets.

## Completed workflow checks

| Stage | Actual result |
| --- | --- |
| Cancellation | Cancelled before publication; validated partial retained and no final path created |
| Process interruption | A separate Windows test process exited with code 79 after atomic publication, before the completion journal |
| Restart | A fresh process rehashed source and output, reconciled the existing final file, and wrote completion without another encode/copy |
| Seeking | Requests at 7, 1 and 4 seconds returned frames at 7.039, 1.033 and 4.036 seconds; each three-frame result exactly matched full-decode pixel hashes |
| Subtitle seek | Both regular and SDH PGS rendered after seeking; the actual diagnostic image was inspected |
| Catalog | Two assets/copies, one immutable derived relationship and reservation; repeated registration idempotent |
| Archive plan | One explicitly labeled qualification manifest; production sample eligibility remained refused |
| Catalog recovery | SQLite backup restored to a separate test database; both reopened in a fresh container with integrity `ok`, matching lineage/manifest and retained immutable guards |
| Read-back | Entire retained output read through a pinned native handle; byte count/SHA-256 matched the manifest and identity stayed unchanged |

The actual test plan label is `LB-GWHHC-7BYT4R-WPKGG2-W`, with manifest digest
`5d782daab0f13b6014153be9f31e105ab258db7d9a8b8e98332682a7f04755b1`.
The catalog ran on a dedicated durable Docker volume, with network disabled and
no media mounts. Publication/read-back ran on native Windows NTFS. This proves
the manually exercised offline components; it does not validate an enabled
production queue or signed worker-receipt integration. A process exit test is
not a machine power-loss test.

## Test-only boundaries

`QualificationPublication` uses the existing rooted Windows handles, exclusive
creation, full read-back hashes, durable journal and no-overwrite publication.
It does not implement a production receipt, queue or transport. Its synthetic
fixture covers cancellation retaining the validated partial, interruption after
publication, idempotent recovery, a second-output refusal and collision retention.

`QualificationCatalogFlow` registers separate source/output assets, a staging
sample copy, immutable derived lineage, one reservation and an audit record in
a new catalog. Repeated registration is idempotent; changed evidence is refused.
The production archive resolver must still reject the sample. An explicitly
labeled offline qualification then exercises the existing packing/manifest
service directly, without promoting the sample to a complete archive copy.

The catalog runner is deliberately outside the application entry point:

```sh
dotnet build tests/Library.Qualification -c Release
dotnet tests/Library.Qualification/bin/Release/net10.0/Library.Qualification.dll NEW_ISOLATED_DIRECTORY COMPLETED_CHECKPOINT_JSON
dotnet tests/Library.Qualification/bin/Release/net10.0/Library.Qualification.dll --verify EXISTING_QUALIFICATION_DIRECTORY
```

Use the supported Linux runtime and durable Linux storage for this catalog test.
The native publication fixture is separate and requires Windows NTFS/.NET 10:

```powershell
./scripts/qualify-offline-workflow.ps1 `
  -TestRoot EXISTING_ISOLATED_TEST_DIRECTORY `
  -CompanionTestAssembly ./companion/Library.Companion.Tests/bin/Release/net10.0/Library.Companion.Tests.dll
```

The fixture creates invented tiny payloads only. It does not discover or process
real media. Real-source qualification still requires an explicitly reviewed local
harness and source-bound evidence; the general application cannot import these
test checkpoints as production authorization.

## Remaining playback and archive limits

Actual moving playback, heard audio, Plex/client direct play or transcoding,
device subtitle switching and display-chain behavior are unrun. The available
inspection surface returns static images. No Plex configuration was changed.
The smallest device handoff is to open the retained file locally, check Japanese
audio and each English subtitle option, seek backward/forward, and inspect the
walking shot and grain. If the user makes it available through their existing
Plex setup, repeat on Apple TV 4K, iPad Pro, Windows and Z Fold7 and record the
client version plus Direct Play/Direct Stream/Transcode and subtitle behavior.
Apple TV audio results also depend on its output chain.

A catalog backup/reopen and full manifest-bound read-back of this one retained
file do not prove an independent media restore or optical recovery. No second
movie copy, disc image or physical burn is created. Those remain separate tests.
