# Networking decision register

**Status (2026-09-30): two decisions are the owner's approvals, and thirty-nine more are approved under the owner's delegation.**
- **D3** is **APPROVED** by the owner.
- **D2** is **APPROVED IN PRINCIPLE** by the owner. Its technical details remain unresolved, pending later security approval.
- **N5** and **L6** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **D9b** and **P2** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **K1**, **K2**, **K6** and **K7** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decisions approved under the delegation".
- **N3**, **D4**, **D5**, **D8**, **D9a**, **I1**, **N1**, **N7**, **P1**, **K3**, **K4**, and the new **R1**, **R2** and **R3**, are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "Decision batch A for NETWORK2".
- **S1**, **D7** and **L8** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "The request proof's decisions (N2-3b)".
- **L10**, **L12** and the new **P3** are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**. See "The persona registry's decisions (N2-5a)".
- **D1**, **D6**, **N2**, **N6**, **S2**, **S3**, **S4**, **I2**, and the new **R4**, **S5** and **P4**, are **APPROVED (Claude, under the owner's delegation of September 29, 2026)**, and **K5** moves to the "D2 details" gate, where it stays UNRESOLVED. See "Decision batch B for the server (N2-7)".
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

**Not settled:** K3 (enabling by capability test), K9 (Wine and other platforms), the exact directory (increment 9's wiring), how the protector derives DPAPI's entropy from the header and what it reports as "locked on this account" (increment 7). `[updated 2026-09-29: N2-4 settles the entropy, the header byte for byte, and reports every blob it cannot open as unavailable; telling "locked on this account" apart is N2-5's, below.]` Increment 7's protector must zero `CryptUnprotectData`'s output before `LocalFree`, and must not use the prompt structure, which Microsoft documents as deprecated. Before a record is persisted (increment 9), the storage's final rename must be written through (`MoveFileEx` with `MOVEFILE_WRITE_THROUGH` on Windows), and key files that no record names must be detected and reported (L12).

**Independent concurrence.** The same security-focused review **concurred**, from primary sources. `CryptProtectData` requires the same optional entropy to decrypt and authenticates the blob, so binding the blob to the header works; `CRYPTPROTECT_UI_FORBIDDEN` makes a call that would prompt fail instead. It asked for the qualification about other machines in the rationale, now made, and for the protector and rename requirements above.

**Applied by N2-4 (NETWORK1 increment 7), September 29, 2026.** `DpapiPersonaKeyProtector` (id `windows.dpapi.currentuser.v1`, compiled only in the preview flavour):
- It calls `CryptProtectData` and `CryptUnprotectData` directly, in CurrentUser scope, with `CRYPTPROTECT_UI_FORBIDDEN` and no prompt structure. The boundary tests allow exactly these two declarations and `LocalFree`, only in this class and only in the preview flavour. The player build declares no P/Invoke of its own, and neither flavour loads a native library itself, makes a delegate from a function pointer, calls through an unmanaged function pointer or declares a COM import.
- Its optional entropy is the envelope's header, byte for byte, so a blob opens only under its own slot, protector id and public key.
- Its managed copies of the secret, the input's and the one `Unprotect` returns, are on the pinned object heap, where the garbage collector never moves them, and are zeroed before release (the second by its caller: the store zeroes what `Unprotect` returns). DPAPI's own output is zeroed before `LocalFree`.
- Where crypt32 is absent, protecting throws and opening returns null, which the capability probe turns into "persona features off".
- Measured on Windows 11: DPAPI authenticates neither bytes appended after a blob nor its 16-byte provider identifier. Several byte strings can therefore open one key, but none opens a different key: the ciphertext and the entropy are authenticated, and the store checks the scalar against the envelope's public key, the application-level check Microsoft's `CryptUnprotectData` page advises. Nothing identifies a key by its file's bytes.
- DPAPI "rests on the password provided" ([Windows Data Protection](https://learn.microsoft.com/en-us/previous-versions/ms995355(v=msdn.10))): a copied profile opens wherever that Windows password is known, and at once if the account has none. N2-5's persona window says so alongside K4's acknowledgement.

It reports every blob it cannot open as unavailable (null), as the protector seam requires. **Not settled after N2-4:** whether the persona window (N2-5) tells a key locked to another account or machine apart from a damaged one; DPAPI's error codes would allow it, but the seam has no way to say which.

**Applied by N2-5b, September 30, 2026.**
- **The directory.** `Network\Personas\` under the plugin's configuration directory holds the registry (`registry.afpr`) and the lock (`instance.lock`); the key files are in its `keys\`, named by slot alone. No name holds an identity.
- **The written-through rename.** A key file's final move is `WrittenThroughMove.MoveNew`: `MoveFileExW` with `MOVEFILE_WRITE_THROUGH`, never `MOVEFILE_COPY_ALLOWED`, on the extended-length path. The registry's is `WrittenThroughMove.Replace`, which adds `MOVEFILE_REPLACE_EXISTING`. The boundary tests allow exactly that declaration, in that class, in the preview flavour only. Off Windows it falls back to `File.Move`, which isn't durable; that serves the persona suite's Linux run only, since persona features never turn on there (K3).
- **Telling a key locked elsewhere from a damaged one: settled, APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026.** Stage 1 doesn't tell them apart: the protector seam returns null for both, and so does a forged header. The persona window's wording names both causes: "damaged, or made on another Windows account or PC".
- **Logs.** The persona session logs error kinds (each exception's type name and HResult, and any persona error), never an exception's text, which can hold a path with the Windows user name.

**Independent concurrence (N2-5b).** A security-focused reviewer examined N2-5b's design (September 30, 2026). Revision 1 needed ten changes: startup counted as an operation for dispose; a lock never released at a timeout, retried at start, with Try again; K2's disclosure in the window; this settlement; no exception text in logs; no fallback on Windows, and long paths; spare copies named only after they are checked; a scan for the registry-less constructor; brief retries of a move a scanner blocks; and the window's wording. It **concurred** with revision 2 on four conditions, which N2-5b applies: after the session closes, its lock is released first and nothing is logged; a retry never runs the probe again; the check for the registry-less constructor reads the compiled IL; and the lock's in-process reload is checked in game. The disclosure, the wording that names both causes of an unreadable key, the spare copies and the window's wording reach code only with N2-5c's window. It then examined the implementation at `422b640` (September 30, 2026) and **concurred**: the session takes the lock only after the probe, loads the registry only with the lock held, and has exactly one party release the lock, decided under its monitor and before that work's unload registration ends, so neither a timeout nor a finalizer ever releases it; a probe that fails or throws turns persona features off for the session with no retry; logs carry type names, HResults and persona errors only, tested with a path planted in every failure; and the preview DLL's IL shows nothing outside `PersonaManager` constructing one without its registry. The solution built with 0 warnings, and the persona, plugin and protocol suites and the preview flavour's 12 boundary tests passed. Its one note on the code, that the key store's report lines should go through the session's log so that nothing is written after it closes, is applied. It rechecked `658b837` (September 30, 2026) and its concurrence stands: its note and the general review's fixes are applied, a start closed during the probe stops before the lock, a probe that throws views nothing, a closed view is no longer left busy, and every release is counted.

**Applied by N2-5c, September 30, 2026.** The persona window says what this entry promises: next to K4's step, that a copy of the Windows profile opens the key wherever the Windows password is known (at once when the account has none), that any program running as the player, other Dalamud plugins included, can use it, and that on a work or school PC the organisation may be able to recover it. A key that doesn't open when checked is described with both causes, "damaged, or made on another Windows account or PC", as settled above; a key file whose header can't be read, which the audit finds without opening any key, is described with that case's own causes instead. The key folder's path appears only in the window, from `%APPDATA%` on when it lies there, or else from `%USERPROFILE%` on when it lies under the user's profile, so a screenshot doesn't show the Windows user name, and is copied only on a click.

**Independent concurrence (N2-4).** A security-focused reviewer with no shared context examined `ffed076` and rechecked its fixes at `3a2d1f0` (September 29, 2026), and **concurred**, from Microsoft's `CryptProtectData`, `CryptUnprotectData`, `DATA_BLOB`, `LocalFree` and `GC.KeepAlive` pages, .NET's own DPAPI interop, Wine's `protectdata.c` and measurements on Windows 11. The declarations match the documented signatures, UI is forbidden and no prompt structure is passed. The managed copies of the secret are on the pinned object heap, every buffer passed to DPAPI is kept alive across the call, DPAPI's output is zeroed before `LocalFree`, and the store zeroes what `Unprotect` returns. Changed or missing entropy and truncation are refused, and so is a change to any single byte (measured byte by byte), except bytes appended after the blob and the provider identifier. DPAPI authenticates neither, but with them changed the same key still opens, never a different one. It asked for this reasoning, the narrower boundary claim and the pinned output copy, now made. The general review found, and this review had missed at `e69d049`, that pinned buffers whose addresses had been taken could be collected during the call. This review reproduced that under forced collections (7 of 240,000 openings and 6 of 60,000 round trips failed) and saw no failure after the fix.

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
- `[added 2026-09-30]` The default-ignorable code points the list leaves out, which a renderer is expected to draw as nothing. Unicode 18.0 has 4,048 of them: U+034F; U+17B4 and U+17B5; the variation selectors U+180B to U+180D, U+180F, U+FE00 to U+FE0F and U+E0100 to U+E01EF; the format characters U+1BCA0 to U+1BCA3 and U+1D173 to U+1D17A; the reserved U+2065, U+FFF0 to U+FFF8, U+E0000, U+E0002 to U+E001F, U+E0080 to U+E00FF and U+E01F0 to U+E0FFF; and the Hangul fillers U+115F, U+1160, U+3164 and U+FFA0 (the bullet above). A name made only of them is accepted and shows nothing, and it can hide at least 64 bytes of data (one byte a variation selector: VS1 to VS256 alone are 256, and 64 of them fit in 256 bytes). Code points that draw as blank without being default-ignorable, such as the spaces above and U+2800 BRAILLE PATTERN BLANK, are outside this set. "Can't hide text" in the rationale holds for the listed characters only. Found by a second review of N2-2 (#38's first head `e03b1d9`) and checked against `master`'s code and Unicode 18.0's DerivedCoreProperties.txt; open as L13.

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. It checked the list against the Unicode Character Database (the 65 Cc code points) and UAX #9 (revision 52, Unicode 18.0.0), confirmed that `PlateNaming` folds Cc and Cf while U+2028, U+2029 and unpaired surrogates pass, and confirmed the CVE-2021-42574 citation. It asked for the exact error order and the invisible format characters, both added above.

`[updated 2026-09-30: N2-6a's snapshot builder refuses a Plate whose name fails this rule, with a reason asking to rename it, never altering the name; ProfileLayoutSnapshot.IsValidName checks the rule exactly as the codec does.]`

### D5: an image digest covers the prepared copy. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** An `ImageReference`'s `sha256`, `format`, `byteLength`, `width` and `height` describe the **prepared copy** the client uploads, never the player's original file:
- **How a copy is prepared.** The publisher decodes the managed copy the Plate already uses and encodes the pixels again, through Dalamud's texture pipeline (`GetRawImageAsync` and `SaveToStreamAsync`), with no new dependency. The result is PNG, or JPEG for a JPEG source; N2-6 sets the encoder parameters.
- **What that drops.** Rebuilding the container drops every metadata block: EXIF, including GPS position; XMP; PNG text chunks; embedded ICC profiles; thumbnails.
- **What the publisher never touches.** It reads only the managed copy. It never touches the player's original file, and never puts the original's bytes or digest into a document.

The server verifies received bytes against the declaration before serving them (specification, section 13, rule 7). Whether it also processes them again is I2 (batch B). `[updated 2026-09-30: it does: every image is re-processed by an isolated worker before it is served (I2).]`

N2-3 changes the specification's wording in section 8.2 from "SHA-256 of the source image bytes" to the prepared copy. That is wording only: there is no salt, and the layout doesn't change.

**Rationale.**
- The original's metadata can carry a location, a device and an editing history, and rebuilding the container is the only reliable way to drop every kind of it.
- Neither the original's bytes nor its digest is ever published. Anyone holding the original can still recognise the published pixels; that is inherent in sharing an image.
- The consent screen shows the prepared copy, so the player sees what is sent.

**Not settled:**
- I2; `[updated 2026-09-30: settled by decision batch B (I2).]`
- the encoder parameters, and whether colour is converted to sRGB before encoding (N2-6, with tests of the result). N2-6 also clears the colour under fully transparent pixels, which a straight-alpha round trip would otherwise keep, and the consent screen shows each whole prepared image, not only the part a Plate element crops. `[updated 2026-09-30: N2-6's design amends this entry, APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026. (1) A prepared copy is cropped to a window the Plate draws of its image: Fill's centred window for an image element or the background, widened to whole pixels equally on both sides so that it stays centred, and the whole image for Stretch, Fit and a component's image. An image drawn with several windows gets a copy of each, except that a window inside another is drawn from that one's copy: both are centred and span the same whole width or height, so Fill on the larger copy gives the smaller window back. Nothing of an image outside what an item draws leaves the machine, beyond the widening to whole pixels (under one pixel on each side), which a union of the windows would not ensure: two windows across each other's axis leave corners neither draws. Preparation refuses a decoded image whose size differs from the size the window was computed from. (2) Colour that alpha hides is cleared, not only under alpha 0: each channel becomes round(round(c*a/255)*255/a), and 0 where a is 0. (3) After encoding, the container is walked against an allowlist measured at the pinned Dalamud (PNG: IHDR, IDAT, IEND; JPEG: SOI, APP0 JFIF, DQT, SOF0, DHT, DRI, SOS, EOI). The EXIF APP1 block Dalamud's encoder adds to every JPEG is stripped, and anything else is refused, before the copy is hashed. A known-answer round trip of a small image with alpha, once a session, must give the expected pixels and inventory, or publishing is off for that session, as K3's probe is for keys. (4) JPEG (quality 0.92, 4:2:0) for an opaque JPEG source, PNG otherwise; no colour conversion and no embedded profile. (5) The consent screen shows each prepared copy, which is now the drawn window, not the whole image. (6) The candidate is built before the consent screen from a private deserialization of the saved Plate's JSON, never from the document an editor holds. The consent record keeps the candidate together with the persona (slot and key) it showed, and the commit signs only that candidate, under that persona, and never reads the Plate again. The consent screen lists every shared text in full beside the rendering, since a text under an opaque item, or running past its box, travels whole. N2-6b applies (1) to (5); N2-6c applies (6)'s build, consent record and commit, and N2-9 its consent screen.]`

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**. `GetRawImageAsync` and `SaveToStreamAsync` exist on `ITextureReadbackProvider` in the installed Dalamud; a round trip through a texture cannot carry container metadata; and decoding the player's own managed copies adds no surface beyond import. It asked for the narrower rationale and the two N2-6 points above.

**Independent concurrence (N2-6).** A security-focused reviewer with no shared context examined N2-6's design (September 30, 2026). It did **not concur** with the first revision, and asked for nine changes, all made in the second:
- a candidate built before the consent screen and signed exactly as shown;
- one persona-session commit with the publication index as its commit point;
- only what finished rendering visibly draws, proven by a canary test;
- each image cropped to what is drawn;
- colour that alpha hides cleared;
- the metadata drop enforced on the encoder's actual output;
- the sniffer's exact rules written into the specification;
- the renderer matched where it resolves more than the Plate stores (effect opacity, the Basic name's fitting, fonts not yet built, the skip rules shared with it);
- a bounded, versioned index and outbox, never treated as empty when unreadable.

It read Dalamud's decoder and encoder at the installed commit, which gave (3) above, and answered the design's open questions from primary sources (Microsoft's WIC encoder pages and the PNG specification's third edition), which gave (4). It then **concurred with the second revision on one condition**: preparation checks that the decoded size equals the size a window was computed from, since an animated WebP's first frame can be smaller than its header's canvas. That is (1) above.

**The builder's reviews (N2-6a, #48).** The same reviewer concurred with the builder at `d35d5a0` and asked that the consent record's rules be written down, which is (6) above. A general review then found that the builder cropped each image to the union of the windows drawn of it. The union came from the security reviewer's note on the second revision, which judged it equivalent because each Fill window is recovered exactly; that note, and its concurrences at `7d27c87` and `d35d5a0`, missed that crossing windows leave corners no item draws. (1) now keeps a copy per window, sharing one only when a window lies inside another, and each Fill window is kept exactly centred, so a copy gives its items back exactly the pixels they draw. The security reviewer **concurred** with that at `114a30f`, after a randomized check of 4,000 Plates (every copy some item's own window, every item's copy holding its window, Fill on each copy within 0.0003 px of the item's own).

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

**Not settled:** a public name (stage 2 at the earliest). N2-5 removes the "provisional" wording from `PersonaLabel` and `PersonaManager`. `[updated 2026-09-30: done in N2-5a. PersonaLabel also refuses unpaired surrogates, so a label survives the registry's round trip exactly (P3).]`

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: a local-only label is the only option consistent with D3 and ROADMAP.md, section 4, rule 8.

### I1: which images can be shared. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Publishing takes the images a Plate already holds as managed copies. Those are PNG, JPEG or WebP, whatever local import accepted through Dalamud's decoders (`ImageFormatSupport`). It shares only their prepared copies (D5), which are:
- 8-bit RGB or RGBA;
- the **first frame** of an animated image;
- at most 8,192 pixels a side and 20,000,000 pixels in all;
- at most 8 MiB each.

The per-Plate image count is schema 2's limit (N2-3). **An image over a limit is refused with a message; nothing is downscaled silently.** The consent screen shows the prepared copies, so a first frame instead of an animation, or colours after conversion, are visible before anything leaves. `[updated 2026-09-30: N2-6's design converts no colour and embeds no profile (D5), so there is no conversion to show; the consent screen shows each prepared copy, cropped to what the Plate draws.]`

**What a viewer accepts, whoever published.** A hostile publisher can skip preparation, so the limit is enforced where images are received, not trusted from the sender:
- schema 2's `ImageReference.format` allows only PNG (1) and JPEG (2) (N2-3);
- the viewer (N2-10) sniffs every image's bytes before decoding anything `[updated 2026-09-30: by the specification's section 8.2.1, which states the rule exactly, with vectors (N2-6b); the publisher applies it to the copies it prepares and the server to what it receives and to its own re-encodes]` `[updated 2026-09-30, APPROVED (Claude, under the owner's delegation of September 29, 2026): the rule is tighter than this entry's list, after the general review of N2-6b's first part (#49). A JPEG has at most 64 scans, since a decoder's work grows with its scans times its pixels, which the pixel limits don't bound (every encoder the reviewer tried wrote 1 to 10). Only the markers such a JPEG needs are allowed, and in a PNG only the critical chunks IHDR, PLTE, IDAT and IEND, with no iCCP, zTXt or iTXt, whose compressed contents no limit bounds. Every rule is checked in reading order (section 9.1). Our own copies never come near any of it: the publisher's and the server's encoders write fewer scans and fewer kinds of segment and chunk.]`, and refuses anything but a non-animated 8-bit PNG, or an 8-bit baseline, extended or progressive JPEG (frame types SOF0 to SOF2) with 1 or 3 components, within the specification's section 8.2 limits, whatever I2 and D6 decide. `[updated 2026-09-30: both are decided in batch B, and the viewer's sniff stays (D6's conditions for N2-10).]`

**Deviation from the recommendation, on sources only.** It proposed refusing animated WebP and CMYK JPEG. As *sources* they are accepted: preparation encodes the pixels again, so neither reaches a viewer in its original form. The risk the recommendation guarded against, which formats a viewer must decode, is covered by the rule above. Refusing them as sources would only turn away images the player already uses locally.

**Rationale.**
- The specification's limits (section 8.2) already bound the decoded size.
- Showing the prepared copy makes the player's consent informed.
- One rule for all animations is simpler than one per format.

**Not settled:** I2. `[updated 2026-09-30: settled by decision batch B (I2).]` `[updated 2026-09-29: schema 2's limits are settled by N2-3a (specification, section 8.5): at most 8 images, whose pixels total at most 33,554,432. The publisher refuses a Plate over them with a message, never dropping or downscaling an image. The security-focused reviewer of N2-3a recommended a cap on the images' total pixels, which bounds what a viewer decodes, and found it sound as implemented at `a388099`, with its exact boundary tested from `3528363`.]`

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

**Rationale.** Deduplicating across personas would let one persona probe whether another holds an image (upload a digest and see whether it is already stored), or reference or overwrite another's asset. Scoping costs only storage. N2-7 applies it. In N2-3, the specification's section 8.4 loses the N1 half of its "Open" paragraph; the N2 half (replay) stays until batch B. `[updated 2026-09-30: removed by decision batch B, which decides N2.]`

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

**Deleting or renaming a Plate never publishes or unpublishes anything by itself.** The sharing view lists published profiles whose Plate is gone and offers to unpublish them. Losing the index loses no Plate. It does lose the local list of what was published: a signed request that lets the server list a persona's profiles is batch B's to decide. `[updated 2026-09-30: none in stage 1 (P4); the operator path (S3) covers a lost index.]`

**Rationale.** Plate JSON travels: export, Duplicate and Save as Template copy it, including unknown fields. Publication state stored there would leak into copies and packages (NETWORK1.md, system 4).

**Not settled:** recovering the list from the server (batch B). `[updated 2026-09-30: stage 2 (P4).]`

**Independent concurrence.** A security-focused reviewer with no shared context examined `4a19eca` (September 29, 2026) and **concurred**: Plate JSON travels through export, Duplicate and Save as Template, unknown fields included, so publication state belongs outside it. It noted that share codes aren't redacted yet and that index files must be named by slot, both now stated above.

### K3: where persona features turn on. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Persona features (creating, opening or using a key, and publishing) turn on for a session only when both of these hold:
1. **A capability probe passes.** Once per session, off the framework thread, the probe runs the exact chain the plugin uses, with a throwaway key and never a persona key:
   - generate, export, and import through `PersonaKeyMaterial`'s checks;
   - sign, and verify through the protocol;
   - protect and unprotect through the protector, with an envelope header as context;
   - write and read the key file storage in a temporary directory.
2. **The protector in use claims protection on this platform.** The DPAPI protector (N2-4) claims it only on native Windows. Under Wine, DPAPI is obfuscation only (NETWORK1_CryptoCompatibility.md, section 3), so it claims none there, and persona features stay off until K9 decides a store for that case.

`[updated 2026-09-30: under D6 a viewer receives server-checked content and verifies no signature, so the probe's verification step protects nothing in viewing: TLS does. Viewing on other platforms also needs K3's question about TLS under Wine answered; and while K3 gates viewing on the verification step, which fails under every Wine version examined (from source, K8), it needs K8 there too, unless a later decision drops that gate for server-checked content. The step stays; on native Windows it passes, and it becomes load-bearing if D6 ever changes. (See D6.)]`

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

**Applied by N2-4, September 29, 2026.** `PersonaCapabilityProbe` checks, in this order, and stops at the first failure:
1. The committed known-answer document (persona A's `profile-snapshot` test vector) verifies as its persona, and a copy with one payload bit changed is refused as `SignatureMismatch`, and for no other reason. Viewing depends on this step alone.
2. The key chain, with a throwaway key in a fresh scratch directory: generate, protect under the envelope's header, store, open again through the key material's checks, sign, and verify through the protocol.
3. The protector's claim on a blob it has just made (for DPAPI, the Windows provider identifier alone), that it opens that blob, and that it refuses it under another context.

Persona features turn on only after all three. Each failure gives one message naming the missing capability, saying the feature isn't available on this system yet and never that a system is excluded. The probe never throws on a platform or storage failure (only on a missing argument, or a scratch root that isn't a full path), and tries to delete its scratch directory whatever happens. Its public entry point takes the DPAPI protector and binds the claim to the Windows provider identifier. The overload that takes a claim is internal and exists for the tests; N2-5 calls only the public one, and its review checks that. A later platform protector gets its own entry point with its own claim. Nothing runs it yet: N2-5 runs it once per session, off the framework thread, with a temporary directory as the scratch root (never the key directory), before persona features turn on. `[updated 2026-09-30: N2-5b runs it so, through the public entry point only; a boundary test refuses the internal one in plugin sources. A probe that fails or throws turns persona features off for the session with no retry, and the session keeps its result for viewing (N2-10).]`

**Independent concurrence (N2-4).** The same review, of `ffed076` and rechecked at `3a2d1f0`, **concurred**. The probe runs K3's chain in K3's order, and viewing depends on the known-answer step alone. `Verify` checks the signature before decoding the payload, so the one-bit change can fail only as `SignatureMismatch`: together the two checks show that the platform verifier both accepts and refuses. Messages name no path, key or blob. The provider identifier is sound positive evidence against every implementation examined, since Wine's blobs carry `Wine Crypt32 ok` there instead. Windows does not authenticate the identifier, so it shows only what this process's crypt32 has just written, and it is never read from a stored blob. A Wine that copied Windows' format would pass; K9 re-checks this. The public entry point binds the claim; the overload that takes one is internal and exists for the tests, and N2-5's review checks that it calls only the public one.

### K4: what comes before a persona's first real publish. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Before a persona's first publish to a real server, the player either has an encrypted backup of it, or explicitly acknowledges, for that persona, the following. Losing its key means never being able to update or unpublish what it published, and no account exists to recover it.

**In NETWORK2's first stage there is no backup,** so the acknowledgement is required:
- it is a separate confirmation with that text, naming the ordinary ways a key is lost: reinstalling Windows, moving to a new PC, deleting the plugin's data, or an administrator resetting the Windows password, which loses the key DPAPI protected it with;
- it is recorded per persona in the persona registry, and asked once per persona; `[updated 2026-09-30: N2-5a records it as bit 0 of the record's flags (P3), set by PersonaManager.Acknowledge; a created or restored persona starts without it. N2-5b's window asks for it.]` `[updated 2026-09-30: N2-5c's persona window asks for it: the step opens, scrolled into view, whenever a persona is created or restored, names the persona, gives this text with K2's disclosure, and records nothing until the player ticks "I understand" and chooses Acknowledge; "Not now" leaves it to be asked again.]`
- it is repeated in the persona window whenever a persona is created.
- `[updated 2026-09-30: decision batch B adds to the text: once the key is lost, only the server's operator can remove what the persona published (S3); and the plugin's networking directory holds the keys and the list of what each persona published, which keeping it keeps and which updating or unpublishing needs, but it is no backup of the identity, since on a new PC or a reinstalled Windows its keys normally won't open (P4). The persona window says both.]`

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

**How codes travel.** A code is sent in a request's body or a header, never in a URL, so no access log records it. `[updated 2026-09-30: only in a request's body. R4 withdraws "or a header", since Caddy's access log, when enabled, records custom headers.]` The server returns a code only in reply to a publish carrying a fresh request proof (S1), never for a replayed document.

**Left to batch B:** the code's format and length, with at least 64 bits from a cryptographically secure generator; a fixed prefixed text form that `LogPrivacy` redacts; and rate limits on lookups. `[updated 2026-09-30: settled by R4.]`

**Target lookup** (by targeting a character in game) is stage 2, with its own privacy decision and an opt-in binding.

**Rationale.** Sharing that the publisher starts deliberately and the viewer opens explicitly, with no character binding (ROADMAP.md, section 4, rule 8). A code is a capability in the sense of the W3C TAG's [Good Practices for Capability URLs](https://www.w3.org/TR/capability-urls/). Anyone who has it can use it, so it leaks the way a link does, and unpublishing revokes it. Stage 1 departs from two of that document's recommendations on purpose: codes don't expire, and a code can't be replaced without unpublishing. Both would complicate a first test, and both can be added later without changing the protocol.

**Not settled:** the format, the length and the rate limits (batch B); expiring or replacing codes; target lookup (stage 2). `[updated 2026-09-30: the format, the length and the rate limits are R4's.]`

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
- **Request and response bodies.** Requests carry the signed documents' exact bytes and the prepared image bytes. Responses are small JSON objects with closed schemas, parsed strictly with bounded sizes. `[updated 2026-09-30: two responses are binary bodies instead, each sent only with HTTP 200: D6's served profile (magic `AFSP`, at most 1,048,576 bytes, the protocol's `MaxDocumentBytes`) and I2's re-processed images, fetched by index. A failure is a status with no body.]`
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

### The request proof's decisions (N2-3b), September 29, 2026

The entries below were decided for NETWORK2's increment N2-3b, the request proof, and are applied by it. They are researched against the primary sources cited in each, and a security-focused reviewer examined the design before any code and the implementation after. The request proof adds a signing context, a signed-byte change the owner approved in advance on conditions (see "Approved decisions").

### S1: request proofs. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** Every document submission to a server carries a request proof (specification, section 14): snapshots and retractions alike. Viewing by share code needs none. A proof is signed by the document's own key, in its own signing context (L8), and binds:
- the deployment name (D7);
- a single-use challenge the server issued;
- SHA-256 of the exact document submitted, signature included.

A server refuses a submission whose proof fails section 14.4, resubmissions included, and returns a share code only in reply to a submission with a valid proof. How a server issues and consumes challenges is section 13, rule 10. Applied by N2-3b (the protocol), N2-7 (the server) and N2-9 (the client).

**Rationale.**
- Without a proof, anyone holding a signed document could submit it: a viewer given signed envelopes (D6), a leaked store, or an old revision the server has pruned (N2). With one, only the key's holder can, and only where and when they choose.
- Freshness comes from a server challenge, not the client's clock. Nonces a server provides are what stop proofs being made in advance ([RFC 9449](https://www.rfc-editor.org/rfc/rfc9449.html), DPoP, section 11.2), and a nonce once used is treated like one never issued ([RFC 8555](https://www.rfc-editor.org/rfc/rfc8555.html), ACME, section 6.5). DPoP's alternative, a client timestamp and a unique id, needs server state all the same (RFC 9449, section 11.1) and fails a player whose clock is wrong.
- Retractions need a proof too, departing from the baseline "not to retract": otherwise a retraction accepted by one deployment could be replayed to another (D7).
- The key travels in the proof, as in DPoP (section 4.2) and ACME (section 6.2), so a later proof kind without a document still works; the library checks that it equals the document's key.

**Consequences, recorded.**
- A retraction can no longer be signed in advance and submitted later, or by someone else. That matters for K4 and the D2 details, where a pre-signed "emergency unpublish" could have been one answer to a lost key.
- A retraction needs the challenge endpoint, like any other submission.
- N6 stays open for a retraction's own `issuedAt` (section 13, rule 6); proofs don't depend on the client's clock. `[updated 2026-09-30: N6 exempts retractions from the future-skew check; their issuedAt is checked for form only and recorded nowhere (decision batch B).]`

**Not settled:** the challenge lifetime beyond the 300-second baseline, the rate limits on issuing challenges, and the server's endpoints (N2-7); any later proof kind.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 29, 2026). It found that the proof defeats third-party submission, cross-deployment relay and proofs made in advance, and asked for eight changes before concurring. The changes: the server's name from configuration only, the challenge consumed atomically, a last label that starts with a letter, refusal rather than normalization, separate hostnames and test-only names, a subject digest open to later proof kinds, the whole operation bound, and the verified bytes stored. All eight are applied here. It then examined the implementation at `7e7015e` (September 29, 2026). It found the proof read and the submission checked exactly in the order of sections 14.3 and 14.4, each on one private copy of its input with every allocation bounded first; the bytes a server stores are the ones hashed and verified; and `Sign` proves only the signer's own valid document and produces nothing its own check would refuse. It asked for a vector of a valid proof over a document that fails its own verification, for a proof to be checkable only together with its document, and for the register and roadmap to show S1, D7 and L8 as decided. It **concurred** on its recheck of `5b39350`, after confirming with a verifier written from the specification that the new vector's proof is valid and only section 14.4, step 4 refuses it.

### D7: documents are not bound to a deployment; submissions are. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** A document's signed bytes name no deployment, as today. What is bound is the act of submitting it: the request proof carries the deployment name, the DNS hostname the client connects to in one canonical form (specification, section 14.1), and a server refuses a proof made for another name. The client takes the name from its own configuration and builds its address from it; the server takes its own from its configuration, never from the request.

**Rationale.** Relay happens at submission, so that is where the binding belongs: at a server that follows section 13, a document is useless without a fresh proof from its own key for that server. The alternative, a deployment id in every document's signing input, would tie each document to one server for good and change signed bytes again. The name is what TLS authenticates, the same approach as DPoP's `htu` (RFC 9449, section 4.2) and ACME's `url` (RFC 8555, section 6.4), at the level of a whole deployment.

**Consequences, recorded.**
- Documents stay portable signed statements. A server that ignores section 13, or a viewer given signed envelopes (D6), still accepts them wherever they came from.
- A retraction accepted by one deployment does not reach another: a player who published to two unpublishes from each.
- Separate deployments need separate hostnames, and a server that accepts real keys never uses a name reserved for tests (`localhost`, `example.com`, `.test` and the rest).

**Not settled:** whether a profile can move between deployments, a later product question.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 29, 2026). It found that the proof defeats third-party submission, cross-deployment relay and proofs made in advance, and asked for eight changes before concurring. The changes: the server's name from configuration only, the challenge consumed atomically, a last label that starts with a letter, refusal rather than normalization, separate hostnames and test-only names, a subject digest open to later proof kinds, the whole operation bound, and the verified bytes stored. All eight are applied here. It then examined the implementation at `7e7015e` (September 29, 2026). It found the deployment name rule as specified: lowercase LDH labels, a last label that starts with a letter, which refuses IPv4 in decimal, hex and octal, and no normalization; and a proof compared only with the name the server passes in from its configuration. It asked for vectors of a label ending in a hyphen and of bytes outside ASCII, and suggested the special-use domains among the names reserved for tests. Both are applied, and it **concurred** on its recheck of `5b39350`.

### L8: the signing-context rules. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 29, 2026

**Option and scope.** The specification's section 5.1 states the rules every signing context follows:
- its own length-prefixed tag, carrying the draft marker until the freeze;
- after the tag, the protocol version and the signer's key, in a fixed canonical layout;
- nothing signed that another party supplies, except inside a fixed-length field of a tagged input;
- a new context only with its own section, vectors and a cross-context test.

Version 1 has two contexts: the signed document and the request proof. Applied by N2-3b.

**Rationale.** One persona key signs in both contexts. ECDSA's standard security notion, existential unforgeability under chosen messages, holds for any set of messages that cannot be confused, and the length-prefixed tags make every input of one context differ from every input of the other (here already at the first byte, since the tags' lengths differ). The known dangers of reusing a key come from using it with different primitives (signing and key agreement), not from separated messages under one scheme. Signing nothing a server supplies except inside a fixed field of a tagged input keeps a hostile server from using a client as a signing oracle.

**Not settled:** later contexts (share grants, key rotation statements), which each follow these rules.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 29, 2026). It found that the proof defeats third-party submission, cross-deployment relay and proofs made in advance, and asked for eight changes before concurring. The changes: the server's name from configuration only, the challenge consumed atomically, a last label that starts with a letter, refusal rather than normalization, separate hostnames and test-only names, a subject digest open to later proof kinds, the whole operation bound, and the verified bytes stored. All eight are applied here. It then examined the implementation at `7e7015e` (September 29, 2026) and **concurred**: the two tags are length-prefixed and of different lengths (44 and 42 bytes; 38 and 36 at the freeze); one writer builds both the proof's signing input and its layout; the tests refuse a signature from either context in the other; and the vectors refuse, as a proof, both a document's own signature and a document-context signature over a proof's fields. Its one note on this entry's wording is applied in `5b39350`.

### The persona registry's decisions (N2-5a), September 30, 2026

The entries below were decided for NETWORK2's increment N2-5a, the persisted persona registry in `AetherFrame.Personas`, and are applied by it. N2-5b applies the rest in the plugin: the registry file, the key file's written-through move, the single-writer lock, the capability probe at session start, the persona window and K4's step. A security-focused reviewer examined the design before any code and the implementation after.

### L10: signing across a persona switch. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** A change of selection revokes every signer lease opened before it, and a revoked lease never signs again, as #23's interim does. An operation is also **bound to the persona it showed the player**:
- when it starts, it records that persona's slot and public key: for publishing, the persona the consent screen shows;
- it opens its lease with `PersonaManager.TryOpenSigner(expectedSlot, expectedKey)`, which opens nothing and answers `ActivePersonaChanged` unless exactly that persona is active;
- it holds no lease across I/O or a dialog: one lease, one signature, disposed at once.

Every signer the plugin opens goes through `TryOpenSigner`. `TryOpenActiveSigner` stays for tests and for callers that show no persona, and N2-5b adds a boundary test refusing it in the plugin's sources. `[updated 2026-09-30: N2-5b adds that boundary test: no plugin source names TryOpenActiveSigner.]` Nothing is sent on a switch: selecting a persona sends nothing (D3), and a signed document waiting in N2-6's outbox is sent only when the player retries while its persona is active. Request proofs already refuse any key but the document's (S1). Applied by N2-5a, the library; N2-6 and N2-9 follow it.

**Rationale.** A switch during the consent screen must never make an operation sign, as the new persona, what the player approved for the old one. Revoking leases stops a lease opened before the switch; binding stops one opened after it. The alternatives are refused:
- **finishing as the old persona** signs for a persona that is not active, which NETWORK1.md's system 1 forbids;
- **refusing a switch while a lease is open** would let a stuck operation lock the player out of their own identity choice (D3).

**Not settled:** nothing for version 1.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 30, 2026). It asked for the binding above, because revoking leases alone left a switch possible between showing a persona and signing, and **concurred** once it was applied. It asked for "every plugin caller uses `TryOpenSigner`" to be checkable, which N2-5b's boundary test makes it. It then examined the implementation at `d0de8f0` (September 30, 2026) and **concurred**: `TryOpenSigner` compares the active persona's slot and public key under the manager's lock and opens nothing otherwise, and the selection counter moves only after a switch is saved, so a failed switch revokes no lease and a saved one revokes every earlier lease. The tests show that no signer is opened for a persona other than the one shown, and that a switch after opening revokes the lease. Its concurrence stood on its recheck of `137872e`, which applies its notes.

### L12: key files no record names. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** A key held under a slot no record names is an **orphan**. Orphans come from a check that fails after a key write (`AddKey` reports `CustodyFailed`), a crash between the key write and the registry save, or a registry save that fails after a key was committed (P3). They are:
- **detected** by `PersonaManager.Audit()`, which lists the key storage and compares it with the records without opening any key. Entries that aren't a slot's name are skipped and counted. A listing that fails is reported, never thrown, and blocks nothing.
- **checked against the records:** a record whose key is missing or unreadable (damaged, not an envelope, or another protector's), or whose envelope names another slot or another public key, is reported as **unusable**, with that reason. The record stays; nothing is repaired.
- **reported by slot**, with the identity the envelope's header claims shown as **unverified**, since anyone who can write the key files can forge a header. `PersonaManager.VerifyOrphan(slot)` proves the claim by opening the key on this account (`IPersonaKeyStore.OpenPublicKey`, which disposes the key at once), on demand and off the framework thread.
- **restored only by the player**, with `PersonaManager.RestoreOrphan(slot, label)`. It checks everything again under the manager's lock: no record names the slot, the registry has room, the key opens on this account and matches its envelope, and no persona holds its identity. The record takes its public key from the key opened then and keeps the slot; it is not selected, starts without K4's acknowledgement, and is saved before it is applied. A key copied from another installation opens only under the same Windows credentials; when it does, its slot exists on two installations, as `PersonaSlotId`'s documentation now says.
- **never deleted** (K6). The persona window (N2-5c) names where the key files are, and warns that a removed key can never be used again.

Logs name slots only, never identities or paths. `[updated 2026-09-30: N2-5c's persona window shows them. Each key without a persona shows its file name and its header's claim as unverified: "claims to be" an identity, or "claims to be a copy of" a persona's key, until Check opens it; only then does it read as a spare copy, with no restore. Each persona's row names its key file, and a persona whose key can't be used shows what its key file's header names, never whose key it is. Restoring asks for a name and runs RestoreOrphan; nothing is deleted.]`

**Rationale.** A key with no record is still the player's identity. Deleting it could destroy the only means of updating or unpublishing what it signed (D2's premise), and ignoring it would hide that identity. Restoring takes the player's explicit act, because an orphan may be a key the player set aside on purpose, or one planted by software running as the player. That software can already read every key (K2), so the checks guard against mistakes, not against it. A retry after a failed save commits the key under a fresh slot, so the first copy stays an orphan whose identity a record now holds, and restoring it is refused.

**Not settled:** removing or setting aside an orphan from the window, and rejoining a record whose key is unusable to a verified copy of that key held under another slot. How the window shows orphans and unusable records was left here to N2-5b, and N2-5c settles it (above). The window shows an orphan whose claimed identity a record already holds as a spare copy of that persona's key once **Check** proves the claim, since restoring it is refused, and makes clear which key file each persona uses: removing that one makes the persona unusable.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 30, 2026). It asked for the audit to open no key, for records to be checked against their keys, for an orphan's identity to be shown as an unverified claim, for a restore to check everything again under the lock, and for `PersonaSlotId`'s documentation to cover a copied key. It **concurred** on one condition: the check that opens an orphan returns the opened key's public key rather than a yes or no, so the check and the restored record come from one read. `IPersonaKeyStore.OpenPublicKey` does that. It then examined the implementation at `d0de8f0` (September 30, 2026) and **concurred**: `Audit` reads envelope headers only, never calls the protector (tested), and gives every orphan and unusable record its reason without throwing on a failed listing. `RestoreOrphan` checks the slot, the room, the key's opening and its identity again under the lock, and takes the record's key from `OpenPublicKey`'s single read; a forged header's claim is refuted by opening, and a moved key, a copy of a held key and a locked key are refused. Its notes are applied: the key file storage's listing reports a directory it can't read as a failed listing rather than an empty one, and the audit's documentation says it reads every key's envelope under the lock, so the plugin runs it off the framework thread. Its concurrence stood on its recheck of `137872e`, which applies its notes.

**Independent concurrence (N2-5c).** The same security-focused reviewer examined N2-5c's implementation at `1c7fb25` (September 30, 2026) and **concurred**: every label, and every message that holds one, is drawn unformatted (`TextUnformatted`, or `AddText` in a callout), never as an ImGui label or through a formatting call, and widgets are keyed by slot; the window never holds the manager, and every change runs off the framework thread through the session; K4's step names its persona, gives K4's text with K2's disclosure, and records nothing until "I understand" is ticked; a key's claim stays a claim until Check opens it, a spare copy is named only then and offers no restore, and a key that doesn't open names both causes; and the key folder's path is shown and copied from `%APPDATA%` on. The solution built with 0 warnings, and the persona, plugin and protocol suites and the preview flavour's boundary tests passed. Its concurrence stood on its recheck of `d3d7924` (September 30, 2026): its notes are applied, so checks are dropped whenever a new audit arrives and a check audits again, a key file whose header can't be read names that case's own causes while K2's two stay for a key that doesn't open, a path under the user's profile shows from `%USERPROFILE%`, and N2-9 is to tell a player whose key doesn't open at their first share; and a restored persona now opens K4's step, as a new one does.

### P3: how the persona registry is kept. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.**
- **Where.** One file in the plugin's own networking directory, beside the key files' directory and never inside it: the key storage's listing counts every entry that isn't a key file as skipped, and the audit would report the registry, its temporary file and the lock as such every time. Never the plugin configuration or Dalamud's reliable storage, and no identity in a file name (K2; NETWORK1.md, safeguard 7). The file is N2-5b's; N2-5a defines its bytes and the manager's use of `IPersonaRegistryStorage`.
- **The bytes**, big-endian: `magic "AFPR" | version u16 = 1 | count u32 (0..256) | records | activeSlot[16] (all zero for none) | sha256[32] of everything before it`. A record is `slot[16] | publicKey[65] | flags u8 (bit 0: K4 acknowledged) | labelLength u16 (UTF-16 code units, 1..64) | label (UTF-16BE code units)`. At most 256 personas and 54,330 bytes, and no private material.
- **Integrity.** The checksum guards against corruption only, and there is no MAC: whoever can rewrite the file runs as the player and can already read every key (K2). A forged record can't make another key sign, since the signer checks refuse it. A swapped label can't be stopped, so N2-9's consent screen shows the persona's identity as well as its label.
- **Loading.** `PersonaManager.Load` reads the registry once. No registry (the file or its directory not found) is a first run. A registry that can't be read (access denied, a sharing violation, over 54,330 bytes) or doesn't decode is refused with `RegistryUnreadable`, and is **never overwritten or replaced**. The plugin then turns persona features off with a message naming where the file is; moving the file aside brings every key back as an orphan to restore (L12). A registry of at most 54,330 bytes that names a later version is refused the same way, but as `RegistryNewerVersion`, so the plugin says to update AetherFrame instead: moving it aside after a downgrade would lose its labels and acknowledgements. A larger one can't be read at all, and is `RegistryUnreadable`. Damage that happens to hit the two version bytes and leave a value above 1 also reads as a newer version. Either way, nothing is overwritten. Decoding refuses anything but exactly this layout, checking in order:
  - the size, then the magic and the version, which need only the first six bytes (every later version keeps both where they are, so a later layout is told apart before anything else is read), then this version's minimum length, the checksum and the count;
  - each record's slot (non-zero and unique), public key (a valid P-256 point, with a unique identity), flags (bit 0 only) and label (1 to 64 code units, equal to its own normalization);
  - that no bytes follow the records, and that the selection names a record or none.

  A temporary file is never read. The registry-less constructor stays for tests, and the plugin never falls back to it.
- **Saving: persist, then apply.** Every change (create, restore from a backup, restore an orphan, rename, select, deselect, acknowledge) encodes the state that would result and has `IPersonaRegistryStorage.Replace` save it atomically and durably. Only then does the manager apply it in memory, under its lock. So:
  - every registry the manager encodes is decoded again before it is saved, as the document codecs check their own output, so the manager never hands the storage a registry this build's load would refuse;
  - a failed save is not applied in memory, and reports `RegistryWriteFailed`;
  - the selection counter moves only when a new selection is applied, so a failed switch revokes no lease;
  - a change that changes nothing saves nothing;
  - a save that fails after the storage held the new bytes is indeterminate. Memory keeps the state from before it and the file may hold either, and both are consistent, because a key committed with no record is an orphan (L12).
- **Before a key is committed**, everything that can be decided in advance is checked: the label, the room (`RegistryFull` at 256), the key's identity against every persona held, and the registry that would result. After `AddKey`, only the save can fail.
- **Listings** (`Personas`, `Active`, `TryGet`) read an immutable snapshot published after each change, without the lock, so they never wait on a save, a key write or the protector.
- **The selection** is one for the installation. It is never keyed by a character, Content ID or account, and is restored only from the player's own last Select or Deselect. The first persona is never selected for the player (D3).

**What N2-5b must do with it:**
- **One writer.** An exclusive lock file, held for the session, taken before `Load` reads anything and released in the plugin's `Dispose`.
- **The file's replacement.** The temporary file is flushed in the same directory, then moved over the registry with `MoveFileExW` and `MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH`. A new key file is moved with `MOVEFILE_WRITE_THROUGH`. Never `MOVEFILE_COPY_ALLOWED`.
- **Not `File.Move`**, which never writes through.
- **Not `ReplaceFile`**, whose write-through flag is documented as unsupported, and which can leave the registry missing after `ERROR_UNABLE_TO_MOVE_REPLACEMENT`. A missing registry would read as a first run.
- **The boundary tests and K2's text** are amended for that one native declaration.

Microsoft states `MOVEFILE_WRITE_THROUGH`'s flush guarantee explicitly only for a move that copies and deletes; that residual risk is recorded here.

**Applied by N2-5b, September 30, 2026.**
- **The file.** `PersonaRegistryFileStorage` keeps `registry.afpr` beside `keys\`. It reads at most one byte beyond the largest registry's 54,330, and throws when that byte is there. A missing file or directory is a first run; anything else throws. A save writes `registry.afpr.tmp`, flushes it, reads it back and compares, then moves it over the registry, written through. A stale temporary file is replaced, and never read.
- **One writer.** `PersonaInstanceLock` holds `instance.lock` with no sharing for the session.
  - The session takes it before `Load` reads anything. While it is held elsewhere (an instance still unloading, a scanner), the session retries for about 10 seconds with backoff, and gives up when the plugin starts unloading.
  - Exactly one party releases it, decided under the session's monitor: `Close` when nothing of the session's runs, or else the work in flight, as it ends and before that work's unload registration ends. It is never released at a timeout, never tied to the plugin's other operations, and never left to a finalizer.
  - `Close` is the first thing the plugin's `DisposeAsync` does, and a failed load closes it too.
- **Never without the registry.** A boundary test reads the preview DLL's IL, and nothing outside `PersonaManager` constructs one through the registry-less constructor.
- **Moves.** A move that meets a sharing violation, a lock violation or a denied access (scanners and indexers open fresh files) is retried three times, 75 ms apart. The rename is one atomic step on NTFS; FAT and exFAT (a launcher on a USB drive) are a remaining risk.

**Rationale.** Persist, then apply means memory never shows a change the file has not accepted. After a failed save the change is not applied, and a key committed with it is one the audit offers back; only when the save's outcome is unknown can a restart show the change after all. Refusing an unreadable registry, rather than starting empty, keeps a sharing violation or a damaged file from erasing every record at the next save. Code units rather than a text encoding keep `System.Text` out of the persona assembly and make a label round-trip exactly. For the same reason `PersonaLabel` refuses unpaired surrogates, a D9a implementation note.

**Not settled:** a later version of the bytes (none exists), and whether the encrypted backup (the D2 details) also covers the registry.

**Independent concurrence.** A security-focused reviewer with no shared context examined the design first (September 30, 2026). It asked for:
- K4's flag in the record;
- every refusal that can be decided in advance made before a key is committed, and persist, then apply;
- labels as UTF-16 code units, and the exact layout with every length checked before it is read;
- an unreadable registry never replaced, and no fallback to a registry-less manager;
- one global selection;
- one writer, the written-through replace, and the boundary amendment;
- no paths in logs, and no identities in names.

It checked the write-through requirements against Microsoft's `MoveFileExW` and `ReplaceFileW` pages and the .NET source. It **concurred** on two conditions:
- N2-5b takes the lock before `Load` reads anything and releases it deterministically. Dalamud reloads plugins in-process, and a lock left to the finalizer would turn persona features off until the game restarts. This is recorded for N2-5b.
- Listings never wait on I/O, which the snapshot provides.

Its note to state the size cap is applied: 54,330 bytes. It then examined the implementation at `d0de8f0` (September 30, 2026) and **concurred**: the codec is exact (its layout is pinned byte for byte, and every rule checked after the checksum is refused with a valid checksum, handing out nothing), and every change is encoded (for a new key, before `AddKey`), saved, and only then applied. Listings read a snapshot that a test shows answering while a save holds the lock; the solution built with 0 warnings, and the persona, plugin and protocol suites and the preview flavour's boundary tests passed. Its notes are applied: the magic and the version are read before the checksum, so a registry a newer AetherFrame wrote is refused as `RegistryNewerVersion` and the plugin can say to update rather than to move a damaged file aside; a failed save says the change was not applied, rather than that nothing changed; and the manager decodes every registry it encodes before saving it, as the document codecs check their own output. Its concurrence stood on its recheck of `137872e`, which applies its notes. Its one correction on that recheck, that a newer registry is told apart only within the size limit, is applied above.

### Decision batch B for the server (N2-7), September 30, 2026

The entries below were decided together for NETWORK2's increments N2-7, the server, and N2-8, its deployment. They are researched against the primary sources cited in each, and recorded here before any code depends on them. None changes signed bytes: the served profile (D6) is a response format, not a signed document, and the share code's check symbol (R4) is a usability filter. A security-focused reviewer with no shared context examined five revisions of the batch, and concurred with every entry. N2-7's implementation gets its own review.

### D1: what a retraction means. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** A retraction is the signed, terminal `ProfileRetraction`. Publishing again uses a new profile id, and so gets a new share code.

**Applying one** deletes, at once, every revision document of (persona, profile), every image, the share code, the profile's revision rows (N2) and the retraction document itself. Only the tombstone is kept (S2). So that the deleted content leaves every copy:
- SQLite runs with `PRAGMA secure_delete = ON` on every connection the server's pool opens (a per-connection setting, off by default), so deleted content doesn't stay in the database file;
- after every retraction and every removal (S3), the server runs `PRAGMA wal_checkpoint(TRUNCATE)`. Active readers can block it, and then its first result column is 1 and nothing is truncated, so the server reads that column and retries with backoff until it is 0. It also sets `journal_size_limit` low, so the write-ahead log never keeps old pages past one checkpoint (sqlite.org, `pragma.html` and `wal.html`);
- tests hold a reader open to prove the retry, and read `secure_delete` back from a fresh pooled connection;
- N2-8's backups have a stated retention, quoted in the consent text, after which a deletion has reached every copy.

**The order of checks** on every submission: section 14.4's proof, then, in stage 1, the persona allowlist (I2), then the tombstone, then rule 4's revision check. Only the key holder ever learns "retracted", and a repeated retraction succeeds.

**Profiles the server never saw** are tombstoned too, rate-limited per persona and per address, since personas cost nothing and these rows are permanent and traceable to no one. A first snapshot still in flight (its challenge lives 300 seconds) that arrives after the unpublish is then refused.

**For N2-9:** one operation at a time per profile, and unpublishing drops that profile's pending outbox entries.

**Rationale.** A terminal retraction needs no ordering by client clocks (specification, section 13, rule 5). S1 already stops anyone but the key holder from submitting, and stops replays. A player who unpublishes expects the content gone, backups included once their stated window has passed.

**Not settled:** the backups' retention period (N2-8).

**Independent concurrence.** A security-focused reviewer with no shared context examined the design in five revisions (September 30, 2026), from primary sources, and **concurred**. It checked that a terminal retraction needs no ordering by client clocks, and that S1 already confines retractions to the key holder. It asked for the exact deletions, `secure_delete` on every pooled connection, a checkpoint retried until it completes, a stated backup retention, the tombstone checked after section 14.4 and before rule 4, and tombstones for unseen profiles rate-limited per persona and per address, all now above.

### D6: what viewers receive. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** **Server-checked content.** A viewer receives a **served profile**, which the server builds from the stored verified bytes. It is one binary body, sent only with HTTP 200:

```
magic "AFSP" | version u16 = 1 | marker bytes[16] | the profile, in section 8.5's encodings, with image fields replaced by an index
```

- **It holds** the name, the canvas, the background and the items. Images are named by index (0 to 7) in the served profile's own image list, and fetched under the share code with the marker and the index.
- **The marker** is 128 random bits from the server's CSPRNG, stored with the revision. It is never the server's sequence, a time or the document's SHA-256. An image request whose marker isn't the current revision's gets "not found", so a refresh never mixes revisions.
- **It never holds** a signature, a signed document, a persona id, a profile id, an asset id, a time (neither the server's receipt time nor `createdAt`), or the signed image fields `sha256` and `byteLength`, which re-encoding changes.
- **A failure** is an HTTP status with no body, and "not found" is one status for every cause. There is no JSON envelope and no multipart, so a viewer runs one strict parser.
- **Its size** is at most 1,048,576 bytes, the protocol's `MaxDocumentBytes`. It always fits: a served profile is its payload less the image fields and the identifiers, plus 22 bytes.
- **The protocol library** gains a strict decoder for it: a distinct type, never a `VerifiedDocument`, with vectors, adversarial and fuzz tests. N2-7 tests that no submission ever reaches that decoder, and sends `Cache-Control: no-store` on served profiles and images.
- **N7** applies to everything served.

**For N2-10:** keep I1's sniff of each image; hold content in memory only; clear the Plate when a refresh answers "not found"; never describe content as signed or verified; and tell the viewer that opening a code sends their address to the server.

**Rationale.** A signed document handed to a viewer is a transferable, non-repudiable statement that persona P published X: it survives retraction and can't be recalled. Off-the-Record messaging leaves messages unsigned for this reason (Borisov, Goldberg and Brewer, WPES 2004). Freshness and retraction rest on the server either way, and integrity on TLS to the one fixed hostname (R2) and on the server's verification (section 13, rule 10). The persona id, the asset ids and the times are left out because each would let code holders link one persona's Plates across characters, or learn when it was last active, which ROADMAP.md, section 4, rule 8 excludes. The choice is reversible later, since the server keeps the verified bytes (rule 3).

**Recorded consequences:**
- NETWORK0.md's authenticity goal now ends at the server: what a viewer sees is attested by the server, not signed by the creator. NETWORK0.md and the specification's section 13, rule 3 say so.
- Under D6 the capability probe's verification step protects nothing in viewing, since TLS does. Viewing on other platforms also needs K3's question about TLS under Wine answered; and while K3 gates viewing on the verification step, which fails under every Wine version examined (from source, K8), it needs K8 there too, unless a later decision drops that gate for server-checked content. The step stays; on native Windows it passes, and it becomes load-bearing if D6 ever changes.

**Not settled:** the served profile's specification text and vectors (N2-7, beside section 8.5).

**Independent concurrence.** The same review **concurred**. It found server-checked content the right choice: a signed envelope is a transferable, non-repudiable statement that outlives retraction, while freshness and retraction rest on the server either way. It asked for a served profile with no persona id, no times, no signed image fields and no asset ids, which could link one persona's Plates again across codes; for a random 128-bit revision marker; and for one strict binary body with its own vectors and fuzz tests. All are now above. It checked the size bound: a served profile omits the ids, `createdAt` and the image references, so it is smaller than its payload even with the 22-byte header.

### K5's gate: the "D2 details", not G3. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** K5 (backup passphrase rules and the key derivation route) leaves G3 and joins the "D2 details" gate, which already gates every `.afpersona` file. This is a decision about a gate: **K5 itself stays UNRESOLVED.**

**Rationale.** K5 changes no bytes and gates no server. K4 makes the acknowledgement the stage 1 path, and no backup exists before stage 2. D2's approval already leaves the password policy and the derivation to "later security approval".

**Recorded for when K5 is decided:**
- it also gates K9's passphrase-wrapped store;
- SP 800-63B-4's 15-character minimum assumes an online verifier that limits attempts (section 3.2.2), but a backup file faces offline guessing, so K5 also draws on SP 800-132, RFC 8018 and OWASP's current guidance (600,000 iterations of PBKDF2-SHA256), and prefers a generated code;
- NFC matches SP 800-63B-4, section 3.1.1.2.

**Independent concurrence.** The same review **concurred**, finding the move sound for the reasons above. It asked that K5 also gate K9's passphrase-wrapped store, and that the decision draw on sources for offline guessing, not only on SP 800-63B-4's minimum for an online verifier. Both are recorded above.

### N2: rollback by replaying a pruned revision. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.**
- **Pruning.** A superseded revision is pruned at once: its document and its images are deleted. The server keeps only (profile, revision id, document SHA-256), with no receipt time, until the profile is retracted or removed (S3). These rows are never expired.
- **Resubmission.** A known revision id with the same document hash is idempotent and never becomes latest again; one with a different hash is refused (rule 4).
- **Order.** "Latest" is ordered by a server-assigned sequence, never a clock.
- **For N2-6 and N2-9:**
  - ECDSA signatures are randomized, so a revision's exact signed bytes are kept until the server acknowledges them, and a revision id is never signed twice;
  - the outbox sends only the newest pending snapshot of each profile;
  - N2-9 asks the player before sending an outbox entry signed more than a day ago.

The specification's section 8.4 "Open (N2)" paragraph is removed; section 13's rule 4 keeps the revision records, and rule 6 names the server's sequence.

**Rationale.** S1 limits replay to the key holder, so what remains is a stale outbox, a restored plugin folder or a second installation resubmitting an old revision. Keeping the ids makes all three harmless. Each row is under 100 bytes, holds no time, reveals only a count of revisions, and goes with its profile. A player who edits something out expects it gone, so fewer signed documents sit at rest.

**Independent concurrence.** The same review **concurred**. It asked for superseded revisions to be pruned at once; for their rows to be kept until retraction and never expired, since expiry would let a restored plugin folder or a second installation roll a Plate back; for "latest" to be ordered by a server sequence; and for exact signed bytes to be kept until the server acknowledges them. All are now above. It noted the one gap no row closes, a restored outbox's never-accepted older revision, which N2-9's prompt covers.

### N6: retractions and a fast client clock. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.**
- **Retractions** are exempt from the future-skew refusal. Their `issuedAt` is checked for form only, recorded nowhere, and used for nothing.
- **Snapshots** keep the skew check as a sanity bound, since ordering is by the server's sequence, with a distinct error that N2-9 shows as "your PC's clock is ahead".

**Rationale.** S1's fresh challenge is what stops a retraction signed in advance, not `issuedAt`. A player whose clock runs fast must still be able to unpublish.

**Independent concurrence.** The same review **concurred**: S1's fresh challenge, not `issuedAt`, stops a retraction signed in advance, so exempting retractions from the skew check costs nothing. It asked that `issuedAt` be recorded nowhere, and that snapshots keep the check as a sanity bound with an error the plugin can explain, both above.

### S2: what a tombstone holds. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** A tombstone is HMAC-SHA256 under a tombstone key, over the ASCII label `AetherFrame.Tombstone.v1`, then the 32-byte persona id (the SHA-256 digest a `psn_` id names), then the 16-byte profile id. The row holds that value and the key's version, and nothing else: no date, no document, no identifier in the clear. Tombstones are kept for the deployment's life.

**The key:**
- 256 random bits, used for tombstones only (SP 800-57 Part 1, section 5.2);
- from configuration or a secret file, never the database, and excluded from N2-8's backups;
- checked at startup against a key-check value, HMAC(key, `AetherFrame.Tombstone.KeyCheck.v1`), stored beside the rows, so a missing or wrong key stops the server. Tombstones are never silently forgotten, which would let a stale outbox revive a retracted profile;
- N2-7 has a known-answer test that pins both inputs byte for byte, since a changed encoding would orphan every tombstone as silently as a lost key.

**Rotation.** After a suspected leak, every row is re-wrapped in one transaction, and the tombstone function becomes the whole chain, HMAC(k2, HMAC(k1, x)), for old and new rows alike. The version names the chain, a lookup is one computation, and each key has its own key-check value. No preimage is needed, so no tombstone is forgotten.

**If the key is truly lost,** the server stays down until the operator records a decision to start a new tombstone set, which forgets every retraction before it (N2-8's runbook).

**Rationale.** A tombstone recognizes a later snapshot of a retracted profile, and nothing more. It is only as private as the deletions around it (D1, N2), which the server makes at once.

**Independent concurrence.** The same review **concurred**. It checked that a keyed hash of the persona and profile ids recognizes a later snapshot of a retracted profile and nothing more. It asked for a 256-bit key used only for tombstones and kept out of the database and its backups; for the exact input bytes and a separately labelled key-check value, pinned by a known-answer test; and for rotation as one re-wrapped chain in one transaction, which, unlike a password pepper (OWASP), needs no preimage. All are now above. It confirmed that the key-check, tombstone and chain inputs are 33, 72 and 32 bytes long, so they can never coincide.

### S3: profiles whose key is lost. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope for stage 1:**
- no automatic expiry;
- the operator (the owner) removes a profile on request, and verifies the requester out of band. A share code proves nothing about authorship, since every viewer holds it; in stage 1 only the two testers can ask, since the persona allowlist (I2) lets only their personas publish;
- a removal is a retraction in every way: the same deletions and the same tombstone (D1, S2);
- K4's text mentions this path.

**Not settled:** expiry after long inactivity, disclosed in advance, decided before the allowlist is removed.

**Independent concurrence.** The same review **concurred** with no automatic expiry in stage 1. It asked that the operator verify a removal request out of band, since every viewer holds the share code, and that a removal delete and tombstone exactly as a retraction does, both above. When it checked this recording, it asked that the allowlist (I2) enforce "only the two testers".

### S4: a persona-level revocation document. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** Deferred. It would be an additive document type, and a signed-byte addition needing the owner's approval. N2-8's runbook covers a stolen key, since DPAPI doesn't stop malware running as the player (K2): the operator removes the persona's profiles, and refuses the persona in the server's configuration.

**Independent concurrence.** The same review **concurred** with deferring a signed revocation, which would change signed bytes. It asked for the runbook's path for a stolen key, above.

### I2: server image processing. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** Every image is re-processed before it is served, in a way that assumes the decoder can fail badly.

**In the server, before any decoding:**
- each received image's SHA-256 and byte length must equal the verified snapshot's declaration (rule 7), so only bytes a verified snapshot declared ever reach a decoder;
- I1's rules: no `acTL` chunk in a PNG (an animated PNG), and JPEG frame types SOF0 to SOF2 with 1 or 3 components.

**A worker decodes and encodes again**, with SixLabors.ImageSharp pinned to a patched version by the lock file, new advisories acted on, and its licence rechecked at each major version:
- only the PNG and JPEG codecs;
- `Image.Identify` first, which must match the declaration and the specification's limits;
- `DecoderOptions` with `MaxFrames = 1`, `SkipMetadata = true` and strict segment integrity;
- `SimpleGcMemoryAllocator`, so its buffers count against the GC heap limit;
- an explicit encoding: 8-bit non-interlaced PNG or baseline JPEG, with every metadata block dropped, no rotation, and no colour transform or ICC application (tested).

**The worker's output is untrusted.** A compromised decoder can return any bytes, so before storing an image the server checks, with its own strict parser, the type and the dimensions (the declared ones), the size bound, and the exact inventory of PNG chunks or JPEG segments the configured encoder produces, pinned by tests. Anything else is refused.

**Serving:** only under the share code, by index (D6), never by asset id or digest; with a fixed `Content-Type`, `X-Content-Type-Options: nosniff` and `Cache-Control: no-store`.

**Isolation in stage 1:**
- **Only the testers upload.** The server's configuration holds a persona allowlist: the two testers' persona ids, which the operator receives from them out of band (N2-8's runbook). A submission from any other persona is refused right after section 14.4's proof, before the tombstone, rule 7 or any decoding. Viewing by code stays open to anyone. The repository is public, so without the allowlist anyone could build the preview flavour, or write a client from the specification, make a persona for nothing, and upload to the owner's server.
- The worker runs in a container of its own: `network_mode: none`, a read-only root filesystem, no database, tombstone key or configuration mounted, its own memory limit and a limit on its number of processes. Its environment carries no secret.
- The server owns the listening socket, and the worker connects to it over a read-only mount, so the worker has no socket it could replace.
- Decoders run one at a time. Each starts in its own process group with rlimits, an explicit GC heap hard limit and `oom_score_adj` 1000, and the group is killed when its job ends, whatever the outcome.
- The server's queue of images is bounded at 16, refusing with a retryable error when full.
- N2-8 checks at startup that the worker container has no network.

**The limit, recorded.** In stage 1 the decoders run as the worker host's user. An exploit could therefore leave a process behind that outlives its job (a process can leave its group) and reads a later upload through `/proc`: another persona's image. It could also write a later job's output. The checks above see structure, not pixels, so the image could reach its thief in the pixels of the thief's own later upload. It has no network, database or key to reach. This is accepted for the two-player test on the owner's server only, where the allowlist confines uploads to the two testers' personas. **Removing the allowlist, to open a server to more than the two testers, requires S3's decision on expiry, and per-job isolation:** each decode runs as a user or in a namespace of its own, no process of it survives the job, and the host verifies that before the next one starts.

**Rationale.** A hostile client can upload crafted bytes that match its own declaration, and every viewer decodes the result through Dalamud's native texture pipeline. ImageSharp is managed code, but not memory-safe in practice: CVE-2024-27929 was a use-after-free in its PNG decoder, CVE-2024-32036 left buffers uncleared, so output could carry another image's data, and more advisories came in September 2026.

**Independent concurrence.** The same review **concurred**. It found ImageSharp's decoders not memory-safe in practice, and asked for rule 7's hash and length check before any decode; a worker container with no network, database, key or configuration; the server owning the socket; one decoder at a time, killed after its job; bounded memory and a bounded queue; and the worker's output treated as untrusted, down to its chunk inventory. All are now above. It asked for the limit's wording above, and concurred with accepting that risk for the two-player test on the owner's server, once an allowlist restricts uploads to the two testers' personas, with per-job isolation required before any wider server, as proportionate. It asked for that allowlist when it checked this recording (September 30, 2026), since nothing else stopped anyone else from uploading.

### R4: the share-code format. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** This settles what R1 left to batch B.

**The form** is `AF-XXXX-XXXX-XXXX-XXXX`: the fixed prefix `AF-`, then 16 Crockford Base32 symbols in groups of four (crockford.com/base32.html).
- 15 of the symbols are random: 75 bits from the server's CSPRNG, above R1's floor of 64.
- The last is a check symbol: a Damm check over GF(2^5) (Damm, "Totally anti-symmetric quasigroups for all orders n ≠ 2, 6", *Discrete Mathematics* 307, 2007), pinned in the specification with vectors:
  - the field is GF(2^5) with the modulus x^5 + x^2 + 1, which is irreducible over GF(2);
  - a symbol's Crockford value v (0 to 31) is the field element whose polynomial coefficients are v's five bits, bit i the coefficient of x^i;
  - the operation is x ∗ y = a·x + y with a = x (the element 2), field multiplication, and addition as XOR: a totally anti-symmetric quasigroup, since a is neither 0 nor 1;
  - from 0, each of the 15 random symbols in order makes the running value a·value + symbol, and the check symbol is a·value, which brings it to 0. A code is valid when folding all 16 symbols from 0 gives 0.

  Tests show that every single substitution, and every swap of two different neighbours, fails the check. The check is a usability filter, not a security control.

**Reading.** Either case is accepted, `i` and `l` read as `1` and `o` as `0`, and hyphens are optional. The plugin rejects a mistyped code by its check symbol without a request, and the server checks it too.

**One code per published profile** (R1), stable across updates. It dies when the profile is retracted, and no record of retired codes is kept.

**Only in a request body.** R1's "or a header" is withdrawn: Caddy's access log, when enabled, records every request header and redacts only Cookie, Set-Cookie, Authorization and Proxy-Authorization (caddyserver.com, `log`). Never in a URL.

**Answers.** An unknown, malformed or retracted code, or one whose check symbol is wrong, gets the same "not found".

**Rate limits** on lookups:
- per IPv4 /32, and per IPv6 /64, /56 and /48, after mapping an IPv4-mapped IPv6 address back to IPv4, since under RFC 6177 one site can hold many /64s;
- a high cap on failed lookups a day, with an alarm. When it trips, only the prefixes producing the failures are tightened, never every client.
- Behind Caddy, ASP.NET Core trusts forwarded headers from Caddy's address alone (`KnownProxies`; by default it trusts only loopback), and the trusted-proxy lists are never cleared.

**`LogPrivacy`** redacts every spelling the parser accepts, as "[share code]": either case, with `i`, `l` and `o`, and with or without hyphens. One test generates codes and checks every accepted spelling of each. An input box that lets a player leave out `AF-` adds it before parsing, and never logs what was typed. This lands with the change that first handles a code (N2-9).

**In the plugin (N2-9):** share a code by /tell, since chat is logged by other players' plugins; unpublishing and sharing again replaces a leaked code.

**Rationale.** A code is typed or pasted in chat, so it is short and survives misreading. 75 bits keeps guessing hopeless even past the rate limits: a million failed lookups a day against 10,000 live codes finds one with a chance of about 10^-10 a year. The check catches typos before any request.

**Independent concurrence.** The same review **concurred**. It asked for 75 random bits (R1's floor is 64, and SP 800-63B-4, section 5.1, asks for 64 even for session secrets); R1's fixed `AF-` prefix; the Damm check, having verified that x^5 + x^2 + 1 is irreducible and that x ∗ y = a·x + y, with a neither 0 nor 1, is totally anti-symmetric; codes only in request bodies, since Caddy's access log records custom headers; rate limits per IPv4 /32 and per IPv6 /64, /56 and /48; and a failed-lookup cap that raises an alarm rather than slowing every client. It asked that `LogPrivacy` redact every spelling the parser accepts, proved by one test over generated codes, and that an input box supplying `AF-` never log what was typed. All are now above.

### S5: server logging. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.**
- **No address is stored in stage 1:** only the rate limiter's memory holds addresses, for its window. Truncated addresses can still be personal data (GDPR Recital 26; CJEU C-582/14, *Breyer*), and a /48 can be one customer's whole allocation (RFC 6177).
- **Logs hold** a request id, the route template, the status, the duration and the error kind. They never hold a document, a proof, a challenge (rule 10), a share code, a revision marker, a persona id or a profile id.
- **Metrics** are aggregate only, never per code or per persona. The rate limiter's own per-persona and per-address state stays in its memory, for its window.
- **Keeping the defaults from leaking:**
  - `Microsoft.AspNetCore` logs at Warning, since its hosting diagnostics log each request's URL at Information;
  - the rate limiter logs no address, unlike Microsoft's sample;
  - Caddy's access log stays off, its default.
- **A log-capture test** in N2-7, over the server and the image worker, finds no address, code or identifier in any line.
- **Retention:** 14 days, enforced by deleting by time, not by size. Docker's json-file driver doesn't rotate by default, and a size cap doesn't bound time on a quiet server.
- **The consent text** (NETWORK2.md, section 2) says that the address is seen and not stored; that content and identifiers are kept until unpublishing, plus the backup window; and that logs are kept 14 days, without identifiers.

**Independent concurrence.** The same review **concurred**. It asked that no address be stored in stage 1, since truncated addresses can remain personal data, and that the leaking defaults be pinned: ASP.NET Core's request lines, the address logging in Microsoft's rate-limiter sample, Caddy's access log and Docker's unrotated logs. It also asked for a log-capture test over the server and the worker, and for 14 days enforced by time. All are now above.

### P4: listing a persona's profiles. APPROVED (Claude, under the owner's delegation of September 29, 2026), September 30, 2026

**Option and scope.** **None in stage 1.** No signed request lists a persona's profiles: one would be a new request kind with its own proof and privacy review.

A lost publication index is covered by the operator path alone (S3). "Unpublishing by share code" has no mechanism: a retraction signs a profile id, D6 keeps profile ids from viewers, and retracting by code would need a signed statement binding the code, a signed-byte change.

**K4's acknowledgement and the consent text** say three things:
- the plugin's networking directory (`Network\Personas\` under AetherFrame's configuration folder) holds the keys and the list of what each persona published;
- keeping it keeps that list, which is what lets the player update or unpublish later;
- it is not a backup of the identity: on a new PC or a reinstalled Windows, the keys in it normally won't open (DPAPI, K2), and then only the operator can remove those Plates (S3).

The publication index (P1) lives in that directory, beside the registry and outside `keys\`, so the texts that name it are right.

**Not settled:** recovering the list from the server (stage 2).

**Independent concurrence.** The same review **concurred** with no listing request in stage 1, since a listing would need a new proof kind and its own privacy review. It asked that the texts not offer "unpublishing by code", which has no mechanism, and say instead what keeping the directory does and doesn't do, as above.

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

- **Approved so far:** D3 (APPROVED) and D2 (APPROVED IN PRINCIPLE) by the owner; N5, L6, D9b, P2, K1, K2, K6, K7, N3, K3, P1 and P3 (APPROVED, Claude, under the owner's delegation of September 29, 2026). **Every row of this table except "D2 details" and K5, which have their own gate, is now approved,** so G1 is complete for native Windows. K8 and K9 below still gate any other platform, and the "D2 details" and K5 still gate any `.afpersona` file.
- **No row of this table other than "D2 details" and K5 remains to be approved** before a persistent private key is created outside tests on native Windows.
- **"D2 details"** and **K5**, which decision batch B moved here from G3, have their own gate: before any `.afpersona` file is written or restored outside tests.
- **Approved rows** state the approved option in the recommendation column.

| Id | Question | Baseline or current state | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D2 | Recovery from key loss | None. Without the key nothing can be retracted, and there is no account. | Approved in principle: encrypted, portable `.afpersona` backups and restoration, including on another computer without a hosted account; no plaintext private key export (see "Approved decisions") | no | **APPROVED IN PRINCIPLE (2026-09-28)** |
| D2 details | Backup encryption scheme, password policy, key derivation parameters, recovery warnings, implementation | Undefined | Subject to later security approval. Related: K4 and K7 (approved), K5 (UNRESOLVED). No `.afpersona` file may be written or restored outside tests before this is approved. | no | UNRESOLVED |
| K5 | Backup passphrase rules and key derivation route | Undefined | At least 15 characters, or a generated code; NFC; never truncated. The key derivation must use a route verified on every supported platform: `Rfc2898DeriveBytes.Pbkdf2` fails under Wine before 11.3, from source and a published report. Its gate is "D2 details", not G3 (decision batch B, "K5's gate"): it is needed before any `.afpersona` file is written outside tests, and also gates K9's passphrase-wrapped store. A backup file faces offline guessing, so it draws on SP 800-132, RFC 8018 and OWASP's work factors as well as SP 800-63B-4. | no | UNRESOLVED |
| D3 | How many personas an installation holds, and how one is chosen | Undefined | Approved: several independent personas; manual selection and switching; one active at a time for identity-dependent operations; never bound automatically to game identifiers; switching never alters saved Plates or triggers publishing (see "Approved decisions") | no | **APPROVED (2026-09-28)** |
| D9b | How the protocol code ships in the plugin (fourth DLL or sources compiled in) | The preview flavour compiles the sources in; player builds compile none | Approved: the sources compiled into AetherFrame.dll, the package at three files, the standalone projects canonical, and only in the preview flavour (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| N3 | Whether draft documents are distinguishable from final v1 in the signed bytes | Drafts use version 1 and the `…SignedDocument.v1` tag | Approved: drafts carry `protocolVersion` `0x8001` and the tag `…SignedDocument.v1-draft` until the owner's freeze; readers accept exactly one of draft and final; identities unchanged (see "Decision batch A") | **yes** | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| N5 | Whether the profile id stays on every `RemoteDocument` | It did, until the protocol API tidy | Approved: the profile id moves to a closed abstract `RemoteProfileDocument` that the snapshot and retraction derive from; `VerifiedDocument.Profile` is null for a document that isn't about a profile; no signed-byte change (see "Decisions approved under the delegation") | no (public API) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| L6 | Where the provisional `IPersonaKeyProvider` and `FuturePolicy` live | Both were public and marked provisional, until the protocol API tidy | Approved: both removed from the protocol. Key storage and the active persona are plugin policy (`AetherFrame.Personas`); the server-only limits are documentation only, in NETWORK0.md, section 7 (see "Decisions approved under the delegation") | no (public API) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K1 | Persona key algorithm | P-256 ECDSA, P1363, low-S (NETWORK0); the key store core holds P-256 scalars only | Approved: keep it (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K2 | Key storage on native Windows | The store core, the envelope and the directory storage exist behind a protector seam; no protector ships | Approved as the target: DPAPI, CurrentUser scope, UI forbidden, entropy from the envelope header, in the plugin's own files named by slot; never plugin configuration or Dalamud reliable storage. The DPAPI protector itself is increment 7 with its own review (see "Decisions approved under the delegation"). `[updated 2026-09-29: implemented by N2-4, compiled only in the preview flavour and wired by nothing yet.]` `[updated 2026-09-30: N2-5b wires it in the preview flavour, under the persona session: see "Applied by N2-5b" in K2 above.]` | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K3 | Platform enablement policy | The architecture review first proposed disabling personas under Wine | Approved: persona features only where a full-chain capability probe passes and the protector claims protection on this platform (DPAPI: native Windows only); viewing needs only the probe's verification step; local features always unaffected (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K6 | Key rotation | None; the store has no replace and no delete | Approved: none in v1. Migrating means a new persona, republishing, and retracting the old profiles with the old key (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| K7 | Where cryptographic implementations come from | Platform only (.NET over CNG/NCrypt) | Approved: platform implementations only, .NET or the same platform's APIs called directly. A third-party library or an algorithm in our own code is a separate owner decision with its own review, and is never chosen to keep the package at three files (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| P1 | Where "this Plate was published" is remembered | Undefined | Approved: a private publication index per persona in the plugin's networking files; never in Plate JSON, bindings, packages, Templates, configuration or logs (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| P2 | Who can create keys during NETWORK1 | Nobody yet: the preview flavour holds the code, no key store exists | Approved: preview builds only, by the compile-time switch `AetherFrameNetworkPreview`; player and official builds contain none of the networking code (see "Decisions approved under the delegation") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026)** |
| P3 | How the persona registry is kept | Nothing kept: the manager held its records in memory only | Approved: one file of its own bytes beside the key files (`AFPR`, version 1, at most 256 personas and 54,330 bytes, a SHA-256 checksum against corruption); persist, then apply; an unreadable registry refused and never replaced; the file itself, its written-through replace and the single-writer lock are N2-5b's (see "The persona registry's decisions (N2-5a)") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |

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

**Every row of this table is now approved** (decision batch B, September 30, 2026); K5 moved to the "D2 details" gate. The two-player test's server may accept documents signed by real keys once N2-7 and N2-8 apply these decisions. A server open to anyone else also needs I2's per-job isolation and S3's decision on expiry. The freeze stays the owner's, and L13 (section 5) is due before it.

| Id | Question | Baseline | Recommendation (not approved) | Bytes | Status |
|---|---|---|---|---|---|
| D1 | What a retraction means | A signed, terminal retraction with a permanent minimal record | Approved: signed and terminal; republishing uses a new profile id; applying one deletes the profile's revisions, images, share code and revision records at once, keeping only a keyed tombstone (S2), with SQLite's secure delete and a truncating checkpoint (see "Decision batch B") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| D6 | Whether viewers receive signed envelopes or server-checked content | Undecided | Approved: server-checked content, a served profile (`AFSP`) holding no signature, identifier or time, with images fetched by index (see "Decision batch B") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| D7 | Whether documents are bound to a deployment | Not bound | Approved: documents stay unbound; each submission is bound through its request proof's deployment name, the canonical DNS hostname (see D7 above) | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| K4 | Whether a backup is required before the first real publish | Undefined | Approved: a backup, or (while none exists) an explicit per-persona acknowledgement that a lost key means never updating or unpublishing, before the first real publish (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| N1 | Scope of revision and asset ids | Profile scoping only | Approved: revisions per (persona, profile); assets and digests per persona, never deduplicated or compared across personas (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| N7 | Whether consumers must escape names before display or logging | Nothing obliges them | Approved: every consumer treats every text as plain text: no markup, format strings, game text payloads, paths, URLs or commands; never auto-linked; escaped in any HTML (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R1 | How a viewer finds a Plate in the first test | None | Approved: share codes issued per published profile, the only way to reach one in stage 1; no directory, search or lookup; target lookup is stage 2 (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R2 | The transport | None | Approved: HTTPS to one fixed DNS hostname, `HttpClient` with Dalamud's dual-stack callback, traffic only on a player's action, signed bytes as bodies, strict small JSON responses except D6's served profile and I2's images, version checks, no cookies or accounts (see "Decision batch A" and "Decision batch B") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| R3 | Where network code may live | Nowhere (NETWORK1.md, safeguard 1) | Approved: an exact allowlist in the preview flavour under `Services/Network` only: `System.Net.Http` (with `.Headers`), `HttpStatusCode`, Dalamud's dual-stack callback and its `AddressFamily` parameter, `System.Net.Security` only for a TLS option; every other networking type refused; nothing in the player flavour (see "Decision batch A") | no | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |

## 4. Backend-time decisions (G4)

**Every row of this table is now approved** (decision batch B, September 30, 2026).

| Id | Question | Recommendation (not approved) | Status |
|---|---|---|---|
| N2 | Rollback by replaying a pruned revision | Approved: a superseded revision is pruned at once, and (revision id, document SHA-256) is kept until the profile is retracted or removed; "latest" is ordered by a server sequence (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| N6 | Retractions blocked by a fast client clock | Approved: retractions are exempt from the future-skew check, and their `issuedAt` is recorded nowhere; snapshots keep the check as a sanity bound (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| S1 | Request proofs | Approved: required for every document submission, snapshots and retractions alike, under a single-use server challenge (see S1 above) | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29** |
| S2 | What a tombstone holds | Approved: HMAC-SHA256 under a tombstone key over (persona id, profile id), with the key's version and nothing else, kept for the deployment's life (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| S3 | Profiles whose key is lost | Approved for stage 1: no automatic expiry; the operator removes a profile on a request verified out of band, exactly as a retraction (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| S4 | A persona-level revocation document | Approved: deferred; the runbook covers a stolen key (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| I2 | Server image processing | Approved: every image re-processed by an isolated worker whose output is checked before storing, and served by index with fixed types; uploads only from an allowlist of the two testers' personas in stage 1, and per-job isolation before the allowlist is removed (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| R4 | The share-code format | Approved: `AF-` and 16 Crockford Base32 symbols, 75 random bits and a Damm check symbol; only in request bodies; lookups rate-limited per IPv4 /32 and per IPv6 /64, /56 and /48 (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| S5 | Server logging | Approved: no address stored in stage 1; logs hold no document, proof, challenge, share code or identifier, and are kept 14 days (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |
| P4 | A signed request that lists a persona's profiles | Approved: none in stage 1; the operator path (S3) covers a lost publication index (see "Decision batch B") | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30** |

## 5. Other open items

| Id | Item | Status |
|---|---|---|
| L2 | Conformance vectors thinner than the rule set (the large text limits live in unit tests only) | UNRESOLVED (NETWORK1, when a second implementation exists) |
| L4 | A public-only key accepted by `EcdsaPersonaSigner` until first `Sign` | SETTLED for the key store core (September 29, 2026): `ProtectedPersonaKeyStore` makes every signer from checked material, which always holds the private half. The manager's public-key check stays, because the interface allows another store to differ |
| L8 | Signing-context rules not yet in the specification | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-29**: specification, section 5.1 (see L8 above) |
| L9 | Whether protocol tests gate plugin releases (`release.yml`) as well as `build.yml` | UNRESOLVED. Its "Linux never ran" part is closed: the ubuntu leg has run green twice (NETWORK0.md, section 13). |
| I3 | Photo metadata in local `.aetherframe` exports (existing local behaviour, not networking) | UNRESOLVED |
| L10 | Signer lease on a persona switch: may an operation holding a lease for persona A still sign as A after the player switches to B? The interim in `AetherFrame.Personas` (#23) revokes the lease on any change of selection, never revives it, and fails closed, following NETWORK1.md system 1. The alternatives are letting the operation finish as A (which needs system 1 amended) or refusing a switch while a lease is open. | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30**: revoked on any switch, and each operation bound to the persona it showed (see L10 above) |
| L11 | Whether the persona suite also gates plugin releases (`release.yml`). Today only `build.yml` runs it. A question of the same kind as L9; L9 itself stays scoped to the protocol suite. | UNRESOLVED (with L9) |
| L12 | Key files that no persona record names. The store has no delete (K6), so when the check after a durable write fails (a read-back that still fails after its retries, or bytes that differ), `AddKey` reports `CustodyFailed` and the envelope may stay under that fresh slot, which is never recorded or reused. A crash between the key write and the record write leaves the same kind of file. The wiring (increment 9) must detect such files and report them to the player; whether to offer removal, quarantine or recovery is decided there, with a security review. Found by the key store core's independent reviews (September 29, 2026). | **APPROVED (Claude, under the owner's delegation of September 29, 2026), 2026-09-30**: detected and reported, restored only by the player, never deleted (see L12 above) |
| L13 | The default-ignorable code points D4 does not refuse (4,048 in Unicode 18.0, listed under D4's "Not settled"): a name made only of them is accepted and shows nothing, and a name can hide at least 64 bytes of data in them. Refusing them changes which names are valid: a signed-byte change that NETWORK1.md's safeguard 3 reserves for the owner. The trade-off to weigh: local naming (`PlateNaming`) already folds the format characters among them to spaces, so refusing those costs an honest player nothing; but U+FE0E and U+FE0F choose how an emoji is drawn (U+2764 U+FE0F is a common heart), so refusing every variation selector would refuse names players type. | UNRESOLVED. Due before the freeze, since refusing more names after it would invalidate version 1 documents; with the UTS #39 work if that comes first. It does not gate a server that accepts only drafts (NETWORK2's N2-8 and N2-11), since drafts are signed again after the freeze. Changing the rule needs the owner's approval; the owner, who alone freezes version 1, sees it then either way. |

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
