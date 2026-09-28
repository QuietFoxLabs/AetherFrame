# AetherFrame remote protocol, version 1 (DRAFT)

**Status: DRAFT.** This document describes the wire format that `AetherFrame.Protocol` reads and writes today, precisely enough to build a second implementation, in any language, that produces byte-identical documents and verifies the same signatures without reading the C# source. It is not yet frozen. Version 1 becomes final only when the open product decisions in [NETWORK0.md](NETWORK0.md), "Open product decisions", are settled, because some of them change bytes that are signed (the `name` rules, D4; what an image digest covers, D5; whether a document is bound to a deployment, D7; what a retraction means, D1). Until then every document produced under this draft is a test document: no server accepts one, and none is to be treated as a version 1 document after the freeze. What is not expected to change: the envelope, the signing input and its tag, the signature form, the key format and the persona identity derivation.

The committed test vectors (`AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json`, section 11) are the check that a second implementation is right. [NETWORK0.md](NETWORK0.md) explains why the protocol looks like this; this document only says what it is. Section 13 states what a server that accepts these documents is obliged to do with them.

Version 1 is the NETWORK0 foundation: signed, identity-bearing documents. It has no transport, no request or response messages, no encryption and no discovery. Everything else a server or a client does with these bytes is outside this document.

## 1. Notation

- `u8`, `u16`, `u32`, `u64`: unsigned integers of 1, 2, 4 and 8 bytes, **big-endian** (most significant byte first), fixed width, never variable-length.
- `bytes[n]`: exactly `n` bytes whose length the schema fixes.
- `bytes`: a `u32` byte length followed by that many bytes.
- `text`: `bytes` holding UTF-8 (section 2.3).
- `list<T>`: a `u32` count followed by that many `T` values, in the order the schema states.
- `‖`: concatenation. Hex is lowercase.

Every structure is a plain concatenation of its fields in the order listed: there are no alignment bytes, no field tags, no optional fields and no terminators. A field is never omitted; "not set" is represented by the field's empty or zero value where the schema allows one, and nowhere else.

## 2. Primitive rules

### 2.1 Integers

Fixed width, big-endian, no sign. A value is read exactly as its width says; a value outside the range its field allows is refused (`InvalidValue` or `LimitExceeded`, section 8), never clamped or wrapped.

### 2.2 Byte strings and counts

A `bytes` value or a `list<T>` starts with a `u32` length or count. A reader compares that number with the field's maximum **before** it looks at how much input remains and before it allocates anything: a length over the maximum is `LimitExceeded`; a length within the maximum but longer than the remaining input is `Truncated`. Nothing is ever allocated in proportion to a declared length that has not passed its maximum.

### 2.3 Text

A `text` is UTF-8 with these rules, and no others:

1. The encoding is valid UTF-8 as defined by the Unicode standard: no overlong sequences, no encoded surrogates (U+D800 to U+DFFF), no code points above U+10FFFF, no truncated sequences. Anything else is `InvalidText`.
2. No byte order mark is written; a leading U+FEFF is content, not a mark, and is kept.
3. The scalar value U+0000 is never present (`InvalidText`). Everything else, including control characters, line breaks of any kind, format characters and unassigned code points, is allowed.
4. At most **32,000 Unicode scalar values** (code points), counted after decoding; a text over that is `LimitExceeded`. The byte length is therefore at most **128,000**, and a reader refuses a declared byte length over 128,000 before decoding.
5. **Nothing is normalized.** No Unicode normalization form is applied, line endings are not converted, whitespace is not trimmed, and case is not changed. `Café` in NFC and `Café` in NFD are different texts with different bytes; `a\r\nb` and `a\nb` are different texts. A text decodes to exactly the scalar values that were encoded.

An empty text is a `u32` zero and no bytes.

### 2.4 Enumerations

An enumeration is a `u8`. Each enumeration lists the values it allows; any other value is `InvalidValue`. Enumerations are closed: a reader never maps an unknown value to a default or a nearest known value.

### 2.5 Timestamps

A timestamp is a `u64` count of whole seconds since 1970-01-01T00:00:00Z (Unix time), with no time zone and no fraction. The value is at most **253,402,300,799** (9999-12-31T23:59:59Z); a larger value is `InvalidValue`. Local time never appears on the wire: a client converts its instant to Unix seconds before encoding, so two clients in different time zones describing the same instant produce the same bytes.

