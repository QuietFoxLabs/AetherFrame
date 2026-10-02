# AetherFrame server interface, version 1 (DRAFT)

What `server/AetherFrame.Server` answers, and how the plugin (N2-9 and N2-10) talks to it. It carries the protocol of [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) over HTTPS and applies decision batches B and C ([DecisionRegister.md](DecisionRegister.md)). It is a draft, like the protocol, until the owner's two-player test (NETWORK2.md, section 2) has passed.

**Built so far** (N2-7b and N2-7c): everything below, and the image worker (section 8). A server with no worker socket configured refuses every image (`image-refused`), so a Plate with images is never served unprocessed.

## 1. Transport

- **HTTPS only**, to the one deployment name the plugin is configured with (section 14.1 of the specification; decision R2). Caddy terminates TLS in front of the server (N2-8), and the server trusts forwarded headers from Caddy's address alone.
- **Every request is a `POST`**, except `GET /v1/status`. Paths carry no identifier, name, code or marker: those travel only in bodies (decisions R4, S5 and C7), since paths reach logs.
- **Request bodies** are `application/octet-stream`, bounded before they are read (section 5).
- **Responses** are `application/octet-stream`, `application/json` (a closed set of small objects, section 4), `image/png` or `image/jpeg`, each with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. A failure is a status with no body, except where section 3 says otherwise.

## 2. Signed requests

Every request but `status` and `challenge` is signed. Its body is:

| Field | Size | Value |
|---|---|---|
| proofLength | 2 | `u16`, big-endian: 201 to 454 |
| proof | proofLength | a request proof (section 14 of the specification) |
| payload | the rest | for a submission, section 2.2; for an action, its body (section 2.1) |

1. The client asks `POST /v1/challenge` for a challenge: the answer is its 32 bytes.
2. It signs a proof of the request's kind, for the deployment it is configured with, under that challenge, binding the payload: `SignAction` for an action, `Sign` for a submission.
3. The server takes the expected kind **from the path**, never from the request, then checks the proof as section 14.4 (a submission) or 14.5 (an action) says, then consumes the challenge (rule 10), and only then acts.

A challenge the server doesn't know, has already consumed, or issued more than 300 seconds ago gets `409` with a fresh challenge as its body: the client signs again under it. That fresh challenge counts against the address's challenge limit like any other: past it, the answer is `429` with none.

### 2.1 Actions

Each body is one JSON object, UTF-8, at most 4,096 bytes, with exactly the properties listed: an unknown or repeated property, a wrong type, or trailing data is `400`.

| Path | Kind | Body | Answer |
|---|---|---|---|
| `/v1/lodestone/code` | 2, a code | `{}` | `200` `{"code": "AF-…", "expiresInSeconds": 3600}` |
| `/v1/lodestone/check` | 3, a check | `{"lodestoneId": "12345678", "code": "AF-…", "name": "…", "world": "…"}` | `200` `{"profileId": "prf_…", "name": "…", "world": "…"}`; `422` for every failure (C2), including a page that doesn't show the name and World the body claims |
| `/v1/lodestone/reread` | 4, a re-read | `{}` | `200` `{"name": "…", "world": "…"}`; `404` when the key is bound to no character, or its character is no longer on the allowlist (the binding is kept); `410` when another key's check took it over |
| `/v1/opt-out` | 5, opting out or pausing | `{}` to opt out; `{"mode": "pause"}` to pause | `204`, whether or not anything was bound |
| `/v1/lookup` | 6, a lookup | `{"name": "…", "world": "…"}` | `200` a served profile (section 8.6 of the specification); `404` for every cause (C5) |
| `/v1/image` | 7, an image | `{"name": "…", "world": "…", "marker": "mrk_…", "index": 0}` | `200` the image; `404` for every cause |
| `/v1/report` | 8, a report | `{"name": "…", "world": "…", "reason": "…"}` | `204`; `404` when no Plate is found |

