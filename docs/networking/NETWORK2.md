# NETWORK2: from local preparation to a two-player test

**Status (2026-09-29): a plan, nothing implemented.** It turns the owner's priority of September 29, 2026 into increments. Each increment is one reviewed pull request and needs the decisions named for it, recorded in [DecisionRegister.md](DecisionRegister.md) before it merges. Nothing here approves a decision. NETWORK1's safeguards ([NETWORK1.md](NETWORK1.md), section 4) hold until the increment that replaces one says so, in the same change.

## 1. Why this exists

NETWORK1 prepares identity and publishing locally and forbids network code. The owner's instruction of September 29, 2026 (ROADMAP.md, section 5, in the owner's words) asks for more: networking "done, implemented and ready for me to test with someone", ahead of the interface work, with the owner buying and setting up a domain and a server when needed. NETWORK2 is the path from where NETWORK1 stands to that test. The CONFIRMED requirements are unchanged:
- local editing never depends on any of this (NETWORK1.md, section 1);
- sharing is intentional;
- the player keeps the only authoritative copy of their work;
- working sharing comes before any external beta.

## 2. The test this plan ends in

Two players on native Windows, each running the **networking preview build** (P2: player builds contain none of it), and one server the owner hosts:

1. **Identity.** Player A creates a persona in AetherFrame and selects it. Before the first publish, A acknowledges that losing the key means the Plate can never be unpublished (K4); the encrypted backup comes later (D2 details).
2. **Publish.** A picks a saved Plate and chooses Share. A consent screen shows exactly what leaves the computer:
   - the Plate's layout and text;
   - prepared copies of its images;
   - the persona's public key.

   It says that nothing else leaves: no character name, no World and no Content ID, unless the player typed them into the Plate. A confirms and gets a **share code**.
3. **View.** A sends the code to B by any means (game chat, Discord). B enters it in AetherFrame. The Plate opens in a read-only viewer and looks as it does for A. It is never added to B's Library unless B explicitly saves a copy (a later increment).
4. **Update.** A edits the Plate and publishes again. B refreshes and sees the new revision under the same code.
5. **Unpublish.** A unpublishes. B's refresh says the Plate is no longer shared, and the server serves nothing for it.
6. **Nothing else changed.** A's and B's Libraries, Plates, Templates and bindings are as before, and everything local works with the network off or the server down.

**Target lookup** (seeing a Plate by targeting its owner's character in game) is the natural next stage. It is **not** part of this first test. It needs an opt-in binding of a Plate to a character name and World, which is a privacy decision that deserves its own review (section 5, stage 2). Share codes are the explicit, target-initiated sharing that ROADMAP.md, section 4, rule 8, prefers, and they need no binding to a character at all.

## 3. Architecture

```
Player A's plugin (preview)                          Owner's server                         Player B's plugin (preview)
 persona store (DPAPI) ─┐                              ASP.NET Core, .NET 10                    viewer: fetch by code,
 snapshot builder ──────┼─ signed documents ──HTTPS──► verifies with AetherFrame.Protocol ─HTTPS─► verify, render read-only
 image preparation ─────┘   + prepared images          stores exact bytes (SQLite + files)
 publication index, outbox                             share codes, retraction, quotas
```

- **Protocol.** The v1 envelope stays: a signed document, verified by the same code in the client and the server. It gains:
  - the draft marker (N3) and the name rule (D4), already planned as increments 2a and 2b;
  - a **ProfileSnapshot schema 2** that carries the Plate's layout: canvas, background, text elements, image elements and Components. It uses fixed-point integers, because the protocol has no floating point. It is its own model and shares nothing with the local `ProfileDocument` (NETWORK0's rule). The snapshot builder converts a saved Plate into it, and the viewer renders from it.
  - a **request proof**: a second signing context, with its own domain tag, that binds a publish to this deployment and to a short time window (S1, D7).
- **Client.** Everything sits under the networking folders and compiles only in the preview flavour:
  - the DPAPI protector and capability probe (increment 7);
  - the persisted persona registry and its minimal UI (part of increment 9), with key files that no record names detected and reported (L12), and the key file's move written through;
  - the snapshot builder, image preparation, publication index and outbox (increments 4 and 8);
  - an HTTP transport: HTTPS to a DNS hostname only, dual-stack, timeouts, and never on the framework thread;
  - the publish and unpublish flow with its consent screen;
  - the viewer.

  The boundary tests change in one way only: `System.Net.Http` becomes allowed inside `Services/Network` in the preview flavour. The player flavour stays free of any networking API.