### 2.6 Opaque identifiers

Profile, revision and asset identifiers are `bytes[16]` chosen at random by the publishing client (a cryptographically secure generator). The all-zero value is not an identifier and is refused (`InvalidValue`).

Each has one text form: a four-character prefix and the 32 **lowercase** hexadecimal digits of the 16 bytes.

| Identifier | Prefix | Text form example |
|---|---|---|
| Profile | `prf_` | `prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1` |
| Revision | `rev_` | `rev_b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2` |
| Asset | `ast_` | `ast_c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3` |

A parser of the text form accepts exactly 36 characters, the prefix, and lowercase digits; uppercase digits, whitespace, another prefix or the all-zero value are refused. Identifiers are ordered by comparing their 16 bytes as unsigned big-endian numbers, which is the same as comparing them byte by byte from the first.

### 2.7 Sets

Where a schema says a list is a **set**, the encoder writes the items in **strictly ascending order** of the stated key and the decoder refuses any other order or a repeated key (`NotCanonical`). A set therefore has exactly one encoding whatever order it was built in.

## 3. Public keys

A persona's public key is a point on the NIST P-256 curve (secp256r1, prime256v1; OID 1.2.840.10045.3.1.7) in **uncompressed** form: `bytes[65]` = `0x04 ‖ X ‖ Y`, with X and Y as 32-byte big-endian unsigned integers.

A reader accepts a key only when all of the following hold; otherwise the key is `InvalidKey`:

1. The length is 65 and the first byte is `0x04`. Compressed points (`0x02`, `0x03`) and any other prefix are refused.
2. `X < p` and `Y < p`.
3. `Y² ≡ X³ − 3X + b (mod p)`, that is, the point is on the curve.

The curve parameters, for a check that does not depend on the platform:

```
p = ffffffff00000001000000000000000000000000ffffffffffffffffffffffff
a = p − 3
b = 5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b
n = ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551   (group order; cofactor 1)
G = (6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296,
     4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5)
```

Because the cofactor is 1, every point on the curve other than the point at infinity (which has no uncompressed encoding) is in the prime-order group; no further subgroup check is needed.

Two keys are equal when their 65 bytes are equal. The point `(X, p − Y)` is a different key with a different identity.

Private keys never appear in this protocol.

## 4. Persona identity

A persona's identity is derived from its public key and nothing else:

```
PersonaIdInput = u8(33) ‖ "AetherFrame.Protocol.PersonaId.v1" ‖ u8(0x01) ‖ PublicKey[65]
PersonaId      = SHA-256(PersonaIdInput)                                  (32 bytes)
```

- The tag is the 33 ASCII bytes `4165746865724672616d652e50726f746f636f6c2e506572736f6e6149642e7631`, preceded by its length as one byte (`0x21`).
- `0x01` is the key format code for an uncompressed P-256 point. It is the only code in version 1.
- The input is 1 + 33 + 1 + 65 = 100 bytes.

Text form: `psn_` followed by the 64 lowercase hex digits of the digest, 68 characters in all. A parser accepts exactly that; the all-zero digest is refused.

The identity carries nothing about the player, the character, the game account or the machine, and any party holding the public key can recompute it. A persona is therefore known to others by this value, or by the key it is derived from, and by nothing else.

## 5. Signing input

Every signature in version 1 is over a **signing input** built as follows, where `Payload` is the exact payload bytes of the document (section 7):

```
SigningInput = u8(38) ‖ "AetherFrame.Protocol.SignedDocument.v1"
             ‖ u16(1)                      protocol version
             ‖ u8(documentType)            section 6.1
             ‖ PublicKey[65]               the signer's key, section 3
             ‖ u32(len(Payload)) ‖ Payload
```

- The tag is the 38 ASCII bytes `4165746865724672616d652e50726f746f636f6c2e5369676e6564446f63756d656e742e7631`, preceded by its length as one byte (`0x26`).
- `Digest = SHA-256(SigningInput)` is the value ECDSA signs and verifies.

