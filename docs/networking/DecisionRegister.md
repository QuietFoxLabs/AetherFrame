# Networking decision register

**Status (2026-09-29): two decisions are the owner's approvals, and twenty-two more are approved under the owner's delegation.**
- **D3** is **APPROVED** by the owner.
- **D2** is **APPROVED IN PRINCIPLE** by the owner. Its technical details remain unresolved, pending later security approval.
- **N5** and **L6** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **D9b** and **P2** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **K1**, **K2**, **K6** and **K7** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **N3**, **D4**, **D5**, **D8**, **D9a**, **I1**, **N1**, **N7**, **P1**, **K3**, **K4**, and the new **R1**, **R2** and **R3**, are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decision batch A for NETWORK2".
- The owner also **approved in advance, with conditions,** NETWORK2's two signed-byte changes, N2-2 and N2-3 (September 29, 2026). This is not a decision of this register, only the owner's approval that NETWORK1.md's safeguard 3 requires. See "Approved decisions".

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

### Signed-byte changes in NETWORK2 (N2-2 and N2-3). APPROVED IN ADVANCE by the owner, with conditions (September 29, 2026)

NETWORK1.md's safeguard 3 lets signed bytes change only "in an increment the owner explicitly approves". Claude asked the owner in chat how to approve NETWORK2's two increments that change them:
- N2-2: the draft marker and the name rule;
- N2-3: ProfileSnapshot schema 2 (the layout) and the request proof.

The owner chose "Approve both now", whose stated terms were:

> You approve N2-2 and N2-3 in advance, as long as each has its decisions recorded, a clean independent and security review, and green CI.

**Scope.** The approval covers exactly those two increments, as [NETWORK2.md](NETWORK2.md), section 5, describes them. Each merges only when all of these hold:
- its decisions (N3 and D4; D5, D8, D9a, I1, S1, D7 and L8) are recorded in this register;
- an independent reviewer and a security-focused reviewer are clean on it;
- CI is green on its exact head.

**What it does not approve:**
- any of those decisions themselves, which are made and recorded in their own entries;
- any other change to signed bytes;
- the protocol freeze, which stays the owner's alone.

Only the owner can change this approval.

**How N2-3 is delivered.** APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026. N2-3 is delivered as two increments, each with the conditions above applied on its own:
- **N2-3a**, the layout schema: its decisions are D5, D8, D9a, I1, N1 and N7, all recorded in decision batch A.
- **N2-3b**, the request proof: its decisions are S1, D7 and L8, recorded in N2-3b before it merges.

The signed-byte changes the owner approved are unchanged: the layout schema and the request proof, nothing more. Rationale: two smaller pull requests are reviewed more thoroughly than one, and the layout does not depend on the request proof. This note records how the approval is applied; it does not change the approval, which only the owner can.

## Decisions approved under the delegation

These are Claude's decisions under the owner's delegation of September 29, 2026, not the owner's own. The owner can overrule any of them in the Owner inbox, and the reversal is recorded here.

### N5: the profile id belongs to profile documents only. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by NETWORK1 increment 1, the protocol API tidy ([#32](https://github.com/QuietFoxLabs/AetherFrame/pull/32)).
- `RemoteDocument` no longer has a `ProfileId`.
- A new abstract `RemoteProfileDocument : RemoteDocument` carries it. `ProfileSnapshot` and `ProfileRetraction` derive from it. Its constructor is private protected, so the document hierarchy stays closed to other assemblies.
- `VerifiedDocument.Profile` becomes `RemoteProfileKey?`:
  - for a profile document, it is the (signing persona, profile id) pair, exactly as before;
  - for a document that isn't about a profile, it is null. No such type exists in version 1, so it is never null today.
- Signed bytes don't change. Payloads, signing inputs, the specification's wire format and the committed vectors are all unchanged: the vector tests pass against the committed `vectors-v1.json`.

**Rationale.**
- This is the recommendation of the NETWORK0 review. Request proofs, share grants and persona statements aren't about a profile. Leaving the id on every document would force a breaking change once NETWORK1 code depends on the API; making the change now costs only the public API list.
- A nullable `Profile` makes every caller handle "no profile" when it compiles. An empty key instead could be stored or compared by mistake, and the profile pair is what a server keys ownership by.
- An intermediate abstract class, rather than an interface, keeps the hierarchy closed: another assembly can't make its own type claim to be a profile document.

**Not settled:**
- which later document types exist (S1 request proofs, S4 persona revocation, share grants), and how each names its subject;
- N1, the scope of revision and asset ids.

**Independent concurrence.** The change touches the ownership key a server uses, so a security-focused reviewer with no shared context examined it at `96deff5` (September 29, 2026) and **concurred**. Its findings:
- The profile pair is still derived only from the verified key and the decoded payload.
- No other assembly can derive a document type or construct a `VerifiedDocument`, apart from the unshipped test assembly, which the protocol opens its internals to. It checked this by compiling against the built DLL.
- A null `Profile` fails closed: it doesn't compile where a key is expected, and it never compares equal to a real key.
- No signed byte, codec, verification step or vector changed.

