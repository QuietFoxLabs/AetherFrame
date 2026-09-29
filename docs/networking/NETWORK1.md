# NETWORK1: planned architecture boundaries

**Status (2026-09-28): planning only.**
- Nothing in this document is implemented.
- No persona key exists.
- Protocol Specification v1 is still a **DRAFT**.
- The plugin still has no network code and does not reference `AetherFrame.Protocol`.
- This document fixes *boundaries*, meaning what each part may and may not touch. It does not settle product decisions; those are recorded in [DecisionRegister.md](DecisionRegister.md). As of 2026-09-28:
  - **D3** (several independent personas, chosen manually) is **approved**, and this document follows it.
  - **D2** (encrypted, portable `.afpersona` backups; no plaintext key export) is **approved in principle**. Its technical details await security approval.
  - Every other decision is unresolved.
- Platform findings are in [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md). NETWORK0's approved behaviour is in [NETWORK0.md](NETWORK0.md) and [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md).

NETWORK1 is the client-side milestone that follows NETWORK0. It prepares identity and publishing on the player's machine. It builds no HTTP, no server, no discovery and no sharing user interface.

## 1. The rule that holds regardless of any decision

**Local editing never depends on the systems below.**
- **What counts as local:**
  - the Plate Library;
  - Basic and Advanced editing;
  - templates;
  - images;
  - `.aetherframe` import and export;
  - the Active Plate;
  - character bindings.
- **What the rule means:**
  - Local code never reads, waits on, or fails because of persona identity, key storage, backups, publishing or a backend.
  - Local code keeps working exactly as today with every one of them absent, disabled, broken or unsupported on the platform.
  - A failure in any of them is reported once and switches that system off for the session. It never touches Plates, templates, images or bindings.
  - Selecting or switching personas never alters saved local Plates and never triggers publishing (D3, approved).

## 2. The six systems

| # | System | Owns | Must never | Where it will live |
|---|---|---|---|---|
| 1 | **Persona identity and signing** | The persona's public key and its identity, derived by the protocol (`psn_` + SHA-256 over the tagged key). Signing only protocol-built signing inputs, through `IPersonaSigner`. | Sign arbitrary or server-supplied bytes outside a tagged signing input. Derive or attach anything from the game: Content ID, character name, world, account, machine. Bind a persona automatically to a character, Content ID, account or other game identifier (D3). Sign for a persona that is not the active one (D3: one persona is active at a time for identity-dependent operations). Expose private key material through any API, log, exception or serialisation. | `AetherFrame.Protocol` for identity, framing and verification (NETWORK0, approved behaviour, DRAFT bytes). A plugin-side signer implementation (NETWORK1, planned). |
| 2 | **Protected private key storage** | Private keys at rest, per installation. The installation's independent personas (D3, approved), and the one active persona, which only the player selects and switches (D3). The platform protection for keys at rest. | Write key material through `PluginConfiguration` or Dalamud's reliable storage, which keeps backup copies the plugin cannot delete. Name files or folders with persona ids. Claim protection the platform doesn't give (DPAPI under Wine; code running as the same user). Switch the active persona by itself (D3). Keep a persona's only copy in a form that cannot be exported into an encrypted backup (follows from D2). | Plugin. A Dalamud-free core behind a protector interface, plus a Windows-specific protector. Other platforms: unresolved (K9). |
| 3 | **Portable encrypted backups** | Encrypted, portable `.afpersona` backups of a persona that the player makes and keeps, and their restoration, including restoring the same identity on another computer without a hosted account (D2, approved in principle). | Export a private key in plaintext (D2). Happen automatically, go to a server, or be included in `.aetherframe` packages, which are unencrypted and made for sharing. | Plugin, alongside key storage. The encryption scheme, password policy, derivation parameters, recovery warnings and implementation await security approval ("D2 details", K4, K5, K7). |
| 4 | **Local Plate persistence** | Plate JSON, templates, managed image copies and packages, exactly as today. | Gain persona ids, remote profile ids or publication state. Export, Duplicate and Save as Template copy Plate JSON *including unknown fields*, so anything stored there would travel. | Unchanged. |
| 5 | **Optional remote publishing** | The snapshot builder, the image preparation, the publication index and the outbox (see below), and the consent step that shows exactly what would leave the machine. | Read the player's original image files, or put their bytes or digests in a document (D5). Send anything anywhere in NETWORK1. Be reachable in player builds before the owner decides it (P2). | Plugin, Dalamud-free core. |
| 6 | **Future backend: ownership and replay protection** | Enforcing ownership by (persona, profile id). Remembering accepted revision ids (N2). Tombstones (D1). Request proofs (S1). Quotas, rate limits, abuse handling, and image processing (I2). | Live in the plugin, or be assumed by it. The plugin never treats a server response as proof of ownership. | A later backend milestone. Its obligations are in the specification, section 13, still a DRAFT baseline. |

