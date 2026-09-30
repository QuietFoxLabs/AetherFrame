# AetherFrame remote protocol, version 1 (DRAFT)

**Status: DRAFT.** This document describes the wire format that `AetherFrame.Protocol` reads and writes today, precisely enough to build a second implementation, in any language, that produces byte-identical documents and verifies the same signatures without reading the C# source. It is not yet frozen. Version 1 becomes final only when the open product decisions in [NETWORK0.md](NETWORK0.md), "Open product decisions", are settled, because some of them change bytes that are signed (the `name` rules, D4; what an image digest covers, D5; what a retraction means, D1). Until then every document is a **draft**, marked in its bytes (decision N3, [DecisionRegister.md](DecisionRegister.md)): `protocolVersion` is `0x8001` and the signature tag ends in `-draft` (sections 5, 7 and 10). A test server may accept drafts; after the freeze no reader accepts one, and a draft can never be read or verified as a final version 1 document. What is not expected to change at the freeze: the envelope layout, the signing input's layout, the signature form, the key format and the persona identity derivation. The version number and the tag change from the draft marker to the final one.

The committed test vectors (`AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json`, section 11) are the check that a second implementation is right. [NETWORK0.md](NETWORK0.md) explains why the protocol looks like this; this document only says what it is. Section 13 states what a server that accepts these documents is obliged to do with them.

Version 1 is the NETWORK0 foundation: signed, identity-bearing documents, and the request proof that authorizes submitting one to a server (section 14). It has no transport, no request or response messages (a proof travels in whatever request a transport defines), no encryption and no discovery. Everything else a server or a client does with these bytes is outside this document.

## 1. Notation

- `u8`, `u16`, `u32`, `u64`: unsigned integers of 1, 2, 4 and 8 bytes, **big-endian** (most significant byte first), fixed width, never variable-length.
- `i32`: a signed integer of 4 bytes, two's complement, big-endian. Only schema 2's layout (section 8.5) uses it.
- `bytes[n]`: exactly `n` bytes whose length the schema fixes.
- `bytes`: a `u32` byte length followed by that many bytes.
- `text`: `bytes` holding UTF-8 (section 2.3).
- `list<T>`: a `u32` count followed by that many `T` values, in the order the schema states.
- `‖`: concatenation. Hex is lowercase.

Every structure is a plain concatenation of its fields in the order listed: there are no alignment bytes, no field tags, no optional fields and no terminators. A field is never omitted; "not set" is represented by the field's empty or zero value where the schema allows one, and nowhere else.

## 2. Primitive rules

### 2.1 Integers

