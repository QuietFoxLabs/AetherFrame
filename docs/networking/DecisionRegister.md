# Networking decision register

**Status (2026-09-28): two decisions are approved.**
- **D3** is **APPROVED**.
- **D2** is **APPROVED IN PRINCIPLE**. Its technical details remain unresolved, pending later security approval.

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
| D9b | How the protocol code ships in the plugin (fourth DLL or sources compiled in) | The plugin does not reference it | Compile the sources into AetherFrame.dll, keeping the package at three files, with the protocol project staying canonical. If a fourth DLL is ever chosen, the release tooling must keep accepting historical three-file packages. | no | UNRESOLVED |
| N3 | Whether draft documents are distinguishable from final v1 in the signed bytes | Drafts use version 1 and the `…SignedDocument.v1` tag | A distinct draft version and tag until the freeze | **yes** | UNRESOLVED |
| N5 | Whether the profile id stays on every `RemoteDocument` | It does | Move it to profile document types only, before NETWORK1 code depends on the API | no (public API) | UNRESOLVED |
| L6 | Where the provisional `IPersonaKeyProvider` and `FuturePolicy` live | Both public, marked provisional | Remove the provider from the protocol (key storage is plugin policy); make the policy numbers internal or documentation only | no (public API) | UNRESOLVED |
| K1 | Persona key algorithm | P-256 ECDSA, P1363, low-S (NETWORK0) | Keep it. Tested working on native Windows and native Linux. | no | UNRESOLVED |
| K2 | Key storage on native Windows | Undefined | DPAPI, CurrentUser scope, UI forbidden, fixed purpose entropy, in the plugin's own files. Never through plugin configuration or Dalamud reliable storage. It works on Windows under both runtimes tested. | no | UNRESOLVED |
| K3 | Platform enablement policy | The architecture review first proposed disabling personas under Wine | Enable persona features only where a startup capability test of the full chain passes; never decide by operating system label; local features always unaffected; no product statement excluding non-Windows players (NETWORK1_CryptoCompatibility.md, section 6) | no | UNRESOLVED |
| K6 | Key rotation | None | None in v1. Migrating means a new persona, republishing, and retracting the old profiles with the old key. | no | UNRESOLVED |
| K7 | Where cryptographic implementations come from | Platform only (.NET over CNG/NCrypt) | Platform implementations only: .NET, or the same platform's APIs called directly. A third-party library or an algorithm in our own code is a separate owner decision with its own review, and is never chosen to keep the package at three files. | no | UNRESOLVED |
| P1 | Where "this Plate was published" is remembered | Undefined | A separate private index per persona, never in Plate JSON, character bindings, packages or logs | no | UNRESOLVED |
| P2 | Who can create keys during NETWORK1 | Nobody (not implemented) | Preview builds only, using a compile-time switch; player and official builds contain no path that creates a key | no | UNRESOLVED |

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
| D9a | Persona display name | None | A private local label only; any public name later, per snapshot | no (for now) | UNRESOLVED |
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
| L4 | A public-only key accepted by `EcdsaPersonaSigner` until first `Sign` | UNRESOLVED (key store design) |
| L8 | Signing-context rules not yet in the specification | UNRESOLVED (before any second signing context) |
| L9 | Whether protocol tests gate plugin releases (`release.yml`) as well as `build.yml` | UNRESOLVED. Its "Linux never ran" part is closed: the ubuntu leg has run green twice (NETWORK0.md, section 13). |
| I3 | Photo metadata in local `.aetherframe` exports (existing local behaviour, not networking) | UNRESOLVED |

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