The signing input binds the protocol version, the document type, the key and the payload. A signature therefore verifies for exactly one (version, type, key, payload) and for nothing else: not for the same payload under another type, not for another version, not with another key, and not over the payload bytes alone. The tag separates document signatures from every other signature AetherFrame may ever define; any future signing context (a request proof, another document family) must use a different tag.

The signing input is never transmitted; both sides rebuild it from the document.

## 6. Signatures

A signature is ECDSA over P-256 with SHA-256, encoded as `bytes[64]` = `r ‖ s`, each a 32-byte big-endian unsigned integer (IEEE P1363 fixed-field concatenation, not DER).

A reader accepts a signature only when:

1. The length is 64.
2. `1 ≤ r ≤ n − 1` and `1 ≤ s ≤ n − 1`.
3. `s ≤ floor(n / 2)` (**low-S**). `floor(n / 2) = 7fffffff800000007fffffffffffffffde737d56d38bcf4279dce5617e3192a8`.

A signature failing any of these is `InvalidSignature` and is never passed to the verification computation. A signature passing them is then verified against the digest and the key; failure is `SignatureMismatch`.

The low-S rule gives every signature exactly one encoding: of the two ECDSA signatures `(r, s)` and `(r, n − s)` over the same input, only the one with the smaller `s` is valid here. A signer that obtains a high `s` from its ECDSA implementation replaces it with `n − s` before emitting the signature. ECDSA itself is randomized (or deterministic per RFC 6979 if an implementation prefers); the protocol does not require a particular nonce scheme, only a canonical encoding of the result.

Verification, from the definition (the test suite contains an independent implementation of exactly this): with `e = Digest` as an integer, `w = s⁻¹ mod n`, `u1 = e·w mod n`, `u2 = r·w mod n`, `R = u1·G + u2·Q`; the signature is valid when `R` is not the point at infinity and `R.x mod n = r`.

## 7. Signed document

A signed document is the only top-level structure of version 1. It is at most **1,048,576 bytes** (1 MiB) in all.

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 4 | magic | ASCII `AFPD` (`41 46 50 44`) |
| 4 | 2 | protocolVersion | `u16` = 1 |
| 6 | 1 | documentType | `u8`, section 7.1 |
| 7 | 65 | personaPublicKey | section 3 |
| 72 | 4 | payloadLength | `u32`, 1 to 1,048,436 |
| 76 | payloadLength | payload | section 8 |
| 76 + payloadLength | 64 | signature | section 6 |

The overhead is 140 bytes, so the payload is at most 1,048,436 bytes. The document ends immediately after the signature: nothing may follow it.

### 7.1 Document types

| Value | Type | Payload |
|---|---|---|
| 1 | ProfileSnapshot | section 8.1 |
| 2 | ProfileRetraction | section 8.3 |

Any other value is `UnknownDocumentType`.

### 7.2 Verification procedure

A reader performs these steps in this order and stops at the first failure with the error named. Steps 1 to 7 cost a few comparisons; only afterwards does any expensive work happen, and the payload is decoded only after the signature has verified.

1. If the input is longer than 1,048,576 bytes: `LimitExceeded`.
2. Read the magic; if fewer than 4 bytes remain: `Truncated`; if they are not `AFPD`: `InvalidFraming`.
3. Read `protocolVersion`; if it is not 1: `UnsupportedVersion`.
4. Read `documentType`; if it is not listed in 7.1: `UnknownDocumentType`.
5. Read the 65 key bytes (not yet validated).
6. Read `payloadLength`; if over 1,048,436: `LimitExceeded`; if more than the remaining input: `Truncated`; if 0: `InvalidLength`. Read the payload.
7. Read the 64 signature bytes; then, if any input remains: `TrailingBytes`. (At each read, too little input is `Truncated`.)
8. Validate the key (section 3): `InvalidKey`.
9. Validate the signature's form (section 6, rules 1 to 3): `InvalidSignature`.
10. Build the signing input (section 5) from the version, type, key and payload as read, and verify the signature against the key: `SignatureMismatch`.
11. Decode the payload as the schema for the document type (section 8), which must consume it exactly: any decoding error as listed there.

The persona of a verified document is the identity (section 4) of the key in the document. A reader never accepts a persona identity from anywhere else and never compares the document against an identity a caller supplied before the signature has verified.

