# NETWORK2: from local preparation to a two-player test

**Status (2026-09-30): a plan, re-aimed the same day by the owner's decisions V1 to V5 (DecisionRegister.md): sharing is viewing an opted-in player's Active Plate by right-clicking their character or searching their name, like the game's Adventure Plates, and the test in section 2 is rewritten for it. Decision batch C (N2-C) comes before any sharing code beyond N2-6. N2-6 is complete: its last part, the share check, merged as [#56](https://github.com/QuietFoxLabs/AetherFrame/pull/56); nothing it builds is sent before N2-9. Claude's working assumption, which batch C settles, is that a character's key is a persona under the hood (V4). N2-0 (this plan) is merged. N2-1, decision batch A, is recorded in [DecisionRegister.md](DecisionRegister.md). N2-2 (the draft marker and the name rule), N2-3a (the layout schema, [#39](https://github.com/QuietFoxLabs/AetherFrame/pull/39), `e385b81`), N2-3b (the request proof, [#40](https://github.com/QuietFoxLabs/AetherFrame/pull/40), `3371d08`) and N2-4 (the DPAPI key protector and the capability probe, [#42](https://github.com/QuietFoxLabs/AetherFrame/pull/42), `2d98245`) are merged. N2-5a, the persona registry in the library ([#44](https://github.com/QuietFoxLabs/AetherFrame/pull/44), `836c6ef`), N2-5b, the plugin's persona storage and session ([#45](https://github.com/QuietFoxLabs/AetherFrame/pull/45), `5ca0c07`), and N2-5c, the persona window ([#46](https://github.com/QuietFoxLabs/AetherFrame/pull/46), `04e3976`), are merged. Decision batch B, for the server, is recorded ([#47](https://github.com/QuietFoxLabs/AetherFrame/pull/47), `7b99eb6`). N2-6a, the snapshot builder, is merged ([#48](https://github.com/QuietFoxLabs/AetherFrame/pull/48), `b510250`). N2-6b's first part, the image rule, is merged ([#49](https://github.com/QuietFoxLabs/AetherFrame/pull/49), `7b35a31`), and its second part, image preparation, is merged too ([#50](https://github.com/QuietFoxLabs/AetherFrame/pull/50), `302af2f`). N2-6c's first part, the publication index, the outbox and the commit, is merged ([#55](https://github.com/QuietFoxLabs/AetherFrame/pull/55)), and so is its second part, the share check ([#56](https://github.com/QuietFoxLabs/AetherFrame/pull/56)). Nothing reaches a player: all of it is in the preview flavour only.** It turns the owner's request of September 29, 2026 into increments. Each increment is one reviewed pull request, and needs the decisions named for it recorded in [DecisionRegister.md](DecisionRegister.md) before it merges. Nothing here approves a decision. Where this plan names an option, it is the recommendation that the decision will weigh, not a choice already made.

**Safeguards.** Safeguards 2 to 8 of [NETWORK1.md](NETWORK1.md), section 4, hold throughout NETWORK2:
- no persistent key before G1 is complete;
- in safeguard 3's words, "NETWORK0's signed bytes do not change except in an increment the owner explicitly approves", and the protocol stays a DRAFT until an explicit owner freeze. N2-2 and N2-3 change signed bytes. The owner approved both in advance on September 29, 2026, on condition that each has its decisions recorded, a clean independent and security review, and green CI ([DecisionRegister.md](DecisionRegister.md), "Signed-byte changes in NETWORK2");
- capability tests, not operating system labels;
- no hand-written cryptography;
- platform validation never trusted alone;
- the privacy rules;
- ownership belongs to the backend.

Safeguard 1 (no network code) is NETWORK1's own. Network code enters only through the boundary amendment, decided in N2-1 and applied in N2-9.

## 1. Why this exists

NETWORK1 prepares identity and publishing locally and forbids network code. On September 29, 2026 the owner asked for two things, quoted in full in ROADMAP.md, section 5:
- a more modern, more fluid interface;
- "maybe even higher priority", networking "done, implemented and ready for me to test with someone".

The owner will buy and set up a domain and a server when needed. Putting networking first is Claude's ordering under the delegation (ROADMAP.md, section 5), not the owner's. NETWORK2 is the path from where NETWORK1 stands to that test. The CONFIRMED requirements are unchanged:
- local editing never depends on any of this (NETWORK1.md, section 1);
- sharing is intentional;
- the player keeps the only authoritative copy of their work;
- working sharing comes before any external beta.

## 2. The test this plan ends in

`[updated 2026-09-30: rewritten after the owner's decisions V1 to V5 (DecisionRegister.md). Sharing is viewing a Plate by character, opt-in and both ways, like the game's Adventure Plates. The share-code test this section described before is superseded, and so is its "stage 2" target lookup, which is now the test.]`

Two players on native Windows, each running a **networking preview build**, and one server the owner hosts. How a preview build reaches the second player is a decision of its own (the tester kit, N2-11), because P2 keeps preview builds out of releases and test builds.

1. **Nothing until you opt in.** A preview build that hasn't opted in adds no menu item and no search box, sends nothing, and looks nothing up.
2. **Opt in (V1).** Player A turns on sharing, which is off by default. A consent screen says:
   - what others will see: A's Active Plate for each character A opts in, as a finished picture, updated whenever A saves it (V2);
   - who can see it: other players who have opted in, from the game's right-click menu on A's character or by searching A's name and World (V5);
   - what the server learns and keeps (decision batch C);
   - how to stop: turning sharing off removes A's Plates from the server.

   Opting in also lets A view other opted-in players' Plates.
3. **Prove the character (V3).** For each character A opts in:
   - a key for that character is made behind the scenes, and A never sees a persona (V4);
   - AetherFrame shows a one-time code, and A pastes it into that character's Lodestone profile;
   - the server checks it on the Lodestone, which binds the character to that key;
   - A can then delete the code.
4. **Published.** A's Active Plate for that character is published. Before anything is sent, AetherFrame shows the actual content: the rendering, every text as it will appear with game-filled text flagged, and the prepared copies of the images (as the D5 and N2-9 notes require).
5. **View.** Player B, also opted in, right-clicks A's character in game and chooses **View AetherFrame Plate**, or searches A's name and World. The Plate opens in a read-only viewer and looks as it does for A, as long as both run the same build. Nothing is added to B's Library.
6. **Update.** A edits the Active Plate and saves. B opens it again and sees the new version. A makes another Plate Active; B then sees that one.
7. **Opt out.** A turns sharing off. B is told A has no AetherFrame Plate, and the server serves nothing for A.
8. **Nothing else changed.** A's and B's Libraries, Plates, Templates and bindings are as before, and everything local works with the network off or the server down.

## 3. Architecture (recommended)

`[updated 2026-09-30: under V1 to V5 the server looks Plates up by Lodestone-checked character and by name search, only for opted-in players, instead of issuing share codes; the viewer opens from the game's right-click menu or a search; and a key per opted-in character replaces personas the player sees. Mentions of share codes below are superseded; decision batch C settles the rest.]`

```
Player A's plugin (preview)                          Owner's server                         Player B's plugin (preview)
 persona store (DPAPI) ─┐                              ASP.NET Core, .NET 10                    viewer: fetch by code,
 snapshot builder ──────┼─ signed documents ──HTTPS──► verifies with AetherFrame.Protocol ─HTTPS─► check (per D6), render
 image preparation ─────┘   + prepared images          stores exact bytes (SQLite + files)       read-only
 publication index, outbox                             share codes, retraction, quotas
```

- **Protocol.** The v1 envelope stays: a signed document, verified by the same code in the client and the server. It gains:
  - the draft marker (N3) and the name rule (D4), already planned as NETWORK1 increments 2a and 2b;
  - a **ProfileSnapshot schema 2** that carries the Plate's layout: canvas, background, text elements, image elements and Components. It uses fixed-point integers, because the protocol has no floating point. It is its own model and shares nothing with the local `ProfileDocument` (NETWORK0's rule).
  - a **request proof**: a second signing context with its own domain tag, binding a publish to one deployment and a short time window (S1, D7, L8).
- **Client.** Everything sits under the networking folders and compiles only in the preview flavour:
  - the DPAPI protector and capability probe (NETWORK1 increment 7);
  - the persisted persona registry and its window (part of NETWORK1 increment 9). The registry detects and reports key files that no record names (L12), and the key file's move is written through;
  - the snapshot builder, image preparation, publication index and outbox (NETWORK1 increments 4 and 8);
  - an HTTP transport: HTTPS to a DNS hostname, with timeouts, never on the framework thread;
  - the publish and unpublish flow with its consent screen;
  - the viewer.

  Network code lives only inside `Services/Network`, and only in the preview flavour, as R3's exact allowlist says; the player flavour stays free of it.
- **The viewer.**
  - It turns a schema 2 snapshot into an in-memory Plate that is never saved, and draws it with the shared renderer. It touches no Library, Template or binding.
  - Components and fonts resolve only against the viewer's own bundled set. An identifier it doesn't know is drawn as a placeholder and named in a note, never fetched.
  - It accepts only the images I1 allows: it sniffs the bytes before decoding and refuses anything but a non-animated 8-bit PNG or an 8-bit JPEG with 1 or 3 components, within the limits imports use (ROADMAP.md, section 4, rule 5).
  - Whether the viewer checks the signature itself or receives server-checked content is D6. `[updated 2026-09-30: server-checked content (D6): the viewer receives a served profile and images by index, and verifies no signature.]`
- **Server.** `server/AetherFrame.Server`, a small ASP.NET Core service in this repository, built and tested by CI like the rest:
  - It references `AetherFrame.Protocol` and verifies every document before storing anything.
  - It applies the server obligations of [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md), section 13.
  - It issues share codes that can't be guessed. `[updated 2026-09-30: in R4's form, the AF- prefix and 75 random bits with a check symbol.]`
  - It rate-limits publishing and lookups by persona and by address, with IPv6 prefixes handled.
  - It keeps quotas and per-persona storage (N1).
  - It keeps its logs and access logs off or bounded, with no Plate content and no addresses beyond the rate-limit window. `[updated 2026-09-30: S5 stores no address at all in stage 1, puts no identifier in any log, and keeps logs 14 days.]`
  - It refuses clients below a minimum version with a clear message.
- **Hosting.** One small Linux server, a domain name, Docker, and Caddy for automatic Let's Encrypt certificates. Dalamud's guidance ([Plugin Technical Considerations](https://dalamud.dev/plugin-development/technical-considerations/)) requires HTTPS with a certificate from a trusted authority and a DNS hostname rather than an IP address. It recommends dual-stack support with IPv6-aware rate limits, and version checks so outdated clients are handled. A GitHub Actions workflow deploys a reviewed commit, each run waiting for the owner's approval in a protected environment, as publication does today.
- **Distribution for the test (the tester kit, decided in N2-11).** P2 leaves the tester kit unsettled, and its package check keeps a preview DLL from becoming a release or a test build. The recommendation: a separately staged preview kit with its own procedure in AUTOPILOT.md, which the owner installs as the dev plugin and gives to the second player. A preview channel, or networking in player builds, is a later decision.

## 4. What only the owner does, and when

Nothing is needed from the owner until increment N2-8. Then, with exact steps posted in the Owner inbox:
1. Buy a domain, and keep it on automatic renewal: a lapsed domain could be registered by someone else, who could then serve players' plugins (R2).
2. Rent a small Linux server (a few dollars a month) with IPv4, and IPv6 if offered, and point the domain's `A` (and `AAAA`) records at it.
3. Create the deploy key and the server's secret values (S2's tombstone key), and add them as secrets of a protected GitHub environment. Claude never sees or handles a credential or a secret.
4. Approve each deploy run in GitHub.

For the test itself (N2-11), the owner installs the preview kit, gives it to the second player, and runs the checklist with them. A formal in-game pass of the current test build (`01a14a5`) is still needed for the separate 0.1.7 release.

## 5. Increments

Each increment is one pull request, with the checks and reviews AUTOPILOT.md requires. Security, cryptography, privacy and data-loss changes get a second, security-focused reviewer. Every decision is recorded before the code that depends on it merges.

**Stage 1: the two-player test.**

| # | Increment | Needs decided | Needs merged |
|---|---|---|---|
| N2-0 | This plan, and the roadmap | none | none |
| N2-1 | Decision batch A: protocol, privacy and boundaries, researched and reviewed | N3, D4, D5, D8, D9a, I1, N1, N7, P1, K3, K4; the lookup model; the transport; the boundary amendment | N2-0 |
| N2-2 | Protocol: the draft marker and the name rule (NETWORK1 increments 2a and 2b) | N3, D4; the owner's approval under safeguard 3, given in advance with conditions | N2-1 |
| N2-3a | Protocol: ProfileSnapshot schema 2, the layout as a resolved paint list (specification, section 8.5) | D8, D5, D9a, I1, N1, N7; the owner's approval under safeguard 3, given in advance with conditions | N2-2 |
| N2-3b | Protocol: the request proof context | S1, D7, L8; the owner's approval under safeguard 3, given in advance with conditions | N2-3a |
| N2-4 | Plugin: the Windows DPAPI protector and the capability probe (NETWORK1 increment 7) | K2 (approved), K3 | N2-2 (NETWORK1's gate for increment 7) |
| N2-5a | Library: the persisted persona registry in `AetherFrame.Personas`: its bytes, persist then apply, the audit of key files no record names and their restore, K4's flag, and the signer bound to the persona an operation showed | L10, L12, P3 (decided in it), K4 | N2-4 |
| N2-5b | Plugin: the registry file and the key file's written-through move, the single-writer lock, and the persona session (the capability probe, the lock, the registry and the audit, off the framework thread); preview test builds | G1 complete (with N3, K3 and P1 from batch A), L10, L12, P3 | N2-2 (the draft marker exists before any persistent key signs), N2-4, N2-5a |
| N2-5c | Plugin: the persona window: personas, K4's step with K2's disclosure, L12's orphans; the Personas button in My Plates | K2, K4, L10, L12 | N2-5b |
| N2-6 | Plugin: the snapshot builder for schema 2, image preparation, the publication index and the outbox (NETWORK1 increments 4 and 8) | P1, D4, D5, D8, I1 | N2-3a, N2-5 |
| N2-C | Decision batch C: viewing by character (V1 to V5), researched and reviewed; recorded as C1 to C9 in DecisionRegister.md. It covers the character's key and its form; the Lodestone check; when publishing happens and what opting out deletes; the game menus and the search; what the server learns, keeps and logs; rate limits against scraping; reporting, hiding and takedown; K4 and D2 under V3; and the consent model for live updates (V2 against D5's N2-6 note, point 6, and the share check's proposed rule in #56, both amended explicitly). R1 and R4 are marked superseded | V1 to V5 (the owner's) | N2-0 |
| N2-7 | Server: verify, store and serve; the Lodestone check; lookup by verified character and name search; removal on opting out; quotas and rate limits; version checks; its own test suite in CI | Batch B as far as V1 to V5 leave it (D1, D6, N2, N6, S2, S3, S4, I2, S5, P4); batch C | N2-3a, N2-3b, N2-C |
| N2-8 | Deployment kit: container, Caddy, the deploy workflow with owner approval, the runbook (with the takedown steps from batch C) | G3 complete before the server accepts documents signed by real keys, and then only from the testers' keys (I2's allowlist); the backups' retention (D1); the owner's hosting (section 4) | N2-7 |
| N2-9 | Plugin: opting in and out, the consent screen, the Lodestone check, publishing the Active Plate when it is saved or changed, the transport; the Personas window leaves My Plates (V4); the tutorial gains a chapter on sharing | The boundary amendment and the transport (batch A); batch C | N2-5, N2-6, N2-7 |
| N2-10 | Plugin: the viewer, opened from the game's right-click menu on a character or by a name search, reading the served profile (D6) and rendering it read-only; hide a player's Plate, and report one; the tutorial's chapter covers viewing `[updated 2026-10-01: the tutorial's chapter waits until sharing reaches player builds ("N2-10's viewer" in the register)]` | N7, I1, K3 (viewing), D6, I2; batch C | N2-3a, N2-7, N2-9 |
| N2-11 | The preview test kit and the two-player checklist (section 2) `[updated 2026-10-01: the kit is "N2-11's tester kit" in the register, made by tools/New-TesterKit.ps1; the checklist is TwoPlayerTest.md]` | The tester kit (P2's unsettled item) and the matching AUTOPILOT.md procedure; G3 complete | everything above, and the owner's server |

`[updated 2026-09-30: N2-6 is delivered in three parts too: N2-6a (the snapshot builder), N2-6b (image preparation) and N2-6c (the publication index, the outbox and the commit). Its design, with a security reviewer's concurrence, is recorded under D5 in DecisionRegister.md.]` `[updated 2026-09-30: N2-6c is delivered in two parts. The first is the publication index, the outbox and the commit, with the index and outbox checked at load. The second builds the candidate from a saved Plate, runs the commit in the persona session once image preparation's known-answer check has passed, and adds a preview-only view of what would be shared.]`

`[updated 2026-09-30: N2-5 is delivered in three parts, as N2-3 was in two: N2-5a (the library), N2-5b (the plugin's persona storage and session) and N2-5c (the persona window); "N2-5" elsewhere in this plan means all three.]`

`[updated 2026-09-30: N2-9 is delivered in three parts: N2-9a (the transport, which applies R3's boundary), N2-9b (opting in: the character's key, the consent screen, the Lodestone code and check, pausing and turning sharing off) and N2-9c (publishing the Active Plate when it is saved or changed, and the Personas window leaving My Plates). The tutorial's chapter comes with N2-10, since it covers viewing too.]` `[updated 2026-09-30: N2-9c drops a revision signed more than a day ago instead of asking about it, since the next save signs the current Plate ("N2-9c's live publishing" in the register).]`

N2-7's server runs locally in its own tests and in the plugin's integration tests, so everything up to N2-10 can be built and tested before the owner's server exists. Only the real test needs it.

**Acceptance carried into the increments:** `[updated 2026-09-30: where these name share codes, R4, /tell or "opening a code", read them under V1 to V5: lookup by character or name search, and "opening a Plate". N2-5's D3 acceptance is amended by V4. Batch C restates what changes.]`
- **N2-5** keeps D3 as approved: several personas, selected and switched only by the player, one active for identity operations, never bound to a character, Content ID or account, and switching never alters Plates or publishes.
- **N2-6** refuses a Plate over a whole-snapshot limit of the specification's section 8.5 (2,048 items, 8 images, 33,554,432 image pixels, 32,000 text scalars), or holding a value no layout field can express (a text with U+0000, a gradient endpoint with a colour component outside 0 to 1), with a message naming it, never clamping or trimming it. Any other value the renderer itself resolves (a colour component outside 0 to 1, an unknown font) is carried as the renderer resolves it. It keeps NETWORK1 increment 4's acceptance:
  - the builder reads only the saved Plate;
  - Plates, bindings and packages gain no publication state;
  - the publication index is per persona and apart from Plates (P1), in `Network\Personas\`, beside the registry and outside `keys\` (P4);
  - the outbox keeps a revision's exact signed bytes until the server acknowledges them, never signs a revision id twice (N2), and sends nothing until N2-9;
  - its tests sign with synthetic keys only.
- **N2-7** keeps section 3's server list: codes that can't be guessed, rate-limited lookups, bounded logs, version checks, and the specification's section 13. It applies decision batch B as the register records it: D1's deletions and order of checks, D6's served profile, N2's revision records, N6's exemption, S2's tombstones, S3's removal, I2's persona allowlist and image worker, R4's share codes and S5's logging.
- **N2-9** tells a player whose key doesn't open when they first share, in K2's words: damaged, or made on another Windows account or PC. The persona window can't: its audit reads key files' headers and opens no key, so such a persona looks normal there until it signs (N2-5c's security review).
- **N2-9** also runs one operation at a time per profile, and unpublishing drops that profile's pending outbox entries (D1). It sends only each profile's newest pending snapshot, and asks before sending one signed more than a day ago (N2). It explains the server's clock-ahead error (N6), and shares codes by /tell, reading every spelling R4 accepts and redacting each in logs (R4). Its consent screen says what S5 and P4 require, and repeats K4's text, which says what S3 and P4 require, and the persona window lets a tester copy their persona's full identity for the operator's allowlist (I2). The consent screen shows the candidate N2-6 built and nothing read again (D5's N2-6 note, (6)): the rendering, every shared text listed in full (a text under an opaque item, or past its box, travels whole), each prepared copy, and what was left out and why. It sends no outbox entry it didn't sign behind that screen: one signed from the preview's share check (N2-6c) is shown on it, from the entry's own verified snapshot and images, before its first send, or else dropped. Its refusals count images as prepared copies: an image drawn through two crossing windows is two.
- **N2-10** keeps I1's sniff of each image, holds content in memory only, clears the Plate when a refresh answers "not found", never describes content as signed or verified, and tells the viewer that opening a code sends their address to the server (D6).
- **N2-11**'s checklist confirms, in game, that the known-answer check of image preparation passes (D5's N2-6 note), and looks at how a greyscale PNG or JPEG draws: the pipeline may read one back as a single channel, which the renderer could draw red. Preparation refuses such images for now, as unshareable. N2-9 gives preparation a reason of its own for them, so that its message can say why.

**Where NETWORK1's increments go:**
- 2 becomes N2-2.
- 4 and 8 become N2-6.
- 7 becomes N2-4.
- 9, the preview wiring, is spread over N2-5, N2-9 and N2-10.
- 6 (the backup codec) and 10 (Wine, Proton and macOS) come in stage 2.
- 11 (acceptance and independent review) happens at N2-11 for what NETWORK2 builds, and again after stage 2.

**Stage 2, after the test:** `[updated 2026-09-30: target lookup is now the test (section 2), and "save a copy" from the viewer is dropped by R5.]`
- the encrypted backup (NETWORK1 increment 6, the D2 details);
- a preview channel, or networking in player builds;
- Wine, Proton and macOS (NETWORK1 increment 10);
- the external beta checklist.

## 6. The interface work

The owner's other request is a more modern and more fluid interface, with less going back and forth between menus. Under the ordering in ROADMAP.md, section 5, it follows this plan, and is interleaved wherever networking waits on the owner or on a review.

Its first step is an audit of the current flows: which tasks need a trip between windows or menus, and what one place could hold them instead. The audit's results become tasks in ROADMAP.md, section 8. The sharing screens of N2-5, N2-9 and N2-10 are designed to fit where that audit is heading, not the old flows. `[updated 2026-09-30: the audit is written, in docs/InterfaceAudit.md; its proposals are interface tasks in ROADMAP.md, section 8.]` `[updated 2026-09-30: its section 7 adds a second pass, with interface tasks 7 to 16 and the owner's decision to merge the two editors into one window. Its section 7.7 details where the consent and publish flow and the share-code viewer would live, as a proposal for N2-9 and N2-10, beside the persona window N2-5c built.]`

## 7. What this plan does not decide

Every decision named above stays UNRESOLVED until the register records it. So do these:
- the tester kit;
- the domain, the server provider and their costs;
- whether networking ever reaches player builds;
- the protocol freeze;
- the external beta's own acceptance list.