- **A Lodestone id** is its decimal digits, as a string: 1 to 10 digits with no leading zero (C2).
- **A name** is the character's full name, and **a World** its Home World's name, compared as C1 says: the name in NFC, lower case, with runs of spaces folded, and the World without regard to case.
- **A reason** is one of `offensive`, `impersonation`, `spam` or `other`.
- **Lookups, images and reports** need the signer to be bound to a character that is on the allowlist (C5, C8): otherwise `404`, as if nothing were found, or `410` when another key's check took the signer's character over.
- **Opting out** deletes the binding and everything published for it (C4). **Pausing** (C3) deletes the published Plate and its images at once and keeps the binding and its revision records: the next publish shares again, with no new check.

### 2.2 Publishing

`POST /v1/publish` is a submission (kind 1). Its payload is:

| Field | Size | Value |
|---|---|---|
| documentLength | 4 | `u32`, big-endian: 1 to 1,048,576 |
| document | documentLength | a signed ProfileSnapshot, schema 2 |
| imageCount | 1 | the number of images, equal to the snapshot's |
| images | | for each of the snapshot's images, in its order: a `u32` length, then the prepared copy's bytes |

`[updated 2026-09-30: a publish must still arrive whole within its challenge's 300 seconds (rule 10), since the challenge is consumed once the body is read: the largest publish needs about 140 KiB/s upstream, a Plate with a few ordinary images a small part of that. The plugin gives a publish 5 minutes (N2-9a).]` `[updated 2026-10-02: the plugin now gives a publish 340 seconds (the proof's 10, the body's 300, and 30 for the server's own work and the network), plus 60 for each image it carries (the worker's 30 seconds to take it and 30 to answer, one image at a time): 820 seconds for eight. So it never stops waiting while the server would still answer. Every other request keeps 30 seconds.]` The server reads the proof first, within 10 seconds, and checks it before anything else: section 14.4's first two steps (`CheckSubmissionProof`), a live challenge (not yet consumed), and a signer bound to a character on the allowlist. Only then does it take a publish slot and read the rest, at no less than 16 KiB a second after a 10-second grace and within 300 seconds, its challenge's life. `[updated 2026-10-01, the open alpha: four slots, at most one per character and one per address range (an IPv4 address or an IPv6 /48), so no one player can hold them all; before, two slots within 45 minutes, when only the testers could hold one.]` Each character also has an hour's budget of 32 images through the worker, and after three images the worker failed on in an hour, its publishes with images wait the hour out (`429`), since each may hold the worker's one pipeline for a whole run. It accepts the publish only when the proof passes all of section 14.4 with the document; the signer is bound to a character on the allowlist; the snapshot carries that binding's profile id (C4); its `createdAt` is at most 300 seconds ahead of the server's clock (N6); its revision is new or an exact resubmission (rule 4); and each image matches its declaration and section 8.2.1, then passes the image worker (I2). Then the revision becomes the character's latest: its served profile is built with a fresh marker, the previous revision's document and images are deleted, and the revision record is kept (N2).

| Answer | When |
|---|---|
| `204` | accepted, or an exact resubmission of a known revision |
| `409` | the challenge, as above |
| `410` | the signer is no longer bound to the character: another key's check took it over (C1) |
| `422` with a reason | refused; the reason is one of the `publish` codes of section 4 |
| `503` | every publish slot is taken, or the publishing character or its address range already holds one, or the image worker's queue is full: try again later |

## 3. Unsigned requests

| Request | Answer |
|---|---|
| `GET /v1/status` | `200` `{"protocolVersion": 32769, "api": 1, "minimumPlugin": "0.1.6"}` |
| `POST /v1/challenge`, empty body | `200` the 32 challenge bytes |

## 4. Closed values

- **A `422` from `publish`** carries one of these reason codes as `text/plain`: `not-bound`, `wrong-profile`, `not-a-layout`, `clock-ahead`, `revision-conflict`, `image-refused`, `document-refused`.
- **Every other failure** is its status alone: `400` for a malformed body, `403` for a proof that fails, `404`, `413` for a body over its bound, `422` for a failed check, `429` for a rate limit.

## 5. Bounds

| Request | Largest body |
|---|---|
| `challenge` | 0 bytes |
| an action | 2 + 454 + 4,096 = 4,552 bytes |
| `publish` | 2 + 454 + 4 + 1,048,576 + 1 + 8 × 4 + 41,943,040 = 42,992,109 bytes; the bound is 43,000,000 |

