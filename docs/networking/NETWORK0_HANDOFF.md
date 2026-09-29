# NETWORK0 handoff

Written on the night of 2026-09-27, at the end of the overnight NETWORK0 run, for the owner's morning review.

- Branch: `claude/network0-protocol-foundation`, started from `origin/master` at `9934650` (Merge AetherFrame custom repository publication workflow).
- Worktree: `AetherFrame/.claude/worktrees/network0-protocol-foundation`. The primary checkout, the stashes, the tags, the releases, the plugin-repository branch and the plugin's AppData were not touched. Nothing was pushed or merged.
- Documents: [NETWORK0.md](NETWORK0.md) (architecture, threat model, privacy, limits, decisions), [ProtocolSpecification-v1.md](ProtocolSpecification-v1.md) (the normative wire format), this file.

## After the merge (2026-09-28)

- **Merged.** The branch was merged into master as `6384db6` through pull request #20. Protocol version 1 is still a **DRAFT**, and the plugin still does not reference the assembly.
- **CI.** Both legs passed on the pull request's head `dcd56f9` and on the merge. That includes the first two runs of the protocol suite on ubuntu-24.04, 153 of 153 each. The run numbers and counts are in NETWORK0.md, section 13. The statements below that Linux had never run are left as written that day and marked in place.
- **Document repair.** A mistaken edit in `8cf071a` had placed two pieces of text above the titles:
  - The refreshed benchmark table from this file, which now sits in its Performance section.
  - The corrected end of NETWORK0.md's "Exceptions leaking bytes" threat-model row, which is now in that row.

  Nothing else in the content changed.
- **What comes next.**
  - Decisions are tracked in [DecisionRegister.md](DecisionRegister.md). D3 was approved and D2 approved in principle on 2026-09-28; every other decision is unresolved.
  - The planned NETWORK1 boundaries are in [NETWORK1.md](NETWORK1.md).
  - The platform cryptography findings are in [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md).

## Remediation (2026-09-28)

An independent review of `568c137` (the state this handoff describes) found no forgery, no signature bypass and no crash from hostile bytes, and confirmed every committed vector with an implementation in another language plus OpenSSL. It also found two code defects, one specification gap and several overstated claims, fixed on the same branch in the commits after `568c137`. The sections below are left as written that night; where a statement was wrong it is marked `[corrected 2026-09-28]` in place, and this section is the current state.

What changed:

1. **Every reader copies its input before checking it** (`SignedDocumentCodec.Verify` copies the whole document; `PersonaPublicKey.FromBytes`, `ProtocolSignature.FromBytes`, the identifiers and the image digest copy or read once). The night's versions checked the caller's span and copied afterwards, so a buffer another thread rewrote between the two yielded keys holding off-curve points and signatures with a high s, and on Windows the platform's `PlatformNotSupportedException` for such a point escaped `Verify`. Platform refusals of a key the protocol already validated are now `InvalidKey`. Tests race each reader against a rewriting thread.
2. **`Sign` verifies what it produced.** The signer's key is read once, a signer that returns nothing is refused, and bytes the reader would refuse never leave `Sign`. Fault-injecting signers are the regression tests.
3. **A profile is (persona, profileId).** Stated normatively (specification, section 8.4) with the server obligations that follow (section 13), exposed as `RemoteProfileKey` on every `VerifiedDocument`, and the retraction vector, which had persona B retracting persona A's profile id, is now B's retraction of B's own profile. Two `serverObligations` vectors hold the cross-persona documents with what a server must do with them.
4. **The coordinate range rule is tested** with the point x = 5 encoded as x + p and a point with y = 1 encoded as y + p (both satisfy the curve equation modulo p, so only the range rule refuses them); the low-S boundary is pinned on both sides; the unit-only negatives are conformance vectors (65 rejected documents, up from 40); the reference checks build the signing input, identity input and envelope from the specification instead of taking the library's digest; platform DER signatures in both halves are exercised; `FromEcdsa` checks the curve identifier; and the image reader validates in schema order, which the specification now defines for input with several faults.
5. **The specification is marked DRAFT** and NETWORK0.md lists the open product decisions (section 11) with the baseline each currently has, without deciding any of them.

Results after remediation: solution builds with 0 warnings; `AetherFrame.Protocol.Tests` 145 passed (112 that night); the plugin's 2739 and the release tooling's 439 unchanged and passing; the regenerated vectors reproduced by the review's independent Python implementation (all 65 rejected documents with the same codes, all 5 documents verified by it and by OpenSSL 3.5.7). The mutation review was repeated on the repaired guards; see "Mutation review" below.

