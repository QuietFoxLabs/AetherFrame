# NETWORK2: from local preparation to a two-player test

**Status (2026-09-29): a plan. N2-0 (this plan) is merged. N2-1, decision batch A, is recorded in [DecisionRegister.md](DecisionRegister.md). N2-2 (the draft marker and the name rule) and N2-3a (the layout schema) are merged, and N2-3b (the request proof) is implemented in the protocol. Nothing reaches a player.** It turns the owner's request of September 29, 2026 into increments. Each increment is one reviewed pull request, and needs the decisions named for it recorded in [DecisionRegister.md](DecisionRegister.md) before it merges. Nothing here approves a decision. Where this plan names an option, it is the recommendation that the decision will weigh, not a choice already made.

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

Two players on native Windows, each running a **networking preview build**, and one server the owner hosts. How a preview build reaches the second player is a decision of its own (the tester kit, N2-11), because P2 keeps preview builds out of releases and test builds.

1. **Identity.** Player A creates a persona in AetherFrame and selects it. Before the first publish, A meets whatever K4 decides. The recommendation is an acknowledgement that losing the key means the Plate can never be unpublished. The encrypted backup comes later (the D2 details).
2. **Publish.** A picks a saved Plate and chooses Share. A consent screen shows what leaves the computer and what the server learns:
   - **The actual content to be published:** every text as it will appear, and the prepared copies of the images.
   - **Game-filled text, flagged.** Adventure Plate Classic fills itself from the logged-in character (name, Home World, Data Center, job, Free Company tag). Publishing adds no name, World or Content ID of its own, but a Plate may already contain them, and the screen says which texts came from the game.
   - **The persona's public key.** Every Plate one persona publishes can be tied together by anyone holding their codes, so a persona used for several characters links those characters.
   - **What the server sees:** the player's network address and when they publish, as well as the profile, revision and asset identifiers.

   A confirms and gets a **share code**.
3. **View.** A sends the code to B by any means (game chat, Discord). B enters it in AetherFrame. The Plate opens in a read-only viewer and looks as it does for A, as long as both run the same build. It is never added to B's Library unless B explicitly saves a copy (a later increment).
4. **Update.** A edits the Plate and publishes again. B refreshes and sees the new revision under the same code.
5. **Unpublish.** A unpublishes. B's refresh says the Plate is no longer shared, and the server serves nothing for it.
6. **Nothing else changed.** A's and B's Libraries, Plates, Templates and bindings are as before, and everything local works with the network off or the server down.

