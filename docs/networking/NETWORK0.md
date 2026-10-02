# NETWORK0: the remote protocol foundation

**Status (2026-09-28): the code is reviewed and remediated; the wire format is a DRAFT** until the open product decisions in section 11 are settled, some of which change signed bytes. Nothing here is published, referenced by the plugin, or accepted by any server.

**Update (2026-09-28, after the merge):** NETWORK0 was merged into master as `6384db6` with both CI legs green (section 13). Protocol version 1 is still a DRAFT. The open decisions are tracked with their current status in [DecisionRegister.md](DecisionRegister.md); on 2026-09-28 the owner approved D3 (several independent personas) and approved D2 in principle (encrypted, portable backups), and every other decision is unresolved. The planned NETWORK1 boundaries are in [NETWORK1.md](NETWORK1.md), and the platform cryptography findings are in [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md).

**Update (2026-10-02):** since "Releases carry sharing" ([DecisionRegister.md](DecisionRegister.md), October 1, 2026) the protocol's sources are compiled into `AetherFrame.dll` (D9b) in every release from 0.1.8 on, and the server at plates.aetherframe.dev ([ServerApi-v1.md](ServerApi-v1.md)) verifies and accepts its documents. Statements below that the plugin does not reference the protocol describe NETWORK0 as merged on 2026-09-28. Protocol version 1 is still a DRAFT.

NETWORK0 is the first networking milestone of AetherFrame, and it builds no network. It is the isolated protocol, identity, signing, verification, serialization and hostile-input foundation that the later milestones (NETWORK1, the client that talks to a backend; the backend itself) depend on, so that those milestones can be built without revisiting how bytes are framed, how a persona is identified, or what a signature covers. This document explains what was built and why; [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) is the normative wire format; [NETWORK0_HANDOFF.md](NETWORK0_HANDOFF.md) is the morning handoff with results and open questions.

Everything described here lives in the `AetherFrame.Protocol` assembly and its tests. **The plugin does not reference it, nothing here opens a connection, reads or writes a file, or needs an account, and the plugin's behaviour is unchanged.**

## 1. What NETWORK0 is for

AetherFrame is local first: Plates, Templates, images, import, export and the Plate Library never depend on an account or a server, and an online service must never hold the only copy of anything a player made. Sharing, when it arrives, is an optional layer on top: a player publishes a copy of a Plate and later withdraws it. For that layer to be trustworthy, three things must be right before any server exists:

1. **Identity.** Who published something must be provable without an account, a login or a game identifier. NETWORK0 makes a persona a P-256 key pair; its public identity is a hash of the public key and nothing else.
2. **Authenticity.** What a viewer receives must be exactly what the creator signed, with no way to alter it, reuse the signature elsewhere, or pass it off as another kind of statement. NETWORK0 signs canonical bytes with domain separation, and refuses anything that is not the one canonical form. `[updated 2026-09-30: under decision D6 (DecisionRegister.md), a viewer receives server-checked content rather than the signed bytes, so this goal now ends at the server: the server receives exactly what the creator signed, and what a viewer sees is attested by the server, not signed by the creator.]`
3. **Safety against hostile input.** Every byte that will one day come from a server is untrusted. NETWORK0's readers check every length against its limit before allocating, fail closed on anything unknown, and refuse every malformed input with a typed error and nothing else.

NETWORK0 also draws the line between the local model and the remote model: what goes on the wire is a separate, deliberately narrower set of types that share no code with the local Plate.

## 2. Architecture

```
AetherFrame.Protocol            net10.0, BCL only, no packages, no Dalamud, no plugin reference
  ProtocolConstants             magic, version, domain separation tags
  ProtocolLimits                every enforced limit
  ProtocolError / Exception     the closed set of refusals
  Encoding/                     CanonicalWriter, CanonicalReader, ProtocolText
  Identity/                     PersonaPublicKey, PersonaId, ProfileId, RevisionId, AssetId, P256Curve
  Signing/                      SigningInput, ProtocolSignature, IPersonaSigner, EcdsaPersonaSigner, SignatureVerifier
  Documents/                    DocumentType, RemoteDocument, SignedDocumentCodec, VerifiedDocument
  Remote/                       RemoteProfileDocument, ProfileSnapshot, ImageReference, ImageFormat, ProfileRetraction, RemoteProfileKey
AetherFrame.Protocol.Tests      xunit; vectors, canonicalization, adversarial, fuzz, boundary, benchmark
```

