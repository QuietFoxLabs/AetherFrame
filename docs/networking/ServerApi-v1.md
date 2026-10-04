# AetherFrame server interface, version 1 (DRAFT)

What `server/AetherFrame.Server` answers, and how the plugin (N2-9 and N2-10) talks to it. It carries the protocol of [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) over HTTPS and applies decision batches B and C ([DecisionRegister.md](DecisionRegister.md)). It is a draft, like the protocol, until the owner's two-player test (NETWORK2.md, section 2) has passed. `[updated 2026-10-02: the test passed on October 1, 2026 (TwoPlayerTest.md). The interface is still marked DRAFT with the protocol, whose version 1 only the owner's freeze finalises.]`

**Built so far** (N2-7b and N2-7c): everything below, and the image worker (section 8). A server with no worker socket configured refuses every image (`image-refused`), so a Plate with images is never served unprocessed. `[updated 2026-10-02: and the health check, GET /v1/health (section 3; known bug 14).]` `[updated 2026-10-03: and the check and the re-read as WebSockets, read through the player's own connection (section 2.3), with each binding's day of last read (sections 6 and 7).]` `[updated 2026-10-04: and the online count's presence sessions (section 2.4).]`

## 1. Transport

- **HTTPS only**, to the one deployment name the plugin is configured with (section 14.1 of the specification; decision R2). Caddy terminates TLS in front of the server (N2-8), and the server trusts forwarded headers from Caddy's address alone.
- **Every request is a `POST`**, except `GET /v1/status` and `GET /v1/health`. Paths carry no identifier, name, code or marker: those travel only in bodies (decisions R4, S5 and C7), since paths reach logs. `[updated 2026-10-03: the check and the re-read may also be a GET that upgrades to a WebSocket (section 2.3).]`
- **Request bodies** are `application/octet-stream`, bounded before they are read (section 5).
- **Responses** are `application/octet-stream`, `application/json` (a closed set of small objects, section 4), `image/png` or `image/jpeg`, each with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. A failure is a status with no body, except where section 3 says otherwise.

## 2. Signed requests

Every request but `status`, `health` and `challenge` is signed. Its body is:

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
| `/v1/lodestone/reread` | 4, a re-read | `{}` | `200` `{"name": "…", "world": "…"}`; `404` when the key is bound to no character, or its character is no longer on the allowlist (the binding is kept); `410` when another key's check took it over, also when it lands while the re-read's own fetch is under way `[updated 2026-10-03]` |
| `/v1/opt-out` | 5, opting out or pausing | `{}` to opt out; `{"mode": "pause"}` to pause | `204`, whether or not anything was bound |
| `/v1/lookup` | 6, a lookup | `{"name": "…", "world": "…"}` | `200` a served profile (section 8.6 of the specification); `404` for every cause (C5) |
| `/v1/image` | 7, an image | `{"name": "…", "world": "…", "marker": "mrk_…", "index": 0}` | `200` the image; `404` for every cause |
| `/v1/report` | 8, a report | `{"name": "…", "world": "…", "reason": "…"}` | `204`; `404` when no Plate is found |
| `/v1/presence` | 9, a presence start | `{}` | `200` `{"session": "<44 characters of base64>", "online": 12}`; see section 2.4 |

- **A Lodestone id** is its decimal digits, as a string: 1 to 10 digits with no leading zero (C2).
- **A name** is the character's full name, and **a World** its Home World's name, compared as C1 says: the name in NFC, lower case, with runs of spaces folded, and the World without regard to case.
- **A reason** is one of `offensive`, `impersonation`, `spam` or `other`.
- **The check and the re-read** may also come as WebSockets, which read the page through the player's own connection (section 2.3). As a `POST` they are answered as above, through the operator's relay when one is set.
- **Lookups, images and reports** need the signer to be bound to a character that is on the allowlist (C5, C8): otherwise `404`, as if nothing were found, or `410` when another key's check took the signer's character over. `[updated 2026-10-03: while no operator relay is set, a character whose last successful Lodestone read is 30 or more days old is not found either, until it is read again (section 7).]`
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

### 2.3 The check and the re-read as WebSockets

`[added 2026-10-03: "Checking a character through the player's own connection" in the decision register]` The check and the re-read may also come as a WebSocket at their own paths: a `GET` of `/v1/lodestone/check` or `/v1/lodestone/reread` that upgrades. The server then reads the character's page through the player's own connection. The plugin opens one TCP connection to `na.finalfantasyxiv.com`, port 443, and carries its bytes. The server runs TLS to the Lodestone over it, so the plugin carries only encrypted bytes. A `POST` to either path is answered as section 2.1 says, through the operator's relay when one is set.

**Before the upgrade,** in this order, each refusal a status with no body:

| Status | When |
|---|---|
| `400` | the `GET` is not a WebSocket request |
| `429` | the address's Lodestone limit (C6, 10 an hour), taken here once for the whole WebSocket: the signed body doesn't take it again |
| `403` | the request has an `Origin` header; plugins send none |
| `503` | 2 WebSockets are already open for one of the address's groups (at the group's multiple, section 7), or 20 in all |