Implementation note: a reader takes a private copy of the input before step 2 and performs every step on the copy, so that a value it checked is the value it uses even when the caller's buffer changes meanwhile. The same holds for a key or a signature parsed on its own.

### 7.3 Signing procedure

A writer encodes the payload (section 8), builds the signing input (section 5) with its own public key, signs the digest, normalizes the signature to low-S (section 6), and lays out the document per the table above. A writer that cannot produce a payload within the limits produces no document.

## 8. Payloads

Each payload starts with its own `u16` schema version, independent of the protocol version: a later schema of the same document type can be introduced while the envelope stays at protocol version 1. A reader refuses any schema version it does not implement (`UnsupportedVersion`). Version 1 defines schema 1 of each type.

### 8.1 ProfileSnapshot, schema 1

One immutable published revision of a remote profile.

| Field | Type | Rules |
|---|---|---|
| schemaVersion | `u16` | 1 |
| profileId | `bytes[16]` | not all zero |
| revisionId | `bytes[16]` | not all zero |
| createdAt | `u64` | timestamp, section 2.5 |
| name | `text` | section 2.3; may be empty |
| images | `list<ImageReference>` | a **set** keyed by `assetId` (section 2.7); at most 8 |

After `images` the payload ends; a byte more is `TrailingBytes`. The `byteLength` values of all images summed must not exceed 41,943,040 (40 MiB): `LimitExceeded`. The sum is computed in arithmetic that cannot overflow (8 values of at most 8,388,608 each).

### 8.2 ImageReference

| Field | Type | Rules |
|---|---|---|
| assetId | `bytes[16]` | not all zero |
| sha256 | `bytes[32]` | SHA-256 of the source image bytes; not all zero |
| format | `u8` | 1 = PNG, 2 = JPEG, 3 = WebP; anything else `InvalidValue` |
| byteLength | `u64` | 1 to 8,388,608 (8 MiB); 0 is `InvalidValue`, more is `LimitExceeded` |
| width | `u32` | 1 to 8192; 0 is `InvalidValue`, more is `LimitExceeded` |
| height | `u32` | 1 to 8192, as width |
| | | `width × height ≤ 20,000,000`: `LimitExceeded` |

The format is what the client sniffed from the image bytes, never a file extension. No file name, path or local identifier is carried.

### 8.3 ProfileRetraction, schema 1

The withdrawal of a remote profile from publication: every revision of the profile is to be taken down.

| Field | Type | Rules |
|---|---|---|
| schemaVersion | `u16` | 1 |
| profileId | `bytes[16]` | not all zero |
| issuedAt | `u64` | timestamp, section 2.5 |

After `issuedAt` the payload ends.

### 8.4 Profile identity and ownership

A remote profile is identified by the pair **(persona, profileId)**: the persona whose key signed the document (section 7.2) together with the profile id the payload carries. A profile id alone identifies nothing. Two personas may use the same 16 bytes for unrelated profiles, and a document can only ever refer to a profile of the persona that signed it:

- A `ProfileSnapshot` signed by persona P is a revision of the profile (P, profileId), whether or not that profile existed before.
- A `ProfileRetraction` signed by persona P withdraws the profile (P, profileId) and nothing else. A retraction that names a profile id another persona uses is a retraction of P's own profile of that id (which may never have been published); it has no effect on any other persona's profile.

Only the owner of a profile can therefore publish to it or retract it, because only the owner holds the key that signs for it. A server, a viewer or any other consumer keys its records by the pair and never by the profile id alone (section 13). The library exposes the pair as `RemoteProfileKey` on every verified document. The `serverObligations` test vectors (section 11) are valid documents signed by one persona that carry another persona's profile id: each verifies, as its signer, and is about the signer's profile only.

## 9. Errors

Every refusal is one of these codes. A reader that wants to react to the kind of failure switches on the code; the accompanying message names fields, lengths and limits and never repeats key, signature or payload bytes.

