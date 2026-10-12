# Integration boundaries and later configuration

Nothing in this implementation contacted a live qBittorrent, Plex, Jellyfin or metadata-provider account. Tests use a stubbed HTTP handler and invented data. No credentials were created. The UI distinguishes disabled, unconfigured, not checked, unavailable, supported and synthetic states.

## qBittorrent read-only adapter — implemented, contract tested

The adapter supports the documented Web API v2 shape for qBittorrent **4.5+/5.x with API 2.8.2 or newer within major 2**. These bounds are enforced after reading application/API versions; other versions are refused. The lower API bound is needed for stable per-file `index` fields. Contracts cover representative 4.6.7/API 2.8.19 and 5.0.4/API 2.11.0 responses. This is not a claim that every release in the range has been live-tested.

The only outbound POST is `auth/login`, to establish an existing user's session. Torrent calls are a fixed GET allowlist: `app/version`, `app/webapiVersion`, `torrents/info`, and `torrents/files`. The adapter has no add/delete/pause/resume/rename/recheck/priority/location or other mutation methods. HTTP redirects are disabled. Authentication failure is not automatically retried. Cookies stay in memory for the bounded check and credential values are never returned to the browser or persisted in the catalog.

Configuration is in **Settings → qBittorrent**:

1. **Disabled** keeps the manual workflow available, with no network requests and an explicit unchecked-completion warning in copy previews.
2. **Synthetic demonstration** runs the same adapter against an in-process HTTP handler. It reports a completed Northlight file and an incomplete Paper Moons file using the generated fixtures. It makes no network connection. Use the synthetic remote prefix `X:/LibraryFixture/source`, mapped to the synthetic source root.
3. **Live read-only adapter** saves a WebUI base URL, optional category and explicit path mappings. Saving never connects. A later operator-controlled check requires externally supplied `LIBRARY_QBIT_USERNAME` and `LIBRARY_QBIT_PASSWORD`, for an already existing account. No secrets belong in the UI, repository, command history, screenshots or exported public examples. Credentials are not requested or created by this implementation session.

For later Docker setup, inject the two environment variables using your existing private runtime configuration mechanism and a private Compose override. Do not commit their values. This release does not add or change a qBittorrent account, WebUI authentication rules, network sharing or Docker permissions. On Docker Desktop, the correct service address depends on where qBittorrent runs; review it before supplying an address. HTTPS is preferred where configured; certificate checks are not bypassed. The default Compose file does not enable the adapter or supply any credentials.

**Refresh torrent status** explicitly checks the saved configuration. A snapshot is capped at 200 torrents and 20,000 files, 8 MB per response, and a 30-second overall deadline (15-second per-request timeout). Exceeding a limit produces unavailable status; a partial snapshot is never treated as trustworthy. Use the category filter to scope larger clients. There is no automatic background polling.

Mappings join the reported `save_path` with each file's relative `name`. Prefix matching requires a directory boundary and uses the longest mapping. Windows/UNC prefixes match case-insensitively; Linux prefixes are case-sensitive. Relative traversal and unsafe path components are refused. Mappings can point only to configured source roots, optionally under a relative subfolder. They do not create folders or move files. Confirm mappings in a controlled live pilot; temporary/incomplete qBittorrent locations may not match the final save path.

Per-file progress, not torrent-wide progress, determines reported completion. A completed file in a partially downloaded torrent can qualify; moving/checking/missing/error/unrecognized torrent states block it. The reported file size must match the actual source. Duplicate mapped ownership blocks copying. A mapped source's completion is freshly checked at preview **and immediately before copying**, and the journal pins the configuration revision. Changing configuration invalidates prior approvals. Source identity checks and SHA-256 destination verification still apply.

An unmatched file remains a manual workflow with a prominent warning: absence from a scoped snapshot is not proof of torrent ownership or completion. Disabling the adapter explicitly returns to manual review. Existing published-copy recovery uses its persisted SHA-256 rather than requiring a live torrent client. Seeding seconds and completion time, when reported, are observations only. The existing configurable retention window still starts at first source observation, not from torrent completion, and never deletes anything.

## Metadata matching — implemented with synthetic responses

`IMetadataProvider` defines status, search and candidate-detail operations. The synthetic provider offers competing invented movie matches and an invented TV match. In the inspector:

1. Expand **Find a metadata match**, enter a title and search the clearly labelled synthetic provider.
2. Choose **Review match**; the service fetches the chosen candidate by ID and verifies title kind/revision.
3. Review old/new fields and proposed Plex paths. Locked fields stay unchanged; missing provider values are left alone, not cleared.
4. Apply the catalog changes explicitly. Media paths and sidecars remain unchanged. Provider/candidate identity and the synthetic flag stay in the durable metadata-plan history.

No-match results preserve the manual editor and NFO import/export workflow. Searching and selecting a match do not alter catalog metadata. Before/after preview is shared with the manual bulk editor.

**TMDB remains explicitly unconfigured and disabled in the selector.** It requires a real HTTP provider adapter, an operator-supplied TMDB API credential, request throttling/error handling, attribution and integration validation. The implementation does not consume a TMDB key, perform live lookup or fabricate live results. For later setup, review [TMDB's API registration documentation](https://developer.themoviedb.org/docs/getting-started) and its current terms; obtain credentials yourself only when implementing that adapter. Do not add a key to this repository. Artwork download, provider-ID mapping into NFO, full provider-specific round trips and automatic matching remain deferred.

## Plex — optional status/scan integration assessed, deferred

Plex naming is implemented for movies, editions and single episodes. No Plex network adapter, credentials, server discovery, status query, scan request or metadata refresh is currently implemented. Settings says this directly and exposes no fake action buttons.

A future integration can first provide read-only server/version and library-section status, with explicit Windows/container/Plex path mappings and a privately supplied existing token. A later explicit **scan selected library** action could request discovery after verified copies finish. This should be separate from metadata refresh: Plex distinguishes scanning for changed media from refreshing metadata, and a refresh can affect metadata choices. Do not automatically request a broad refresh, use Plex status as evidence of disk integrity, or treat server reachability as proof that a copied path is visible. Each mutation would need its own clearly labelled user action, scoped section and tested failure state. See [Plex's scanning versus refreshing guide](https://support.plex.tv/articles/200289306-scanning-vs-refreshing-a-library/).

## References used for the contract

- [Official qBittorrent 5.0+ WebUI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29)
- [Official qBittorrent 4.1+ WebUI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-4.1%29)

qBittorrent's API account is not assumed to have server-enforced read-only permissions: the client adapter's fixed endpoint allowlist is the implemented restriction. No permission changes are proposed or performed.
