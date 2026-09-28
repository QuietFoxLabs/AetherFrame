# NETWORK0: the remote protocol foundation

NETWORK0 is the first networking milestone of AetherFrame, and it builds no network. It is the isolated protocol, identity, signing, verification, serialization and hostile-input foundation that the later milestones (NETWORK1, the client that talks to a backend; the backend itself) depend on, so that those milestones can be built without revisiting how bytes are framed, how a persona is identified, or what a signature covers. This document explains what was built and why; [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) is the normative wire format; [NETWORK0_HANDOFF.md](NETWORK0_HANDOFF.md) is the morning handoff with results and open questions.

Everything described here lives in the `AetherFrame.Protocol` assembly and its tests. **The plugin does not reference it, nothing here opens a connection, reads or writes a file, or needs an account, and the plugin's behaviour is unchanged.**

## 1. What NETWORK0 is for

AetherFrame is local first: Plates, Templates, images, import, export and the Plate Library never depend on an account or a server, and an online service must never hold the only copy of anything a player made. Sharing, when it arrives, is an optional layer on top: a player publishes a copy of a Plate and later withdraws it. For that layer to be trustworthy, three things must be right before any server exists:

1. **Identity.** Who published something must be provable without an account, a login or a game identifier. NETWORK0 makes a persona a P-256 key pair; its public identity is a hash of the public key and nothing else.
2. **Authenticity.** What a viewer receives must be exactly what the creator signed, with no way to alter it, reuse the signature elsewhere, or pass it off as another kind of statement. NETWORK0 signs canonical bytes with domain separation, and refuses anything that is not the one canonical form.
3. **Safety against hostile input.** Every byte that will one day come from a server is untrusted. NETWORK0's readers check every length against its limit before allocating, fail closed on anything unknown, and refuse every malformed input with a typed error and nothing else.

NETWORK0 also draws the line between the local model and the remote model: what goes on the wire is a separate, deliberately narrower set of types that share no code with the local Plate.

## 2. Architecture

```
AetherFrame.Protocol            net10.0, BCL only, no packages, no Dalamud, no plugin reference
  ProtocolConstants             magic, version, domain separation tags
  ProtocolLimits                every enforced limit; FuturePolicy declares the server-side ones
  ProtocolError / Exception     the closed set of refusals
  Encoding/                     CanonicalWriter, CanonicalReader, ProtocolText
  Identity/                     PersonaPublicKey, PersonaId, ProfileId, RevisionId, AssetId, P256Curve
  Signing/                      SigningInput, ProtocolSignature, IPersonaSigner, EcdsaPersonaSigner, SignatureVerifier
  Documents/                    DocumentType, RemoteDocument, SignedDocumentCodec, VerifiedDocument
  Remote/                       ProfileSnapshot, ImageReference, ImageFormat, ProfileRetraction
  Integration/                  IPersonaKeyProvider (the seam NETWORK1 fills; nothing implements it)
AetherFrame.Protocol.Tests      xunit; vectors, canonicalization, adversarial, fuzz, boundary, benchmark
```

Why a standalone project rather than a folder in the plugin: the plugin's release package is exactly three files (`docs/CustomRepository.md`), and the release tooling refuses anything else. A plugin reference would add a fourth assembly, which is a packaging decision for NETWORK1 (link the sources in, or change the package rule), not something to slip in with the protocol. Standalone also means the same assembly can be used unchanged by a backend, so client and server can never disagree about the bytes. The project has no NuGet packages and references only `System.*`: a test asserts that, and that nothing from `System.Net`, the file system or process APIs is referenced.

Why the plugin's own tests project is not reused: `AetherFrame.Tests` compiles the plugin's Dalamud-free sources by linking them; the protocol is a separate assembly with its own public surface, and its tests check that surface (an approved public API list is committed).

### 2.1 Data flow

Publishing (a NETWORK1 client, not built): build a `ProfileSnapshot` from the local Plate through a builder that lives on the plugin side; `SignedDocumentCodec.Sign(snapshot, signer)` encodes the payload, builds the signing input, signs, and lays out the document. The only thing the plugin will need from its side is an `IPersonaSigner`, obtained from an `IPersonaKeyProvider` that keeps the key in protected local storage.