The parts of system 5 in more detail:
- **Snapshot builder:** reads the *saved* Plate, never editor memory.
- **Image preparation:** makes prepared copies; the originals are never touched.
- **Publication index:** kept per persona and separate from Plates.
- **Outbox:** holds signed documents; nothing sends them in NETWORK1.

## 3. Allowed dependencies

```
Local Plate persistence ◄─── read only (saved Plate, managed images) ───  Optional remote publishing
      (depends on nothing below)                                                   │
                                                                                   ▼
                                                              Persona identity and signing ◄── AetherFrame.Protocol
                                                                                   │
                                                                                   ▼
                                               Portable encrypted backups ◄──► Protected private key storage

Future backend: consumes signed documents only; the plugin depends on it for nothing local.
```

- **Arrows point from the user to the used.** Nothing points into local persistence except a read-only view of saved Plates and managed images.
- **Nothing in local persistence refers to the other five systems.** Planned boundary tests enforce this:
  - the plugin assembly has no `System.Net` reference;
  - only the networking folders may name `AetherFrame.Protocol`;
  - local folders never reference the networking folders;
  - `PluginConfiguration` has no persona members.

## 4. Safeguards that hold whatever is decided

1. **No network code in NETWORK1.**
2. **No persistent persona key** is created outside tests until every decision marked G1 in [DecisionRegister.md](DecisionRegister.md) is approved.
3. **NETWORK0's signed bytes do not change** except in an increment the owner explicitly approves (N3, D4 and D7 are the candidates). **Protocol v1 stays a DRAFT** until an explicit owner freeze.
4. **Implementations are enabled by capability tests, not operating system labels.** This is a recommendation, K3. See NETWORK1_CryptoCompatibility.md, section 6.
5. **No hand-written cryptographic algorithms** (signing, key derivation, encryption, or production verification) are approved as a way to keep the release package at three files or to avoid a dependency. Moving an algorithm into our own code, or adding a cryptography dependency, is its own owner decision with its own review (K7, K8).
6. **Platform validation is never trusted alone.** The protocol's managed key and signature checks stay in front of any platform or alternative backend.
7. **Privacy:**
   - no telemetry;
   - no game identifiers in documents;
   - no persona ids in file names;
   - persona, profile, revision and asset ids redacted from logs (planned).
8. **Ownership belongs to the backend.** The plugin cannot enforce it and must not claim to.

## 5. What NETWORK1 does not include

- HTTP, a server, uploads or downloads.
- Discovery or target lookup.
- Share links.
- A viewer.
- Sharing user interface.
- Accounts.
- Changes to the Plate JSON schema, the plugin configuration version or the `.aetherframe` format.
- A protocol freeze.

## 6. Proposed increments (non-binding)

Each increment needs its own approval, and none may cross a gate in DecisionRegister.md before that gate's decisions are approved.

| # | Increment | Gate |
|---|---|---|
| 0 | Documentation repair and architecture preparation (this change) | none |
| 1 | Protocol API tidy with no signed-byte change (N5, L6) | N5, L6 |
| 2 | Draft marker in the signed bytes, and the name rule | N3, D4 |
| 3 | Plugin integration skeleton and boundary tests | D9b, P2 |
| 4 | Publication index, outbox, snapshot builder (name only) | P1, D8, D4 |
| 5 | Key store core, with a fake protector only | K1, K2, K6, K7 (D2 in principle and D3 approved) |
| 6 | Backup codec | "D2 details" (security approval), K5, K7 |
| 7 | Windows platform layer and capability probe | K2, K3; increment 2 merged first |
| 8 | Image preparation | D5, I1 |
| 9 | Preview wiring in game | P2, K4 |
| 10 | Wine, Proton and macOS measurements | K3, K8, K9 |
| 11 | Acceptance and independent review | all of the above |
