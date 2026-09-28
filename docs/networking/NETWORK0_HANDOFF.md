# NETWORK0 handoff

Written on the night of 2026-09-27, at the end of the overnight NETWORK0 run, for the owner's morning review.

- Branch: `claude/network0-protocol-foundation`, started from `origin/master` at `9934650` (Merge AetherFrame custom repository publication workflow).
- Worktree: `AetherFrame/.claude/worktrees/network0-protocol-foundation`. The primary checkout, the stashes, the tags, the releases, the plugin-repository branch and the plugin's AppData were not touched. Nothing was pushed or merged.
- Documents: [NETWORK0.md](NETWORK0.md) (architecture, threat model, privacy, limits, decisions), [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) (the normative wire format), this file.

## Remediation (2026-09-28)

An independent review of `568c137` (the state this handoff describes) found no forgery, no signature bypass and no crash from hostile bytes, and confirmed every committed vector with an implementation in another language plus OpenSSL. It also found two code defects, one specification gap and several overstated claims, fixed on the same branch in the commits after `568c137`. The sections below are left as written that night; where a statement was wrong it is marked `[corrected 2026-09-28]` in place, and this section is the current state.

What changed:

1. **Every reader copies its input before checking it** (`SignedDocumentCodec.Verify` copies the whole document; `PersonaPublicKey.FromBytes`, `ProtocolSignature.FromBytes`, the identifiers and the image digest copy or read once). The night's versions checked the caller's span and copied afterwards, so a buffer another thread rewrote between the two yielded keys holding off-curve points and signatures with a high s, and on Windows the platform's `PlatformNotSupportedException` for such a point escaped `Verify`. Platform refusals of a key the protocol already validated are now `InvalidKey`. Tests race each reader against a rewriting thread.
2. **`Sign` verifies what it produced.** The signer's key is read once, a signer that returns nothing is refused, and bytes the reader would refuse never leave `Sign`. Fault-injecting signers are the regression tests.
3. **A profile is (persona, profileId).** Stated normatively (specification, section 8.4) with the server obligations that follow (section 13), exposed as `RemoteProfileKey` on every `VerifiedDocument`, and the retraction vector, which had persona B retracting persona A's profile id, is now B's retraction of B's own profile. Two `serverObligations` vectors hold the cross-persona documents with what a server must do with them.
4. **The coordinate range rule is tested** with the point x = 5 encoded as x + p and a point with y = 1 encoded as y + p (both satisfy the curve equation modulo p, so only the range rule refuses them); the low-S boundary is pinned on both sides; the unit-only negatives are conformance vectors (65 rejected documents, up from 40); the reference checks build the signing input, identity input and envelope from the specification instead of taking the library's digest; platform DER signatures in both halves are exercised; `FromEcdsa` checks the curve identifier; and the image reader validates in schema order, which the specification now defines for input with several faults.
5. **The specification is marked DRAFT** and NETWORK0.md lists the open product decisions (section 11) with the baseline each currently has, without deciding any of them.

Results after remediation: solution builds with 0 warnings; `AetherFrame.Protocol.Tests` 145 passed (112 that night); the plugin's 2739 and the release tooling's 439 unchanged and passing; the regenerated vectors reproduced by the review's independent Python implementation (all 65 rejected documents with the same codes, all 5 documents verified by it and by OpenSSL 3.5.7). The mutation review was repeated on the repaired guards; see "Mutation review" below.

## What was built