**The exchange:**
1. The plugin's first message is binary: exactly the signed body a `POST` carries (section 2), at most 4,552 bytes, within 10 seconds of the upgrade.
2. The server checks it as it checks a `POST`: the envelope, the proof's kind, the challenge, the body, the limits per key and per Lodestone id, then the code (a check) or the binding (a re-read). An answer that needs no page is the final message, sent at once, except that a wrong code waits for the 3-second floor, as a `POST` does.
3. Otherwise the server sends the text message `open`, once. The plugin connects, then answers `opened`, within 10 seconds of `open`, or `failed`.
4. Binary messages carry that connection's bytes both ways, each at most 64 KiB: at most 16 KiB toward the Lodestone, and 2 MiB from it. When the Lodestone closes its side, the plugin sends the text message `eof`.
5. When the read is done, or has failed, the server sends `close`, whatever happened after `open`. The plugin then closes its connection. Bytes, an `eof`, or a late `opened` or `failed` already on their way are dropped, within the same totals.
6. The final message is text (below). Then the server closes the WebSocket, and the plugin closes in answer.

**Deadlines.** The first message comes within 10 seconds of the upgrade, and `opened` within 10 seconds of `open`. The fetch ends within 20 seconds of `open`, the wait for `opened` included. The whole session ends within 40 seconds of the upgrade. Caddy closes any upgraded connection after 60 seconds, as an outer bound; Kestrel holds at most 32 upgraded connections.

**Anything else ends the session at once,** with no final message: the server drops the WebSocket. That covers a first message that is late, text or over its bound; any message before `open`; anything but `opened` or `failed` after it; bytes before `opened` or after `eof`; a message over 64 KiB; more than 2 MiB from the Lodestone; a second answer to `open`; an unknown text; the plugin closing the WebSocket before the final message; and the session's deadline.

**The final message** is one JSON object:

| Property | Value |
|---|---|
| `status` | the status a `POST` would have got |
| `body` | when that `POST`'s answer is JSON, the same object (section 2.1) |
| `challenge` | with `409`, the fresh challenge's 32 bytes in base64: the plugin signs again under it, on a new WebSocket |
| `reason` | with `503`, `lodestone:refused` when the Lodestone answered the player's connection with `403` or `429` |
| `readDay` | with `200` for a check or a re-read: the answered binding's day of last successful Lodestone read as stored, a whole number of UTC days since the Unix epoch (1970-01-01), read in the same transaction that applied the read. A first "not found" keeps the day it had; a failed read carries none and moves none. A day the server gave a binding when it brought its file up to date, or repaired at a start, is a grace date: it doesn't prove that the Lodestone was read then. A `POST`'s body never carries it, since released plugins' readers refuse any field they don't know, and lookups, images, reports and the logs never show it. |

For example `{"status":200,"body":{"profileId":"prf_…","name":"…","world":"…"},"readDay":20729}`, or `{"status":422}`.

**The read** is C2's: `GET` of the one fixed address with the server's `User-Agent`, over HTTP/1.1, following no redirect, at most 1 MiB, with no compression. It runs over a client made for that one pipe and disposed with it. Its TLS is version 1.3 only, and the Lodestone's certificate is checked as .NET checks any, so a pipe that answers with its own certificate, or one for another name, gets no request. A response whose body would end only when the connection closes, with neither a length nor chunks, is refused. The Lodestone's response headers are never logged.

