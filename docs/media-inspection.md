# Media inspection and review

**Media review** provides read-only inspection and preset/track previews. These
actions create no media or archive-verification evidence. A separate, disabled by
default [stream-copy sample workflow](media-processing.md) requires a fresh live
inspection, an explicit plan and separate approval; a track preview alone cannot
start it.

## Try the review workflow

1. Open Media review and choose **Load synthetic example**. This is invented
   metadata, visibly labelled as such. It includes Japanese original audio, an
   English dub, commentary, accessibility audio, three English subtitle tracks
   and an intentionally unknown audio track.
2. Enter `jpn` as the original language. The Japanese soundtrack and English dub
   are retained. Confirmed commentary is excluded; accessibility audio stays.
3. Resolve the unknown track by explicitly confirming its language and role.
   A ready selection remains a preview: it is not permission to process media.
4. Import an exported HandBrake JSON file to see its actual video settings,
   audio/subtitle behavior and conflicts with the Library policy. Original files
   are read in the browser and are not uploaded or rewritten.
5. Alternatively import FFprobe JSON. Imported metadata is unverified and never
   attached as evidence to a catalog file. Corrections remain in page state and
   are discarded when another observation is loaded or the page is left.

## Optional live read-only inspection

Live inspection is disabled unless `Library__FfprobePath` names an existing,
absolute binary path inside the Linux service container. The Dockerfile installs
the distribution FFmpeg package and configures `/usr/bin/ffprobe`; it does not
install host software. `GET /api/media/capabilities` reports whether
the configured file exists; a successful probe is still required to establish
that it works. `POST /api/media/inspect/{copyId}` inspects only a registered source
or library copy. There is no arbitrary-path or arbitrary-argument API.

The service keeps a Storage-opened regular-file handle pinned while FFprobe opens
its seekable `/proc/<parent-pid>/fd/<fd>` reference. It rejects stale catalog
fingerprints and rechecks both the held descriptor and named source after the
probe. The explicit demuxer/protocol allowlists exclude network and playlist
inputs. The child has a 45-second total deadline, bounded output (4 MiB JSON and
64 KiB errors), bounded probe analysis and one inspection slot. Error output,
nonzero exit, cancellation, malformed inventory or source changes fail closed.
No file is opened for writing and no observed hash/verified state is changed.

The returned tool version, source identity and time are observations, **not**
full-decode checks, content hashes, seeding completion or optical evidence.
Deep HDR frame analysis and cache persistence are deferred. The `/proc` handoff
was qualified in the actual non-root service image with an isolated Windows
read-only source mount. Other deployment/storage combinations still require
qualification; imported JSON works without that runtime dependency.

## Selection rules

- English and explicitly confirmed original-language main audio are retained.
- Confirmed commentary is excluded from a future optimized variant only.
- Descriptive/accessibility audio is retained; removal has not been authorized.
- Unknown roles, missing/unrecognized/conflicting language tags and unconfirmed
  foreign original languages require review. Default disposition, channel count
  stream order and a language-only title do not prove a main soundtrack. ISO language aliases are
  canonicalized using the runtime language catalogue; unrecognized, private,
  multiple-language and conflicting tags require review rather than guessing.
- English regular, forced and SDH subtitles remain separate, with flags intact.
  Unknown subtitle languages require review. Subtitle fonts are preserved.
- No confirmed main audio or an ambiguous primary video blocks readiness.
- The audio policy is copy-only; incompatible containers may not fall back to
  AAC or any other encoder. A preview does not yet qualify container/HDR/client
  compatibility. Only the separate sample plan creates explicit stream mappings.

Presets are references, not executable profiles. In particular x265 RF values do
not become NVENC CQ values, automatic crop numbers are not global fixed crops,
and a stored x265 multipass flag is inactive in constant-quality mode. Arbitrary
advanced preset strings are displayed only and are never passed to a process.

Samples now use **new derived assets** linked to the original, with immutable
lineage in schema v6. Future optimized files must follow that same model rather
than adding a PhysicalCopy to the original Asset. Discs will contain
validated optimized variants. Neither previewing nor archiving authorizes
original deletion, source replacement or changes to torrent payloads/hardlinks.
Bounded samples are excluded from library-copy promotion and disc planning.

## Validation

`npm --prefix client run test:media` runs the review domain tests using Node's
built-in test runner and type stripping (Node 24). It needs no dependency install
and uses only synthetic metadata. The regular .NET test project includes child
process bounds/cancellation contracts and synthetic executable fixtures that
exercise the pinned descriptor, stale catalog rejection, symlink rejection,
source replacement and inspection-slot recovery. These require .NET 10 and
Linux; the existing catalog-restore tests additionally need Python 3.11+.
Browser coverage exercises track corrections, preset/probe imports, invalid
inventories, mobile layout and absence of mutation requests during local review.
Use disposable Linux-backed synthetic roots for tests that publish verified
copies; a Windows-mounted WSL directory may not support every required primitive.

Client qualification must cover the intended playback devices and client versions,
including desktop application versus browser and the TV audio output chain.
Plex playback conversion is distinct from
Library's copy-only stored-audio policy; universal Direct Play is not promised.