## Pre-merge cleanup (2026-09-28, after the second review)

The second independent review of `a96d7fd` verified the security fixes and found no new code vulnerability. It listed five test and documentation defects to correct before a merge, and six findings (L4, L6, N1, N2, N3, N5) to fix where small or to record. All of it was done on this branch in the commits after `a96d7fd`; nothing about the wire format, a domain tag or the protocol version changed, and the specification stays DRAFT.

**T1, the DER test.** `ThirdPartySignatureTests` asserted that platform DER signatures are 70 to 72 bytes. Shorter ones are legal: 20,000 platform signatures measured 44 of 69 bytes (0.22 %, the reviewers' 0.21 %), each a run the Release workflow could have failed. A strict X.690 reader (`DerEcdsaSignature`) now validates the grammar (SEQUENCE with short-form length equal to the remainder, two INTEGERs with minimal positive encodings of 1 to 33 bytes, no trailing bytes, r and s in 1 to n-1) and the test asserts that the length equals 6 plus the two integer lengths derived from r and s. Two new tests: 2,000 platform signatures of every length parse, re-encode to the same bytes and verify; and a deterministic set of the shortest (8 bytes), longest (72) and intermediate encodings is accepted while a padding zero, a negative integer, long-form lengths, wrong tags, trailing bytes, zero, n and a 34-byte integer are refused. Removing the leading-zero, negativity, range or sequence-length rule from the reader fails the deterministic test.

**T2, the identifier race test.** The test rewrote byte 15 of `a1a1…a1`, which never becomes an empty id, so the 568c137 check-then-read code passed it. The buffer now alternates between an all-zero id and one whose only non-zero byte is the last, and the digest race likewise; the test also requires that the race produced both an acceptance and an `InvalidValue` refusal whenever a second core exists, so it can no longer pass vacuously. With the old check-then-read `Id128.FromBytes` restored it failed 5 of 5 runs. A deterministic test is not possible without a production seam for intercepting reads, which would be worse than the stress test; the verification against the old code is the evidence that it detects the targeted behaviour.

**T3, ownership fixtures.** The fixture now carries a `profiles` table (the recorded owner of each profile id, as a backend would hold it) and each `serverObligations` vector names the recorded owner it is not. New tests: every valid vector is signed by the recorded owner of its profile id and its verified profile is that owner's; every obligation vector verifies, is about the signer's profile, and is not the recorded owner's act; obligation documents never appear among the valid ones. Reverting the retraction vector to persona B retracting persona A's profile id and regenerating fails exactly `ValidDocuments_AreSignedByTheRecordedOwnerOfTheirProfile` (verified; also with the ownership assertion removed, the remaining assertions still fail it). Recording the signer as an obligation's owner fails exactly `ServerObligationDocuments_AreValidSignaturesButNotTheRecordedOwnersActs`. The specification (section 8.4) now says in so many words that the ownership rule is normative for what a document means, that the library cannot enforce it, and that refusing to apply a valid document to another persona's profile is the backend's obligation with the backend's own owner records.

**T4, revision fixtures.** `profile-snapshot`, `-unicode`, `-empty` and `-maximal` shared `rev_b2b2…` with different content, which section 13 rule 4 forbids. They now carry `rev_b2b2…`, `rev_b3b3…`, `rev_b4b4…` and `rev_b5b5…`; a new test refuses a shared revision id over different payload bytes. The fixture was regenerated (every signature changed, as always) and the maximal vector's construction text names its revision id. The review's independent Python and OpenSSL 3.5.7 checker, with its hard-coded maximal revision id updated to match, reproduced the regenerated fixture: both personas, all 5 documents (Python ECDSA and OpenSSL), all 77 rejected vectors with the same codes.

**T5, documentation.** Corrected in place, each marked `[corrected 2026-09-28]` where it was wrong before: the local Plate name limit is 64 UTF-16 code units after folding, not scalars; every recommendation to digest stored or source images is withdrawn pending D5; the exception claims (hostile bytes versus caller misuse, `Sign` passing signer exceptions through, `FromEcdsa` now mapping every export refusal); the reference verification claim (personas and documents, not rejected vectors, and no non-C# checker in the repository); the "refuse regressions" ordering line; specification rule 3 no longer decides D6; the fixture notes say DRAFT; `CurvePoints` no longer says x = 1 is the smallest x on the curve (x = 0 is, pinned by a test); and the Linux status is stated (section 13 of NETWORK0.md and below).

**L4, L6, N1, N2, N3, N5.** Small defects fixed: `FromEcdsa` maps every export refusal (`NotSupportedException`, `NotImplementedException`, `PlatformNotSupportedException`, a disposed key) to `InvalidKey`, with a test; twelve per-field range vectors were added (dimension, per-image bytes, zero asset id, format 0, zero height, height over the limit, width at u32 max, nine images declared, fewer images than present, truncated image, nested count abuse, createdAt at u64 max), so a reader without the per-dimension or per-image limit now fails the vectors (verified by mutation); the vector builder's false claim about range coverage is corrected. Everything else is recorded, not decided, in NETWORK0.md section 12: N1 and N2 are backend gates, N3 needs an explicit decision before real keys sign (no tag was changed), N5 and L6 are API-shape decisions for NETWORK1 (both marked provisional in their documentation), L4's remainder is a key-provider decision.

**Results.** `AetherFrame.Protocol.Tests` 153 passed (145 before; 8 added: 2 DER, 3 vector ownership and uniqueness, 2 payload theory rows, 1 export refusal), about 3 s. The plugin's 2739 and the release tooling's 439 pass unchanged; solution 0 warnings. Targeted mutations: T1 rules (4 of 4 detected), T2 (old code fails 5 of 5), T3 (3 of 3), T4 (1 of 1), image limits V1 to V5 (dimension, bytes, format, pixels detected; the constructor-only zero-asset-id check is redundant with `Id128.FromBytes` on the wire path and its removal is equivalent), `FromEcdsa` catch narrowed back (detected by the new test). Benchmark refreshed (below). **Linux CI verification remains outstanding**: no test here has run on Linux; the draft PR's ubuntu leg will be the first. `[corrected 2026-09-28, after the merge: it has run. The protocol suite passed 153 of 153 on ubuntu-24.04 in pull request run 36446719752 (dcd56f9) and in the merge's run 36474945050 (6384db6), with every other suite green on both legs; see NETWORK0.md, section 13.]`

## What was built

- `AetherFrame.Protocol`, a standalone net10.0 assembly (no packages, no Dalamud, no plugin reference, no `System.Net`, no file or process APIs; a test enforces this). 36 KB compiled, 0 warnings with warnings as errors and XML docs on every public member.
  - Canonical binary encoding: `CanonicalWriter`, `CanonicalReader`, `ProtocolText` (strict UTF-8, no U+0000, 32,000 scalar values, no normalization).
  - Identity: `PersonaPublicKey` (65-byte uncompressed P-256 point with the protocol's own on-curve check), `PersonaId` (`psn_` + SHA-256 over a tagged key), `ProfileId`, `RevisionId`, `AssetId` (16 random bytes, one strict text form each).
  - Signing: `SigningInput` (tag ‖ version ‖ type ‖ key ‖ length ‖ payload), `ProtocolSignature` (64-byte P1363, low-S required), `IPersonaSigner`, `EcdsaPersonaSigner` (in-memory, owns its `ECDsa`, exports nothing), `SignatureVerifier`.
  - Documents: `SignedDocumentCodec.Sign` and `.Verify`, `VerifiedDocument` (persona derived from the verifying key), `DocumentType` (ProfileSnapshot = 1, ProfileRetraction = 2).
  - Remote model: `ProfileSnapshot` (profile id, revision id, createdAt, name, up to 8 `ImageReference`s held as a sorted set), `ProfileRetraction` (profile id, issuedAt). Immutable, validated on construction.
  - `ProtocolLimits`: every enforced limit, plus `FuturePolicy` for the approved limits only a server can enforce. `[updated 2026-09-29: FuturePolicy was removed under L6; those limits are documentation only, in NETWORK0.md, section 7.]`
  - `ProtocolError` (13 codes) and `ProtocolException`, the only exception the assembly throws. `[corrected 2026-09-28: the only exception for hostile bytes; caller misuse throws the usual .NET argument and object-disposed exceptions, and Sign passes the signer's own exceptions through unchanged.]`
  - `IPersonaKeyProvider`: the seam for NETWORK1's protected key storage. Nothing implements it. `[updated 2026-09-29: removed under L6. Key storage is plugin policy, modelled by AetherFrame.Personas. Under N5 the profile id also moved from RemoteDocument to RemoteProfileDocument. Both are APPROVED (Claude, under the owner's delegation of September 29, 2026) (DecisionRegister.md).]`
- `AetherFrame.Protocol.Tests`: 112 tests (xunit, same package versions as the other test projects), plus an independent affine-arithmetic P-256 implementation (`ReferenceP256.cs`) written from the specification, used to check every vector. `[corrected 2026-09-28: the reference checks the personas and the five documents (keys, identities, signing inputs, digests, signatures, envelopes); the rejected vectors are checked against the library only. No non-C# checker is committed; the reviews' Python and OpenSSL checker, kept outside the repository, reproduced all vectors including the 77 rejected ones after the pre-merge regeneration.]`
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

The exact results, with failure counts (each run in a scratch clone of the remediated branch; R1 to R20 as the remediation report numbered them; that report is not in the repository, so the counts are copied here):

| # | Mutation | Suite |
|---|---|---|
| R1 | Coordinate range check removed entirely | 3 failures (was green at 568c137) |
| R2 | Only the y-range half removed | 2 |
| R3 | Only the x-range half removed | 2 |
| R4 | Low-S bound off by one in `FromBytes` | 2 (was green at 568c137) |
| R5 | Whole-input copy in `Verify` removed | 1 (the payload race) |
| R6 | `PersonaPublicKey.FromBytes` checks the span, copies after | 1 (the key race) |
| R7 | `ProtocolSignature.FromBytes` checks the span, copies after | 1 (the signature race) |
| R8 | Platform exception mapping narrowed to `CryptographicException` | 1 |
| R9 | `Sign` no longer verifies its output | 2 |
| R10 | `Sign` reads the signer's key twice | 2 |
| R11 | Curve identifier check in `FromEcdsa` removed | 1 |
| R12 | Set-order check moved after the digest (precedence) | 1 |
| R13 | Set-order check removed | 5 |
| R14 | `RemoteProfileKey` equality ignores the persona | 3 |
| R15 | Opaque id zero check removed | 1 |
| R16 | Image digest zero check removed | 5 |
| R17 | Signer high-S normalization removed | 48 |
| R18 | Unknown document type accepted | 3 |
| R19 | Envelope empty-payload check removed | 2 |
| R20 | `Sign` accepts a null signature | 1 |

## Performance

`BenchmarkTests.Measure_WhenAskedTo` (set `AETHERFRAME_PROTOCOL_BENCHMARK=<file>`), Release, Windows 11 x64, .NET 10.0.12, 32 cores, single thread. `[refreshed 2026-09-28 at the pre-merge cleanup; the night's figures predate the whole-input copy in Verify and are replaced here. "Sign" is the signer alone; SignedDocumentCodec.Sign also verifies its output, so it costs about a sign plus a verify.]`

| Operation | Time | Allocation |
|---|---|---|
| Encode snapshot payload (332-byte document) | 0.4 µs | 592 B |
| Encode maximal payload (128,710-byte document) | 131 µs | 519 KB |
| Sign (signer only) | 98 µs | 564 B |
| Verify signature only | 337 µs | 1.3 KB |
| Verify and decode, normal document | 345 µs | 4.2 KB |
| Verify and decode, maximal document | 1.24 ms | 647 KB |
| Refuse an oversized declared length | 1.7 µs | 1.1 KB |
| Refuse random bytes | 1.1 µs | 632 B |
| Refuse a flipped payload bit (signature mismatch) | 357 µs | 3.5 KB |
| Refuse an off-curve key | 2.1 µs | 1.4 KB |
| Parse a public key (on-curve check) | 0.7 µs | 664 B |
| Derive a persona id | 0.8 µs | 312 B |

`[repaired 2026-09-28, after the merge: the paragraph and table above were placed before this document's title by a mistaken edit in 8cf071a; they are moved here unchanged. The night's figures below are kept as the record of 2026-09-27 and are superseded by the table above.]`

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

No quadratic behaviour was found; everything is linear in the input. Two things could be cheaper but are not pathological: verification imports the public key into a platform `ECDsa` on every call (most of the 337 µs; a per-key cache would cut it), and a maximal document now costs one whole-input copy plus the name twice (the UTF-16 string and a validation re-encoding). Both are NETWORK1 options, not blockers.

## Open security points

- The strength of ECDSA rests on the platform (Windows CNG, OpenSSL on Linux); nonce generation is theirs. The protocol only fixes the encoding. `[updated 2026-09-28: under Wine, Proton or macOS compatibility layers the platform is Wine's ncrypt and bcrypt. Source inspection indicates the path the protocol uses today does not work there. That is not yet confirmed by running anything: see NETWORK1_CryptoCompatibility.md.]`
- Nothing yet protects a private key at rest; that is NETWORK1's `IPersonaKeyProvider`. Until then the only signer is in-memory. `[updated 2026-09-28: the provider's shape (L6) and the storage mechanism are unresolved; see NETWORK1.md and DecisionRegister.md.]` `[updated 2026-09-29: L6 is decided: the protocol has no provider, and key storage is plugin policy. The storage mechanism (K2) is still unresolved.]`
- Replay of whole documents is a server concern. `[corrected 2026-09-28: not "order by createdAt and refuse regressions", which two client clocks cannot support; the baseline is specification section 13 (terminal retraction, receipt-time ordering, idempotent resubmission), and rollback by replaying a pruned revision (N2) is still open.]` The protocol gives timestamps and ids but no nonce or sequence, on purpose, so a client without server state can sign.
- `EcdsaPersonaSigner` is not thread-safe; NETWORK1 must serialize signing.

## Product decisions still needed

`[superseded 2026-09-28 by NETWORK0.md, section 11, "Open product decisions" (D1 to D9), which lists these five and the review's additions with the baseline each has today. Nothing there is decided.]` `[updated 2026-09-28, after the merge: the current status of every decision is kept in DecisionRegister.md. D3 is approved and D2 is approved in principle (2026-09-28); all others are still unresolved.]`

1. Keep `ProfileRetraction` as a signed document, or unpublish through an authenticated request? (NETWORK0.md, section 10, decision 2; now D1.)
2. Is a metadata-only snapshot the right first remote model, with the layout as schema 2, or should the layout come first? (D8.)
3. Persona display name: none, or a field in a future persona document? (D9.)
4. Whether the 32,000-character text limit should be tightened per field (the name) at the protocol level, or left to server policy. (D4.)
5. How the protocol assembly ships in the plugin package (fourth file versus linked sources). (D9.)

## Recommended NETWORK1 scope

Client side only, still no backend: `IPersonaKeyProvider` over a DPAPI-protected P-256 key in the plugin's configuration folder (generate once, never export); a snapshot builder from `ProfileDocument` (name and image references) `[corrected 2026-09-28: not "digests computed from the stored assets": what a digest covers, and whether original image bytes are ever uploaded or digested, is decision D5, and no milestone may upload originals or expose their digests before the image processing and privacy design is approved]`; a minimal publish and retract flow behind a feature flag with no UI beyond a command, that produces documents and, for now, writes them to a local outbox instead of a server; the request-proof signing tag and schema, defined and vectored the same way as the documents. Only then the backend, which reuses `AetherFrame.Protocol` unchanged.

`[updated 2026-09-28, after the merge: this paragraph was the night's recommendation, not a decision. Three parts of it were open owner decisions. "Generate once, never export" is superseded by D2, approved in principle on 2026-09-28: a key may leave the machine only inside an encrypted, portable .afpersona backup, never in plaintext, and the backup's security details await approval. "A DPAPI-protected P-256 key": the storage and platform choices are K1 to K3; both were measured working on native Windows, while under Wine DPAPI is obfuscation only, from source inspection. "The request-proof signing tag and schema" inside NETWORK1: the architecture review suggests reserving only the tag name until the transport is designed, which is also unresolved. See NETWORK1.md for the planned boundaries and DecisionRegister.md for the gates; apart from D2 in principle, none of this is approved.]`

## How to review

```
git -C "E:/Plugin development" log --oneline origin/master..claude/network0-protocol-foundation
git -C "E:/Plugin development" diff --stat origin/master...claude/network0-protocol-foundation
```

In the worktree, `DALAMUD_HOME` set as for any plugin build: `dotnet build AetherFrame.slnx -c Release`, then `dotnet test AetherFrame.Protocol.Tests/AetherFrame.Protocol.Tests.csproj -c Release --no-build`. Start with `docs/networking/ProtocolSpecification-v1.md`, then `SignedDocumentCodec.Verify`, `SigningInput.Create`, `PersonaPublicKey.FromBytes` and `ProtocolSignature.FromBytes`; they are the security-critical hundred lines.

One housekeeping note: two idle `python.exe` processes started by a mistyped command during the night are still waiting on stdin (PIDs 34636 and 45632 at the time of writing). They consume nothing and touch nothing; ending them is safe.