**What the pipe changes in the answers:**
- `503` with `lodestone:refused`: the Lodestone answered `403` or `429`. The plugin tells the player it turned their connection away.
- `503` with no reason, "try again later": the plugin answered `failed`, or no `opened` came in time; the fetch passed its deadline; the connection broke or was cut off; TLS failed; or the body wasn't framed. Also when the 20 places for piped reads are all taken.
- **A check** reads the page before it applies the allowlist (C8) and the second-character rule, so whether `open` comes tells nothing about an id. Every failure after the page is read is the same `422`, sent no sooner than 3 seconds after the check started, and no sooner than 2 seconds after the read ended. A wrong code is the same `422` after the 3-second floor, with no pipe opened.
- **A re-read** counts only the Lodestone's own "not found" page, with a `404`, as "not found" (section 7). A dropped pipe, a failed handshake or a cut-off page never does.
- **Piped reads** have their own 20 places, taken only once the code is valid (a check) or the binding found (a re-read). They never wait for the relay's reads, and spend none of the fetch budget's hour (C2's 60). C6's limits per key, per Lodestone id and per address apply as for a `POST`.
- **No final message.** When the server ends a session for a violation or a deadline, no final message comes. A plugin treats that as no answer, as for a request that got none, knowing that a binding or a removal may already have been committed: its next request tells it where things stand.

### 2.4 The online count

Added October 4, 2026 ("The online count" in DecisionRegister.md, the one exception to R2's "no background traffic"). While a character is logged in and shares, the plugin keeps it counted, and shows the count of sharing characters online beside its version in My Plates. Its own paths: `/v1/status` is unchanged, since older plugins read it strictly.

| Request | Body | Answer |
|---|---|---|
| `POST /v1/presence`, signed, kind 9 | `{}` | `200` `{"session": "…", "online": n}`: a new session's token (32 random bytes, in base64) and the count; `404` when the key is bound to no character or its character isn't on the allowlist; `410` when another key's check took it over; `429` past a limit; `503` when the server holds its most sessions |
| `POST /v1/presence/beat` | the token's 32 bytes | `200` `{"online": n}`; `404` when the server doesn't know the session (never started, expired, past its hour, replaced, or the server restarted); `429` within 20 seconds of the session's last counted heartbeat, or past the address's limit |
| `POST /v1/presence/leave` | the token's 32 bytes | `204`, whether or not the token named a session |

- **A session** counts its key's character as online for 180 seconds after its start or its last counted heartbeat, and for an hour after its start at most, whatever its heartbeats. After that the plugin signs a new start, so the binding and the allowlist are checked again at least once an hour. A key holds one session: a new start replaces it. Pausing, opting out (`/v1/opt-out`) and another key's check taking the character over (C1) end the key's sessions at once.
- **The count** is the number of distinct Lodestone ids with a session, so a character is counted once however many sessions name it. Expired sessions are swept at most every 5 seconds, so a session can count that long past its expiry. Answers carry the count alone: no name, World, id, key, time or list.
- **Nothing is kept.** Sessions live in the server's memory only, as each token's SHA-256 with the key's identity, the Lodestone id and two times. Nothing about them is written to disk or logged, and a session's times go with it. A restart forgets every session; each plugin's next heartbeat gets `404` and it starts a new one, spread over up to 10 seconds. The server holds at most 100,000 sessions.
- **Limits.** A start is signed like any action, so it takes one challenge from the address's 600 an hour, and is limited to 12 an hour per key and 60 an hour per address group (a tenth of the challenges). Heartbeats and leaves take no challenge, and count against a limit of their own, 120 a minute per address group (C6's groups), which no other request takes from. So players sharing a network can't use up each other's publishing, lookups or checks with heartbeats, and at the plugin's pace about 100 of them fit behind one IPv4 address.
- **The plugin's pace.** A start when the character logs in or starts sharing, then a heartbeat every 50 to 70 seconds. After a failure it waits a minute, then twice as long each time, to 15 minutes at most. A heartbeat's `404` starts a new session once; a second `404` before a counted heartbeat counts as a failure. A logout, a pause, turning sharing off, a takeover, another character or unloading the plugin ends the session with a leave; a crash leaves it to expire. The start's signature is made under the persona session in one short operation, and every request is sent outside it, so presence never holds up a publish. Nothing is sent for a character that doesn't share, or before the player has seen what the count sends (the one-time notice in Sharing, or the consent).

## 3. Unsigned requests

| Request | Answer |
|---|---|
| `GET /v1/status` | `200` `{"protocolVersion": 32769, "api": 1, "minimumPlugin": "0.1.9"}` |
| `POST /v1/challenge`, empty body | `200` the 32 challenge bytes |
| `GET /v1/health`, no body | `200` `{"worker": true, "images": true, "backup": true}`, with each `true` or `false`; `413` for a body |

`GET /v1/health` is for the operator's monitor (known bug 14). The plugin never calls it: its only background traffic is the online count's (section 2.4). It answers `200` with exactly those three booleans, however they stand: a server that answers at all is up, and one that answers `5xx`, or nothing, isn't. It never carries a count, a time, a version, an identifier, an address or an error's text. It reads only what the server holds in memory, so a request does no database, worker, Lodestone or relay work, and many requests change nothing. Any other method gets `405`.
- **`worker`**: an image worker run connected to the server within the last 2 minutes. It has no grace after a start: it is `false` until the first run connects, which takes seconds on a healthy host. (`images` and `backup` do have one: they read `true` for the server's first 15 minutes, while their first results come in.) While the worker host is up, a run connects at least every 20 seconds or so (a run with no job ends after 15 seconds, and the host starts the next), so a host that starts none (known bug 13) shows here from the start, and within 2 minutes of its last run otherwise.
- **`images`**: an image went through the worker and passed the server's check (section 8) within the last 3 hours. The server sends its own canary, a fixed 2 by 2 PNG, along a publish image's whole path, on its own timer: a minute after it starts, then every hour, and 5 minutes after a failure. The answer is discarded, and the canary touches no database, character, limit or budget. It is `false` after two failed canaries in a row (refused, or an answer the check refuses). A worker that is busy, or doesn't come in time, counts neither way, and the next canary comes 5 minutes later. Before any canary has finished, it is `true` for the server's first 15 minutes.
- **`backup`**: the last backup run succeeded within the last 3 hours. The server runs one every hour: it writes the day's copy of the database when there isn't one yet, so there is one copy a UTC day, and deletes each copy at the first run 6 days and 22 hours or more after its day began. A run succeeds when the day's copy is in place and the deletion is done. It is `false` when the last run failed, and when no run has succeeded once the server is 15 minutes old. A failed run is simply tried again at the next one, an hour later, and the deletion runs even when the copy fails. So each copy is gone before its day plus 7 days, and so within 7 days of its writing, through any restart shorter than an hour (D1).

`worker` and `images` are always `false` on a server with no image worker configured, and `backup` on one with no backup folder. The server also logs a warning each time one of the three changes, naming only which one.

## 4. Closed values

- **A `422` from `publish`** carries one of these reason codes as `text/plain`: `not-bound`, `wrong-profile`, `not-a-layout`, `clock-ahead`, `revision-conflict`, `image-refused`, `document-refused`.
- **Every other failure** is its status alone: `400` for a malformed body, `403` for a proof that fails, `404`, `413` for a body over its bound, `422` for a failed check, `429` for a rate limit.
- **A WebSocket's final message** (section 2.3) carries one `reason` only: `lodestone:refused`, with `503`.

## 5. Bounds

| Request | Largest body |
|---|---|
| `challenge`, `health` | 0 bytes |
| a presence heartbeat or leave | 32 bytes |
| an action | 2 + 454 + 4,096 = 4,552 bytes |
| `publish` | 2 + 454 + 4 + 1,048,576 + 1 + 8 × 4 + 41,943,040 = 42,992,109 bytes; the bound is 43,000,000 |

A body over its bound is refused with `413` before it is buffered: Kestrel's own limit is set per endpoint, and the server reads no more than the bound however the request is framed.

A WebSocket (section 2.3) has its own bounds: a first message of at most 4,552 bytes, read into a buffer of that size; later messages of at most 64 KiB; and, through one pipe, at most 16 KiB toward the Lodestone and 2 MiB from it.

## 6. What the server keeps

Decision batch C, C7, says it: for each binding, the Lodestone id, the name and World, the key's identity and the profile id, the latest revision's document, served profile and images, and N2's revision records; and reports, for 30 days or until the operator acts. It keeps no lookup log, no address, and no copy of a Lodestone page. The health check keeps nothing either: its three signals live in memory only, the canary's image is discarded, and a request to it writes nothing. A day number is kept only while a binding's Lodestone page shows "not found", for C1's two re-reads a day apart. `[updated 2026-10-03: and, for each binding, the day number (UTC) of its last successful Lodestone read, for the 30 days of section 7.]` `[updated 2026-10-04: the online count's sessions live in memory only, and nothing about them is ever written or logged (section 2.4).]`

## 7. Choices batch C left to N2-7

Recorded in DecisionRegister.md as "N2-7b's server details":
- **Framing.** A signed request is `u16` proof length, the proof, then the payload. Actions carry small JSON objects read strictly, and failures are bare statuses. One reader handles every action. No field outside the proof and the payload changes what the server does.
- **A failed check doesn't use up the code.** Only a successful binding does, so a player who pastes the code after asking for a check can simply check again. A code is still bound to one key and lives an hour (C2).
- **A check by the key already bound** refreshes the name and World and keeps the profile id. A check by another key takes the character over with a fresh one (C1, C4).
- **A re-read that reads a name and World another binding shows** hides that other binding: the newest read wins, as C1 says for checks. Two characters can't hold one name on one World.
- **Re-reads** are paced evenly over the day, at least 2 minutes apart, within half of the fetch budget (C2), so they never crowd out a check. A re-read the player asks for counts as a check against the player's limits and uses the whole budget. `[updated 2026-10-03: the daily re-read runs only while the operator's relay (LodestoneRelay) is set; a re-read through the player's own connection uses none of the budget (section 2.3).]`
- **A binding's last read** `[added 2026-10-03]`. Each successful read of its page records the day number (UTC): a check, a player's re-read (as a `POST` or through a pipe), and the daily re-read. A "not found" is no read. While no operator relay is set, lookups, images and reports find a binding only while that day is within the last 30: read on day D, it answers through day D + 29, so for at most 30 days and at least 29. It is hidden, not deleted, and its key still finds it, to re-read it, publish and opt out; its next successful read shows it again. Bindings from before this change were given the day the server first started with it, so none hid at once, and a binding with no day (0), which only a server from before this change writes after a rollback, gets the day of the next start. While a relay is set, lookups don't hide by the day: the daily re-read keeps it only while the relay is open. The 30 days are provisional, awaiting product review. They bound how long a renamed, transferred or deleted character's Plate answers under its old name and World once the daily re-read no longer runs.
- **"Not found"** is the Lodestone's own page: status 404, its title, and one `error__body` with no character on it. Anything else is an outage and changes nothing (C1). While a binding's page shows "not found", the server keeps the day number (UTC) it first did, and nothing else. It removes the binding on a "not found" two or more days later by that number: at least 24 hours after the first, and at most 48. A re-read applies only to the character it read, so a re-read in flight while the key opts out and binds another character changes nothing.
- **A failed check** answers no sooner than 3 seconds after it started, wherever it failed, so its timing doesn't tell an id on the allowlist from one off it. "Try again later" (`503`, a full fetch budget) is answered before the code, the allowlist or the key's binding are looked at. What remains is a fetch longer than 3 seconds, which only an allowlisted id can cause: in stage 1 that tells someone holding a key and a live code that one of two ids is a tester's. `[updated 2026-10-03: through a pipe (section 2.3) every id's page is read, and a failure answers no sooner than 2 seconds after the read ended as well (ServerOptions.CheckFailureAfterRead): longer than parsing a page and the steps after the allowlist take, so an allowlist refusal and a later failure leave at the same time.]`
- **Limits C6 doesn't state:**
  - challenges: 600 an hour per address, counting those a `409` carries;
  - opting out: 10 an hour per key and 30 per address;
  - images: 8 times the lookup limits, since a Plate has up to 8.
  - status and health: no limit, since neither does any I/O: each answers from memory.
  - An address limit applies to an IPv6 address's /64, /56 and /48 at 1, 4 and 16 times the limit.
- **An unhandled error** answers `500`, and logs only the exception's type.
- **Pausing** is the opting-out kind with `{"mode": "pause"}` in its signed body, so it adds no request kind (C9). It deletes what is served and keeps the binding and the revision records (N2), so an old revision can't come back.
- **Retractions.** Publishing accepts only a schema 2 snapshot: a `ProfileRetraction` is refused (`not-a-layout`). In stage 1, opting out and pausing take its place, and there are no tombstones (C4).
- **A key whose character another key took over** is remembered, by its identity alone, until it binds again or opts out, so that its plugin gets `410` and can say what happened (C1).
- **Publishing** is also limited to 120 an hour per address, besides C6's 60 per character, and to four at a time, at most one per character and one per address range (section 2.2): one more gets `503`, so buffered bodies stay bounded. `[updated 2026-10-02: four since the open alpha of October 1, 2026; it was two.]` A publish is authenticated before it waits for a slot (section 2.2), and its body is read once, into a buffer of its declared length.
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