A body over its bound is refused with `413` before it is buffered: Kestrel's own limit is set per endpoint, and the server reads no more than the bound however the request is framed.

## 6. What the server keeps

Decision batch C, C7, says it: for each binding, the Lodestone id, the name and World, the key's identity and the profile id, the latest revision's document, served profile and images, and N2's revision records; and reports, for 30 days or until the operator acts. It keeps no lookup log, no address, and no copy of a Lodestone page. A day number is kept only while a binding's Lodestone page shows "not found", for C1's two re-reads a day apart.

## 7. Choices batch C left to N2-7

Recorded in DecisionRegister.md as "N2-7b's server details":
- **Framing.** A signed request is `u16` proof length, the proof, then the payload. Actions carry small JSON objects read strictly, and failures are bare statuses. One reader handles every action. No field outside the proof and the payload changes what the server does.
- **A failed check doesn't use up the code.** Only a successful binding does, so a player who pastes the code after asking for a check can simply check again. A code is still bound to one key and lives an hour (C2).
- **A check by the key already bound** refreshes the name and World and keeps the profile id. A check by another key takes the character over with a fresh one (C1, C4).
- **A re-read that reads a name and World another binding shows** hides that other binding: the newest read wins, as C1 says for checks. Two characters can't hold one name on one World.
- **Re-reads** are paced evenly over the day, at least 2 minutes apart, within half of the fetch budget (C2), so they never crowd out a check. A re-read the player asks for counts as a check against the player's limits and uses the whole budget.
- **"Not found"** is the Lodestone's own page: status 404, its title, and one `error__body` with no character on it. Anything else is an outage and changes nothing (C1). While a binding's page shows "not found", the server keeps the day number (UTC) it first did, and nothing else. It removes the binding on a "not found" two or more days later by that number: at least 24 hours after the first, and at most 48. A re-read applies only to the character it read, so a re-read in flight while the key opts out and binds another character changes nothing.
- **A failed check** answers no sooner than 3 seconds after it started, wherever it failed, so its timing doesn't tell an id on the allowlist from one off it. "Try again later" (`503`, a full fetch budget) is answered before the code, the allowlist or the key's binding are looked at. What remains is a fetch longer than 3 seconds, which only an allowlisted id can cause: in stage 1 that tells someone holding a key and a live code that one of two ids is a tester's.
- **Limits C6 doesn't state:**
  - challenges: 600 an hour per address, counting those a `409` carries;
  - opting out: 10 an hour per key and 30 per address;
  - images: 8 times the lookup limits, since a Plate has up to 8.
  - An address limit applies to an IPv6 address's /64, /56 and /48 at 1, 4 and 16 times the limit.
- **An unhandled error** answers `500`, and logs only the exception's type.
- **Pausing** is the opting-out kind with `{"mode": "pause"}` in its signed body, so it adds no request kind (C9). It deletes what is served and keeps the binding and the revision records (N2), so an old revision can't come back.
- **Retractions.** Publishing accepts only a schema 2 snapshot: a `ProfileRetraction` is refused (`not-a-layout`). In stage 1, opting out and pausing take its place, and there are no tombstones (C4).
- **A key whose character another key took over** is remembered, by its identity alone, until it binds again or opts out, so that its plugin gets `410` and can say what happened (C1).
- **Publishing** is also limited to 120 an hour per address, besides C6's 60 per character, and to two at a time: a third gets `503`, so buffered bodies stay bounded. A publish is authenticated before it waits for a slot (section 2.2), and its body is read once, into a buffer of its declared length.
- **Resuming after a pause** takes a new revision: resending the revision that was live before the pause is an exact resubmission (rule 4), answered `204`, and shows nothing. N2-9 signs a new revision to resume.
- **Reports** are limited to 60 a day per address as well as C6's 20 per key, and an hourly sweep drops those past 30 days whether or not another arrives (C5).
- **Viewing's limits** are taken once the requester is found bound and allowed, before the target is looked for: a key with no character costs nothing, and a "not found" counts as a find (C6).
- **A taken-over key's identity** is kept until that key binds again or opts out, with no time. It is one row per takeover, and says only that the key once held a character that another key now holds.
- **The served profile** is built when a revision is published, under a fresh marker (D6), and stored with it. An image is served for the current marker and an index only, as `image/png` or `image/jpeg`.
- **Forwarded headers** are believed from `AetherFrame:KnownProxies` alone (R4). The server refuses to start with ASP.NET Core's `ForwardedHeaders_Enabled` switch on, from any configuration source, since that switch clears the trusted lists; N2-8 puts Caddy's container address in `KnownProxies`, or every client would share one address's limits.
- **A deletion's checkpoint** runs after the deletion commits, whether or not the client is still there. One that readers block for 30 seconds is retried every 30 seconds until it completes (D1).
- **The allowlist** is read from the configuration at each use. Configuration from environment variables is read once, at start, so N2-8 keeps it in a configuration file that reloads, or removing an id takes a restart.