- `AetherFrame.Protocol`, a standalone net10.0 assembly (no packages, no Dalamud, no plugin reference, no `System.Net`, no file or process APIs; a test enforces this). 36 KB compiled, 0 warnings with warnings as errors and XML docs on every public member.
  - Canonical binary encoding: `CanonicalWriter`, `CanonicalReader`, `ProtocolText` (strict UTF-8, no U+0000, 32,000 scalar values, no normalization).
  - Identity: `PersonaPublicKey` (65-byte uncompressed P-256 point with the protocol's own on-curve check), `PersonaId` (`psn_` + SHA-256 over a tagged key), `ProfileId`, `RevisionId`, `AssetId` (16 random bytes, one strict text form each).
  - Signing: `SigningInput` (tag ‖ version ‖ type ‖ key ‖ length ‖ payload), `ProtocolSignature` (64-byte P1363, low-S required), `IPersonaSigner`, `EcdsaPersonaSigner` (in-memory, owns its `ECDsa`, exports nothing), `SignatureVerifier`.
  - Documents: `SignedDocumentCodec.Sign` and `.Verify`, `VerifiedDocument` (persona derived from the verifying key), `DocumentType` (ProfileSnapshot = 1, ProfileRetraction = 2).
  - Remote model: `ProfileSnapshot` (profile id, revision id, createdAt, name, up to 8 `ImageReference`s held as a sorted set), `ProfileRetraction` (profile id, issuedAt). Immutable, validated on construction.
  - `ProtocolLimits`: every enforced limit, plus `FuturePolicy` for the approved limits only a server can enforce.
  - `ProtocolError` (13 codes) and `ProtocolException`, the only exception the assembly throws.
  - `IPersonaKeyProvider`: the seam for NETWORK1's protected key storage. Nothing implements it.
- `AetherFrame.Protocol.Tests`: 112 tests (xunit, same package versions as the other test projects), plus an independent affine-arithmetic P-256 implementation (`ReferenceP256.cs`) written from the specification, used to check every vector.
- `AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json` (synthetic personas, canonical bytes, digests, signatures, 40 rejected documents) and `public-api.txt` (the approved public surface, compared on every run). Both regenerate deliberately with `AETHERFRAME_PROTOCOL_REGENERATE_VECTORS=1`. `[corrected 2026-09-28: 65 rejected documents and a serverObligations section; the public API list regenerates with its own switch, AETHERFRAME_PROTOCOL_REGENERATE_PUBLIC_API=1, and a regeneration run reads back what it wrote.]`
- Solution and CI: both projects in `AetherFrame.slnx`; `build.yml` and `release.yml` run the protocol tests after the release tooling tests. `CHANGELOG.md` has an Unreleased entry.

## What was deliberately not built

No server contact, HTTP, authentication, sessions, capability tokens, shares, Cloudflare, PostgreSQL, object storage, discovery, target lookup, sharing UI, editor or persistence changes, accounts, encryption, key storage or DPAPI, key rotation, the remote layout schema (elements, Components, fonts, colours), persona display names, RP metadata, or a JSON presentation of documents. The plugin does not reference the assembly (a fourth file would break the three-file release package rule; NETWORK1 decides how to ship it). Rationale for each choice: NETWORK0.md, sections 3 and 10.

## Security choices worth knowing

1. One canonical binary encoding is both the transport and the signed bytes; JSON is nowhere in the signing path.
2. The signing input binds a domain tag, the protocol version, the document type and the signer's public key to the payload. A signature cannot move across types, versions, keys or signing contexts, and a duplicate-signature-key-selection construction is defeated by the key binding.
3. Signatures have exactly one encoding (P1363, low-S, r and s range-checked); the signer normalizes, the verifier requires it.
4. Keys are checked against the curve by the protocol itself, so Windows and Linux refuse the same material for the same reason.
5. Every length and count is compared with its limit before allocation; a hostile length costs about 700 bytes and 2 µs to refuse. The whole document is capped at 1 MiB before parsing.
6. The payload is decoded only after the signature verifies, from a private copy, so a shared buffer cannot change between check and use. `[corrected 2026-09-28: the key and the signature were still checked on the caller's buffer before being copied, so this held for the payload only; every reader now copies its whole input first.]`
7. Nothing exports, formats or logs private material; exception messages never repeat bytes (a test scans every message thrown by tens of thousands of hostile inputs). `[corrected 2026-09-28: on Windows a PlatformNotSupportedException could escape Verify for a key that changed under it; platform refusals are now InvalidKey.]`

## Findings from the adversarial review, fixed during the night

- `ProfileSnapshot.Images` exposed the backing array through `IReadOnlyList<T>`; a cast to `IList<T>` could replace elements of a verified document. Now a `ReadOnlyCollection` view; regression test in `SignedDocumentTests`.
- `SignedDocumentCodec.Verify` decoded the caller's span after verifying it, a time-of-check to time-of-use gap for a buffer shared across threads. Now verifies and decodes one private copy of the payload. `[corrected 2026-09-28: the payload copy closed the gap for the payload only; the key and signature had the same gap, closed by copying the whole input first.]`
- `ProfileSnapshot`'s constructor materialized the caller's enumerable before checking the count; now stops at nine.
- Two test expectations were wrong rather than the code: swapping r and s is refused as non-canonical when the old r lands in the high half (deterministic per document, now computed), and a snapshot payload presented under the retraction type fails on the timestamp, not on trailing bytes.

## Test results

Run on the night of 2026-09-27 from a Release build of `AetherFrame.slnx` (0 errors, 0 warnings, locked restore):

| Suite | Result |
|---|---|
| `AetherFrame.Tests` (plugin, unchanged) | 2739 passed, 0 failed |
| `AetherFrame.ReleaseTools.Tests` (unchanged) | 439 passed, 0 failed |
| `AetherFrame.Protocol.Tests` (new) | 112 passed, 0 failed, about 4 s |

The 112 protocol tests include: 8 encoding primitives; 6 identity; 7 signature; 6 envelope round trip and framing; 4 remote model; 12 canonicalization (insertion order, six cultures, time zones, 1000 repeated serializations, NFC/NFD and line endings never normalized); 6 framing adversarial (truncation at every offset of two documents, every bit flipped at every position, extreme lengths with allocation bounds, 2000 random inputs, repeated hostile parsing); 3 key and signature adversarial (14 invalid key encodings, 12 signature variants, cross-document, cross-persona, cross-type and cross-version reuse); 45 validly signed but schema-breaking payloads; 6 fuzz (3 seeds × 4000 mutations across 15 strategies with per-case allocation and campaign time bounds, 3000 re-signed payload mutations proving accepted bytes re-encode to themselves, seed reproducibility); 5 assembly boundary (references, no private material on the public surface, closed hierarchy, no bytes in ToString, approved public API); 7 test vector checks against the library and the reference implementation; 1 benchmark (off by default).

## Mutation review

24 guards were removed one at a time (script kept in the session scratchpad, not committed; nothing mutated was committed) and the protocol suite run each time:

| # | Guard removed | Suite |
|---|---|---|
| 1 | Signature domain tag | 5 failures |
| 2 | Protocol version check | 7 |
| 3 | Signature verification | 10 |
| 4 | Length-prefix maximum before allocation | 12 |
| 5 | Trailing-bytes check | 12 |
| 6 | On-curve public key check | 8 |
| 7 | Unknown document type refusal | 4 |
| 8 | Low-S requirement | 3 |
| 9 | Sorted, unique image set | 4 |
| 10 | U+0000 refusal in text | 5 |
| 11 | Signer refuses inputs naming another persona | 1 |
| 12 | 1 MiB document limit | 2 |
| 13 | All-zero identifier refusal | 1 |
| 14 | Envelope empty-payload check | 2 |
| 15 | Text scalar limit | 2 |
| 16 | Megapixel limit | 3 |
| 17 | Total image bytes limit | 3 |
| 18 | Version and type bound into the signing input | 8 |
| 19 | Public key bound into the signing input | 5 |
| 20 | Timestamp maximum | 1 |
| 21 | Unknown image format refusal | 4 |
| 22 | Payload schema version check | 4 |
| 23 | Signature r range | 3 |
| 24 | 0x04 key prefix requirement | 4 |

Every removal was detected. Mutations 18 and 19 first appeared undetected because git had restored the file with CRLF after an earlier mutation and the multi-line pattern no longer matched, so nothing had changed; re-applied on a normalized file they failed 8 and 5 tests. No missing test had to be added. `[corrected 2026-09-28: the 24 guards above were detected, but the review's own mutations found guards outside that list with no test: the coordinate range check (x < p, y < p) could be removed with the suite green, as could the low-S boundary (off by one) and the private payload copy. Each now has tests and, where a vector can express it, a vector; the "Mutation review, repeated" section below has the results.]`

## Mutation review, repeated (2026-09-28)

The guards the review found untested, and the guards the remediation added, removed one at a time in a scratch clone of the remediated branch (never in this worktree), with the suite run each time:

| Guard removed | Suite |
|---|---|
| Coordinate range check (x < p, y < p) | fails (was green that night) |
| Low-S bound off by one (s = floor(n/2) + 1 accepted) | fails (was green that night) |
| Whole-input copy in `Verify` (check on the caller's span again) | fails (the race tests) |
| Copy-then-check in `PersonaPublicKey.FromBytes` | fails (the race tests) |
| Copy-then-check in `ProtocolSignature.FromBytes` | fails (the race tests) |
| Platform exception mapping in `CreateEcdsa` | fails |
| Output verification in `Sign` | fails (the signer fault tests) |
| Single read of the signer's key in `Sign` | fails |
| Curve identifier check in `FromEcdsa` | fails (message check) |
| Set-order check at the key (moved to after the item) | fails (the precedence tests) |
| `RemoteProfileKey` equality on the persona | fails |
| Signer high-S normalization, unknown type refusal, empty payload check (from the original list, re-run) | fails |

The exact results, with failure counts, are in the remediation report delivered with the branch.

## Performance

`BenchmarkTests.Measure_WhenAskedTo` (set `AETHERFRAME_PROTOCOL_BENCHMARK=<file>`), Release, Windows 11 x64, .NET 10.0.12, 32 cores, single thread:

| Operation | Time | Allocation |
|---|---|---|
| Encode snapshot payload (332-byte document) | 0.5 µs | 592 B |
| Encode maximal payload (128,710-byte document) | 243 µs | 519 KB |
| Sign | 113 µs | 563 B |
| Verify signature only | 389 µs | 1.3 KB |
| Verify and decode, normal document | 407 µs | 3.4 KB |
| Verify and decode, maximal document | 1.14 ms | 517 KB |
| Refuse an oversized declared length | 1.9 µs | 704 B |
| Refuse random bytes | 1.1 µs | 304 B |
| Refuse a flipped payload bit (signature mismatch) | 640 µs | 3.1 KB |
| Refuse an off-curve key | 4.2 µs | 984 B |
| Parse a public key (on-curve check) | 1.1 µs | 664 B |
| Derive a persona id | 0.3 µs | 312 B |

No quadratic behaviour was found; everything is linear in the input. Two things could be cheaper but are not pathological: verification imports the public key into a platform `ECDsa` on every call (most of the 389 µs; a per-key cache would cut it), and a maximal snapshot allocates the name twice (the UTF-16 string and a validation re-encoding). Both are NETWORK1 options, not blockers.

## Open security points

- The strength of ECDSA rests on the platform (Windows CNG, OpenSSL on Linux); nonce generation is theirs. The protocol only fixes the encoding.
- Nothing yet protects a private key at rest; that is NETWORK1's `IPersonaKeyProvider`. Until then the only signer is in-memory.
- Replay of whole documents is a server concern (order snapshots and retractions per profile; refuse regressions). The protocol gives timestamps and ids but no nonce or sequence, on purpose, so a client without server state can sign.
- `EcdsaPersonaSigner` is not thread-safe; NETWORK1 must serialize signing.

## Product decisions still needed

`[superseded 2026-09-28 by NETWORK0.md, section 11, "Open product decisions" (D1 to D9), which lists these five and the review's additions with the baseline each has today. Nothing there is decided.]`

1. Keep `ProfileRetraction` as a signed document, or unpublish through an authenticated request? (NETWORK0.md, section 10, decision 2; now D1.)
2. Is a metadata-only snapshot the right first remote model, with the layout as schema 2, or should the layout come first? (D8.)
3. Persona display name: none, or a field in a future persona document? (D9.)
4. Whether the 32,000-character text limit should be tightened per field (the name) at the protocol level, or left to server policy. (D4.)
5. How the protocol assembly ships in the plugin package (fourth file versus linked sources). (D9.)

## Recommended NETWORK1 scope

Client side only, still no backend: `IPersonaKeyProvider` over a DPAPI-protected P-256 key in the plugin's configuration folder (generate once, never export); a snapshot builder from `ProfileDocument` (name and image references, digests computed from the stored assets); a minimal publish and retract flow behind a feature flag with no UI beyond a command, that produces documents and, for now, writes them to a local outbox instead of a server; the request-proof signing tag and schema, defined and vectored the same way as the documents. Only then the backend, which reuses `AetherFrame.Protocol` unchanged.

## How to review

```
git -C "E:/Plugin development" log --oneline origin/master..claude/network0-protocol-foundation
git -C "E:/Plugin development" diff --stat origin/master...claude/network0-protocol-foundation
```

In the worktree, `DALAMUD_HOME` set as for any plugin build: `dotnet build AetherFrame.slnx -c Release`, then `dotnet test AetherFrame.Protocol.Tests/AetherFrame.Protocol.Tests.csproj -c Release --no-build`. Start with `docs/networking/ProtocolSpecification-v1.md`, then `SignedDocumentCodec.Verify`, `SigningInput.Create`, `PersonaPublicKey.FromBytes` and `ProtocolSignature.FromBytes`; they are the security-critical hundred lines.

One housekeeping note: two idle `python.exe` processes started by a mistyped command during the night are still waiting on stdin (PIDs 34636 and 45632 at the time of writing). They consume nothing and touch nothing; ending them is safe.
