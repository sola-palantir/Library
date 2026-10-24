# Local sample handoff

The optional handoff connects a validated sample on Linux staging to the native
Windows NTFS publisher. **It defaults to disabled.** The October 24 authorization
and runtime test covered one invented 803,603-byte sample over localhost, not
supplied movies, the main library, full-title processing or a live deployment.

## Authorization boundary

The sender exposes only two routes, both requiring the same offline grant:

- `GET /api/media/samples/{jobId}/handoff`: bounded descriptor and lineage IDs.
- `GET /api/media/samples/{jobId}/content`: the pinned, fully rehashed sample.

`Library__EnableSampleHandoff` must be true and `SampleHandoffGrant` must contain
one exact job ID, byte count, output SHA-256, token SHA-256, issue time and expiry.
The window must be positive and at most ten minutes. The server retains only
the SHA-256 of the random 32-byte bearer token's hexadecimal ASCII representation;
the receiver supplies the original token in `Authorization: Bearer ...` plus the
approved digest in `X-Library-Sample-Sha256`. Comparisons of token hashes use
constant-time comparison. No token belongs in a URL, log, catalog, plan or receipt.

There is no unauthenticated grant-creation endpoint. Missing/incorrect/expired
credentials return 401; a valid credential with another job or hash returns 403;
disabled or invalid grant configuration returns 404. Only the current
`sample-validated` staging copy with matching length/hash and immutable
`stream-copy-sample` lineage is eligible. Changed file identity/content, withdrawn
samples and other asset kinds fail. Output is never substituted from an
arbitrary pathname. Responses are marked `no-store`, and transfer lifetime is
bounded by expiry and request cancellation.

The verified host binding was **127.0.0.1:5086 to container port 8080**. The
manually invoked receiver accepts only an explicit `http://127.0.0.1:<port>`
origin: no user information, additional path/query/fragment, proxy, cookie,
redirect or off-machine URL. It opens no listener. The feature does not configure
firewalls, sharing, pairing or long-lived access. A grant authorizes one job;
retries within its window remain possible. The receiver's journal and exclusive
lock prevent another final output for that job.

## Windows receiver

```text
Library.Companion receive-sample receiver-config.json private-request.json
```

The existing companion configuration specifies two pre-existing, distinct local
directories (`stagingRoot` and `stateRoot`). The private request supplies
`baseUrl`, `jobId`, `bytes`, `sha256` and `bearerToken`. Keep this short-lived
request private and outside source control; passing its filename avoids putting
the token on the command line. Neither the receiver nor the API creates a grant.

The receiver verifies the descriptor against the offline request, saves an
immutable credential-free plan, creates one exclusive owned partial, and checks
the exact length and SHA-256 while copying. It flushes and independently rehashes
the output before publishing through the pinned NTFS handle. After publication,
it reopens and rehashes the final file, checks its file identity, and writes a
local receipt while holding the file against replacement.

The generated filename is `sample-<jobId>.mkv`. A pre-existing file without that
job's immutable plan is a collision, even if its name happens to match. A repeat
of a completed operation revalidates the existing file rather than downloading
or overwriting it. A complete owned partial can be reconciled after interruption;
an incomplete/mismatching partial is retained for manual review. No path silently
truncates, replaces or deletes a file.

The local result is **content-verified-sample**, with `sampleOnly=true` and
`archiveEligible=false`. The plan retains source/output Asset IDs and the
server's immutable-lineage digest. This receipt is not signed optical evidence,
does not modify the service catalog and cannot promote a clip to a complete
movie or archive. Windows input/media roots and torrent payloads are untouched.

## Observed qualification

The compiled receiver and its command route ran through the existing Windows
PowerShell .NET 10.0.12 host. The single invented clip was received from the
isolated ext4 volume and published under the approved workspace test directory.
Independent Windows hashing matched the Linux SHA-256. Missing/wrong token,
wrong job/hash, existing-destination collision, expired grant, disabled feature,
and repeat reconciliation passed actual service/Windows tests. Unit tests also
refuse changed descriptors, redirect destinations, altered/truncated/oversized
payloads and cancellation.

After testing the service was stopped, the grant removed, the receiver token
cleared, and handoff left disabled. The sample and local verification receipts
were retained. No real-title transfer, remote access, host-power-loss,
standalone CLI-host installation or optical-device test is claimed. Native
Windows filesystem and durable Linux staging limits are in
[durable staging](durable-staging.md).