### L6: no key storage seam or server policy in the protocol. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by the same change.
- **The key provider.** `IPersonaKeyProvider`, and with it the `AetherFrame.Protocol.Integration` namespace, is removed from the protocol.
  - Key storage and choosing the active persona are plugin policy. The protocol's only signing seam is `IPersonaSigner`.
  - `AetherFrame.Personas` (#23) already models that policy with `IPersonaKeyStore` and the manager's `TryOpenActiveSigner`, which returns a typed availability and a revocable lease. Nothing implemented or used the provider.
- **The server limits.** `ProtocolLimits.FuturePolicy` is removed, and those limits become documentation only:
  - elements and Components per layout, processed image size, profiles per persona, active shares, and storage per persona;
  - their values are recorded, unchanged, in NETWORK0.md, section 7 ("Resource limits"). The specification's server obligation 8 names some of them (profiles per persona, storage per persona, active shares) and points there;
  - the protocol neither declares nor enforces them.
- `ProtocolLimits` keeps every limit the codecs enforce, unchanged.

**Rationale.**
- It follows the register's recommendation for both parts.
- The provider was a placeholder. Its synchronous single-signer getter can't express several personas (D3), an unavailable or locked key, prompting, or lease revocation. The persona foundation already replaces it on the plugin side.
- Server policy compiled into callers as constants means an assembly built earlier keeps old numbers after the policy changes. Documentation keeps one home for the numbers without making them a protocol contract. The recommendation also allowed "internal", but internal constants nothing reads would be dead code.

**Not settled:**
- the key store's shape and storage (K2, K9, increment 5);
- L4 (a public-only key accepted by the signer until its first `Sign`);
- the limit values themselves, which a backend (G4) confirms and enforces with its own configuration.

**Independent concurrence.** The same security-focused review concurred. Nothing referenced or enforced the provider or the limits, so removing them takes away no check or guarantee, and `IPersonaSigner` remains the only signing seam.

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

**Independent concurrence.** A security-focused reviewer with no shared context examined the change at `0c1cf96` (September 29, 2026) and **concurred**: P-256 ECDSA with P1363 and low-S is a standard construction every measured platform supports; the store accepts 32-byte scalars only, and `PersonaKeyMaterial.Import` runs every check (range, public point derived from the scalar alone, point match, and the platform holding the same scalar).

### K2: key storage on native Windows. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** The target for native Windows: DPAPI in CurrentUser scope with UI forbidden, its optional entropy derived from the envelope's header (so a blob is bound to its slot, its protector and its public key), in the plugin's own files under its configuration directory, one `.afkey` file per slot named by the slot alone; never `PluginConfiguration`, never Dalamud's reliable storage (which keeps copies the plugin cannot delete), never a persona identity in a name. What the key store core builds: the store (`ProtectedPersonaKeyStore`), the envelope (`AFPK` version 1), the two seams (`IPersonaKeyProtector`, `IPersonaKeyBlobStorage`) and the plugin's directory storage (`PersonaKeyFileStorage`, compiled only in the preview flavour, tested from the persona suite). What it does not build: the DPAPI protector itself, which is increment 7 and gets its own review under this entry. Until then no protector exists outside tests, and no key is written outside tests.

**Rationale.** The recommendation and the Windows measurements ([NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md), sections 1 and 5): DPAPI in CurrentUser scope protects a copied key file against other accounts, and against nothing that runs as the same user, including other plugins in the game process; the register says so plainly and the plugin will too. It protects against other machines only in part. Microsoft's documentation for `CryptProtectData` says a user with a roaming profile can decrypt on another computer. And in a domain, the domain controllers' backup key can recover the user's master key, by the BackupKey Remote Protocol ([MS-BKRP]). The plugin's own files let it delete a key it no longer holds; reliable storage keeps copies it cannot. Binding the blob to the header costs nothing and stops a blob from being moved under another slot or key.

**Not settled:** K3 (enabling by capability test), K9 (Wine and other platforms), the exact directory (increment 9's wiring), how the protector derives DPAPI's entropy from the header and what it reports as "locked on this account" (increment 7). Increment 7's protector must zero `CryptUnprotectData`'s output before `LocalFree`, and must not use the prompt structure, which Microsoft documents as deprecated. Before a record is persisted (increment 9), the storage's final rename must be written through (`MoveFileEx` with `MOVEFILE_WRITE_THROUGH` on Windows), and key files that no record names must be detected and reported (L12).

**Independent concurrence.** The same security-focused review **concurred**, from primary sources. `CryptProtectData` requires the same optional entropy to decrypt and authenticates the blob, so binding the blob to the header works; `CRYPTPROTECT_UI_FORBIDDEN` makes a call that would prompt fail instead. It asked for the qualification about other machines in the rationale, now made, and for the protector and rename requirements above.

### K6: key rotation. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** None in v1: a persona's key is its identity for its whole life. Migrating means creating a new persona, republishing under it, and retracting the old profiles with the old key (D1). The store therefore has no replace and no delete.

**Rationale.** Rotation with continuity of identity needs a signed link between keys, a server that honours it and a protocol change; nothing in NETWORK1 needs any of that, and a new persona is cheap. Without rotation the store's contract stays small enough to verify: one key per slot, written once.

**Not settled:** whether a later protocol version adds a signed successor link.

**Independent concurrence.** The same security-focused review **concurred**: rotation that keeps an identity needs a signed successor link, a server that honours it and a protocol change, none of which exist, and having no replace and no delete keeps the contract small enough to verify. It noted the cost, now stated in the store's contract: with no delete, a store cannot roll back a key it wrote when the check after the write fails (L12).

### K7: where cryptographic implementations come from. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Platform implementations only: .NET's `ECDsa` and `RandomNumberGenerator` over the platform (CNG on Windows, OpenSSL on Linux), and the same platform's APIs called directly where .NET does not expose them (crypt32's DPAPI, increment 7). No third-party cryptography package, and no algorithm written in this repository: not signing, not key derivation, not encryption, not verification in production. The existing managed validation checks are not cryptographic implementations and stay, in front of the platform (NETWORK1.md, safeguard 6): in the protocol, the curve-membership check on public keys (`P256Curve.IsOnCurve`) and the low-S check and normalization of signatures against `P256Curve.HalfN`; in the persona code, the range check on a private scalar in `PersonaKeyMaterial`, which runs in time independent of the scalar's value. The test fake protector implements nothing cryptographic and never ships. Neither our own code nor a dependency is chosen to keep the package at three files; either is a separate owner decision with its own review.

**Rationale.** [NETWORK1.md](NETWORK1.md), safeguard 5, made binding.

**Not settled:** K8 (a verification path that does not import through NCrypt, for Wine), which this entry does not allow by itself.

**Independent concurrence.** The same security-focused review **concurred**: platform `ECDsa` and random numbers, with crypt32 called directly for DPAPI (so no `ProtectedData` package is added), satisfy AUTOPILOT.md's rule against custom cryptography. It asked for the exemption of the existing managed validation checks, now stated above.

### Decision batch A for NETWORK2 (N2-1), September 29, 2026

The entries below were decided together for [NETWORK2.md](NETWORK2.md)'s increments. They are researched against the primary sources cited in each and recorded here before any code depends on them. None changes bytes by itself: N3 and D4 are applied by N2-2, and D5's wording by N2-3. Both increments change signed bytes, which the owner approved in advance on conditions (see "Approved decisions").

### N3: draft documents are marked in the signed bytes. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by N2-2 (the draft marker and the name rule, September 29, 2026). Until the owner freezes version 1, every document the library writes or accepts is a draft, marked in two places:
- **The envelope and the signing input** carry `protocolVersion` = `0x8001` (32,769) instead of `1`. The high bit marks a draft, and the low fifteen bits name the version it drafts.
- **The signing input's tag** is `AetherFrame.Protocol.SignedDocument.v1-draft` (44 ASCII bytes, length byte `0x2c`) instead of `…SignedDocument.v1`.

**Readers and later changes:**
- A draft-period reader accepts only `0x8001`, and refuses `1` as `UnsupportedVersion`. At the freeze the library accepts only `1` with the `v1` tag and refuses `0x8001`. No reader ever accepts both.
- **The persona identity derivation (`PersonaId.v1`) does not change.** An identity is a hash of a public key, not a signature, so a persona made during the draft period keeps its identity after the freeze; only its documents must be signed again.
- Every later signing context, starting with N2-3's request proof, carries a draft marker in its own tag in the same way.
- N2-2 regenerates the vectors and updates the specification, the reference implementation in the tests, and the independent checker, all together. In the specification it rewrites sections 5, 7.2 and 10, and the status paragraph, which today says no server accepts a draft and that the tag isn't expected to change: the two-player test's server accepts drafts.

**Rationale.** The two marks do different jobs:
- **The version number makes a mismatch fail early and plainly.** A reader stops at step 3 of the specification's section 7.2 with `UnsupportedVersion`, before any cryptography, instead of reporting `SignatureMismatch`.
- **The tag separates the signature domains.** Even a reader misconfigured to accept the draft number could never verify a draft signature as a final one, because the digest differs. This is the rule the specification's section 10 already sets for new versions.

The two-player test's server accepts drafts. After the freeze, no document signed during testing can be presented as a version 1 document.

**Not settled:**
- the freeze itself, which is the owner's alone;
- whether a later draft revision needs a number of its own. Schema versions already separate payload layouts inside one envelope version.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. The new tag is length-prefixed, so the digests differ; `0x8001` stops a mismatch at step 3 of section 7.2, before any cryptography; each reader matches exactly one marker; and `PersonaId.v1` is a hash of a key, never a signature, so keeping it creates no cross-domain risk.

### D4: the rule for a remote Plate's `name`. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by N2-2 to schema 1's `name` (`ProtocolName`, specification section 8.1.1), and by N2-3 to schema 2's. A `name` is valid when all of these hold:
- it is **1 to 64 Unicode scalar values** and **at most 256 bytes** of UTF-8;
- it contains none of these code points:
  - the C0 controls U+0000 to U+001F, U+007F, and the C1 controls U+0080 to U+009F (General_Category Cc in the Unicode Character Database);
  - U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR;
  - U+FEFF;
  - the twelve directional formatting characters of [UAX #9](https://www.unicode.org/reports/tr9/) (revision 52, Unicode 18.0): U+061C, U+200E, U+200F, U+202A to U+202E, and U+2066 to U+2069;
  - the invisible format characters U+00AD, U+180E, U+200B to U+200D, U+2060 to U+2064, U+206A to U+206F, U+FFF9 to U+FFFB, U+E0001, and U+E0020 to U+E007F.

**Nothing is normalized**, as for every protocol text (specification, section 2.3).

**Errors, in the specification's reading order (section 9.1), each checked in this order:**
1. an empty name is `InvalidLength` as soon as its length is read;
2. a byte length over 256 is `LimitExceeded`;
3. invalid UTF-8 is `InvalidText`;
4. a refused code point, which includes U+0000, is `InvalidText`;
5. more than 64 scalars is `LimitExceeded`.

The list is fixed code points, not Unicode properties, so what is valid never changes with the Unicode version.

The rule governs `name` only. Other texts are schema 2's (N2-3), and N7 governs how every text is displayed.

**The local Plate name is never altered to fit.** `PlateNaming` already turns control and format characters into spaces and caps a name at 64 UTF-16 units, so a name it produced can fail this rule only through U+2028, U+2029 or an unpaired surrogate. A name loaded from an older or hand-edited Plate file isn't normalized again, and can fail in other ways. In every case the publish flow refuses and asks the player to rename. Local naming, files and packages are unchanged.

**Rationale.**
- A Plate's name is what a viewer reads first, in lists and in logs. Control and directional characters let a name hide text or display it reversed: the "Trojan Source" technique, CVE-2021-42574.
- The limits keep a name to a line.
- Refusing is better than normalizing, which would change signed bytes after the fact.
- The recommended list is kept, verified against UAX #9's table of directional formatting characters. It gains the invisible format characters the security review asked for, which `PlateNaming` already folds away locally, so an honest publisher loses nothing, while a hostile one can't hide text in a name.

**Not settled:**
- confusable names ([UTS #39](https://www.unicode.org/reports/tr39/)), which a first test does not need;
- persona names, which do not exist (D9a);
- Hangul fillers, noncharacters and names made only of spaces, which are cosmetic and left to the UTS #39 work.
- `[added 2026-09-30]` The default-ignorable code points the list leaves out, which a renderer is expected to draw as nothing. Unicode 18.0 has 4,048 of them: U+034F; U+17B4 and U+17B5; the variation selectors U+180B to U+180D, U+180F, U+FE00 to U+FE0F and U+E0100 to U+E01EF; the format characters U+1BCA0 to U+1BCA3 and U+1D173 to U+1D17A; the reserved U+2065, U+FFF0 to U+FFF8, U+E0000, U+E0002 to U+E001F, U+E0080 to U+E00FF and U+E01F0 to U+E0FFF; and the Hangul fillers above. A name made only of them is accepted and shows nothing, and it can hide at least 64 bytes of data (one byte a variation selector: there are 256 of them, and 64 fit in 256 bytes). "Can't hide text" in the rationale holds for the listed characters only. Found by a post-merge review of N2-2, checked against Unicode 18.0's DerivedCoreProperties.txt and the code; open as L13.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. It checked the list against the Unicode Character Database (the 65 Cc code points) and UAX #9 (revision 52, Unicode 18.0.0), confirmed that `PlateNaming` folds Cc and Cf while U+2028, U+2029 and unpaired surrogates pass, and confirmed the CVE-2021-42574 citation. It asked for the exact error order and the invisible format characters, both added above.

### D5: an image digest covers the prepared copy. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** An `ImageReference`'s `sha256`, `format`, `byteLength`, `width` and `height` describe the **prepared copy** the client uploads, never the player's original file:
- **How a copy is prepared.** The publisher decodes the managed copy the Plate already uses and encodes the pixels again, through Dalamud's texture pipeline (`GetRawImageAsync` and `SaveToStreamAsync`), with no new dependency. The result is PNG, or JPEG for a JPEG source; N2-6 sets the encoder parameters.
- **What that drops.** Rebuilding the container drops every metadata block: EXIF, including GPS position; XMP; PNG text chunks; embedded ICC profiles; thumbnails.
- **What the publisher never touches.** It reads only the managed copy. It never touches the player's original file, and never puts the original's bytes or digest into a document.

The server verifies received bytes against the declaration before serving them (specification, section 13, rule 7). Whether it also processes them again is I2 (batch B).

N2-3 changes the specification's wording in section 8.2 from "SHA-256 of the source image bytes" to the prepared copy. That is wording only: there is no salt, and the layout doesn't change.

**Rationale.**
- The original's metadata can carry a location, a device and an editing history, and rebuilding the container is the only reliable way to drop every kind of it.
- Neither the original's bytes nor its digest is ever published. Anyone holding the original can still recognise the published pixels; that is inherent in sharing an image.
- The consent screen shows the prepared copy, so the player sees what is sent.

**Not settled:**
- I2;
- the encoder parameters, and whether colour is converted to sRGB before encoding (N2-6, with tests of the result). N2-6 also clears the colour under fully transparent pixels, which a straight-alpha round trip would otherwise keep, and the consent screen shows each whole prepared image, not only the part a Plate element crops.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. `GetRawImageAsync` and `SaveToStreamAsync` exist on `ITextureReadbackProvider` in the installed Dalamud; a round trip through a texture cannot carry container metadata; and decoding the player's own managed copies adds no surface beyond import. It asked for the narrower rationale and the two N2-6 points above.

### D8: no metadata-only snapshot reaches players. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by N2-3a (specification, section 8.5). Schema 1 of `ProfileSnapshot` (a name and image references) stays a test schema:
- the plugin never builds one to publish;
- the server (N2-7) refuses to publish one;
- the library keeps reading it, and its vectors stay, changed only as N3's marker and D4's name rule require (N2-2). Three of them hold names D4 refuses: an empty name, one with CR LF, TAB and U+FEFF, and a 32,000-scalar name; N2-2 turns them into rejected vectors and says where the general 32,000-scalar text limit stays tested (L2).

The first schema players publish is schema 2, with the layout (N2-3).

**Rationale.** A Plate without its layout is not what the owner means by showing a Plate. Publishing one would put documents into the world that no viewer can show properly and that would later need retracting.

**Not settled:** nothing further.

### D9a: a persona's label is private. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** A persona's label lives only in the persona registry on the player's computer, and nowhere else:
- no document, request, share code or server record;
- no log.

The protocol has no persona display name; what others see is the Plate's own content. The provisional `PersonaLabel` of #23 becomes this decision's implementation. A public persona name, if one is ever wanted, would be a per-snapshot field decided on its own.

**Rationale.** D3 keeps personas independent. A label such as "Main, Aria on Twintania" would tie a persona to a character the moment it was published.

**Not settled:** a public name (stage 2 at the earliest). N2-5 removes the "provisional" wording from `PersonaLabel` and `PersonaManager`.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: a local-only label is the only option consistent with D3 and ROADMAP.md, section 4, rule 8.

### I1: which images can be shared. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Publishing takes the images a Plate already holds as managed copies. Those are PNG, JPEG or WebP, whatever local import accepted through Dalamud's decoders (`ImageFormatSupport`). It shares only their prepared copies (D5), which are:
- 8-bit RGB or RGBA;
- the **first frame** of an animated image;
- at most 8,192 pixels a side and 20,000,000 pixels in all;
- at most 8 MiB each.

The per-Plate image count is schema 2's limit (N2-3). **An image over a limit is refused with a message; nothing is downscaled silently.** The consent screen shows the prepared copies, so a first frame instead of an animation, or colours after conversion, are visible before anything leaves.

**What a viewer accepts, whoever published.** A hostile publisher can skip preparation, so the limit is enforced where images are received, not trusted from the sender:
- schema 2's `ImageReference.format` allows only PNG (1) and JPEG (2) (N2-3);
- the viewer (N2-10) sniffs every image's bytes before decoding anything, and refuses anything but a non-animated 8-bit PNG, or an 8-bit baseline, extended or progressive JPEG (frame types SOF0 to SOF2) with 1 or 3 components, within the specification's section 8.2 limits, whatever I2 and D6 decide.

**Deviation from the recommendation, on sources only.** It proposed refusing animated WebP and CMYK JPEG. As *sources* they are accepted: preparation encodes the pixels again, so neither reaches a viewer in its original form. The risk the recommendation guarded against, which formats a viewer must decode, is covered by the rule above. Refusing them as sources would only turn away images the player already uses locally.

**Rationale.**
- The specification's limits (section 8.2) already bound the decoded size.
- Showing the prepared copy makes the player's consent informed.
- One rule for all animations is simpler than one per format.

**Not settled:** I2. `[updated 2026-09-29: schema 2's limits are settled by N2-3a (specification, section 8.5): at most 8 images, whose pixels total at most 33,554,432. The publisher refuses a Plate over them with a message, never dropping or downscaling an image. The security-focused reviewer of N2-3a recommended a cap on the images' total pixels, which bounds what a viewer decodes, and found it sound as implemented at `a388099`, with its exact boundary tested from `3528363`.]`

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026). It did **not concur** with the first wording, which relied on the publisher preparing images honestly, and asked for the receive-side rule, now above. It **concurred** with the amended entry on its recheck of `779e873`, adding that the sniff runs before any decoding and allows only frame types SOF0 to SOF2, also above.

### N1: identifier scope. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Every identifier is scoped to a persona:
- **Revision ids** are scoped to (persona, profile id): the uniqueness in the specification's section 13, rule 4, is checked within that pair only.
- **Asset ids and image digests** are scoped to the persona. A server:
  - stores and serves an asset under (persona, asset id);
  - never deduplicates or compares digests across personas;
  - never answers whether another persona holds an asset or a digest.
- **Profile ids** are already scoped (specification, section 8.4).
- **The client** picks every identifier from a cryptographically secure generator (specification, section 2.6) and never reuses one across personas.

**Rationale.** Deduplicating across personas would let one persona probe whether another holds an image (upload a digest and see whether it is already stored), or reference or overwrite another's asset. Scoping costs only storage. N2-7 applies it. In N2-3, the specification's section 8.4 loses the N1 half of its "Open" paragraph; the N2 half (replay) stays until batch B.

**Not settled:** nothing further.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: storage scoped to the persona, with no cross-persona deduplication or existence answers and fresh random ids never reused across personas, closes the probe-and-overwrite problem.

### N7: names and texts are plain text. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Every consumer of a document treats every text in it (the name, and every text of schema 2) as plain text. Consumers are the plugin's viewer, the server, logs and any tool. A text is never interpreted as any of these:
- markup, or a format string;
- a game text command, a chat macro or an `SeString` payload;
- a path, a URL or a command.

How texts are handled:
- **Display.** Texts are drawn through calls that don't format their argument, and are never auto-linked. A remote text is never an ImGui label or ID, because `##` and `###` in a label change what is shown and which widget it is.
- **Logs.** Texts are logged only through the plugin's redacting log, with a length bound and control characters escaped, since schema 2 texts may hold line breaks.
- **The server.** It never puts a text into HTML without escaping it, serves no HTML page of a Plate at all in stage 1, and passes texts to its database only as query parameters.

N2-3 writes this into the specification as a consumer obligation.

**Rationale.** Texts come from other players. Format strings, game text payloads and HTML are the usual ways plain text turns into behaviour.

**Not settled:** nothing further.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. It asked for the ImGui label rule, escaped control characters in logs and parameterised queries, all added above.

### P1: where "this Plate was published" is remembered. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** A **publication index**, one per persona, in the plugin's own files under its networking directory, each file named by the persona's local slot, never by its identity (NETWORK1.md, safeguard 7). For each published profile it records:
- the local Plate's id;
- the profile id;
- the latest revision id;
- the share code;
- the time of the last publish.

**Where it never appears.** Never in Plate JSON, character bindings, `.aetherframe` packages, Templates or the plugin configuration. Profile ids are never logged in the clear (`LogPrivacy`, NETWORK1's safeguard 7). Share codes must not be either: batch B gives them a fixed prefixed form, and `LogPrivacy` gains it in the same change that first handles a code.

**Deleting or renaming a Plate never publishes or unpublishes anything by itself.** The sharing view lists published profiles whose Plate is gone and offers to unpublish them. Losing the index loses no Plate. It does lose the local list of what was published: a signed request that lets the server list a persona's profiles is batch B's to decide.

**Rationale.** Plate JSON travels: export, Duplicate and Save as Template copy it, including unknown fields. Publication state stored there would leak into copies and packages (NETWORK1.md, system 4).

**Not settled:** recovering the list from the server (batch B).

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: Plate JSON travels through export, Duplicate and Save as Template, unknown fields included, so publication state belongs outside it. It noted that share codes aren't redacted yet and that index files must be named by slot, both now stated above.

### K3: where persona features turn on. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Persona features (creating, opening or using a key, and publishing) turn on for a session only when both of these hold:
1. **A capability probe passes.** Once per session, off the framework thread, the probe runs the exact chain the plugin uses, with a throwaway key and never a persona key:
   - generate, export, and import through `PersonaKeyMaterial`'s checks;
   - sign, and verify through the protocol;
   - protect and unprotect through the protector, with an envelope header as context;
   - write and read the key file storage in a temporary directory.
2. **The protector in use claims protection on this platform.** The DPAPI protector (N2-4) claims it only on native Windows. Under Wine, DPAPI is obfuscation only (NETWORK1_CryptoCompatibility.md, section 3), so it claims none there, and persona features stay off until K9 decides a store for that case.

**Deciding by capability, not by name.** Which operations work is decided by the probe, never by the operating system's name. The one platform fact used is the protector's own statement about protection, which no probe can measure.

**Conditions for N2-4.** The claim of protection rests on positive evidence and fails closed:
- It is made only when a blob the protector just produced carries the Windows DPAPI provider identifier (`df9d8cd0-1501-11d1-8c7a-00c04fc297eb`). Wine's implementation writes its own marker instead.
- It never rests on `wine_get_version`, which wine-staging can hide, or on environment variables.
- Before viewing turns on, the probe verifies a committed known-answer document and refuses a tampered copy of it as `SignatureMismatch`. A round trip alone doesn't show that verification works.

**When either condition fails:**
- persona features are off for the session, with one message naming the missing capability, distinct from a protocol refusal;
- **viewing** a shared Plate needs no persona, and turns on whenever the probe's verification step passes (K8 decides a verification path where it doesn't);
- every local feature stays on;
- nothing states that players on other systems are excluded; the message says the feature isn't available on this system yet.

**Rationale.** This is the recommendation (NETWORK1_CryptoCompatibility.md, section 6), plus the one gap it left: a probe tests function, not protection. A store that only obfuscates must not be offered as protection (section 5).

**Not settled:** K8, K9, macOS, and whether TLS certificate validation under Wine can be relied on for viewing (nobody has assessed it).

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**, with the conditions for N2-4 above. The exception only ever withholds a protection claim, so it fails toward "off", and no probe can measure confidentiality. It read Wine's `protectdata.c`, and confirmed that wine-staging's patch hiding Wine's exports exists.

### K4: what comes before a persona's first real publish. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Before a persona's first publish to a real server, the player either has an encrypted backup of it, or explicitly acknowledges, for that persona, the following. Losing its key means never being able to update or unpublish what it published, and no account exists to recover it.

**In NETWORK2's first stage there is no backup,** so the acknowledgement is required:
- it is a separate confirmation with that text, naming the ordinary ways a key is lost: reinstalling Windows, moving to a new PC, deleting the plugin's data, or an administrator resetting the Windows password, which loses the key DPAPI protected it with;
- it is recorded per persona in the persona registry, and asked once per persona;
- it is repeated in the persona window whenever a persona is created.

Once the backup exists (stage 2), the first publish offers the backup first and the acknowledgement as the alternative.

**Rationale.** Without the key nothing can be retracted: that is D2's premise. The player has to know it before content leaves the computer.

**Not settled:** the backup (the D2 details, K5).

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: for stage 1 on the owner's server, an informed acknowledgement per persona is proportionate, because key loss never touches local Plates, only retraction, and the owner can remove content on the server. It asked for the concrete ways a key is lost in the text, now above.

### R1: how a viewer finds a Plate in the first test. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** **Share codes.**
- The server issues one random code per published profile (persona, profile id) and returns it only to the publisher.
- Anyone holding the code can view that profile's latest revision until it is unpublished.
- In stage 1 the code is the only way to reach a profile. There is no directory, search, listing or enumeration, and no lookup by persona, character, World or anything else.
- A code binds nothing to a character.

**How codes travel.** A code is sent in a request's body or a header, never in a URL, so no access log records it. The server returns a code only in reply to a publish carrying a fresh request proof (S1), never for a replayed document.

**Left to batch B:** the code's format and length, with at least 64 bits from a cryptographically secure generator; a fixed prefixed text form that `LogPrivacy` redacts; and rate limits on lookups.

**Target lookup** (by targeting a character in game) is stage 2, with its own privacy decision and an opt-in binding.

**Rationale.** Sharing that the publisher starts deliberately and the viewer opens explicitly, with no character binding (ROADMAP.md, section 4, rule 8). A code is a capability in the sense of the W3C TAG's [Good Practices for Capability URLs](https://www.w3.org/TR/capability-urls/). Anyone who has it can use it, so it leaks the way a link does, and unpublishing revokes it. Stage 1 departs from two of that document's recommendations on purpose: codes don't expire, and a code can't be replaced without unpublishing. Both would complicate a first test, and both can be added later without changing the protocol.

**Not settled:** the format, the length and the rate limits (batch B); expiring or replacing codes; target lookup (stage 2).

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: a bearer code with no enumeration and no character binding fits rule 8, and at least 64 random bits with rate limits is an adequate floor. It asked for codes to stay out of URLs, for the fresh-proof rule and the prefixed form, and for the TAG departures to be recorded, all now above.

### R2: the transport. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Applied by N2-9 and N2-7.
- **HTTPS only**, to one DNS hostname fixed in the preview build (set in N2-8):
  - the certificate is validated normally, never disabled or pinned to a self-signed one;
  - there is no plain HTTP, no user-entered server address, and redirects are not followed.
- **The client stack.** .NET's `HttpClient` over `SocketsHttpHandler`, with Dalamud's `Dalamud.Networking.Http.HappyEyeballsCallback` as its connect callback for dual-stack connections (Dalamud v9 and later). Every call runs off the framework thread. The handler's defaults are overridden where they matter:
  - redirects are off (the default follows up to 50);
  - cookies are off;
  - no credentials are ever sent: `Credentials` and `DefaultProxyCredentials` stay empty, so no NTLM or Kerberos exchange happens;
  - the system proxy may be used, since TLS stays end to end through it, but never with credentials;
  - automatic decompression is off;
  - HTTP/1.1 or HTTP/2 only, never HTTP/3;
  - explicit connect and request timeouts (the defaults are none and 100 seconds), and a response size bound far below the default 2 GiB buffer;
  - certificate revocation is not checked, as by default. The server's Let's Encrypt certificates last 90 days or less, and N2-8 may choose a shorter-lived profile.
- **Traffic only on a player's action:** publish, unpublish, open a code, refresh. No polling, no background traffic, no telemetry.
- **Request and response bodies.** Requests carry the signed documents' exact bytes and the prepared image bytes. Responses are small JSON objects with closed schemas, parsed strictly with bounded sizes.
- **Version checks.** Every request names the plugin's version, and nothing else about the player or the machine, so the server can refuse an outdated client with a clear message.
- **No cookies and no accounts.** Identity comes only from signatures (specification, section 13, rule 2).

**Rationale.** Dalamud requires HTTPS with a certificate from a trusted authority, and a DNS hostname rather than an IP address. It recommends dual-stack support and version checks ([Plugin Technical Considerations](https://dalamud.dev/plugin-development/technical-considerations/), [What's New in Dalamud v9](https://dalamud.dev/versions/v9/)). A fixed hostname means no one can point a player's plugin at a hostile server, as long as the owner keeps the domain. A lapsed domain could be registered by someone else and given a valid certificate, so the owner keeps it on automatic renewal (NETWORK2.md, section 4).

**Not settled:** the hostname (N2-8), and the server's endpoints (N2-7).

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. It verified Dalamud's requirements ("must" for HTTPS, a trusted certificate and a hostname; "should" for dual-stack and version checks) and measured .NET 10's handler defaults, which led to the overrides listed above.

### R3: where network code may live. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** An exact allowlist, and only in one place: the preview flavour, under `Services/Network`. Nothing else in the plugin may use a networking type, and the player flavour contains none at all, as today.

**Allowed there:**
- the assemblies `System.Net.Http` and `System.Net.Primitives`, and `System.Net.Security` only if R2 ever sets a TLS option;
- every type in `System.Net.Http` and `System.Net.Http.Headers`;
- `System.Net.HttpStatusCode`;
- `System.Net.Security.SslClientAuthenticationOptions`, only under the same condition;
- Dalamud's `HappyEyeballsCallback`;
- `System.Net.Sockets.AddressFamily`, only as the type of that callback's constructor parameter. Dalamud's shared instance is internal, so the plugin must construct the callback itself.

**Refused everywhere:** every other type in `System.Net.Sockets` (`Socket`, `TcpClient`, `NetworkStream` and the rest); `SslStream`; WebSockets; QUIC; `Dns`; `WebRequest`, `WebClient` and `HttpListener`; mail; and Dalamud's `Util.OpenLink`.

The boundary tests change in N2-9, the first change that brings network code, to enforce exactly this list, both by referenced types in the compiled flavour and by a source scan. Until then they keep refusing every networking API everywhere, which is NETWORK1.md's safeguard 1. The source scan's `Sockets` substring would match `SocketsHttpHandler`, so N2-9 makes it match whole names.

**Rationale.** It is the smallest surface R2 needs, found by compiling a minimal R2 transport against the installed Dalamud and reading the type references it produces. Dalamud's callback provides dual-stack connections without the plugin opening sockets itself.

**Not settled:** nothing further.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026). It did **not concur** with the first wording, which refused `System.Net.Sockets` outright although R2's callback can't be constructed without `AddressFamily`, and a status code needs `HttpStatusCode`. It asked for this exact allowlist, and **concurred** with the amended entry on its recheck of `779e873`. It confirmed that the list is exactly what its compiled probe referenced, and that both ways of overriding certificate validation reference `SslPolicyErrors`, outside the list, so the type check refuses them too.

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

- **Approved so far:** D3 (APPROVED) and D2 (APPROVED IN PRINCIPLE) by the owner; N5, L6, D9b, P2, K1, K2, K6, K7, N3, K3 and P1 (APPROVED, Claude, under the owner's delegation of September 29, 2026). **Every row of this table except "D2 details", which has its own gate, is now approved,** so G1 is complete for native Windows. K8 and K9 below still gate any other platform, and the "D2 details" still gate any `.afpersona` file.
- **No row of this table other than "D2 details" remains to be approved** before a persistent private key is created outside tests on native Windows.
- **"D2 details"** has its own gate: before any `.afpersona` file is written or restored outside tests.
- **Approved rows** state the approved option in the recommendation column.

| Id | Question | Baseline or current state | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D2 | Recovery from key loss | None. Without the key nothing can be retracted, and there is no account. | Approved in principle: encrypted, portable `.afpersona` backups and restoration, including on another computer without a hosted account; no plaintext private key export (see "Approved decisions") | no | **APPROVED IN PRINCIPLE (2026-09-28)** |
| D2 details | Backup encryption scheme, password policy, key derivation parameters, recovery warnings, implementation | Undefined | Subject to later security approval. Related: K4 and K7 (approved), K5 (UNRESOLVED). No `.afpersona` file may be written or restored outside tests before this is approved. | no | UNRESOLVED |
| D3 | How many personas an installation holds, and how one is chosen | Undefined | Approved: several independent personas; manual selection and switching; one active at a time for identity-dependent operations; never bound automatically to game identifiers; switching never alters saved Plates or triggers publishing (see "Approved decisions") | no | **APPROVED (2026-09-28)** |
| D9b | How the protocol code ships in the plugin (fourth DLL or sources compiled in) | The preview flavour compiles the sources in; player builds compile none | Approved: the sources compiled into AetherFrame.dll, the package at three files, the standalone projects canonical, and only in the preview flavour (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| N3 | Whether draft documents are distinguishable from final v1 in the signed bytes | Drafts use version 1 and the `…SignedDocument.v1` tag | Approved: drafts carry `protocolVersion` `0x8001` and the tag `…SignedDocument.v1-draft` until the owner's freeze; readers accept exactly one of draft and final; identities unchanged (see "Decision batch A") | **yes** | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| N5 | Whether the profile id stays on every `RemoteDocument` | It did, until the protocol API tidy | Approved: the profile id moves to a closed abstract `RemoteProfileDocument` that the snapshot and retraction derive from; `VerifiedDocument.Profile` is null for a document that isn't about a profile; no signed-byte change (see "Decisions approved under the delegation") | no (public API) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| L6 | Where the provisional `IPersonaKeyProvider` and `FuturePolicy` live | Both were public and marked provisional, until the protocol API tidy | Approved: both removed from the protocol. Key storage and the active persona are plugin policy (`AetherFrame.Personas`); the server-only limits are documentation only, in NETWORK0.md, section 7 (see "Decisions approved under the delegation") | no (public API) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K1 | Persona key algorithm | P-256 ECDSA, P1363, low-S (NETWORK0); the key store core holds P-256 scalars only | Approved: keep it (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K2 | Key storage on native Windows | The store core, the envelope and the directory storage exist behind a protector seam; no protector ships | Approved as the target: DPAPI, CurrentUser scope, UI forbidden, entropy from the envelope header, in the plugin's own files named by slot; never plugin configuration or Dalamud reliable storage. The DPAPI protector itself is increment 7 with its own review (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K3 | Platform enablement policy | The architecture review first proposed disabling personas under Wine | Approved: persona features only where a full-chain capability probe passes and the protector claims protection on this platform (DPAPI: native Windows only); viewing needs only the probe's verification step; local features always unaffected (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K6 | Key rotation | None; the store has no replace and no delete | Approved: none in v1. Migrating means a new persona, republishing, and retracting the old profiles with the old key (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K7 | Where cryptographic implementations come from | Platform only (.NET over CNG/NCrypt) | Approved: platform implementations only, .NET or the same platform's APIs called directly. A third-party library or an algorithm in our own code is a separate owner decision with its own review, and is never chosen to keep the package at three files (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| P1 | Where "this Plate was published" is remembered | Undefined | Approved: a private publication index per persona in the plugin's networking files; never in Plate JSON, bindings, packages, Templates, configuration or logs (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| P2 | Who can create keys during NETWORK1 | Nobody yet: the preview flavour holds the code, no key store exists | Approved: preview builds only, by the compile-time switch `AetherFrameNetworkPreview`; player and official builds contain none of the networking code (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |

Only if persona features are pursued under Wine, Proton or macOS. These must be decided before a persistent key is created there, and only after measurements under those platforms:

| Id | Question | Current state | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| K8 | A protocol verification path that does not import keys through NCrypt | `Verify`, and `Sign`'s self-check, always import through .NET's `ECDsa`. From source, that fails under every Wine version examined. | Decide only after Wine measurements. Options: a platform BCrypt verifier supplied by the plugin (public API change), or verification in our own code (an algorithm under K7, needing independent review). The threat model changes either way. | no | UNRESOLVED |
| K9 | Key storage where DPAPI gives no protection (Wine) | Wine DPAPI is obfuscation only (source) | Decide only after measurements. Options: a passphrase-wrapped store whose primitives are verified under the target Wine builds, an explicitly disclosed unprotected state, or no support. | no | UNRESOLVED |

## 2. Decisions needed before the snapshot builder, image work or a signed-byte change (G2)

| Id | Question | Baseline | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D4 | Rules for the schema-1 `name` | Any text without U+0000, up to 32,000 scalars | Approved: 1–64 scalars, at most 256 bytes, refusing C0/C1 controls and DEL, U+2028, U+2029, U+FEFF, UAX #9's twelve directional formatting characters and the invisible format characters listed in the entry; no normalisation (see "Decision batch A") | **yes** | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| D5 | What an image digest covers; metadata | "The source image bytes" | Approved: the digest and declarations describe the prepared copy (decoded and encoded again, which drops all metadata), never the original (see "Decision batch A") | wording only, unless a salt is added | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| D8 | Whether metadata-only schema 1 is ever exposed to players | Test-only | Approved: no; schema 1 stays a test schema the plugin never publishes and the server refuses, and players publish schema 2 with the layout (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| D9a | Persona display name | None in the protocol. `AetherFrame.Personas` (#23) implements the recommendation provisionally as `PersonaLabel`, so the persona model can be exercised; the plugin does not reference that assembly. | Approved: a private local label only, never in any document, request, record or log; no public persona name in v1 (see "Decision batch A") | no (for now) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| I1 | Which images can be shared | Undefined | Approved: managed PNG, JPEG and WebP sources, shared only as prepared 8-bit RGB(A) PNG or JPEG copies, first frame of an animation, within the specification's limits, over-limit images refused; schema 2 allows only PNG and JPEG, and the viewer sniffs and refuses anything else whoever published (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |

## 3. Decisions needed before the freeze or the first real server (G3)

| Id | Question | Baseline | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D1 | What a retraction means | A signed, terminal retraction with a permanent minimal record | Keep it signed and terminal; republishing uses a new profile id | yes, only if changed | UNRESOLVED |
| D6 | Whether viewers receive signed envelopes or server-checked content | Undecided | Server-checked content (reversible later; signed proofs handed out cannot be recalled) | no | UNRESOLVED |
| D7 | Whether documents are bound to a deployment | Not bound | Bind through a short-lived publish request proof. The alternative is a deployment id in the signing input, which is only possible before the freeze. | yes, only for the alternative | UNRESOLVED |
| K4 | Whether a backup is required before the first real publish | Undefined | Approved: a backup, or (while none exists) an explicit per-persona acknowledgement that a lost key means never updating or unpublishing, before the first real publish (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K5 | Backup passphrase rules and key derivation route | Undefined | At least 15 characters, or a generated code; NFC; never truncated. The key derivation must use a route verified on every supported platform: `Rfc2898DeriveBytes.Pbkdf2` fails under Wine before 11.3, from source and a published report. Because D2 is approved in principle, this is also needed before any `.afpersona` file is written outside tests (part of "D2 details"). | no | UNRESOLVED |
| N1 | Scope of revision and asset ids | Profile scoping only | Approved: revisions per (persona, profile); assets and digests per persona, never deduplicated or compared across personas (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| N7 | Whether consumers must escape names before display or logging | Nothing obliges them | Approved: every consumer treats every text as plain text: no markup, format strings, game text payloads, paths, URLs or commands; never auto-linked; escaped in any HTML (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R1 | How a viewer finds a Plate in the first test | None | Approved: share codes issued per published profile, the only way to reach one in stage 1; no directory, search or lookup; target lookup is stage 2 (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R2 | The transport | None | Approved: HTTPS to one fixed DNS hostname, `HttpClient` with Dalamud's dual-stack callback, traffic only on a player's action, signed bytes as bodies, strict small JSON responses, version checks, no cookies or accounts (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R3 | Where network code may live | Nowhere (NETWORK1.md, safeguard 1) | Approved: an exact allowlist in the preview flavour under `Services/Network` only: `System.Net.Http` (with `.Headers`), `HttpStatusCode`, Dalamud's dual-stack callback and its `AddressFamily` parameter, `System.Net.Security` only for a TLS option; every other networking type refused; nothing in the player flavour (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |

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
| L12 | Key files that no persona record names. The store has no delete (K6), so when the check after a durable write fails (a read-back that still fails after its retries, or bytes that differ), `AddKey` reports `CustodyFailed` and the envelope may stay under that fresh slot, which is never recorded or reused. A crash between the key write and the record write leaves the same kind of file. The wiring (increment 9) must detect such files and report them to the player; whether to offer removal, quarantine or recovery is decided there, with a security review. Found by the key store core's independent reviews (September 29, 2026). | UNRESOLVED (before increment 9 persists a record) |
| L13 | The default-ignorable code points D4 does not refuse (4,048 in Unicode 18.0, listed under D4's "Not settled"): a name made only of them is accepted and shows nothing, and a name can hide at least 64 bytes of data in them. Refusing them changes which names are valid: a signed-byte change that NETWORK1.md's safeguard 3 reserves for the owner. The trade-off to weigh: local naming (`PlateNaming`) already folds the format characters among them to spaces, so refusing those costs an honest player nothing; but U+FE0E and U+FE0F choose how an emoji is drawn (U+2764 U+FE0F is a common heart), so refusing every variation selector would refuse names players type. | UNRESOLVED (with the UTS #39 work; needs the owner's approval before it changes the rule) |

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