`[updated 2026-09-29: NETWORK1 increment 1 applied decisions N5 and L6, both APPROVED (Claude, under the owner's delegation of September 29, 2026) (DecisionRegister.md). The profile id moved from RemoteDocument to the new RemoteProfileDocument. Integration/IPersonaKeyProvider and ProtocolLimits.FuturePolicy were removed. No signed byte changed.]`

Why a standalone project rather than a folder in the plugin: the plugin's release package is exactly three files (`docs/CustomRepository.md`), and the release tooling refuses anything else. A plugin reference would add a fourth assembly, which is a packaging decision for NETWORK1 (link the sources in, or change the package rule), not something to slip in with the protocol. Standalone also means the same assembly can be used unchanged by a backend, so client and server can never disagree about the bytes. The project has no NuGet packages and references only `System.*`: a test asserts that, and that nothing from `System.Net`, the file system or process APIs is referenced.

Why the plugin's own tests project is not reused: `AetherFrame.Tests` compiles the plugin's Dalamud-free sources by linking them; the protocol is a separate assembly with its own public surface, and its tests check that surface (an approved public API list is committed).

### 2.1 Data flow

Publishing (a NETWORK1 client, not built): build a `ProfileSnapshot` from the local Plate through a builder that lives on the plugin side; `SignedDocumentCodec.Sign(snapshot, signer)` encodes the payload, builds the signing input, signs, and lays out the document. The only thing the plugin will need from its side is an `IPersonaSigner`, obtained from an `IPersonaKeyProvider` that keeps the key in protected local storage. `[updated 2026-09-29: under L6 the protocol no longer defines a provider. The plugin side obtains the signer from its own key storage, which AetherFrame.Personas models (IPersonaKeyStore and a revocable signer lease).]`

Receiving (a viewer or a server; `[updated 2026-09-30: under D6 only the server receives signed documents, apart from the capability probe's known-answer check]`): `SignedDocumentCodec.Verify(bytes)` copies the input, then checks the framing, the version, the type, the lengths and the trailing bytes, then the key, then the signature, and only then decodes the payload. The result is a `VerifiedDocument` whose persona is derived from the key that verified; the caller cannot supply one. Its `Profile` is the pair (persona, profile id): the only thing a server may key a profile by. `[updated 2026-09-29: under N5, Profile is nullable. It is the pair for a profile document, which every version 1 type is, and null for a later type that isn't about a profile.]`

### 2.2 The signing approach, exactly

- The **wire format is one canonical binary encoding**: big-endian fixed-width integers, four-byte length prefixes, strict UTF-8 text, closed enumerations, no floats, no optional fields, sets sorted on the wire. There is no JSON anywhere in the signing path, so no canonical-JSON scheme, property order or number formatting can enter into what is signed. The bytes that are transmitted are the bytes that are signed.
- The **signing input** is `tag ‖ version ‖ type ‖ public key ‖ length ‖ payload`, with the tag `AetherFrame.Protocol.SignedDocument.v1` and its length in front. Binding the version and the type stops cross-version and cross-type reuse; binding the key stops key substitution (including duplicate-signature-key-selection constructions, where an attacker fits a new key to an existing signature); the tag stops reuse against any other signing context AetherFrame introduces later. `[updated 2026-09-29: NETWORK2's N2-2 applied decision N3: every document is a draft until the owner's freeze, with protocolVersion 0x8001 and the tag AetherFrame.Protocol.SignedDocument.v1-draft. The persona identity tag is unchanged.]`
- The **signature** is ECDSA P-256 / SHA-256, 64 bytes `r ‖ s`, with `s` forced into the low half at signing time and required there at verification. Every signature therefore has exactly one byte form; a third party cannot mint a second valid encoding of an existing signature.
- **Identity** is `psn_` + hex(SHA-256(tag ‖ 0x01 ‖ key)) with its own tag, `AetherFrame.Protocol.PersonaId.v1`, and a key-format byte, so a future key type gets a different identity space.

## 3. Non-goals

NETWORK0 does not contact a server, make HTTP requests, implement authentication, sessions, capability tokens or shares, touch Cloudflare, PostgreSQL or object storage, implement discovery or target lookup, add sharing UI, modify the editor, modify local Plate persistence, require an account, encrypt anything, store or protect private keys, or define the remote layout schema (elements, Components, fonts, colours). Each of those is either NETWORK1 or the backend, and the handoff says which.

Nothing was added to make the protocol "extensible": no dictionaries, no object bags, no unknown-field passthrough, no reflection-driven polymorphism. A future need is a new schema number or a new document type, refused by readers that predate it.