**Target lookup** (seeing a Plate by targeting its owner's character in game) is the natural next stage, and **not** part of this first test. It needs an opt-in binding of a Plate to a character name and World, which is a privacy decision that deserves its own review (section 5, stage 2). A share code is sharing the publisher starts deliberately and the viewer opens explicitly, and it binds nothing to a character. The lookup model is decided in N2-1.

## 3. Architecture (recommended)

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
  - Whether the viewer checks the signature itself or receives server-checked content is D6.
- **Server.** `server/AetherFrame.Server`, a small ASP.NET Core service in this repository, built and tested by CI like the rest:
  - It references `AetherFrame.Protocol` and verifies every document before storing anything.
  - It applies the server obligations of [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md), section 13.
  - It issues share codes that can't be guessed.
  - It rate-limits publishing and lookups by persona and by address, with IPv6 prefixes handled.
  - It keeps quotas and per-persona storage (N1).
  - It keeps its logs and access logs off or bounded, with no Plate content and no addresses beyond the rate-limit window.
  - It refuses clients below a minimum version with a clear message.
- **Hosting.** One small Linux server, a domain name, Docker, and Caddy for automatic Let's Encrypt certificates. Dalamud's guidance ([Plugin Technical Considerations](https://dalamud.dev/plugin-development/technical-considerations/)) requires HTTPS with a certificate from a trusted authority and a DNS hostname rather than an IP address. It recommends dual-stack support with IPv6-aware rate limits, and version checks so outdated clients are handled. A GitHub Actions workflow deploys a reviewed commit, each run waiting for the owner's approval in a protected environment, as publication does today.
- **Distribution for the test (the tester kit, decided in N2-11).** P2 leaves the tester kit unsettled, and its package check keeps a preview DLL from becoming a release or a test build. The recommendation: a separately staged preview kit with its own procedure in AUTOPILOT.md, which the owner installs as the dev plugin and gives to the second player. A preview channel, or networking in player builds, is a later decision.

## 4. What only the owner does, and when

Nothing is needed from the owner until increment N2-8. Then, with exact steps posted in the Owner inbox:
1. Buy a domain, and keep it on automatic renewal: a lapsed domain could be registered by someone else, who could then serve players' plugins (R2).
2. Rent a small Linux server (a few dollars a month) with IPv4, and IPv6 if offered, and point the domain's `A` (and `AAAA`) records at it.
3. Create the deploy key and the server's secret values (the S2 pepper, if S2 keeps one), and add them as secrets of a protected GitHub environment. Claude never sees or handles a credential or a secret.
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
| N2-5 | Plugin: the persisted persona registry and the persona window; L12 detection; the written-through move; K4's step | G1 complete (with N3, K3 and P1 from batch A), K4, L10, L12 | N2-2 (the draft marker exists before any persistent key signs), N2-4 |
| N2-6 | Plugin: the snapshot builder for schema 2, image preparation, the publication index and the outbox (NETWORK1 increments 4 and 8) | P1, D4, D5, D8, I1 | N2-3a, N2-5 |
| N2-7 | Server: verify, store and serve; share codes; retraction; quotas and rate limits; version checks; its own test suite in CI | Decision batch B: D1, D6, K5, N2, N6, S2, S3, S4, I2; the share-code format; server logging | N2-3a, N2-3b |
| N2-8 | Deployment kit: container, Caddy, the deploy workflow with owner approval, the runbook | G3 complete (D1, D6, D7, K4, K5, N1, N7) before the deployed server accepts documents signed by real keys; the owner's hosting (section 4) | N2-7 |
| N2-9 | Plugin: the transport, publish and unpublish, the consent screen, share codes | The boundary amendment and the transport (batch A) | N2-5, N2-6, N2-7 |
| N2-10 | Plugin: the viewer (open by code, check per D6, render read-only, refresh) | N7, I1, K3 (viewing), D6, I2 | N2-3a, N2-7, N2-9 |
| N2-11 | The preview test kit and the two-player checklist | The tester kit (P2's unsettled item) and the matching AUTOPILOT.md procedure; G3 complete | everything above, and the owner's server |

N2-7's server runs locally in its own tests and in the plugin's integration tests, so everything up to N2-10 can be built and tested before the owner's server exists. Only the real test needs it.

**Acceptance carried into the increments:**
- **N2-5** keeps D3 as approved: several personas, selected and switched only by the player, one active for identity operations, never bound to a character, Content ID or account, and switching never alters Plates or publishes.
- **N2-6** refuses a Plate over a whole-snapshot limit of the specification's section 8.5 (2,048 items, 8 images, 33,554,432 image pixels, 32,000 text scalars), or holding a value no layout field can express (a text with U+0000, a gradient endpoint with a colour component outside 0 to 1), with a message naming it, never clamping or trimming it. Any other value the renderer itself resolves (a colour component outside 0 to 1, an unknown font) is carried as the renderer resolves it. It keeps NETWORK1 increment 4's acceptance:
  - the builder reads only the saved Plate;
  - Plates, bindings and packages gain no publication state;
  - the publication index is per persona and apart from Plates (P1);
  - the outbox sends nothing until N2-9;
  - its tests sign with synthetic keys only.
- **N2-7** keeps section 3's server list: codes that can't be guessed, rate-limited lookups, bounded logs, version checks, and the specification's section 13.

**Where NETWORK1's increments go:**
- 2 becomes N2-2.
- 4 and 8 become N2-6.
- 7 becomes N2-4.
- 9, the preview wiring, is spread over N2-5, N2-9 and N2-10.
- 6 (the backup codec) and 10 (Wine, Proton and macOS) come in stage 2.
- 11 (acceptance and independent review) happens at N2-11 for what NETWORK2 builds, and again after stage 2.

**Stage 2, after the test:**
- target lookup with an opt-in character binding;
- the encrypted backup (NETWORK1 increment 6, the D2 details);
- "save a copy" from the viewer;
- a preview channel, or networking in player builds;
- Wine, Proton and macOS (NETWORK1 increment 10);
- the external beta checklist.

## 6. The interface work

The owner's other request is a more modern and more fluid interface, with less going back and forth between menus. Under the ordering in ROADMAP.md, section 5, it follows this plan, and is interleaved wherever networking waits on the owner or on a review.

Its first step is an audit of the current flows: which tasks need a trip between windows or menus, and what one place could hold them instead. The audit's results become tasks in ROADMAP.md, section 8. The sharing screens of N2-5, N2-9 and N2-10 are designed to fit where that audit is heading, not the old flows.

## 7. What this plan does not decide

Every decision named above stays UNRESOLVED until the register records it. So do these:
- the tester kit;
- the domain, the server provider and their costs;
- whether networking ever reaches player builds;
- the protocol freeze;
- the external beta's own acceptance list.