Fixed width, big-endian, no sign except `i32` (two's complement). A value is read exactly as its width says; a value outside the range its field allows is refused (`InvalidValue` or `LimitExceeded`, section 8), never clamped or wrapped.

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

A `name` field follows the stricter name rule of section 8.1.1 instead of these limits. The limits above apply to every other text; version 1 has none yet, and the unit tests keep covering them for the texts a later schema carries.

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
SigningInput = u8(44) ‖ "AetherFrame.Protocol.SignedDocument.v1-draft"
             ‖ u16(0x8001)                 protocol version (the draft marker, section 10)
             ‖ u8(documentType)            section 7.1
             ‖ PublicKey[65]               the signer's key, section 3
             ‖ u32(len(Payload)) ‖ Payload
```

- The tag is the 44 ASCII bytes `4165746865724672616d652e50726f746f636f6c2e5369676e6564446f63756d656e742e76312d6472616674`, preceded by its length as one byte (`0x2c`).
- At the freeze the tag becomes the 38 ASCII bytes of `AetherFrame.Protocol.SignedDocument.v1` (length byte `0x26`) and the version `u16(1)`. A draft signature therefore never verifies as a final one, and the reverse.
- `Digest = SHA-256(SigningInput)` is the value ECDSA signs and verifies.

The signing input binds the protocol version, the document type, the key and the payload. A signature therefore verifies for exactly one (version, type, key, payload) and for nothing else: not for the same payload under another type, not for another version, not with another key, and not over the payload bytes alone. The tag separates document signatures from every other signature AetherFrame defines: the request proof has its own (section 5.1), and so will any later signing context.

The signing input is never transmitted; both sides rebuild it from the document.

### 5.1 Signing contexts

A persona's key signs in exactly two contexts in version 1, and in nothing else (decision L8):

| Context | Tag during the draft | Section |
|---|---|---|
| Signed document | `AetherFrame.Protocol.SignedDocument.v1-draft` (44 bytes) | 5 |
| Request proof | `AetherFrame.Protocol.RequestProof.v1-draft` (42 bytes) | 14 |

Every context follows these rules:
1. Its signing input starts with `u8(len(tag)) ‖ tag`, and no other context uses its tag. Because the tag's length comes first, no signing input of one context is ever equal to one of another, or a prefix of it.
2. After the tag, the input binds the protocol version and the signer's public key, in a fixed canonical layout: no field is optional, free-form or left out.
3. Its tag carries the draft marker (decision N3, section 10) until the owner freezes the version: it ends in `-draft`, and changes together with the version at the freeze.
4. A key signs only signing inputs built as this specification says. It never signs bytes another party supplies, except inside a fixed-length field of a tagged input, as a request proof's challenge (section 14.2).
5. A new context needs its own tag, its own section here, vectors, and a test that its signatures verify in no other context, and no other context's in it.

The persona identity (section 4) has a tag too, `AetherFrame.Protocol.PersonaId.v1`, but it is a hash domain, not a signing context: nothing is signed under it.

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
| 4 | 2 | protocolVersion | `u16` = `0x8001` while the protocol is a draft (section 10); `1` after the freeze |
| 6 | 1 | documentType | `u8`, section 7.1 |
| 7 | 65 | personaPublicKey | section 3 |
| 72 | 4 | payloadLength | `u32`, 1 to 1,048,436 |
| 76 | payloadLength | payload | section 8 |
| 76 + payloadLength | 64 | signature | section 6 |

The overhead is 140 bytes, so the payload is at most 1,048,436 bytes. The document ends immediately after the signature: nothing may follow it.

### 7.1 Document types

| Value | Type | Payload |
|---|---|---|
| 1 | ProfileSnapshot | section 8.1 (schema 1) or 8.5 (schema 2) |
| 2 | ProfileRetraction | section 8.3 |

Any other value is `UnknownDocumentType`.

### 7.2 Verification procedure

A reader performs these steps in this order and stops at the first failure with the error named. Steps 1 to 7 cost a few comparisons; only afterwards does any expensive work happen, and the payload is decoded only after the signature has verified.

1. If the input is longer than 1,048,576 bytes: `LimitExceeded`.
2. Read the magic; if fewer than 4 bytes remain: `Truncated`; if they are not `AFPD`: `InvalidFraming`.
3. Read `protocolVersion`; if it is not `0x8001` (a draft reader) or `1` (a reader after the freeze): `UnsupportedVersion`. No reader accepts both.
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

Each payload starts with its own `u16` schema version, independent of the protocol version: a later schema of the same document type can be introduced while the envelope stays at protocol version 1. A reader refuses any schema version it does not implement (`UnsupportedVersion`). Version 1 defines schema 1 of each type, and schema 2 of `ProfileSnapshot` (section 8.5).

### 8.1 ProfileSnapshot, schema 1

One immutable published revision of a remote profile, without its layout. Schema 1 is a test schema (decision D8): no client publishes it and no server accepts it for publication; players publish schema 2 (section 8.5).

| Field | Type | Rules |
|---|---|---|
| schemaVersion | `u16` | 1 |
| profileId | `bytes[16]` | not all zero |
| revisionId | `bytes[16]` | not all zero |
| createdAt | `u64` | timestamp, section 2.5 |
| name | `text` | the name rule, section 8.1.1 |
| images | `list<ImageReference>` | a **set** keyed by `assetId` (section 2.7); at most 8 |

After `images` the payload ends; a byte more is `TrailingBytes`. The `byteLength` values of all images summed must not exceed 41,943,040 (40 MiB): `LimitExceeded`. The sum is computed in arithmetic that cannot overflow (8 values of at most 8,388,608 each).

#### 8.1.1 The name rule

A `name` (decision D4, [DecisionRegister.md](DecisionRegister.md)) is a `text` with these rules instead of section 2.3's limits:

1. It holds **1 to 64 Unicode scalar values** in **at most 256 bytes** of UTF-8.
2. It is valid UTF-8, as in section 2.3, rule 1.
3. It contains none of these code points. The list is fixed code points, not Unicode properties, so what is valid never changes with the Unicode version:
   - U+0000 to U+001F, U+007F to U+009F (the C0 and C1 controls and DEL);
   - U+00AD, U+061C, U+180E;
   - U+200B to U+200F, U+2028, U+2029, U+202A to U+202E;
   - U+2060 to U+2064, U+2066 to U+206F;
   - U+FEFF, U+FFF9 to U+FFFB;
   - U+E0001, U+E0020 to U+E007F.
4. **Nothing is normalized**, as in section 2.3, rule 5.

A reader checks in this order and stops at the first fault:
1. a declared length of 0: `InvalidLength`, as soon as the length is read;
2. a declared length over 256: `LimitExceeded`, before the input's own length is consulted (section 2.2); a length within 256 but longer than the input: `Truncated`;
3. invalid UTF-8: `InvalidText`;
4. a refused code point (which includes U+0000): `InvalidText`;
5. more than 64 scalar values: `LimitExceeded`.

A writer refuses the same names, and an unpaired surrogate as `InvalidText`.

### 8.2 ImageReference

| Field | Type | Rules |
|---|---|---|
| assetId | `bytes[16]` | not all zero |
| sha256 | `bytes[32]` | SHA-256 of the **prepared copy** the client uploads (decision D5), never of the player's original file; not all zero |
| format | `u8` | 1 = PNG, 2 = JPEG, 3 = WebP; anything else `InvalidValue` |
| byteLength | `u64` | 1 to 8,388,608 (8 MiB); 0 is `InvalidValue`, more is `LimitExceeded` |
| width | `u32` | 1 to 8192; 0 is `InvalidValue`, more is `LimitExceeded` |
| height | `u32` | 1 to 8192, as width |
| | | `width × height ≤ 20,000,000`: `LimitExceeded` |

The format is what the client sniffed from the image bytes, never a file extension. No file name, path or local identifier is carried. Every field describes the prepared copy: the client decodes the image and encodes the pixels again, which drops every metadata block, and never puts the original's bytes or digest into a document (decision D5). In schema 2 the format is PNG or JPEG only (decision I1).

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

Only the owner of a profile can therefore publish to it or retract it, because only the owner holds the key that signs for it. A server, a viewer or any other consumer keys its records by the pair and never by the profile id alone (section 13). The library exposes the pair as a `RemoteProfileKey`, `VerifiedDocument.Profile`, on every verified document about a profile, which every version 1 document is. A later document type that isn't about a profile has no pair (null), never an empty one. The `serverObligations` test vectors (section 11) are valid documents signed by one persona that carry another persona's profile id: each verifies, as its signer, and is about the signer's profile only.

This rule is normative for what a document *means*; it is not something the library can enforce. The library has no record of who owns which profile id, so it can only report the pair. Refusing to apply a valid document to another persona's profile is the backend's obligation (section 13, rule 1) and needs the backend's own owner records; the test fixture models such a record (its `profiles` table) only to check that the vectors are labelled consistently. A valid signature proves who signed; it never proves that the signer owns a profile id somebody else uses.

**Identifier scope (decision N1).** The same scoping holds for revision ids and asset ids. A revision id is scoped to (persona, profileId), and an asset id and an image digest to the persona: a revision id or an asset id carried in one persona's document says nothing about another persona's use of the same bytes. A backend stores and serves an asset under (persona, assetId), never deduplicates or compares digests across personas, and never answers whether another persona holds an asset or a digest (section 13, rule 1).

**Open (review finding N2, not decided):** rule 4 below compares a resubmitted revision only against a stored one, and rule 6 orders by receipt time, so a replayed revision the server no longer holds could become "latest" (N2). It is a backend-gating decision (decision batch B, with the server) and changes no bytes.

### 8.5 ProfileSnapshot, schema 2

One immutable published revision of a remote profile **with its layout**: what a viewer draws, already resolved. It is the first schema players publish (decision D8); schema 1 stays a test schema. It is document type 1, like schema 1, told apart by its `schemaVersion`.

The layout is a **paint list**. The publishing client resolves everything its editor derives, and lists what is drawn, in the order it is drawn:
- which elements are hidden or empty;
- the display text of each text, affixes included;
- where each Component's pieces go.

A viewer draws the list and nothing else. It never needs the publisher's editor logic, and nothing in a document selects behaviour beyond what is listed here. The model shares nothing with the local Plate model. Its value ranges cover every value a local Plate can hold (the package limits), so a publisher never clamps a value. A Plate over a whole-snapshot limit below (items, texts, images) is refused by its publisher with a message, and so is a value no layout field can express (a text holding U+0000, say, or a gradient endpoint with a colour component outside 0 to 1, which the local renderer blends before resolving); any other value the local renderer itself resolves (a colour component outside 0 to 1, an unknown font) is carried as the renderer resolves it.

**Units.** Every value is a fixed-point integer:

| Unit | Encoding | Meaning |
|---|---|---|
| coordinate | `i32`, two's complement, big-endian | hundredths of a canvas unit; -1,000,000,000 to 1,000,000,000 (10,000,000 units, room for a Plate's elements and the Components scaled from them) |
| extent | `i32` | hundredths of a canvas unit; 0 to 10,000,000 |
| angle | `i32` | hundredths of a degree; -36,000 to 36,000 |
| colour | `bytes[4]` | red, green, blue, alpha, each 0 to 255 (straight alpha) |
| opacity | `u8` | 0 to 255 |
| ident | `u8` length, then ASCII | 1 to 96 bytes of `a` to `z`, `0` to `9`, `.` and `-`, starting with a letter. A length of 0 is `InvalidLength` and over 96 `LimitExceeded`, before the bytes are looked at; any other character is `InvalidValue` |

A value outside its range is `InvalidValue`. A canvas unit is what the local Plate calls a pixel of its canvas. A publisher rounds each value to the nearest hundredth, except that an outline thickness above 0 is never rounded down to 0.

**The payload:**

| Field | Type | Rules |
|---|---|---|
| schemaVersion | `u16` | 2 |
| profileId | `bytes[16]` | not all zero |
| revisionId | `bytes[16]` | not all zero |
| createdAt | `u64` | timestamp, section 2.5 |
| name | `text` | the name rule, section 8.1.1 |
| canvasWidth | extent | 100 to 819,200 (1 to 8,192 units) |
| canvasHeight | extent | 100 to 819,200 |
| background | Background | below |
| items | `list<Item>` | at most 2,048, in paint order; a count over 2,048 is `LimitExceeded` before any item is read |
| images | `list<ImageReference>` | a **set** keyed by `assetId` (section 2.7); at most 8; `format` 1 (PNG) or 2 (JPEG) only, anything else `InvalidValue` |

After `images` the payload ends; a byte more is `TrailingBytes`. The rules over the whole payload, checked after the last field in this order:
1. The images' `byteLength` sum is at most 41,943,040 bytes, as in schema 1: `LimitExceeded`.
2. The images' `width` × `height` sum is at most 33,554,432 pixels, so a viewer's decoding stays bounded however well the images compress: `LimitExceeded`.
3. The items' texts hold at most 32,000 scalar values in all: `LimitExceeded`.
4. Every asset id the background or an item names is in `images`, and every entry of `images` is named at least once: `InvalidValue`. A snapshot therefore carries no image nobody draws.

**Background:**

| Field | Type | Rules |
|---|---|---|
| mode | `u8` | 0 none, 1 solid colour, 2 linear gradient, 3 textured fill, 4 image |
| primary | colour | |
| secondary | colour | |
| gradientAngle | angle | |
| opacity | opacity | |
| texture | `u8` | 0 none, 1 to 20 the patterns of Appendix A |
| textureIntensity | opacity | |
| textureScale | extent | 400 to 12,800 |
| textureRotation | angle | |
| imageAssetId | `bytes[16]` | all zero for no image; otherwise an asset id of `images`. Not all zero exactly when `mode` is 4 |
| imageFit | `u8` | 0 stretch, 1 fit, 2 fill |
| imageFlips | `u8` | bit 0 horizontal, bit 1 vertical; other bits 0 |

**Item.** A `u8` kind, then the fields of that kind:

| Kind | Name | Fields |
|---|---|---|
| 1 | Text | `x`, `y` coordinates; `width`, `height` extents; `text` (section 2.3, at most 2,048 scalars, may be empty); `font` ident; `fontSize` `i32` (100 to 102,400, hundredths of a canvas unit); `color` colour; `align` `u8` (0 left, 1 centre, 2 right); `verticalAlign` `u8` (0 top, 1 middle, 2 bottom); `flags` `u8` (bit 0 wrap, 1 bold, 2 italic, 3 underline, 4 strikethrough, 5 auto-fit, 6 outline, 7 shadow); `letterSpacing` `i32` (-1,000,000 to 1,000,000, hundredths of a canvas unit); `lineSpacing` `i32` (-1,000,000 to 1,000,000, hundredths of the font size: 100 is single spacing); `autoFitMinimum` `i32` (100 to 102,400); `outlineColor` colour; `outlineThickness` `u16` (0 to 1,600, hundredths of a canvas unit); `shadowColor` colour; `shadowX`, `shadowY` `i32` (-1,000,000 to 1,000,000, hundredths of a canvas unit); `layout` `u8` (0 legacy, 1 current) |
| 2 | Image | `assetId` `bytes[16]` (in `images`); `x`, `y` coordinates; `width`, `height` extents; `rotation` angle; `fit` `u8` (0 stretch, 1 fit, 2 fill); `flips` `u8` (as the background's); `opacity` opacity |
| 3 | Quad | four points, each `x`, `y` coordinates, clockwise from the top left; `color` colour |
| 4 | Triangle | three points; `color` colour |
| 5 | Image quad | `assetId` `bytes[16]` (in `images`); four points; `tint` colour |
| 6 | Art quad | `art` ident; four points; `tint` colour |

Any other kind is `InvalidValue`. Each field is checked as soon as it is read, in the order of the tables, including an item's kind before its fields (section 9.1). An item text follows section 2.3 with a limit of 2,048 scalars: its declared byte length over 8,192 is `LimitExceeded` before the bytes are looked at. Line breaks and format characters are allowed in an item text; only the name refuses them.

**Drawing.** How a viewer draws each part, so that every viewer draws a document the same way:
- **The canvas** is the rectangle from (0, 0) to (`canvasWidth`, `canvasHeight`). A viewer may draw its own backdrop under it; the backdrop is not part of the Plate. Items are not clipped to the canvas, but a viewer clips everything a document draws to the region it gives the Plate, so no document can draw over the viewer's own interface. A viewer computes positions in floating point or 64-bit integers, since products of the ranges above exceed 32 bits, and rasterises glyphs at a bounded size.
- **The background.** Mode 0 draws nothing at all, pattern included. Every other mode draws its base, then the pattern over it when `texture` is not 0, and the whole is multiplied by `opacity`:
  - mode 1 and mode 3 fill with `primary`, its alpha multiplied by `opacity`;
  - mode 2 is a linear gradient from `primary` to `secondary` at `gradientAngle`, with both endpoints drawn opaque and only `opacity` applied;
  - mode 4 draws the image with `imageFit` and `imageFlips`, multiplied by `opacity`;
  - the pattern is drawn in `secondary` made opaque, at an alpha of `secondary`'s alpha × `textureIntensity` × `opacity`, at `textureScale`, anchored at the canvas centre, rotated about it by `textureRotation` only for the patterns that rotate (Appendix A), and clipped to the canvas.

  A field a mode does not use is carried as the publisher set it and ignored.
- **A text** is drawn in its box with its font, its colour and its switches. An outline is drawn when flags bit 6 is set and `outlineThickness` is above 0, at least one screen pixel thick; a shadow is drawn when flags bit 7 is set. The text is clipped to its box grown on every side by the thickness of the outline drawn plus the larger absolute value of the two offsets of the shadow drawn; an outline or shadow not drawn adds nothing. Its outline and its shadow each take their own colour, with that colour's alpha further multiplied by the text colour's alpha. With auto-fit, the size shrinks from `fontSize` to no less than the smaller of `autoFitMinimum` and `fontSize`, until the text fits.
- **Layout 1 (current)** wraps words at the box's width when wrap is set, pads by 4 canvas units (identical at every zoom), and aligns each line. **Layout 0 (legacy)** is how Plates saved before text layout versions render: no wrapping and no auto-fit (both are carried but not applied), a fixed four-pixel padding on screen, and the whole block aligned as one unit.
- **An image** fills its box with `fit` and `flips`, is rotated about the box's centre by `rotation`, and is multiplied by `opacity`.
- **A quad or triangle** is filled with its colour.
- **An image quad or art quad** maps the image's corners onto the four points and multiplies it by `tint`.

A font or art ident is looked up by exact match in a table the viewer bundles. It is never used to build a file path, a URL or anything else. An ident the viewer does not bundle is drawn as a placeholder and named in a note, never fetched.

**Consumers treat every text as plain text** (decision N7): never markup, a format string, a game text payload, a path, a URL or a command.

**A viewer accepts only the images I1 allows**, whoever published: it sniffs each image's bytes before decoding anything, and refuses anything but a non-animated 8-bit PNG, or an 8-bit JPEG with frame type SOF0 to SOF2 and 1 or 3 components, within section 8.2's limits.

## 9. Errors

Every refusal is one of these codes. A reader that wants to react to the kind of failure switches on the code; the accompanying message names fields, lengths and limits and never repeats key, signature or payload bytes.

| Code | Meaning |
|---|---|
| InvalidFraming | The input does not start with the magic of what is read: `AFPD` for a document, `AFRQ` for a request proof. |
| UnsupportedVersion | The protocol version or a payload schema version is not one this reader implements. |
| UnknownDocumentType | The document type is not listed in 7.1. |
| Truncated | The input ended before a declared value was complete. |
| TrailingBytes | Input continues after the last value of the document or of a payload. |
| LimitExceeded | A length, count, size or total is over its limit. |
| InvalidLength | A length the format forbids, such as an empty payload. |
| InvalidKey | The public key is not an uncompressed point on P-256. |
| InvalidSignature | The signature is not 64 bytes with r and s in range and s in the low half. |
| SignatureMismatch | The signature is well formed but does not verify over the document with its key. |
| InvalidText | A text is not valid UTF-8 or contains U+0000, or a name contains a refused code point (section 8.1.1). |
| InvalidValue | A value outside what its field allows: an unknown enumeration code, an all-zero identifier, a zero dimension, a timestamp out of range. |
| NotCanonical | The input is well formed but not the one canonical encoding of its content, such as an unsorted set. |
| ProofMismatch | A request proof verifies but does not authorize the submission it came with: it names another deployment, binds another document, or is signed by another key than the document's (section 14.4). |

A reader never returns a partially decoded document: either every step of section 7.2 succeeds or nothing is produced.

### 9.1 Input with several faults

A reader checks each rule at the earliest point in reading order at which it can be checked, and refuses the input for the first rule that fails:

- the envelope's rules in the order of section 7.2;
- a fixed-width field's rules as soon as the field has been read (an all-zero identifier, an unknown enumeration code, a timestamp out of range, a zero or over-limit dimension);
- a length's or count's limit as soon as the length has been read, before the bytes or items it announces are looked at;
- a text's byte limit, then its UTF-8 validity, then U+0000, then its scalar limit; a name's emptiness, byte limit, UTF-8 validity, refused code points and scalar limit, in the order of section 8.1.1;
- a set's ordering rule as soon as an item's key has been read, before the rest of the item;
- a rule over several fields of one item (the pixel product) when the last of them has been read;
- after the last field: trailing bytes, then the rules over the whole payload: in schema 1 the total of the image byte lengths; in schema 2 that total, then the images' total pixels, then the total of the item texts, then the image references (section 8.5).

A request proof is read in the order of section 14.3, and a submission checked in the order of section 14.4.

The rejected test vectors each contain one fault. The library's adversarial tests cover the order for inputs with two.

## 10. Versioning policy

- **Protocol version** (envelope, `u16` at offset 4): changes only when the envelope layout, the signing input or the signature scheme changes. A reader implements a closed set of versions and refuses the rest.
- **The draft marker** (decision N3). Until the owner freezes a version, its documents are drafts: the version has its high bit set (`0x8000`), the low fifteen bits name the version it drafts, and the signature tag ends in `-draft`. Version 1's drafts are `0x8001` with `AetherFrame.Protocol.SignedDocument.v1-draft`. A draft-period reader accepts only the draft marker and refuses `1`; after the freeze a reader accepts only `1` with the final tag and refuses `0x8001`. A test server may accept drafts; a server after the freeze accepts none. A draft is never read or verified as final. The persona identity derivation (section 4) has no marker: an identity is a hash of a key, not a signature, so it is the same before and after the freeze. A new version uses a **new signature domain tag** (for example `...SignedDocument.v2`) as well as a new number, so a version 1 signature can never verify under version 2 even if the layouts coincided.
- **Document type** (`u8` at offset 6): new types get new codes; codes are never reused or renumbered. A reader refuses codes it does not know. The type is part of the signing input, so a signature never carries across types.
- **Payload schema version** (`u16` at the start of each payload): a type's payload can evolve without touching the envelope. A reader implements a closed set of schemas per type and refuses the rest. Fields are never added to an existing schema; a new schema number is a new layout.
- **Domain tags** are fixed strings that are part of the byte format. Every signing context has its own (section 5.1): the document's and the request proof's in version 1. A later one, such as a share grant or a key rotation statement, needs its own too.
- **Request proof kind** (`u8` at offset 6 of a proof): new kinds get new codes, which are never reused or renumbered. Each kind defines the layout after its kind byte, and a reader refuses kinds it does not know.
- **Limits** (section 2 and section 8) are part of the version: raising a limit is a new schema or protocol version, since a reader at the old limit would refuse documents a newer writer produces.
- Nothing in version 1 is extensible by adding fields, keys or unknown-value passthrough. A reader that sees something it does not know refuses the document.

## 11. Test vectors

`AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json` holds:

- `personas`: two synthetic identities, A and B. Each private scalar is `SHA-256(label) mod n` for the label given, so an implementation can derive the private key, the public key (`d·G`) and the persona identity and compare all three. These keys exist only for testing and must never be used for anything else.
- `documents`: for each sample, the canonical payload, the signing input, the digest, one valid signature and the complete document as hex, with the decoded field values expected from it. `profile-snapshot-maximal` is at the limits (a 256-byte name of 64 four-byte scalars, eight images totalling 40 MiB); its bytes are omitted for size and its `construction` says how to rebuild them, with the digest and signature over exactly that. `profile-snapshot-minimal` has a one-character name, the earliest timestamp and no images. `profile-layout-snapshot` is schema 2 (section 8.5) with every item kind once, an image background, a PNG and a JPEG. Every document carries the draft marker. Each snapshot vector has its own revision id (`rev_b2b2…`, `rev_b3b3…`, `rev_b4b4…`, `rev_b5b5…`, and `rev_b6b6…` for the layout): within one profile a revision id names exactly one document (section 13, rule 4), so vectors with different content never share one.
- `rejected`: documents that must be refused, each with the error code expected, derived from `profile-snapshot` by one change (a flipped bit, an extreme length, a substituted key, a high-S signature, and so on) or validly signed over a payload that breaks one schema rule. Every rule of the name (section 8.1.1) has an entry: its limits, a declared length over the byte limit with few bytes present, and at least one code point from every refused range. So do the three names the rule turned from valid samples into rejected ones (an empty name, a name with line breaks and invisible characters, and a 32,000-scalar name). Every fault of schema 2 the library's tests exercise has an entry (`signed-layout-...`): each range, each closed code, the identifiers, the item text limits, the image formats and order, and the four rules over the whole payload. Two entries carry the final version 1 marker instead of the draft one: a final document (`UnsupportedVersion`), and the same document with its version changed to the draft's (`SignatureMismatch`). The general 32,000-scalar text limit of section 2.3, which no version 1 field reaches any more, is covered by the library's unit tests, not by a vector.
- `profiles`: the owner of each profile id the vectors use, as the record a backend would hold. The protocol cannot know owners; this table lets the tests check that every valid document is signed by the recorded owner of its profile id and that every `serverObligations` document is not.
- `requestProofs`: valid request proofs (section 14), each for one of the `documents` at the deployment `plates.example.com` under its own challenge, with the subject digest, the signing input, its digest, the signature and the complete proof. Persona A proves `profile-snapshot` and `profile-layout-snapshot`, and persona B `profile-retraction`.
- `rejectedProofs`: proofs that must be refused, each with the document and the deployment it is checked with (section 14.4) and the error expected. Each is derived from the first valid proof by one change, or signed validly but wrongly (in the document context, or by persona B over persona A's document). Every step of sections 14.3 and 14.4 has an entry, and so does each part of the deployment name rule.
- `serverObligations`: valid documents that verify, each with the persona it verifies as, the profile id it carries, the recorded owner of that id (always another persona) and what a server is obliged to do with it (section 13). Both are signed by persona B and carry the profile id of persona A's snapshots: they are about (B, that id) and touch nothing of A's. Signature validity and authorization are different things: these verify, and a backend holding the `profiles` record refuses to treat them as A's acts.

An implementation is right when it (1) derives the same keys and identities, (2) produces the same payload and signing-input bytes for the sample models, (3) verifies every stored signature, (4) accepts every stored document and decodes the expected values, (5) refuses every rejected document with the stated error, (6) verifies each `serverObligations` document as the stated persona, (7) builds the same proof signing inputs and accepts every request proof with its document and deployment, and (8) refuses every rejected proof with the stated error. ECDSA signatures are randomized. A regeneration of the file (set `AETHERFRAME_PROTOCOL_REGENERATE_VECTORS=1` and run the tests; the approved public API list has its own switch) keeps each committed signature whose signed bytes are unchanged, and signs afresh only what changed. Every other value is stable, and one rejected vector's error code (`signature-r-s-swapped`) depends on the signature and is recomputed.

The test project contains two pieces written from this document rather than from the library: `ReferenceP256.cs`, an affine-arithmetic implementation of key derivation, the curve equation and ECDSA verification, and `ReferenceProtocol.cs`, the signing input, persona identity input and envelope, and the request proof's signing input and layout, built from the tables above. The vector tests check the personas, the documents' signing inputs, digests, signatures and layouts, and the request proofs', against both. The rejected vectors are checked against the library only; an independent verifier written in another language during the 2026-09-28 review reproduced all of them.

Worked example, persona A: label `AetherFrame.Protocol test persona A`; the public key and identity are in the fixture; the sample snapshot's payload begins `0001` (schema 1), then `a1a1…` (the profile id), `b2b2…` (the revision id), `00000000 6553f100` (createdAt 1,700,000,000), `0000000c` and `Sample Plate` in UTF-8, then `00000002` and two image references in ascending asset id order.

## 12. Not in version 1

Deliberately absent, so that nothing has to be removed later: any transport (HTTP or otherwise), request and response messages, request authentication beyond the request proof (section 14), encryption, key rotation or revocation statements, share grants or capability tokens, image bytes themselves, persona display names, timestamps with sub-second precision, floating-point values (schema 2 carries fixed-point integers), optional fields, and any form of unknown-field passthrough.

## 13. Server obligations

The protocol verifies bytes; this section says what a server that accepts version 1 documents must do with a verified document. A valid document is an authorization by its signer for exactly what this section allows, and a server that applies it any other way lets a signature authorize something its signer never signed for. Rules 1, 2 and 8, and the storage half of rule 3, follow from the byte format and are settled; rule 9 is settled by decision N7, and rule 10 by decisions S1 and D7. Rules 4 to 7 and the serving half of rule 3 are the **baseline** a backend implements unless the owner decides otherwise (NETWORK0.md, "Open product decisions"); they are policy, can change without a new protocol version, and are marked with the decision they depend on. Nothing in this section is enforced by the library: it holds no server state, and a backend that skips a rule is not caught by any test here.

1. **Scope by owner.** A server keys every profile by (persona, profileId) (section 8.4). A snapshot is applied to the signing persona's profile of that id, creating the profile if it does not exist; a retraction is applied to the signing persona's profile of that id. Neither is ever looked up, matched or applied by profile id alone. Revision ids are scoped the same way, and assets and image digests by persona (section 8.4, decision N1). The `serverObligations` vectors are the conformance cases.
2. **Identity comes from the key.** The persona of a document is the identity of the key that verified it (section 7.2). Persona ids carried in requests, sessions, URLs or headers never attribute a document; at most they are compared with the verified persona and the request refused when they differ.
3. **Nothing is repaired or rewritten.** A server stores the exact bytes it verified, or nothing; it never re-encodes, trims, normalizes or fills in a document, and whatever it derives from one (a listing, a rendered view) is derived from those bytes. Whether viewers receive those signed bytes themselves or server-attested content derived from them is decision D6 (NETWORK0.md), not settled here; either way the stored record is the verified bytes.
4. **Revision uniqueness** (decision D1). Within one profile, a revision id names exactly one document. Resubmitting a document whose bytes equal the stored one is idempotent: accepted, nothing changes. A document that carries a stored revision id with different bytes is refused.
5. **Retraction** (decision D1). Once a retraction of (persona, profileId) is applied, no revision of that profile is served. Baseline: a retraction is terminal. The server keeps a minimal permanent record (persona, profileId, its own receipt time) and refuses every later snapshot of that profile whatever its `createdAt`; to publish again, the client uses a new profile id. Ordering snapshots against retractions by `createdAt` and `issuedAt` alone is not sufficient, because both are client clocks.
6. **Timestamps** (decisions D1 and N6). `createdAt` and `issuedAt` are the client's claims. A server records its own receipt time and uses that for ordering and retention; it refuses a document whose timestamp is more than a bounded skew ahead of its receipt time (baseline: 300 seconds) and keeps, but does not trust, timestamps in the past.
7. **Image declarations** (decision D5). An `ImageReference` is a claim about bytes the server has yet to receive. Before serving an asset the server verifies the received bytes against the declared digest, sniffed format, byte length and dimensions, and serves nothing that fails. What it serves after processing (downscaling) is attested by the server, not signed by the creator.
8. **Limits a document cannot carry** (NETWORK0.md, section 7: profiles per persona, storage per persona, active shares) are enforced by the server with a server-side refusal, never by altering or dropping part of a document.
9. **Texts are plain text** (decision N7). A server, like every consumer, never interprets a text as markup, a format string, a path, a URL or a command, never puts one into HTML unescaped, and passes texts to a database only as query parameters.
10. **Request proofs** (decisions S1 and D7). A server accepts a document only as a submission whose request proof passes section 14.4, whatever the document's type. A document without a proof, or with one that fails, is refused and changes nothing. Resubmitting a stored document needs a fresh proof too, and a share code is returned only in reply to a submission with a valid proof. The server's deployment name comes from its configuration only (section 14.1). A server that accepts documents signed by real keys refuses to start with a name reserved for tests. The challenges:
    - are issued from a cryptographically secure generator, at least 128 bits unpredictable to anyone else, and issuing them is rate-limited;
    - are each accepted at most once, across every instance that serves the name, and only for a bounded time after issue (baseline: 300 seconds);
    - are consumed in one atomic step (known, unexpired and unused, then removed) after section 14.4 has passed and before any state changes or any share code is issued, whatever the submission's outcome afterwards (a refused revision, a terminal retraction). A consumed challenge is never restored, and a server that loses its record of challenges treats every one it issued as consumed;
    - are taken only from the proof; a challenge sent any other way is ignored;
    - when refused, are refused with their own retryable error that carries a fresh challenge;
    - are never logged, and neither are proofs.

## 14. Request proof

A request proof authorizes one submission of one signed document to one deployment of a server (decisions S1 and D7, [DecisionRegister.md](DecisionRegister.md)). A document on its own verifies anywhere, for anyone who holds a copy. A server accepts one only with a fresh proof from the document's own key, made for that server under a challenge the server issued (section 13, rule 10). So only a document's signer can submit it, only to the deployment they chose, and only once per challenge.

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 4 | magic | ASCII `AFRQ` (`41 46 52 51`) |
| 4 | 2 | protocolVersion | `u16` = `0x8001` while the protocol is a draft (section 10); `1` after the freeze |
| 6 | 1 | proofKind | `u8`: 1 = document submission. Closed: any other value is `InvalidValue` |
| 7 | 65 | personaPublicKey | section 3 |
| 72 | 1 | deploymentLength | `u8`, 1 to 253 |
| 73 | n | deployment | the deployment name, section 14.1 |
| 73 + n | 32 | challenge | section 14.2 |
| 105 + n | 32 | subjectDigest | for kind 1, SHA-256 of the complete signed document submitted, signature included |
| 137 + n | 64 | signature | section 6 |

A proof is 201 + n bytes, at most **454**, and ends immediately after its signature. The kind decides the layout after it: this table is kind 1's, and a later kind may define its own.

The signing input (section 5.1):

```
ProofSigningInput = u8(42) ‖ "AetherFrame.Protocol.RequestProof.v1-draft"
                  ‖ u16(0x8001)                 protocol version (the draft marker)
                  ‖ u8(proofKind)
                  ‖ PublicKey[65]
                  ‖ u8(n) ‖ deployment
                  ‖ challenge[32]
                  ‖ subjectDigest[32]
```

- The tag is the 42 ASCII bytes `4165746865724672616d652e50726f746f636f6c2e5265717565737450726f6f662e76312d6472616674`, preceded by its length as one byte (`0x2a`).
- The input is therefore the tag with its length, followed by the proof's bytes from offset 4 up to the signature.
- At the freeze the tag becomes the 36 ASCII bytes of `AetherFrame.Protocol.RequestProof.v1` (length byte `0x24`) and the version `u16(1)`.
- `Digest = SHA-256(ProofSigningInput)` is the value ECDSA signs and verifies, as in section 5.

### 14.1 The deployment name

The DNS hostname a client connects to over HTTPS (decision R2), in its one canonical form:
- 1 to 253 bytes. A length of 0 is `InvalidLength` and one over 253 `LimitExceeded`, before the bytes are looked at.
- Labels separated by `.`, each 1 to 63 bytes of `a` to `z`, `0` to `9` and `-`, neither starting nor ending with `-`: the LDH syntax of RFC 5890, section 2.3.1, in lowercase.
- The last label starts with a letter, as every top-level domain does, so no IP address literal in any notation (`127.0.0.1`, `0x7f000001`) is a name.
- Nothing else: no uppercase, no trailing dot, no port, no scheme, no path. Anything else is `InvalidValue`; a name is refused, never normalized.

An internationalized name appears only as A-labels (`xn--…`), which are not checked beyond this rule: both sides take the name from their own configuration and compare bytes.

- A **client** takes the name from its own configuration, never from anything a server sends (a response, a header, a redirect), and builds the address it connects to from the name, never the name from an address.
- A **server** takes its own name from its configuration, checks it with this rule when it starts, and compares proofs with it. It never uses the request's `Host` header, the TLS server name or anything else in the request for this.
- Separate deployments (production, staging, another operator's) never share a hostname, since ports and paths are not part of the name.
- Some names no real deployment can have, and only tests use them: a single label; `localhost` and names under it; names under `test`, `example` and `invalid` (RFC 2606, RFC 6761); and `example.com`, `example.net`, `example.org` and names under them. The vectors use `plates.example.com`.

### 14.2 The challenge

32 bytes a server issues for one proof, which the client copies into the proof unchanged. The all-zero value is refused (`InvalidValue`), so a buffer a server forgot to fill is never accepted. Text form: `chl_` followed by the 64 lowercase hex digits, parsed as strictly as the identifiers of section 2.6.

What makes a challenge work is the server's side of it (unpredictable, used once, short-lived), which the protocol cannot check: section 13, rule 10.

### 14.3 Reading a proof

A reader performs these steps in this order and stops at the first failure with the error named:
1. If the input is longer than 454 bytes: `LimitExceeded`.
2. Read the magic; if fewer than 4 bytes remain: `Truncated`; if they are not `AFRQ`: `InvalidFraming`. A signed document (`AFPD`) is never read as a proof, nor a proof as a document.
3. Read `protocolVersion`, as section 7.2, step 3: `UnsupportedVersion`.
4. Read `proofKind`; anything but 1: `InvalidValue`.
5. Read the 65 key bytes (not yet validated).
6. Read `deploymentLength` and check it (section 14.1), then read the name and check it: `InvalidLength`, `LimitExceeded`, `Truncated` or `InvalidValue`.
7. Read the challenge and check it (section 14.2), the subject digest and the 64 signature bytes; then, if any input remains: `TrailingBytes`. At each read, too little input is `Truncated`.
8. Validate the key (section 3): `InvalidKey`.
9. Validate the signature's form (section 6, rules 1 to 3): `InvalidSignature`.
10. Build the signing input from the fields as read, and verify the signature with the key: `SignatureMismatch`.

A proof that passes these steps authorizes nothing yet: it must also match the submission it came with.

### 14.4 Checking a submission

A server that receives a document with its proof checks, in this order, and stops at the first failure:
1. The proof, as section 14.3.
2. The proof's deployment name equals the server's own: otherwise `ProofMismatch`.
3. The document is at most 1,048,576 bytes (section 7.2, step 1): otherwise `LimitExceeded`. Its SHA-256 equals the proof's `subjectDigest`: otherwise `ProofMismatch`.
4. The document, as section 7.2.
5. The document's key equals the proof's key: otherwise `ProofMismatch`.

Only then does the server consume the challenge (section 13, rule 10), and only after that does it act on the document. It stores the exact bytes it hashed and verified in steps 3 and 4 (rule 3), never the request's buffer read a second time.

What a server does with a submission is decided by the proof's kind and the document, and by nothing else in the request. No header, query or other field may change what is stored, which profile it is applied to, or any setting. A parameter that should change the outcome needs a proof kind that signs it.

## Appendix A. Background patterns

The `texture` codes of a schema 2 background (section 8.5). How each is drawn is the viewer's; the code names the pattern. `textureRotation` applies only to the patterns marked as rotating.

| Code | Pattern | Rotates | Code | Pattern | Rotates |
|---|---|---|---|---|---|
| 0 | none | | 11 | honeycomb | yes |
| 1 | fine noise | no | 12 | scales | yes |
| 2 | dots | yes | 13 | speckle | no |
| 3 | grid | yes | 14 | diamonds | yes |
| 4 | diagonal lines | yes | 15 | chevron | yes |
| 5 | crosshatch | yes | 16 | sparkle | yes |
| 6 | subtle paper | no | 17 | linen | yes |
| 7 | checkerboard | yes | 18 | ripples | no |
| 8 | stripes | yes | 19 | quatrefoil | yes |
| 9 | waves | yes | 20 | brick | yes |
| 10 | herringbone | yes | | | |
