# Networking decision register

**Status (2026-09-29): two decisions are the owner's approvals, and six more are approved under the owner's delegation.**
- **D3** is **APPROVED** by the owner.
- **D2** is **APPROVED IN PRINCIPLE** by the owner. Its technical details remain unresolved, pending later security approval.
- **D9b**, **P2**, **K1**, **K2**, **K6** and **K7** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".

**Every other product and architecture decision below is UNRESOLVED.**

This file is the one place a networking decision is recorded as approved. Nothing is approved by appearing in NETWORK0.md, NETWORK1.md, a handoff, a review report, a baseline in the code, or a recommendation. An entry changes to APPROVED only when the owner approves it, with the date and the approved option written here.

**Delegation (September 29, 2026).** The owner delegated the UNRESOLVED decisions to Claude (ROADMAP.md, section 5, quoted there in the owner's words). A decision Claude makes under that delegation is written here as "APPROVED (Claude, under the owner's delegation of September 29, 2026)". Each such entry gives the date, the exact option and scope, the rationale, and what it doesn't settle. Security, cryptography, privacy and data-loss decisions also need an independent reviewer's concurrence, recorded in the entry. D3 and D2 stay the owner's own approvals, and only the owner can change them. The owner can overrule any delegated entry, and the reversal is recorded here.

- Protocol Specification v1 remains a **DRAFT**.
- The NETWORK0 baselines (NETWORK0.md, section 11) describe what the merged code does today. They are not decisions.
- Recommendations come from the NETWORK1 architecture review and the Windows cryptography investigation (2026-09-28). They are proposals only.
- Ids D1–D9 and N1–N7 are NETWORK0.md's. Its D9 is split here into D9a (display name) and D9b (packaging). The K, P, I and S ids are NETWORK1 additions.

## Approved decisions

### D3: personas per installation. APPROVED (September 28, 2026)

Approved by the owner, in these words:

> AetherFrame supports multiple independent personas per installation.
> Players manually select and switch personas.
> One persona may be active at a time for identity-dependent operations.
> Personas are never automatically bound to characters, Content IDs, accounts or other game identifiers.
> Switching personas must not alter saved local Plates or trigger publishing.

**Wording the approved text does not quote.** The owner's instruction to the persona foundation (#23) also said that distinct personas remain independent unless the user deliberately associates them. `AetherFrame.Personas` enforces it: no operation associates two personas. The approved text above is left exactly as approved. Adding the clause changes the owner's own approval, so only the owner can do it.

**What the approval does not settle:**
- how personas are stored (K2, K9);
- which platforms can create them (K3);
- whether they carry a display name (D9a);
- any user interface.

### D2: recovery from key loss. APPROVED IN PRINCIPLE (September 28, 2026)

Approved by the owner, in these words:

> AetherFrame will support encrypted, portable `.afpersona` identity backups and restoration.
> Users must be able to restore the same cryptographic identity on another computer without a hosted account.
> Plaintext private key exports are not permitted.
> The exact backup encryption scheme, password policy, derivation parameters, recovery warnings and implementation remain subject to later security approval.

**What follows directly from the approval.** This is not a separate decision: a persona's private key must remain exportable *into an encrypted backup*. So a storage option that makes the key permanently unexportable cannot be the only copy of it.

**Still UNRESOLVED:** the encryption scheme, password policy, key derivation parameters, recovery warnings and implementation. They are tracked as "D2 details" in section 1 and relate to K4, K5 and K7. No `.afpersona` file may be written or restored outside tests until they have security approval.

## Decisions approved under the delegation

These are Claude's decisions under the owner's delegation of September 29, 2026, not the owner's own. The owner can overrule any of them in the Owner inbox, and the reversal is recorded here.