## 4. Threat model

Every byte a client will one day receive from a server, and every byte a server receives from a client, is hostile. Within that:

| Threat | Where it is met |
|---|---|
| Forged or altered content presented as a persona's | ECDSA over a tagged signing input that binds version, type, key and payload; the persona is derived from the verifying key, never supplied by the caller. |
| A signature reused for another document, type, version or signing context | Type and version are inside the signed bytes; a fixed domain tag separates document signatures from anything else; a new version gets a new tag. |
| A signature transplanted under another key (key substitution, duplicate-signature-key selection) | The key is inside the signed bytes. |
| Two encodings of one signature (malleability) | Low-S required; r and s range-checked; fixed 64-byte form, no DER. |
| Two encodings of one document (canonicalization ambiguity) | One binary encoding; no optionals; sets sorted on the wire and refused otherwise; text never normalized, so what was signed is what is shown. |
| Invalid curve material (off-curve or compressed points, coordinates at or above p, other curves) | The protocol's own range and on-curve check before the platform sees the key, so behaviour is the same on Windows and Linux; the range rule is tested with the same point encoded as x + p and as y + p, which the curve equation alone would accept. A platform key is checked by curve identifier before its point. Whatever exception the platform uses for a point it dislikes (Windows CNG: `PlatformNotSupportedException`) is reported as `InvalidKey`. |
| Oversized or hostile lengths driving allocation | The whole document is capped at 1 MiB before parsing; every length and count is compared with its limit before allocation; nothing is ever sized by a declared length that has not passed its limit. A reader's allocations are proportional to the input it was given (one copy of the input, the signing input, and the decoded model), never to what the input claims. Measured: a hostile length costs about 700 bytes and 2 µs to refuse. |
| Truncation, trailing bytes, nested-count abuse, malformed UTF-8, U+0000 | Refused with a typed error at the exact boundary; covered by truncation at every offset, a flipped bit at every position, and seeded fuzz campaigns. |
| Exceptions leaking bytes or crashing the host | For hostile bytes, the only exception type is `ProtocolException`; messages name fields and numbers, never content; tests assert no other exception type escapes for tens of thousands of hostile inputs, including inputs rewritten by another thread while they are read. Misuse of the API by its caller (a null argument, a destination span that is too short, a disposed signer) throws the usual .NET argument and object-disposed exceptions (`ArgumentNullException`, `ArgumentException`, `ObjectDisposedException`); those are not listed per member, only `ProtocolException` is. `Sign` lets the signer's own exceptions pass through unchanged. |
| A value that passed a check differing from the value used (time-of-check to time-of-use) | Every reader copies its input before the first check and uses only the copy: `Verify` copies the whole document, `PersonaPublicKey.FromBytes` and `ProtocolSignature.FromBytes` copy their bytes, identifiers and digests are read once. Every model and result type is immutable, and list views cannot reach their storage. Tests race each reader against a rewriting thread. |
| A signer that misbehaves (misreports its key, signs with another key, signs other bytes) | `Sign` reads the signer's key once and verifies the finished document before returning it; bytes the reader would refuse never leave `Sign`. |
| A document applied to a profile its signer does not own | A profile is (persona, profileId); a document names the signing persona's profile and nobody else's (specification, sections 8.4 and 13), and `VerifiedDocument.Profile` exposes exactly that pair. |
| Private key exposure through the protocol | No API exports, serializes, logs or formats private material; the signer signs only protocol-built signing inputs; a test asserts the public surface returns no platform key type. |

Out of scope for NETWORK0, and named so nothing is assumed: transport security, server compromise, replay of a whole document and the ordering of snapshots and retractions (server obligations, specification section 13; the baseline is stated there and depends on decision D1) `[updated 2026-09-30: decided in decision batch B; section 13's rules 3 to 7 name the decisions]`, denial of service at the network layer, moderation, key loss and rotation, and the strength of the platform's ECDSA implementation.

## 5. Privacy guarantees

What the protocol can promise on its own, because it is a property of the bytes:

- A persona is a random key and a hash of it. Nothing in a document identifies the player, the character, the world, the account, the machine or the installation: no Content ID, no character name field, no world position, no local paths or ids, no telemetry or machine identifiers exist in any schema, and a test keeps the assembly free of the APIs that could reach them.
- A remote profile, revision and asset are random 128-bit identifiers chosen by the client, unrelated to the local Plate's id or any file name.
- An image reference carries a digest, a format, a size and pixel dimensions, never the file name.
- Text is carried exactly as authored, so the protocol never rewrites what a player wrote.
- What a document does reveal: `createdAt` and `issuedAt` are publishing times at one-second precision, and an image reference's pixel dimensions can identify a screenshot's screen resolution. Neither is hidden by the protocol; whether to coarsen timestamps or omit dimensions before publishing is for the sharing design (D5 and the viewer design).