| Code | Meaning |
|---|---|
| InvalidFraming | The input does not start with the document magic. |
| UnsupportedVersion | The protocol version or a payload schema version is not one this reader implements. |
| UnknownDocumentType | The document type is not listed in 7.1. |
| Truncated | The input ended before a declared value was complete. |
| TrailingBytes | Input continues after the last value of the document or of a payload. |
| LimitExceeded | A length, count, size or total is over its limit. |
| InvalidLength | A length the format forbids, such as an empty payload. |
| InvalidKey | The public key is not an uncompressed point on P-256. |
| InvalidSignature | The signature is not 64 bytes with r and s in range and s in the low half. |
| SignatureMismatch | The signature is well formed but does not verify over the document with its key. |
| InvalidText | A text is not valid UTF-8 or contains U+0000. |
| InvalidValue | A value outside what its field allows: an unknown enumeration code, an all-zero identifier, a zero dimension, a timestamp out of range. |
| NotCanonical | The input is well formed but not the one canonical encoding of its content, such as an unsorted set. |

A reader never returns a partially decoded document: either every step of section 7.2 succeeds or nothing is produced.

### 9.1 Input with several faults

A reader checks each rule at the earliest point in reading order at which it can be checked, and refuses the input for the first rule that fails:

- the envelope's rules in the order of section 7.2;
- a fixed-width field's rules as soon as the field has been read (an all-zero identifier, an unknown enumeration code, a timestamp out of range, a zero or over-limit dimension);
- a length's or count's limit as soon as the length has been read, before the bytes or items it announces are looked at;
- a text's byte limit, then its UTF-8 validity, then U+0000, then its scalar limit;
- a set's ordering rule as soon as an item's key has been read, before the rest of the item;
- a rule over several fields of one item (the pixel product) when the last of them has been read;
- after the last field: trailing bytes, then the rules over the whole payload (the total of the image byte lengths).

The rejected test vectors each contain one fault. The library's adversarial tests cover the order for inputs with two.

## 10. Versioning policy

- **Protocol version** (envelope, `u16` at offset 4): changes only when the envelope layout, the signing input or the signature scheme changes. A reader implements a closed set of versions and refuses the rest; version 1 readers refuse everything but 1. A new version uses a **new signature domain tag** (for example `...SignedDocument.v2`) as well as a new number, so a version 1 signature can never verify under version 2 even if the layouts coincided.
- **Document type** (`u8` at offset 6): new types get new codes; codes are never reused or renumbered. A reader refuses codes it does not know. The type is part of the signing input, so a signature never carries across types.
- **Payload schema version** (`u16` at the start of each payload): a type's payload can evolve without touching the envelope. A reader implements a closed set of schemas per type and refuses the rest. Fields are never added to an existing schema; a new schema number is a new layout.
- **Domain tags** are fixed strings that are part of the byte format. Any new signing context AetherFrame introduces (a request proof, a share grant, a key rotation statement) must use its own tag, never this document tag.
- **Limits** (section 2 and section 8) are part of the version: raising a limit is a new schema or protocol version, since a reader at the old limit would refuse documents a newer writer produces.
- Nothing in version 1 is extensible by adding fields, keys or unknown-value passthrough. A reader that sees something it does not know refuses the document.

## 11. Test vectors

`AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json` holds:

- `personas`: two synthetic identities, A and B. Each private scalar is `SHA-256(label) mod n` for the label given, so an implementation can derive the private key, the public key (`d·G`) and the persona identity and compare all three. These keys exist only for testing and must never be used for anything else.
- `documents`: for each sample, the canonical payload, the signing input, the digest, one valid signature and the complete document as hex, with the decoded field values expected from it. `profile-snapshot-maximal` is at the limits (a 128,000-byte name of 32,000 four-byte scalars, eight images totalling 40 MiB); its bytes are omitted for size and its `construction` says how to rebuild them, with the digest and signature over exactly that.
- `rejected`: documents that must be refused, each with the error code expected, derived from `profile-snapshot` by one change (a flipped bit, an extreme length, a substituted key, a high-S signature, and so on) or validly signed over a payload that breaks one schema rule.
- `serverObligations`: valid documents that verify, each with the persona it verifies as, the profile id it carries and what a server is obliged to do with it (section 13). Both are signed by persona B and carry the profile id of persona A's snapshots: they are about (B, that id) and touch nothing of A's.