Receiving (a viewer or a server): `SignedDocumentCodec.Verify(bytes)` checks the framing, the version, the type, the lengths and the trailing bytes, then the key, then the signature, and only then decodes the payload. The result is a `VerifiedDocument` whose persona is derived from the key that verified; the caller cannot supply one.

### 2.2 The signing approach, exactly

- The **wire format is one canonical binary encoding**: big-endian fixed-width integers, four-byte length prefixes, strict UTF-8 text, closed enumerations, no floats, no optional fields, sets sorted on the wire. There is no JSON anywhere in the signing path, so no canonical-JSON scheme, property order or number formatting can enter into what is signed. The bytes that are transmitted are the bytes that are signed.
- The **signing input** is `tag ‖ version ‖ type ‖ public key ‖ length ‖ payload`, with the tag `AetherFrame.Protocol.SignedDocument.v1` and its length in front. Binding the version and the type stops cross-version and cross-type reuse; binding the key stops key substitution (including duplicate-signature-key-selection constructions, where an attacker fits a new key to an existing signature); the tag stops reuse against any other signing context AetherFrame introduces later.
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
| Invalid curve material (off-curve or compressed points, coordinates at or above p, other curves) | The protocol's own on-curve check before the platform sees the key, so behaviour is the same on Windows and Linux. |
| Oversized or hostile lengths driving allocation | The whole document is capped at 1 MiB before parsing; every length and count is compared with its limit before allocation; a reader never allocates more than its input. Measured: a hostile length costs about 700 bytes and 2 µs to refuse. |
| Truncation, trailing bytes, nested-count abuse, malformed UTF-8, U+0000 | Refused with a typed error at the exact boundary; covered by truncation at every offset, a flipped bit at every position, and seeded fuzz campaigns. |
| Exceptions leaking bytes or crashing the host | The only exception type is `ProtocolException`; messages name fields and numbers, never content; tests assert no other exception type escapes for tens of thousands of hostile inputs. |
| A verified object changed afterwards (time-of-check to time-of-use) | Verification and decoding work on one private copy of the payload; every model and result type is immutable, and list views cannot reach their storage. |
| Private key exposure through the protocol | No API exports, serializes, logs or formats private material; the signer signs only protocol-built signing inputs; a test asserts the public surface returns no platform key type. |

Out of scope for NETWORK0, and named so nothing is assumed: transport security, server compromise, replay of a whole document (a server decides how it orders snapshots and retractions; section 9), denial of service at the network layer, moderation, key loss and rotation, and the strength of the platform's ECDSA implementation.

## 5. Privacy guarantees

What the protocol can promise on its own, because it is a property of the bytes:

- A persona is a random key and a hash of it. Nothing in a document identifies the player, the character, the world, the account, the machine or the installation: no Content ID, no character name field, no world position, no local paths or ids, no telemetry or machine identifiers exist in any schema, and a test keeps the assembly free of the APIs that could reach them.
- A remote profile, revision and asset are random 128-bit identifiers chosen by the client, unrelated to the local Plate's id or any file name.
- An image reference carries a digest, a format, a size and pixel dimensions, never the file name.
- Text is carried exactly as authored, so the protocol never rewrites what a player wrote.

What it deliberately does not do, matching the product's privacy direction: viewer identities, "last seen", location, social graphs, discovery and nearby scraping have no representation. They cannot be added by extending a schema; they would need a new document type, which is a visible design decision.

What only NETWORK1 and the backend can promise: that the key never leaves the machine (protected local storage), that a persona is not linked to game identifiers server-side, that shares are intentional, and that a retraction is honoured.

## 6. Remote model versus local model

The local `ProfileDocument` is a mutable, versioned JSON document with elements, Components, a background, canvas size, Basic-mode bookkeeping, a legacy owner Content ID field, asset GUIDs and extension data that carries unknown fields through. None of that goes on the wire. The remote `ProfileSnapshot` is immutable, binary, validated on construction, and holds only: profile id, revision id, creation time, name, and up to eight image references. Nothing in `AetherFrame.Protocol` references a local type, and nothing in the plugin references the protocol; the builder that turns a Plate into a snapshot will live on the plugin side (NETWORK1) and choose what to publish, field by field.