What it deliberately does not do, matching the product's privacy direction: viewer identities, "last seen", location, social graphs, discovery and nearby scraping have no representation. They cannot be added by extending a schema; they would need a new document type, which is a visible design decision.

What only NETWORK1 and the backend can promise: that the key never leaves the machine (protected local storage), that a persona is not linked to game identifiers server-side, that shares are intentional, and that a retraction is honoured. `[updated 2026-09-28: D2 was approved in principle on 2026-09-28. The key may leave the machine only inside an encrypted, portable .afpersona backup that the player makes, which can restore the same identity on another computer without a hosted account; plaintext private key export is not permitted. The backup's encryption scheme, password policy, derivation parameters and recovery warnings await security approval (DecisionRegister.md). "Protected local storage" is platform-dependent: see NETWORK1_CryptoCompatibility.md, section 5.]`

Known open points, each an owner decision (section 11), none of them settled by the bytes as they are:

- An image reference's digest is specified over the source bytes. If NETWORK1 uploaded and digested the player's original files, photo metadata (EXIF location and device data, XMP edit history) would travel with them, and the digest would be a public fingerprint that links two personas publishing the same file. No milestone may upload originals or expose their digests before D5 is decided; NETWORK1 must not follow the older handoff wording that said otherwise.
- The remote `name` allows control and format characters and 32,000 scalars, where the local Plate name rules fold those characters and stop at 64. A modified client can publish names the official client cannot (D4). `[updated 2026-09-29: NETWORK2's N2-2 applied decision D4: a name is 1 to 64 scalars in at most 256 bytes, refusing control, directional and invisible format characters (ProtocolSpecification-v1.md, section 8.1.1). The 32,000-scalar limit still applies to other texts, which a later schema carries.]` `[updated 2026-09-30: the rule refuses only the invisible format characters D4 lists; L13 in DecisionRegister.md records the default-ignorable code points it allows. Schema 2 (N2-3a) limits an item text to 2,048 scalars.]`
- A signed snapshot is transferable proof that a persona published it, even after a retraction; whether viewers receive signed envelopes or server-attested content is undecided (D6). `[updated 2026-09-30: decided (D6): viewers receive server-checked content, so no signed snapshot reaches a viewer. The server keeps the verified bytes, and deletes them when the profile is retracted (D1).]`
- One key per install would link every character's published profiles; persona granularity is undecided (D3). `[updated 2026-09-28: D3 approved. An installation holds several independent personas, which the player selects and switches manually; one is active at a time for identity-dependent operations; personas are never bound automatically to characters, Content IDs, accounts or other game identifiers.]`
- User-scoped platform key protection (DPAPI) keeps a key from other Windows accounts and from offline copies, not from other plugins in the same game process or from software running as the user; its behaviour under Wine is unverified (D2, NETWORK1). `[updated 2026-09-28: DPAPI was measured working on native Windows. Wine's source shows its DPAPI derives the key from the user name and a constant, which is obfuscation rather than protection. That is a source finding, not a runtime test: see NETWORK1_CryptoCompatibility.md.]`

## 6. Remote model versus local model

The local `ProfileDocument` is a mutable, versioned JSON document with elements, Components, a background, canvas size, Basic-mode bookkeeping, a legacy owner Content ID field, asset GUIDs and extension data that carries unknown fields through. None of that goes on the wire. The remote `ProfileSnapshot` is immutable, binary, validated on construction, and holds only: profile id, revision id, creation time, name, and up to eight image references. Nothing in `AetherFrame.Protocol` references a local type, and nothing in the plugin references the protocol; the builder that turns a Plate into a snapshot will live on the plugin side (NETWORK1) and choose what to publish, field by field.