## 8. The image worker

Decision I2's worker, `server/AetherFrame.ImageWorker`, decodes each image a publish carries and encodes it again. The server serves only what the worker made, and only after checking it.
- **The exchange.** The server owns a Unix socket (`AetherFrame:ImageWorkerSocket`) in a folder the worker's container mounts read-only. A worker run connects and receives one job:
  - the job is `AFIJ`, version 1, the format (1 PNG, 2 JPEG), the width, the height, then the bytes;
  - the run answers once, with `AFIR`, version 1, an outcome (0 re-encoded, 1 refused) and the bytes;
  - then it ends. Both sides read strictly, within 8 MiB, and the server trusts nothing the worker sends.
- **One socket per run** (`AetherFrame:ImageWorkerRuns`, October 1, 2026: I2's per-job isolation). The server offers one fresh socket at a time in that folder, under a random name; it answers one connection, then is closed and deleted before the next is offered. The host mounts only the socket on offer into each run's container, as its one mount, so a run can reach the job it was started for and nothing else. `AetherFrame:ImageWorkerSocket`, one socket for every run, remains for tests and a server that stays closed; `AetherFrame:OpenToEveryone` refuses it.
- **One job per run.** The worker's container is started again for the next job, so no process of one job survives into another once its run ends. A watchdog in the worker ends a stuck run 20 seconds after its job arrives. It runs in the worker's own process, so a run an exploit controls can defeat it. What bounds that run is the deployment, which ends every worker run from outside after a fixed life (N2-8). The server gives a job 30 seconds from the moment it is queued to get its turn and a worker, and a worker 30 seconds to answer. Jobs run one at a time, and at most 16 wait; beyond that, or with no worker in time, the publish gets `503`.
- **Re-encoding.** ImageSharp's PNG and JPEG codecs only, with the GC heap as the allocator. The image is identified first and must match its declared format and size. One frame is decoded, with no metadata. The encoding is fixed: 8-bit RGBA non-interlaced PNG, or baseline 4:2:0 JPEG at quality 90, with no metadata block and no colour profile applied.
- **The server's check** of the answer: section 8.2.1, the declared format and size, and exactly what the worker's encoder writes, and nothing more:
  - for a PNG, `IHDR` (8-bit, RGBA, not interlaced), then only `IDAT`, then `IEND` at the very end;
  - for a JPEG, the encoder's fixed bytes, compared exactly: SOI, its JFIF header (no thumbnail), a baseline frame of the declared size with its 4:2:0 components, its Huffman and quantization tables, and its scan header. Then entropy-coded data with no marker in it, and EOI.

  A compromised worker therefore can't hand viewers tables, thumbnails or segments of its own. Every read in the check is bounded, and hostile output is refused, never thrown on.
- **PNG checksums.** A PNG is decoded with every chunk's CRC checked, which is ImageSharp 3's form of I2's "strict segment integrity". Its JPEG decoder has no such switch.
- **ImageSharp 3.1.12**, pinned by the lock file: the 3.x line's last release. The Six Labors Split License grants its Apache 2.0 terms to software under an open source licence, and AetherFrame is AGPL-3.0.
  - Six Labors' advisories of August and September 2026 are fixed only in 4.1.x. None reaches this worker's configuration, as the register records for each.
  - ImageSharp 4 checks a signed licence key at build time, which needs an account with Six Labors, and so the owner. The move to 4.x waits for the owner's decision in the Owner inbox.