- **Server.** `server/AetherFrame.Server`, a small ASP.NET Core service in this repository, built and tested by CI like the rest:
  - It references `AetherFrame.Protocol` and verifies every document before storing anything.
  - It applies the server obligations of [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md), section 13: scoped by (persona, profile id), exact bytes stored, revision uniqueness, terminal retraction, receipt-time ordering and bounded clock skew.
  - It issues share codes, keeps quotas and IPv6-aware rate limits, stores SQLite and content files per persona (N1), and logs no Plate content and no addresses beyond the rate-limit window.
- **Hosting.** One small Linux server with IPv4 and IPv6, a domain name, Docker, and Caddy for automatic Let's Encrypt certificates. Dalamud's guidance requires HTTPS with a trusted certificate, a DNS hostname rather than an IP address, and dual-stack support ([Plugin Technical Considerations](https://dalamud.dev/plugin-development/technical-considerations/)). A GitHub Actions workflow deploys a reviewed commit to it, and each run waits for the owner's approval in a protected environment, as publication does today.
- **Distribution for the test.** The preview build is not a player build (P2). For the first test the owner installs it as the dev plugin, as with every test build, and gives the same build to the second player to install the same way. A preview channel in the custom repository, or networking in player builds, is a later decision.

## 4. What only the owner does, and when

Nothing is needed from the owner until increment N2-8. Then, with exact steps posted in the Owner inbox:
1. Buy a domain.
2. Rent a small Linux server (a few dollars a month) with IPv4 and IPv6, and point the domain's `A` and `AAAA` records at it.
3. Create the deploy key and add it and the server's address as secrets of a protected GitHub environment. Claude never sees or handles a credential.
4. Approve each deploy run in GitHub.

For the test itself (N2-11), the owner installs the preview build, gives it to the second player, and runs the checklist with them. A formal in-game pass of the current test build (`01a14a5`) is still needed for the separate 0.1.7 release.

## 5. Increments

Each increment is one pull request, with the checks and reviews AUTOPILOT.md requires. Security, cryptography, privacy and data-loss changes get a second, security-focused reviewer, and every decision is recorded before the code that depends on it merges.

**Stage 1: the two-player test.**

| # | Increment | Needs |
|---|---|---|
| N2-0 | This plan, the roadmap and the owner's priority | none |
| N2-1 | Decision batch A: protocol, privacy and boundaries, researched and reviewed | N3, D4, D5, D8, I1, N1, N7, P1, K3, K4; the lookup model (share codes first); the transport; the boundary amendment |
| N2-2 | Protocol: the draft marker and the name rule (NETWORK1 increments 2a and 2b) | N3, D4 |
| N2-3 | Protocol: ProfileSnapshot schema 2 (the layout) and the request proof context | D8, D5, I1, S1, D7, L8 |
| N2-4 | Plugin: the Windows DPAPI protector and the capability probe (NETWORK1 increment 7) | K2 (approved), K3 |
| N2-5 | Plugin: the persisted persona registry, the persona window, L12 detection, the written-through move, and the K4 acknowledgement | K4, L10, D9a |
| N2-6 | Plugin: the snapshot builder for schema 2, image preparation, the publication index and the outbox (NETWORK1 increments 4 and 8) | P1, D5, I1 |
| N2-7 | Server: verify, store and serve; share codes; retraction; quotas and rate limits; its own test suite in CI | Decision batch B: D1, D6, N2, N6, S2, S3, S4, I2, and server logging |
| N2-8 | Deployment kit: container, Caddy, the deploy workflow with owner approval, the runbook | The owner's hosting (section 4) |
| N2-9 | Plugin: the transport, publish and unpublish, the consent screen, share codes | The boundary amendment (N2-1) |
| N2-10 | Plugin: the viewer (open by code, verify, render read-only, refresh) | N7 |
| N2-11 | Preview test build and the two-player checklist | Everything above; the owner's in-game run |

N2-7's server runs locally in its own tests and in the plugin's integration tests, so everything up to N2-10 can be built and tested before the owner's server exists. Only the real test needs it.

**Stage 2, after the test:**
- target lookup with an opt-in character binding;
- the encrypted backup (NETWORK1 increment 6, the D2 details);
- "save a copy" from the viewer;
- a preview channel, or networking in player builds;
- Wine, Proton and macOS (NETWORK1 increment 10);
- the external beta checklist.

## 6. The interface work

The owner's second priority is a more modern and more fluid interface, with less going back and forth between menus. It follows this plan, and it is interleaved wherever networking waits on the owner or on a review. Its first step is an audit of the current flows: which tasks need a trip between windows or menus, and what one place could hold them instead. The audit's results become tasks in ROADMAP.md, section 8. The sharing screens of N2-5, N2-9 and N2-10 are designed to fit where that audit is heading, not the old flows.

## 7. What this plan does not decide

Every decision named above stays UNRESOLVED until the register records it. So do these:
- the domain, the server provider and their costs;
- whether networking ever reaches player builds;
- the protocol freeze;
- the external beta's own acceptance list.