An implementation is right when it (1) derives the same keys and identities, (2) produces the same payload and signing-input bytes for the sample models, (3) verifies every stored signature, (4) accepts every stored document and decodes the expected values, (5) refuses every rejected document with the stated error, and (6) verifies each `serverObligations` document as the stated persona. ECDSA signatures are randomized, so a regeneration of the file (set `AETHERFRAME_PROTOCOL_REGENERATE_VECTORS=1` and run the tests; the approved public API list has its own switch) yields different signatures; every other value is stable, and one rejected vector's error code (`signature-r-s-swapped`) depends on the signature and is recomputed.

The test project contains two pieces written from this document rather than from the library: `ReferenceP256.cs`, an affine-arithmetic implementation of key derivation, the curve equation and ECDSA verification, and `ReferenceProtocol.cs`, the signing input, persona identity input and envelope built from the tables above. The vector tests check the personas, the five documents' signing inputs, digests, signatures and layouts against both. The rejected vectors are checked against the library only; an independent verifier written in another language during the 2026-09-28 review reproduced all of them.

Worked example, persona A: label `AetherFrame.Protocol test persona A`; the public key and identity are in the fixture; the sample snapshot's payload begins `0001` (schema 1), then `a1a1…` (the profile id), `b2b2…` (the revision id), `00000000 6553f100` (createdAt 1,700,000,000), `0000000c` and `Sample Plate` in UTF-8, then `00000002` and two image references in ascending asset id order.

## 12. Not in version 1

Deliberately absent, so that nothing has to be removed later: any transport (HTTP or otherwise), request and response messages, request authentication, encryption, key rotation or revocation statements, share grants or capability tokens, the profile layout (elements, Components, canvas, fonts, colours), image bytes themselves, persona display names, timestamps with sub-second precision, floating-point values, optional fields, and any form of unknown-field passthrough.

## 13. Server obligations

The protocol verifies bytes; this section says what a server that accepts version 1 documents must do with a verified document. A valid document is an authorization by its signer for exactly what this section allows, and a server that applies it any other way lets a signature authorize something its signer never signed for. Rules 1 to 3 and 8 follow from the byte format and are settled. Rules 4 to 7 are the **baseline** a backend implements unless the owner decides otherwise (NETWORK0.md, "Open product decisions"); they are policy, can change without a new protocol version, and are marked with the decision they depend on.

1. **Scope by owner.** A server keys every profile by (persona, profileId) (section 8.4). A snapshot is applied to the signing persona's profile of that id, creating the profile if it does not exist; a retraction is applied to the signing persona's profile of that id. Neither is ever looked up, matched or applied by profile id alone. The `serverObligations` vectors are the conformance cases.
2. **Identity comes from the key.** The persona of a document is the identity of the key that verified it (section 7.2). Persona ids carried in requests, sessions, URLs or headers never attribute a document; at most they are compared with the verified persona and the request refused when they differ.
3. **Nothing is repaired or rewritten.** A server stores and serves the exact bytes it verified, or nothing. It never re-encodes, trims, normalizes or fills in a document.
4. **Revision uniqueness** (decision D1). Within one profile, a revision id names exactly one document. Resubmitting a document whose bytes equal the stored one is idempotent: accepted, nothing changes. A document that carries a stored revision id with different bytes is refused.
5. **Retraction** (decision D1). Once a retraction of (persona, profileId) is applied, no revision of that profile is served. Baseline: a retraction is terminal. The server keeps a minimal permanent record (persona, profileId, its own receipt time) and refuses every later snapshot of that profile whatever its `createdAt`; to publish again, the client uses a new profile id. Ordering snapshots against retractions by `createdAt` and `issuedAt` alone is not sufficient, because both are client clocks.
6. **Timestamps** (decisions D1 and D7). `createdAt` and `issuedAt` are the client's claims. A server records its own receipt time and uses that for ordering and retention; it refuses a document whose timestamp is more than a bounded skew ahead of its receipt time (baseline: 300 seconds) and keeps, but does not trust, timestamps in the past.
7. **Image declarations** (decision D5). An `ImageReference` is a claim about bytes the server has yet to receive. Before serving an asset the server verifies the received bytes against the declared digest, sniffed format, byte length and dimensions, and serves nothing that fails. What it serves after processing (downscaling) is attested by the server, not signed by the creator.
8. **Limits a document cannot carry** (`ProtocolLimits.FuturePolicy`: profiles per persona, storage per persona, active shares) are enforced by the server with a server-side refusal, never by altering or dropping part of a document.