### D9b: how the protocol code ships in the plugin. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by the plugin integration skeleton (NETWORK1 increment 3).
- The protocol and persona code ship, when they ship, as sources compiled into `AetherFrame.dll`: `AetherFrame.csproj` compiles the canonical sources of `AetherFrame.Protocol` and `AetherFrame.Personas` in, linked, never as a fourth assembly. The release package stays exactly the three files the release tooling requires, whichever flavour is built.
- The standalone projects stay canonical: the code lives, is reviewed and is tested there (the protocol vectors, the persona suite); the plugin holds no copy and no fork.
- Only the preview flavour compiles them in (P2). A player build compiles none of the networking sources, so the player's `AetherFrame.dll` is unchanged by this decision.

**Rationale.**
- The architecture review's recommendation. A fourth DLL would change the release package, the tooling's three-file rule and every check that re-validates historical releases (the publication tooling re-validates old releases, so a relaxed rule would have to accept both shapes for good); linked sources change nothing in the package and are fully reversible by removing one condition.
- Compiling the same sources, rather than referencing the assembly, keeps one implementation: no API surface between the plugin and the protocol to version, and the plugin's own build settings (Dalamud's SDK: x64, C# 14, nullable) compile the code CI tests standalone.

**Not settled:** whether networking code ever ships in a player build at all (the sharing milestone's decision, after the G1 to G3 gates); the shape of the plugin-side services (increments 4 to 9).

**Independent concurrence.** Two reviewers with no shared context examined the change at `644fe32` on September 29, 2026: a general reviewer over the whole diff, and a security-focused reviewer over the packaging, the flavour switch and the boundaries. Neither found anything blocking, and the security reviewer **concurred** with this entry as recorded. Its findings: the property is set nowhere but the CI preview step, and no build props file above the project could set it; the linked sources carry no assembly attribute, module initializer or package reference, so compiling them in adds no file to the package and no entry to the lock file; DalamudPackager packages the DLL under `bin/`, so a preview build with its output elsewhere never reaches a package; the release workflow and the package script have no preview step.

### P2: who can create keys during NETWORK1. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by the same change.
- Preview builds only, by a compile-time switch: `dotnet build -p:AetherFrameNetworkPreview=true` defines `AETHERFRAME_NETWORK_PREVIEW`, compiles the networking sources in, and is the only flavour in which a persona key or a signed document could ever be made. Player builds and every official build are the default flavour: they contain none of that code, not merely no path to it.
- The switch is visible: `/aetherframe version` and Help say "[network preview]" in that flavour. CI builds the preview flavour once, on Windows, into a separate output that nothing packages or stores, and runs the boundary tests against it with `AETHERFRAME_PLUGIN_FLAVOUR=preview`.
- The boundary tests (`AetherFrame.Tests/PluginAssemblyBoundaryTests.cs`) hold both flavours to no networking assembly reference, no `System.Net` type and no networking API in any plugin source; the player flavour to no type in `AetherFrame.Protocol` or `AetherFrame.Personas`; every source outside the networking folders (`Services/Network`, `Hosting/Network`, `Windows/Network`, which a player build does not compile) to never name the networking code except inside `#if AETHERFRAME_NETWORK_PREVIEW`; and `PluginConfiguration` to no persona member. The release tooling's package check, which CI, the release workflow and the test-build procedure all run, refuses a DLL holding any `AetherFrame.Protocol` or `AetherFrame.Personas` type, so a preview DLL can become neither a release nor a test build.
- The preview flavour creates no persistent key and no signed document yet either: that waits for the remaining G1 decisions.

**Rationale.** The recommendation. A runtime flag in player builds would ship the code and a way to reach it, and the README's and the Dalamud submission's "no networking, no account" would stop being literally true. A compile-time switch keeps them true and keeps the player DLL free of the code.

**Not settled:** which preview commands exist (increment 9), the tester kit, and when a flavour with networking becomes the player build.

**Independent concurrence.** The same two reviews. The security reviewer **concurred** with this entry as recorded: no plugin source names the networking code or a key, signing or random-number API (ECDsa, RandomNumberGenerator, CngKey, NCrypt), so no path in a player build can create a key or a signed document; the boundary tests fail loudly when either CI variable is misnamed (a misnamed assembly variable falls back to the player DLL and fails the preview assertion, and a misnamed flavour variable runs the player assertions against the preview DLL); the README's statements that no account and no online service are needed stay literally true. Its notes were applied in the same change: the source scan resumes after `#else` inside a preview block, the plugin's own networking folders compile only in the preview flavour, and the package check refuses a DLL holding a protocol or persona type.

### K1: the persona key algorithm. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by the key store core (NETWORK1 increment 5, [NETWORK1_KeyStoreCore.md](NETWORK1_KeyStoreCore.md)). Keep P-256 ECDSA with P1363 signatures and low-S, as NETWORK0's approved signed bytes fix it: the key store holds P-256 private scalars and nothing else, and `PersonaKeyMaterial`'s managed checks stay the only way a scalar becomes a key.

**Rationale.** The signed bytes are approved and every identity is derived from a P-256 public key, so another algorithm would change every identity and every document. Every platform measured creates, imports, signs and verifies P-256 (native Windows CNG, the windows-2022 runner, Linux OpenSSL: [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md), sections 1 and 2).

**Not settled:** Wine, Proton and macOS, where NCrypt import fails from source and nothing has been run (K8).

**Independent concurrence.** Recorded below once the security-focused review of the change has run.

### K2: key storage on native Windows. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** The target for native Windows: DPAPI in CurrentUser scope with UI forbidden, its optional entropy derived from the envelope's header (so a blob is bound to its slot, its protector and its public key), in the plugin's own files under its configuration directory, one `.afkey` file per slot named by the slot alone; never `PluginConfiguration`, never Dalamud's reliable storage (which keeps copies the plugin cannot delete), never a persona identity in a name. What the key store core builds: the store (`ProtectedPersonaKeyStore`), the envelope (`AFPK` version 1), the two seams (`IPersonaKeyProtector`, `IPersonaKeyBlobStorage`) and the plugin's directory storage (`PersonaKeyFileStorage`, compiled only in the preview flavour, tested from the persona suite). What it does not build: the DPAPI protector itself, which is increment 7 and gets its own review under this entry. Until then no protector exists outside tests, and no key is written outside tests.

**Rationale.** The recommendation and the Windows measurements ([NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md), sections 1 and 5): DPAPI in CurrentUser scope protects a copied key file against other accounts and other machines, and against nothing that runs as the same user, including other plugins in the game process; the register says so plainly and the plugin will too. The plugin's own files let it delete a key it no longer holds; reliable storage keeps copies it cannot. Binding the blob to the header costs nothing and stops a blob from being moved under another slot or key.

**Not settled:** K3 (enabling by capability test), K9 (Wine and other platforms), the exact directory (increment 9's wiring), how the protector derives DPAPI's entropy from the header and what it reports as "locked on this account" (increment 7).

**Independent concurrence.** Recorded below once the security-focused review of the change has run.

### K6: key rotation. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** None in v1: a persona's key is its identity for its whole life. Migrating means creating a new persona, republishing under it, and retracting the old profiles with the old key (D1). The store therefore has no replace and no delete.

**Rationale.** Rotation with continuity of identity needs a signed link between keys, a server that honours it and a protocol change; nothing in NETWORK1 needs any of that, and a new persona is cheap. Without rotation the store's contract stays small enough to verify: one key per slot, written once.

**Not settled:** whether a later protocol version adds a signed successor link.

**Independent concurrence.** Recorded below once the security-focused review of the change has run.

### K7: where cryptographic implementations come from. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Platform implementations only: .NET's `ECDsa` and `RandomNumberGenerator` over the platform (CNG on Windows, OpenSSL on Linux), and the same platform's APIs called directly where .NET does not expose them (crypt32's DPAPI, increment 7). No third-party cryptography package, and no algorithm written in this repository: not signing, not key derivation, not encryption, not verification in production. The test fake protector implements nothing cryptographic and never ships. Neither our own code nor a dependency is chosen to keep the package at three files; either is a separate owner decision with its own review.

**Rationale.** [NETWORK1.md](NETWORK1.md), safeguard 5, made binding.

**Not settled:** K8 (a verification path that does not import through NCrypt, for Wine), which this entry does not allow by itself.

**Independent concurrence.** Recorded below once the security-focused review of the change has run.

## Gates

| Gate | Must be decided before |
|---|---|
| **G1** | Any **persistent private key** is created outside tests, including preview or developer builds on a player's or developer's machine |
| **G2** | The snapshot builder, image preparation, or any change to signed bytes |
| **G3** | The protocol v1 freeze, or any server accepting documents signed by real keys |
| **G4** | Backend design |
| **—** | No gate; can be decided whenever it becomes relevant |

"Bytes" means whether an option changes what is signed (a schema, vector or tag change).

## 1. Decisions that must be approved before any persistent private key is created (G1)

- **Approved so far:** D3 (APPROVED) and D2 (APPROVED IN PRINCIPLE).
- **Every other row in this table must still be approved** before a persistent private key is created outside tests.
- **"D2 details"** has its own gate: before any `.afpersona` file is written or restored outside tests.
- **Approved rows** state the approved option in the recommendation column.

| Id | Question | Baseline or current state | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D2 | Recovery from key loss | None. Without the key nothing can be retracted, and there is no account. | Approved in principle: encrypted, portable `.afpersona` backups and restoration, including on another computer without a hosted account; no plaintext private key export (see "Approved decisions") | no | **APPROVED IN PRINCIPLE (2026-09-28)** |
| D2 details | Backup encryption scheme, password policy, key derivation parameters, recovery warnings, implementation | Undefined | Subject to later security approval. Related proposals, not approved: K4, K5, K7. No `.afpersona` file may be written or restored outside tests before this is approved. | no | UNRESOLVED |
| D3 | How many personas an installation holds, and how one is chosen | Undefined | Approved: several independent personas; manual selection and switching; one active at a time for identity-dependent operations; never bound automatically to game identifiers; switching never alters saved Plates or triggers publishing (see "Approved decisions") | no | **APPROVED (2026-09-28)** |
| D9b | How the protocol code ships in the plugin (fourth DLL or sources compiled in) | The preview flavour compiles the sources in; player builds compile none | Approved: the sources compiled into AetherFrame.dll, the package at three files, the standalone projects canonical, and only in the preview flavour (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| N3 | Whether draft documents are distinguishable from final v1 in the signed bytes | Drafts use version 1 and the `…SignedDocument.v1` tag | A distinct draft version and tag until the freeze | **yes** | UNRESOLVED |
| N5 | Whether the profile id stays on every `RemoteDocument` | It does | Move it to profile document types only, before NETWORK1 code depends on the API | no (public API) | UNRESOLVED |
| L6 | Where the provisional `IPersonaKeyProvider` and `FuturePolicy` live | Both public, marked provisional | Remove the provider from the protocol (key storage is plugin policy); make the policy numbers internal or documentation only | no (public API) | UNRESOLVED |
| K1 | Persona key algorithm | P-256 ECDSA, P1363, low-S (NETWORK0); the key store core holds P-256 scalars only | Approved: keep it (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K2 | Key storage on native Windows | The store core, the envelope and the directory storage exist behind a protector seam; no protector ships | Approved as the target: DPAPI, CurrentUser scope, UI forbidden, entropy from the envelope header, in the plugin's own files named by slot; never plugin configuration or Dalamud reliable storage. The DPAPI protector itself is increment 7 with its own review (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K3 | Platform enablement policy | The architecture review first proposed disabling personas under Wine | Enable persona features only where a startup capability test of the full chain passes; never decide by operating system label; local features always unaffected; no product statement excluding non-Windows players (NETWORK1_CryptoCompatibility.md, section 6) | no | UNRESOLVED |
| K6 | Key rotation | None; the store has no replace and no delete | Approved: none in v1. Migrating means a new persona, republishing, and retracting the old profiles with the old key (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K7 | Where cryptographic implementations come from | Platform only (.NET over CNG/NCrypt) | Approved: platform implementations only, .NET or the same platform's APIs called directly. A third-party library or an algorithm in our own code is a separate owner decision with its own review, and is never chosen to keep the package at three files (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| P1 | Where "this Plate was published" is remembered | Undefined | A separate private index per persona, never in Plate JSON, character bindings, packages or logs | no | UNRESOLVED |
| P2 | Who can create keys during NETWORK1 | Nobody yet: the preview flavour holds the code, no key store exists | Approved: preview builds only, by the compile-time switch `AetherFrameNetworkPreview`; player and official builds contain none of the networking code (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |

Only if persona features are pursued under Wine, Proton or macOS. These must be decided before a persistent key is created there, and only after measurements under those platforms:

| Id | Question | Current state | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| K8 | A protocol verification path that does not import keys through NCrypt | `Verify`, and `Sign`'s self-check, always import through .NET's `ECDsa`. From source, that fails under every Wine version examined. | Decide only after Wine measurements. Options: a platform BCrypt verifier supplied by the plugin (public API change), or verification in our own code (an algorithm under K7, needing independent review). The threat model changes either way. | no | UNRESOLVED |
| K9 | Key storage where DPAPI gives no protection (Wine) | Wine DPAPI is obfuscation only (source) | Decide only after measurements. Options: a passphrase-wrapped store whose primitives are verified under the target Wine builds, an explicitly disclosed unprotected state, or no support. | no | UNRESOLVED |

## 2. Decisions needed before the snapshot builder, image work or a signed-byte change (G2)

| Id | Question | Baseline | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D4 | Rules for the schema-1 `name` | Any text without U+0000, up to 32,000 scalars | 1–64 scalars, a 256-byte limit, and a fixed list of refused code points (C0/C1 controls, line and paragraph separators, BOM, bidi controls), with no normalisation | **yes** | UNRESOLVED |
| D5 | What an image digest covers; metadata | "The source image bytes" | The digest of the prepared upload copy, never the original. The client strips metadata by rebuilding the container; the server always re-processes. | wording only, unless a salt is added | UNRESOLVED |
| D8 | Whether metadata-only schema 1 is ever exposed to players | Test-only | No; the first public schema includes the layout | no | UNRESOLVED |
| D9a | Persona display name | None in the protocol. `AetherFrame.Personas` (#23) implements the recommendation provisionally as `PersonaLabel`, so the persona model can be exercised; the plugin does not reference that assembly. | A private local label only; any public name later, per snapshot | no (for now) | UNRESOLVED |
| I1 | Which images can be shared | Undefined | Still PNG, JPEG and WebP. The first frame of animated PNG. Refuse animated WebP, CMYK JPEG and oversize images; no silent downscaling. | no | UNRESOLVED |

## 3. Decisions needed before the freeze or the first real server (G3)

| Id | Question | Baseline | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D1 | What a retraction means | A signed, terminal retraction with a permanent minimal record | Keep it signed and terminal; republishing uses a new profile id | yes, only if changed | UNRESOLVED |
| D6 | Whether viewers receive signed envelopes or server-checked content | Undecided | Server-checked content (reversible later; signed proofs handed out cannot be recalled) | no | UNRESOLVED |
| D7 | Whether documents are bound to a deployment | Not bound | Bind through a short-lived publish request proof. The alternative is a deployment id in the signing input, which is only possible before the freeze. | yes, only for the alternative | UNRESOLVED |
| K4 | Whether a backup is required before the first real publish | Undefined | Required, or an explicit acknowledgement that losing the key means never being able to unpublish | no | UNRESOLVED |
| K5 | Backup passphrase rules and key derivation route | Undefined | At least 15 characters, or a generated code; NFC; never truncated. The key derivation must use a route verified on every supported platform: `Rfc2898DeriveBytes.Pbkdf2` fails under Wine before 11.3, from source and a published report. Because D2 is approved in principle, this is also needed before any `.afpersona` file is written outside tests (part of "D2 details"). | no | UNRESOLVED |
| N1 | Scope of revision and asset ids | Profile scoping only | Revisions per (persona, profile); assets per persona; nothing global; no deduplication across personas | no | UNRESOLVED |
| N7 | Whether consumers must escape names before display or logging | Nothing obliges them | Oblige every consumer to treat names as plain text | no | UNRESOLVED |

## 4. Backend-time decisions (G4)

| Id | Question | Recommendation (not approved) | Status |
|---|---|---|---|
| N2 | Rollback by replaying a pruned revision | The server keeps every accepted revision id; a replay never becomes "latest" | UNRESOLVED |
| N6 | Retractions blocked by a fast client clock | Exempt retractions from the future-clock check | UNRESOLVED |
| S1 | Request proofs | Required to publish, not to retract | UNRESOLVED |
| S2 | What a tombstone holds | A peppered hash of (persona, profile) and a date | UNRESOLVED |
| S3 | Profiles whose key is lost | Expiry after long inactivity, disclosed in advance | UNRESOLVED |
| S4 | A persona-level revocation document | Defer; it would be an additive document type | UNRESOLVED |
| I2 | Server image processing | Always re-process; serve PNG or JPEG only; no auto-rotation or colour transform | UNRESOLVED |

## 5. Other open items

| Id | Item | Status |
|---|---|---|
| L2 | Conformance vectors thinner than the rule set (the large text limits live in unit tests only) | UNRESOLVED (NETWORK1, when a second implementation exists) |
| L4 | A public-only key accepted by `EcdsaPersonaSigner` until first `Sign` | SETTLED for the key store core (September 29, 2026): `ProtectedPersonaKeyStore` makes every signer from checked material, which always holds the private half. The manager's public-key check stays, because the interface allows another store to differ |
| L8 | Signing-context rules not yet in the specification | UNRESOLVED (before any second signing context) |
| L9 | Whether protocol tests gate plugin releases (`release.yml`) as well as `build.yml` | UNRESOLVED. Its "Linux never ran" part is closed: the ubuntu leg has run green twice (NETWORK0.md, section 13). |
| I3 | Photo metadata in local `.aetherframe` exports (existing local behaviour, not networking) | UNRESOLVED |
| L10 | Signer lease on a persona switch: may an operation holding a lease for persona A still sign as A after the player switches to B? The interim in `AetherFrame.Personas` (#23) revokes the lease on any change of selection, never revives it, and fails closed, following NETWORK1.md system 1. The alternatives are letting the operation finish as A (which needs system 1 amended) or refusing a switch while a lease is open. | UNRESOLVED (before the preview wiring, NETWORK1.md increment 9, lets a player start an operation that signs) |
| L11 | Whether the persona suite also gates plugin releases (`release.yml`). Today only `build.yml` runs it. A question of the same kind as L9; L9 itself stays scoped to the protocol suite. | UNRESOLVED (with L9) |

## 6. Closed items

These are not product decisions, and are listed so they aren't reopened by accident:

| Id | Item | Closed by |
|---|---|---|
| N4 | Specification rule 3 prejudged D6 | Specification text before the NETWORK0 merge; rule 3 now leaves serving to D6 |
| T1–T5 | NETWORK0 pre-merge test and documentation defects | The pre-merge cleanup (NETWORK0_HANDOFF.md) |
| — | Stray text above the titles of NETWORK0.md and NETWORK0_HANDOFF.md | Increment 0 (2026-09-28) |
| — | Statements that the protocol suite had never run on Linux | Increment 0 (2026-09-28), with the CI results in NETWORK0.md, section 13 |

## Changing this register

To approve an entry:
- set its status to `APPROVED (date)`;
- write the approved option in the recommendation's place;
- link the change that applies it.

An approval that changes signed bytes also needs:
- regenerated vectors;
- the independent checker updated and re-run;
- the specification updated in the same change.