The layout a viewer would render is intentionally absent: it is the largest design question of sharing (what a remote viewer can render without the creator's fonts, assets and plugin version) and belongs with the renderer work, as a new payload schema version. Nothing in version 1 has to change to add it.

## 7. Resource limits

Enforced by the version 1 codecs (`ProtocolLimits`):

| Limit | Value | Enforced on |
|---|---|---|
| Serialized document | 1 MiB | the input, before parsing |
| Payload | 1 MiB − 140 B | the declared length |
| Text field | 32,000 scalar values (at most 128,000 bytes) | every text, both directions `[updated 2026-09-29: NETWORK2's N2-2 applied decision D4: a name is 1 to 64 scalars in at most 256 bytes, refusing control, directional and invisible format characters (ProtocolSpecification-v1.md, section 8.1.1). The 32,000-scalar limit still applies to other texts.]` `[updated 2026-09-29: schema 2 (N2-3a) allows 2,048 scalars an item text and 32,000 in all.]` `[updated 2026-09-30: the rule refuses only the invisible format characters D4 lists; L13 in DecisionRegister.md records the default-ignorable code points it allows.]` |
| Images per profile | 8 | the declared count, before any is read |
| Source image bytes | 8 MiB | each declaration |
| Source image dimension | 8,192 px | each declaration |
| Source image pixels | 20,000,000 | width × height, overflow-safe |
| Declared image bytes per profile | 40 MiB | the sum, overflow-safe |
| Timestamp | at most 253,402,300,799 | every timestamp |

Declared only, because no document can carry the state they depend on: 256 elements and 32 Components per profile (the layout schema does not exist yet), 4,096 px processed images (the server downscales), 20 profiles per persona, 10 active shares, 250 MiB per persona and 50 MiB in the first week (server accounting). Nothing enforces them; nothing pretends to. `[updated 2026-09-29: until L6 these were also constants in ProtocolLimits.FuturePolicy. They are now documentation only: their values are here, unchanged, and the specification's server obligation 8 points here. A backend enforces them with its own configuration (G4).]` `[updated 2026-09-29: the layout schema now exists (ProtocolSpecification-v1.md, section 8.5, NETWORK2's N2-3a) and carries its own limits: 2,048 items, 8 images totalling at most 33,554,432 pixels, and 32,000 text scalars.]`

## 8. Error behaviour

A reader either returns a fully verified and decoded document or throws `ProtocolException` with one of thirteen codes (specification, section 9). It never returns a partial result, never repairs input, never logs, and never includes content in a message. The order of checks is fixed so that cheap structural refusals come before the on-curve check and the signature computation, and the payload is decoded only after the signature verified; an input with several faults is refused for the first fault in reading order (specification, section 9.1). Errors on the writing side are the same type: a model outside the limits cannot be constructed, so a document that exists is always encodable and always within the limits, and `Sign` verifies what it produced before returning it, while letting an exception the signer itself throws pass through unchanged (a key store's failure is the caller's to handle, not a protocol refusal). These guarantees are about bytes and models; passing a null argument, a too-short destination span or a disposed signer is a programming error and throws the exception .NET uses for it.

## 9. NETWORK1 integration points

- `IPersonaKeyProvider` → `IPersonaSigner`: NETWORK1 implements a provider over a key generated once and kept in protected local storage (user-scoped platform protection), and decides how the active persona is chosen. `EcdsaPersonaSigner` wraps whatever `ECDsa` the provider produces. A signer is not thread-safe; serialize access to it. `[updated 2026-09-28: D3 (several independent personas, one active at a time, chosen manually) is approved, and D2 (encrypted, portable backups; no plaintext export) is approved in principle, so a persona key must stay exportable into an encrypted backup. The provider's shape (L6), the backup's security details, and which platform implementation is used (K1 to K3) are unresolved; see DecisionRegister.md and NETWORK1.md.]` `[updated 2026-09-29: L6 is APPROVED (Claude, under the owner's delegation of September 29, 2026). IPersonaKeyProvider is removed from the protocol, and key storage and the active persona are plugin policy. The backup's security details and K1 to K3 remain unresolved.]`
- A snapshot builder on the plugin side: `ProfileDocument` → `ProfileSnapshot`, choosing the name and the image references. What an image reference's digest covers, and whether original image bytes are ever uploaded or digested, is decision D5: until the image processing and privacy design is approved, the builder must not upload originals or publish digests of them. The protocol never sees the Plate.
- Transport: the document bytes are the request body (or base64 in JSON if an API prefers text); `SignedDocumentCodec.Verify` is the server's entry point, and the same assembly runs on both sides.
- Server obligations (specification, section 13): profiles keyed by (persona, profileId), identity from the key, nothing rewritten; and, as the baseline until decision D1, a retraction that is terminal for its profile, revision ids unique within a profile with byte-identical resubmission idempotent, and timestamps treated as client claims bounded against the server's own receipt time. Ordering by `createdAt` and `issuedAt` alone is not enough, since both are client clocks. Storage accounting and creating a persona from the key of its first document remain server policy. `[updated 2026-09-30: decision batch B settles what was a baseline here: D1 keeps a retraction terminal, with the deletions and keyed tombstone of S2; N2 keeps each accepted revision's id and hash and orders by a server sequence; N6 exempts retractions from the skew check (specification, section 13, rules 4 to 6).]`
- Not yet defined, and each needs its own signing tag when it is: request authentication or proofs, share grants, key rotation. `[updated 2026-09-29: the request proof is defined, with its own tag (specification, sections 5.1 and 14; decisions S1 and L8).]` Any future signing input starts with a one-byte length and an ASCII tag, and nothing AetherFrame signs is ever server-supplied bytes outside such a tagged input.

## 10. Decisions made without the owner

Each was chosen as the safest, simplest option that preserves flexibility; each is easy to revisit before NETWORK1 builds on it. Where the 2026-09-28 review found that a decision should be the owner's, it is listed again in section 11 with what depends on it.

1. **Binary rather than JSON** for the whole wire format. The brief allowed JSON as an outer transport if the signed bytes were built independently; a single canonical binary form removes every canonicalization question and every "which bytes were signed" question, and the same assembly serves a C# backend. A JSON presentation for debugging can be added later without touching what is signed.
2. **Two document types**, `ProfileSnapshot` and `ProfileRetraction`. A second real type was needed to prove domain separation; a retraction is the smallest one that is certainly needed, since a player must be able to unpublish with the same key-first mechanism and no session. If NETWORK1 prefers unpublishing through an authenticated request, the type can be dropped; it is a few dozen lines.
3. **Metadata-only snapshot**, no layout (section 6).
4. **Low-S required** at verification, not only produced at signing. Any third-party signer must normalize; this is standard and cheap.
5. **All-zero identifiers and digests refused**, so an uninitialized value can never be signed by mistake.
6. **Text limits counted in scalar values**, and no normalization of any kind (specification, section 2.3).
7. **No optional fields**: "not set" is the empty text; every other field is required.
8. **The plugin does not reference the assembly.** Doing so is a packaging change (four files in the release) that belongs to NETWORK1.
9. **Persona display names and RP metadata are not represented.** Nothing in the approved direction defined them precisely enough to freeze into a signed schema.

## 11. Open product decisions

These are the owner's to make. None is decided here, and none is frozen: the specification is a draft until they are, because D1, D4, D5 and D7 change what is signed or what a signature means. `[updated 2026-09-29: D4 and D5 are decided and written into the specification (NETWORK2's N2-2 and N2-3a; L13 may still narrow D4's rule, and N2-6 prepares the images D5 describes), and D7 is decided without changing what is signed: documents stay unbound, and the request proof binds each submission to a deployment (N2-3b).]` `[updated 2026-09-30: D1 and D6 are decided in decision batch B, neither changing what is signed: a retraction stays signed and terminal, and viewers receive server-checked content.]` For each, the baseline is what the code and the specification do today, so that NETWORK1 can be built against something definite; changing a baseline before the freeze costs a schema edit and a vector regeneration, nothing more.

`[updated 2026-09-28: D3 is approved and D2 is approved in principle (both 2026-09-28); every other decision below is still unresolved. The table records the NETWORK0 baseline as merged. The current status of each decision, the NETWORK1 additions and the point by which each must be decided are kept in DecisionRegister.md, which is the one place a decision is marked as approved.]`

| # | Decision | Baseline today | What changes if decided otherwise |
|---|---|---|---|
| D1 | What a retraction means, and whether it stays a signed document at all. | A signed `ProfileRetraction` withdraws the signer's own profile; the server obligations make it terminal (a permanent minimal record, no later snapshot of that profile) and key revisions by id with idempotent resubmission. | Unpublishing through an authenticated request instead removes the type (a few dozen lines) and its vectors. A non-terminal retraction needs an ordering rule that does not rest on client clocks, such as a server-assigned sequence. `[updated 2026-09-30: decided (D1, decision batch B): kept signed and terminal, with a keyed tombstone (S2); specification, section 13, rule 5.]` |
| D2 | Recovery from key loss. Without the key nothing can be retracted, and there is no account. | None; NETWORK1's key provider is undefined. | A passphrase-protected export or backup, a support takedown path, or accepting loss as final. Non-exportable platform keys prevent theft and backup alike. |
| D3 | Persona granularity: one per install, one per character, or the player's choice. | Undefined; the protocol does not care. | Affects only NETWORK1's key provider and UI. |
| D4 | The rules for schema-1 `name`. | Any text without U+0000, up to 32,000 scalars, nothing normalized. | Matching the local Plate name rules (`PlateNaming`: control and format characters folded to spaces, surrounding whitespace trimmed, then 1 to 64 UTF-16 code units, which counts a supplementary character as 2) changes the payload schema's rules and the unicode vector. Leaving it to server policy requires every consumer to sanitize before display. `[updated 2026-09-29: decided and applied, see DecisionRegister.md and ProtocolSpecification-v1.md, section 8.1.1.]` |
| D5 | What an image reference's digest covers, and whether metadata is stripped. | The source image bytes, whatever they contain. `[updated 2026-09-30: decided under the owner's delegation (D5, DecisionRegister.md): the digest covers the prepared copy the client uploads. N2-3a wrote it into the specification's section 8.2; the preparation itself is N2-6.]` | Digesting the bytes actually uploaded after a client re-encode (metadata gone) changes nothing on the wire but changes the snapshot builder; a per-snapshot salt to stop cross-persona joins would change the schema. |
| D6 | Whether viewers receive signed envelopes or server-attested content. | Undecided; only the server verifies in the baseline. | Signed envelopes give viewers proof of authorship that survives retraction. `[updated 2026-09-30: decided (D6, decision batch B): server-checked content.]` |
| D7 | Whether a document is bound to a deployment (production versus staging). | Not bound: a document verifies anywhere. | A deployment identifier in the signing input or the payload is cheap now and impossible to add to existing documents later. `[updated 2026-09-29: decided (D7, APPROVED (Claude, under the owner's delegation of September 29, 2026)). Documents stay unbound, and each submission is bound through a request proof's deployment name: specification, section 14, NETWORK2's N2-3b.]` |
| D8 | Whether the metadata-only schema 1 is ever exposed to players, or waits for the layout (schema 2). | Schema 1 exists and is test-only. | Exposing publishing before the layout exists makes schema-1 documents real and freezes their rules. |
| D9 | Persona display name, and how the assembly ships in the plugin package (fourth file versus linked sources). | Neither exists. | Neither affects NETWORK0's safety. |

## 12. Review findings kept open

The second independent review (2026-09-28, of `a96d7fd`) confirmed the security fixes and found no new code vulnerability. What it left open is listed here with the exact risk, the milestone responsible, and the decision the owner has to make. None of these is fixed by test or documentation hygiene, and none is silently decided here: in particular protocol version 1 is **not frozen** and no domain tag has been changed.

| Finding | Risk left open | Responsible milestone | Owner decision needed |
|---|---|---|---|
| N1 asset and revision ids not owner-scoped | A backend that keys images or revisions by id or digest alone lets one persona reference, probe or overwrite another's; the same class of bug as H1, for images. The specification states this only as open (section 8.4). | Backend, before any storage design | Whether ids are scoped to (persona) or (persona, profile), and whether a per-persona digest salt is wanted; both change section 13 and possibly the schema. `[updated 2026-09-29: decided (N1) and written into the specification, sections 8.4 and 13, by N2-3a.]` |
| N2 rollback by replaying a pruned revision | Rule 4 compares against stored revisions only and rule 6 orders by receipt time, so a replayed revision the server no longer holds becomes "latest". | Backend, with D1 | Whether a server keeps a permanent record of every revision id it ever accepted, or the protocol gains a monotonic counter in a later schema. `[updated 2026-09-30: decided (N2, decision batch B): a server keeps (revision id, document SHA-256) for every revision it accepted until the profile is retracted or removed; specification, section 13, rule 4.]` |
| N3 DRAFT is not visible in the bytes | Draft documents carry protocol version 1 and the tag `…SignedDocument.v1`. If the open decisions end up changing no bytes, draft documents remain valid version 1 documents forever, although the specification says the draft is not version 1. | Before NETWORK1 signs with real persona keys | Either a distinct draft tag or version until the freeze, or a commitment that the freeze changes the tag (invalidating everything signed so far). Not done here: changing a tag is a design decision, not hygiene. `[updated 2026-09-29: decided (N3) and applied by N2-2: drafts carry version 0x8001 and the -draft tag.]` |
| N5 `RemoteDocument.ProfileId` and `VerifiedDocument.Profile` assume every document is about a profile | Future document types (request proofs, share grants, persona statements) will not be, and the API would need a breaking change. | Before NETWORK1 code depends on the API | Whether to move the profile pair to the profile document types only, now, at the cost of the public API list. `[2026-09-29: moved. APPROVED (Claude, under the owner's delegation of September 29, 2026), DecisionRegister.md.]` |
| N6 retraction subject to the future-skew refusal | An owner whose clock runs fast cannot unpublish. | Backend, with D1 | Exempt retractions from the skew rule, or bound them differently. `[updated 2026-09-30: decided (N6, decision batch B): retractions are exempt; specification, section 13, rule 6.]` |
| N7 nothing obliges a consumer to fold or escape `name` | A viewer or log that displays the name raw shows control and format characters the local editor never allows. | With D4 | Whether the payload rule tightens, or every consumer sanitizes. |
| L2 conformance vectors thinner than the rule set | A second implementation missing the text limits (a name one scalar or one byte over the maximum) passes every committed vector; those cases are 32 to 128 KB and live in unit tests only. The other per-field limits now have vectors (12 added). `[updated 2026-09-30: NETWORK2's N2-2 applied D4, so a name is at most 64 scalars in 256 bytes, and rejected vectors cover a name one scalar over, one byte over, and a declared length over the byte limit. Only the general 32,000-scalar text limit, which no version 1 field reaches, stays in the unit tests (specification, sections 2.3 and 11).]` | NETWORK1, when a second implementation exists | Whether to commit the large vectors or a construction-rule form for them. |
| L4 a public-only key is accepted by `EcdsaPersonaSigner` | Detected at the first `Sign`, as `InvalidKey`; nothing wrong is produced. Probing at construction would mean signing a probe message. | NETWORK1 key provider | Whether the provider guarantees a private half, or the signer probes. |
| L6 `FuturePolicy` and `IPersonaKeyProvider` in the public API | Policy numbers compile into callers as constants; the provider's synchronous getter will not fit DPAPI loading, prompting or several personas. Both are marked provisional in their documentation; neither was moved. | NETWORK1 | Where the policy numbers live, and the provider's real shape (D3). `[2026-09-29: both removed from the protocol. APPROVED (Claude, under the owner's delegation of September 29, 2026), DecisionRegister.md.]` |
| L8 signing-context rules non-normative | The rules for new tags (one-byte length, ASCII tag, never server-supplied bytes outside a tagged input, never a truncated persona id) are only in this document. | Before any second signing context | Move them into the specification's versioning section when the first new context is designed. `[updated 2026-09-29: moved into the specification, section 5.1, with the request proof, the first new context (N2-3b; L8, APPROVED (Claude, under the owner's delegation of September 29, 2026)). Rule 2 there keeps "never a truncated persona id" as the full 65-byte key.]` |
| L9 release workflow gated on protocol tests | A protocol test failure blocks a plugin release. `[corrected 2026-09-28: this row also said the ubuntu leg had never executed the suite. It has since run green twice (section 13).]` | Owner | Keep the step in `release.yml` or move it to `build.yml` only (still unresolved). |

## 13. Verification status

Everything in this document and the handoff was first verified on Windows 11 x64 (.NET 10.0.12, Windows CNG).

`[corrected 2026-09-28, after the merge: this section said the Linux leg had never run and that Linux compatibility was a claim, not a result. The protocol suite has since run green on ubuntu-24.04 twice.]`

GitHub Actions "Build and test" (`build.yml`), .NET runtime 10.0.12 and SDK 10.0.401 on both legs:

| Run | Commit | Leg | Plugin tests | Release tooling tests | Protocol tests |
|---|---|---|---|---|---|
| 36446719752 (pull request #20, the first Linux run of the protocol suite) | `dcd56f9` | ubuntu-24.04 (image 20260920.314.1) | 2739 passed | 439 passed | 153 passed |
| 36446719752 | `dcd56f9` | windows-2022 | 2739 passed | 439 passed | 153 passed |
| 36474945050 (push of the merge) | `6384db6` | ubuntu-24.04 (image 20260920.314.1) | 2739 passed | 439 passed | 153 passed |
| 36474945050 | `6384db6` | windows-2022 | 2739 passed | 439 passed | 153 passed |

So the suite's Linux behaviour (the race tests on fewer cores, the platform DER lengths, the exception mapping) is now a result for native Linux .NET with OpenSSL. The independent Python and OpenSSL 3.5.7 checker from the reviews had already reproduced the vectors.

What these runs do not show: how the Windows build of .NET behaves under Wine, Proton or macOS compatibility layers. Players on those systems run Dalamud on the Windows .NET runtime inside Wine, where cryptography comes from Wine's `ncrypt`, `bcrypt` and `crypt32` rather than OpenSSL. A native Linux result says nothing about that environment. See [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md).