The layout a viewer would render is intentionally absent: it is the largest design question of sharing (what a remote viewer can render without the creator's fonts, assets and plugin version) and belongs with the renderer work, as a new payload schema version. Nothing in version 1 has to change to add it.

## 7. Resource limits

Enforced by the version 1 codecs (`ProtocolLimits`):

| Limit | Value | Enforced on |
|---|---|---|
| Serialized document | 1 MiB | the input, before parsing |
| Payload | 1 MiB − 140 B | the declared length |
| Text field | 32,000 scalar values (at most 128,000 bytes) | every text, both directions |
| Images per profile | 8 | the declared count, before any is read |
| Source image bytes | 8 MiB | each declaration |
| Source image dimension | 8,192 px | each declaration |
| Source image pixels | 20,000,000 | width × height, overflow-safe |
| Declared image bytes per profile | 40 MiB | the sum, overflow-safe |
| Timestamp | at most 253,402,300,799 | every timestamp |

Declared only, as `ProtocolLimits.FuturePolicy`, because no document can carry the state they depend on: 256 elements and 32 Components per profile (the layout schema does not exist yet), 4,096 px processed images (the server downscales), 20 profiles per persona, 10 active shares, 250 MiB per persona and 50 MiB in the first week (server accounting). Nothing enforces them; nothing pretends to.

## 8. Error behaviour

A reader either returns a fully verified and decoded document or throws `ProtocolException` with one of thirteen codes (specification, section 9). It never returns a partial result, never repairs input, never logs, and never includes content in a message. The order of checks is fixed so that cheap structural refusals come before the on-curve check and the signature computation, and the payload is decoded only after the signature verified. Errors on the writing side are the same type: a model outside the limits cannot be constructed, so a document that exists is always encodable and always within the limits.

## 9. NETWORK1 integration points

- `IPersonaKeyProvider` → `IPersonaSigner`: NETWORK1 implements a provider over a key generated once and kept in protected local storage (user-scoped platform protection), and decides how the active persona is chosen. `EcdsaPersonaSigner` wraps whatever `ECDsa` the provider produces. A signer is not thread-safe; serialize access to it.
- A snapshot builder on the plugin side: `ProfileDocument` → `ProfileSnapshot`, choosing the name and the images (asset id per remote copy, source digest, format, size, dimensions). The protocol never sees the Plate.
- Transport: the document bytes are the request body (or base64 in JSON if an API prefers text); `SignedDocumentCodec.Verify` is the server's entry point, and the same assembly runs on both sides.
- Server policy the protocol leaves open on purpose: ordering of snapshots and retractions for one profile (a retraction must not take down a snapshot created after it; process per profile in `createdAt` and `issuedAt` order and refuse regressions), uniqueness of profile and revision ids per persona, storage accounting, and creating a persona from the key of its first document.
- Not yet defined, and each needs its own signing tag when it is: request authentication or proofs, share grants, key rotation.

## 10. Decisions made without the owner

Each was chosen as the safest, simplest option that preserves flexibility; each is easy to revisit before NETWORK1 builds on it.

1. **Binary rather than JSON** for the whole wire format. The brief allowed JSON as an outer transport if the signed bytes were built independently; a single canonical binary form removes every canonicalization question and every "which bytes were signed" question, and the same assembly serves a C# backend. A JSON presentation for debugging can be added later without touching what is signed.
2. **Two document types**, `ProfileSnapshot` and `ProfileRetraction`. A second real type was needed to prove domain separation; a retraction is the smallest one that is certainly needed, since a player must be able to unpublish with the same key-first mechanism and no session. If NETWORK1 prefers unpublishing through an authenticated request, the type can be dropped; it is a few dozen lines.
3. **Metadata-only snapshot**, no layout (section 6).
4. **Low-S required** at verification, not only produced at signing. Any third-party signer must normalize; this is standard and cheap.
5. **All-zero identifiers and digests refused**, so an uninitialized value can never be signed by mistake.
6. **Text limits counted in scalar values**, and no normalization of any kind (specification, section 2.3).
7. **No optional fields**: "not set" is the empty text; every other field is required.
8. **The plugin does not reference the assembly.** Doing so is a packaging change (four files in the release) that belongs to NETWORK1.
9. **Persona display names and RP metadata are not represented.** Nothing in the approved direction defined them precisely enough to freeze into a signed schema.
