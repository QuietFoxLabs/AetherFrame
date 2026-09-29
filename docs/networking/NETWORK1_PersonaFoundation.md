# NETWORK1: the persona management foundation

**Status (2026-09-29):** implemented and tested as a standalone assembly the plugin does not reference, corrected after an independent security audit and an independent review of those corrections (section 8). It generates no persistent key, stores nothing, encrypts nothing, opens no connection and changes no plugin behaviour. It applies decision D3 and the principle of D2 as recorded in the networking decision register ([DecisionRegister.md](DecisionRegister.md), merged with the planned boundaries in [NETWORK1.md](NETWORK1.md) by the NETWORK1 increment 0 documentation change, pull request #22). Where it had to choose behaviour that an unresolved decision governs, it says so and names the decision (section 7); nothing here approves anything.

## 1. What this increment is

NETWORK0 gave AetherFrame the grammar of a signed publication and a persona identity derived from a public key ([NETWORK0.md](NETWORK0.md)). The owner has since decided that an installation holds several independent personas which the player switches by hand (D3), and that a persona must be exportable as an encrypted, portable backup whose format is still to be approved (D2, in principle).

This increment is the smallest piece of NETWORK1 that those two decisions make safe to build: the **local model** of personas and the **seams** that the still-undecided pieces (protected key storage, the backup format) will plug into. It is not one of the increments NETWORK1.md section 6 proposes: it is the Dalamud-free groundwork that increments 5 (key store core) and 6 (backup codec) would build on, and it has no store, protector or codec, so it crosses none of their gates. It holds the records, the selection, the identity rules and the contracts, with keys living behind an interface that has only in-memory test implementations.

It is deliberately not:

- a key store: no key is written anywhere, protected by anything, or read from anywhere;
- a backup format: no bytes of any `.afpersona` container are defined, and no encryption is implemented or faked;
- a persistence schema: nothing is serialized, and the in-memory text form of a slot id decides nothing about a file;
- a plugin feature: `AetherFrame.csproj` is unchanged, the release package is the same three files, and no command or window exists;
- a change to the protocol: `AetherFrame.Protocol` is unchanged, its public surface and vectors included;
- a release policy: the Release workflow is unchanged (section 6, and L9 in section 7).

## 2. The model

Everything lives in `AetherFrame.Personas` (net10.0, BCL plus `AetherFrame.Protocol`, no NuGet packages), tested by `AetherFrame.Personas.Tests`. In the terms of [NETWORK1.md](NETWORK1.md), it is the Dalamud-free core of system 2 (the installation's independent personas and the one active selection) with custody of the keys themselves left to a later store, and the contracts of system 3 (portable encrypted backups) without their implementation.

| Type | Role |
|---|---|
| `PersonaManager` | The personas this installation holds and which one is active. Create, select, deselect, rename, list, open a signer for the active persona, export a backup, inspect and restore one. In memory only; every member is thread-safe; nothing is persisted, loaded or deleted. |
| `PersonaRecord` | What a listing shows: the local slot, the public key, the identity the protocol derives from it, and the private label. Immutable; no secret is reachable from it; `ToString` gives the slot only. |
| `PersonaSlotId` | A random local handle for a record, minted here and never derived from the persona. Logs, error text and future file names can name a slot without naming an identity, which is what keeps two mentions from being correlated. Different on every installation that holds the same persona. |
| `PersonaLabel` | The private label rule: trimmed, 1 to 64 UTF-16 code units, no control characters, nothing else rewritten. Never published, never logged by this assembly. **Provisional under D9a, which is unresolved** (section 7). |
| `IPersonaKeyStore` | The seam for key custody: generate a key without holding it, commit a checked key under a slot, open a signer for one operation, open material for an export. Nothing in it deletes a key. No implementation in the product assembly. |
| `PersonaKeyMaterial` | A private key while it is in memory: a platform key object that this type created itself, whose only public members are the public key and `CreateSigner`. Accepted only as a consistent P-256 key pair (section 4). No public constructor and no public member takes a platform key: only custody code in this assembly makes material. |
| `PersonaSignerLease` | A borrowed signer for one operation. It signs only as its persona, and only while that persona is still the selection it was opened under; a switch revokes it (section 3, and section 7 for the decision this awaits, L10 in the register). It never hands out the store's own signer. |
| `IPersonaBackupCodec` | The seam for the future `.afpersona` codec: inspect a container without a secret, write material under a secret, open a container under a secret. No implementation in the product assembly. |
| `PersonaBackupSecret` | The player's secret as characters zeroed on disposal, printed as a placeholder. Applies no policy: K5 is open. |
| `PersonaBackupInspection` | What inspection found: a defined status and a version coherent with it (1 or more when a version was read, 0 when the container is malformed). The constructor refuses anything else. |
| `PersonaBackupStatus`, `PersonaRestoreResult`, `PersonaRestoreStatus`, `PersonaSignerAvailability` | Typed outcomes, so a caller never has to parse a message. |
| `PersonaException`, `PersonaError` | Refusals that are programming, store or codec faults (an unknown slot, a bad label, a duplicate identity from a store, a store handing out the wrong key, a codec giving no coherent inspection, a revoked lease). Messages name rules, never labels, identities or bytes. |

The public surface is listed in `AetherFrame.Personas.Tests/Fixtures/public-api.txt` and a test fails when it drifts; regenerate it on purpose with `AETHERFRAME_PERSONAS_REGENERATE_PUBLIC_API=1`. Like the protocol's, the whole surface is provisional until the plugin integrates it.

## 3. The rules the model enforces

Each rule is a test in `AetherFrame.Personas.Tests`.

- **Several independent personas.** Each has its own slot, key and identity; the store holds one key per slot; nothing links two personas but their presence in the same list.
- **One active persona, chosen by the player.** `Select` and `Deselect` are the only ways the selection changes. A table of every other public member of the manager, each in its succeeding and failing forms (create, a duplicate create, a failed commit, rename, list, open a signer, export, inspect, restore, a duplicate restore, an unsupported, malformed or undecodable backup, a wrong secret, a failed select), is run with and without a selection, and none of them changes it. The table is checked against the manager's public members by reflection, by full signature (so a new overload counts), and the manager is pinned to implement no interface and to have no base class but `object`, so a new way in fails the tests until it is classified. Without a selection, the answer to "sign as the active persona" is `NoActivePersona`, and nothing is chosen on the player's behalf.
- **Switching changes only the selection, and revokes open leases.** The record list is the same objects in the same order and the store is not called. A lease opened before the switch stops signing: its signer refuses with `LeaseRevoked`, and stays revoked if the first persona is selected again. Selecting the persona that is already active is not a switch and revokes nothing. This follows NETWORK1.md, system 1: nothing signs for a persona that is not the active one. It is the interim policy; the decision it awaits (L10 in the register) is in section 7.
- **A lease signs only as its persona.** Its signer checks, under the manager's lock and for every signature, that the lease is open and current, that the signing input names its persona, and that the store's signer still reports that persona's key; the store's signer never leaves the lease. A test holds a signature in flight and shows that a switch waits for it and that the lease is revoked after it; another shows that disposing a lease waits for its signature and disposes the store's signer after it; a third shows that a store call waits while a store signer is being disposed. Revocation refuses signatures; it does not release the store's signer, which the caller releases by disposing the lease.
- **Personas stay independent.** No operation associates two personas, and nothing links two records but their presence in the same list. A deliberate association, if the owner ever wants one, would be an explicit feature and never a side effect of anything here.
- **An identity is unique, and checked before anything is committed.** A new key's identity, and a restored key's, is compared with every persona held before the store is asked to keep it. A duplicate from a store is refused with `DuplicateIdentity`; a duplicate restore reports `AlreadyPresent` and leaves the existing record, label, key and selection exactly as they were. Neither reaches the store. The check and the commit are one step under the manager's lock; a test holds one restore's commit open while a second restore of the same backup runs, and the backup is committed once.
- **No orphaned keys.** Nothing refuses a create or a restore after the store has committed the key, so a refusal never leaves a key in the store without a record (section 4). `[updated 2026-09-29: except in one case a store without a delete cannot undo. When its verification fails after the storage accepted the key, the envelope may stay under a fresh slot the manager never records or reuses (IPersonaKeyStore.AddKey; L12 in DecisionRegister.md; NETWORK1_KeyStoreCore.md, section 3).]`
- **A missing key is a typed answer.** When the store cannot open the active persona's key, `TryOpenActiveSigner` answers `KeyUnavailable`; the selection and the records are untouched. A store that opens a key belonging to another persona, or that holds a different key than it was given, is caught by comparing public keys at every use, and refused; a store signer the manager refuses, or whose key cannot be read, is disposed. A store signer that reports the right public key but holds no private half is not caught until its first signature fails (L4, section 7).
- **No character, account or Plate can reach the model.** No public member takes a Content ID, a Guid, a character, a World or a Plate, and no type or member in the assembly, public or private, names one. The assembly references only the runtime and `AetherFrame.Protocol`; it cannot see the plugin's Plate, character-binding or configuration types, so it cannot read or rewrite them. Nothing in it is keyed by, or derived from, anything about the player.
- **No network, no files.** The assembly references no `System.Net`, process or JSON assembly, and a test reads the compiled assembly's type references and allows only the namespaces the model needs, with no file, stream, network, process, console or environment type. That test is needed because on .NET 10 file access needs no file-system assembly reference: `File` lives in `System.Runtime`. Every test runs with no connection and touches no user data, saved Plate or Dalamud configuration.
- **Identity is the protocol's.** A record's identity is `PersonaPublicKey.Id`, exactly as NETWORK0 derives it; a document signed through a lease verifies to that identity; a persona restored elsewhere has the same identity under a different slot.

## 4. What the seams promise, and what they do not

`IPersonaKeyStore` and `IPersonaBackupCodec` are shapes, not protection. Nothing about either interface makes a key safe or a backup secure; only a reviewed implementation can, and none exists. What the shapes do guarantee is where private material can and cannot go, and in what state it may be accepted.

**Key material is a checked key pair.** `PersonaKeyMaterial` accepts a private scalar and a recorded public key only after four checks, in this order:

1. The scalar is exactly 32 big-endian bytes from 1 to n − 1, checked in managed code, in time that does not depend on the value, **before any platform import**.
2. The platform derives the public point from the scalar alone. No point is given to the import, so no platform can accept a pair by trusting a point it was handed. This uses .NET's `ECDsa` import, the platform's own implementation; no elliptic-curve arithmetic is written here.
3. The derived point must equal the recorded one (fixed-time comparison; the protocol's own on-curve check also runs).
4. The scalar the platform then holds must equal the scalar given.

The scalar is copied once before step 1, and only the copy is checked and imported. The platform key that passes is one this type created and shares with nobody. There is no public way to hand in an `ECDsa`: the internal path that reads one copies it through the four checks, zeroes the scalar it read, never retains the caller's object and never disposes it, so a caller that later imports another key into its object, generates a new one in it or disposes it cannot reach the material. Every copy (`Copy`, `CreateSigner`) goes through the same checks again.

What the tests pin, and what they cannot: steps 1 to 3, the refusal to retain or dispose a caller's key, the zeroing of the scalars read from a caller's key, and the re-check on every copy each have a test that fails without them. Step 4 is defence in depth: no platform tested alters a scalar that passed step 1, so no test can tell it apart from its absence without a faulty platform. The zeroing of the private copies made inside the checks is not observable from a test either.

The platforms differ at step 1, which is why no platform is trusted with it (NETWORK1.md, safeguard 6). With these checks in place, the corrected suite passed on the windows-2022 runner and on ubuntu-24.04 in CI, so deriving the point from the scalar alone works on both; that too is a CI observation, not a local Windows measurement.

| Platform | What it does with a bad scalar | How this is known |
|---|---|---|
| Windows 11 Pro, CNG | Refuses D = 0, D = n, D = 2²⁵⁶ − 1 and a D/Q mismatch | The native Windows harness, [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md), section 1 |
| windows-2022 GitHub runner, CNG (Windows Server 2022, 10.0.20348) | Imported D = 2²⁵⁶ − 1 paired with an unrelated key's Q without an error, so it did not refuse an inconsistent pair, and handed back a scalar in range, not the one given. Whether the key then held the given Q or a recomputed one was not recorded | Observed in CI only (run 36502675177, the first run of this pull request); not measured on a local machine |
| Linux, OpenSSL 3.0.13, .NET 10.0.12 | Refuses D = 0, n, n + 1, 2²⁵⁶ − 1 and a D/Q mismatch; derives Q from D alone; **accepts a 31-byte D**, reading it as a smaller number | Measured in this increment's Linux container on 2026-09-29 (not a platform players run Dalamud on) |

So a scalar a platform would reduce or pad, and a scalar whose point is someone else's, are refused on every platform by the managed checks, with no platform error involved. The tests say so directly: every non-canonical vector (zero, n, n + 1, n + 2 and 2²⁵⁶ − 1; lengths of 0 and 31 bytes; and 33- and 64-byte inputs whose first 32 bytes are a canonical scalar, so that only the length check can refuse them) is paired with the point its reduced value would give, and is refused with no inner platform exception; `n + 1` with the base point, which a reducing platform would find consistent, is refused; the boundaries 1 and n − 1 are accepted with G and −G; a scalar with another key's point, with its own point negated, with the base point, and the neighbouring scalar with its point, are refused.

**Custody is taken in two steps, and nothing refuses after the second.**

- `GenerateKey` makes a key the store is able to hold but does not hold; a restored key comes from the codec, also unheld.
- The manager checks the key's identity against every persona it holds.
- Only then does `AddKey` commit a copy of it under a fresh slot, and the record is added. Nothing after `AddKey` returns can refuse the operation.

`AddKey`'s contract binds every implementation:

- **It commits exactly the key it was given.** The manager records the persona when `AddKey` returns and never sees the private key again, so a store that commits anything else (a scalar that lost a byte in serialization, say) leaves a persona that can never sign or be backed up. The manager catches that at first use and never signs as the wrong persona, but it cannot recover the key; a store must verify what it wrote before it returns.
- **It never replaces.** A slot already held is refused and its key is left as it was.
- **It is atomic.** When it throws, nothing is held under the slot. `[updated 2026-09-29: narrowed by the key store core. When it throws before anything became durable, nothing is held. A store that cannot delete may be left holding an envelope when its verification fails after a durable write, and must say so in its exception; the caller never records or reuses that slot (IPersonaKeyStore.AddKey; L12 in DecisionRegister.md).]`
- **It never retains the caller's material.** The caller disposes that whether `AddKey` returns or throws.

So a duplicate create or restore never reaches the store, and a store failure leaves no record and, from a store that honours the contract, no key. The manager has no way to delete a key, so cleaning up after a failed operation can never remove a valid persona's. No rollback is therefore needed or performed. A store that breaks atomicity (commits, then throws) is not hidden: its exception reaches the caller unchanged, and a test pins that behaviour. Ownership is uniform:

- material or a signer a store returns is the caller's to dispose;
- material a caller passes in stays the caller's;
- every material the manager is handed is disposed on every path, including a codec or store exception.

**A restore opens only what it inspected, and only when inspection says so.**

- The manager copies the caller's bytes once, and that private copy is what the codec inspects and then opens. A buffer another thread changes meanwhile cannot change what is opened, and the copy is zeroed afterwards. A test overwrites the caller's buffer with another persona's backup during inspection, and the persona restored is the one inspected.
- The secret reaches the codec only after inspection returned a coherent `Supported` result. No result, an undefined status (including 0, the value of a status nobody set: `PersonaBackupStatus` starts at 1), or a version that contradicts its status is refused with `InvalidBackupInspection`, and `InspectBackup` refuses it the same way. `UnsupportedVersion` and `Malformed` stop the restore with their own outcomes. None of these presents the secret. A test rewrites the caller's buffer before the codec reads it, and the codec still sees the private copy.
- A restore never overwrites: a persona already held is reported present, the material the codec produced is disposed, and the existing record is returned unchanged. A restored persona gets a fresh slot and is not selected.
- Once the codec has the secret, every refusal is reported as `CannotOpen` (a wrong secret, damaged content, or content the codec finds unsupported or malformed only when it opens it: not distinguished), or as `InvalidKey` for a key pair that `PersonaKeyMaterial` refuses or a codec that opens nothing. So `UnsupportedVersion` and `Malformed` always mean the secret was not used.

**Only an export reaches for material.** A private key crosses a public boundary only as `PersonaKeyMaterial`, which has no public accessor to the key bytes; there is no `byte[]` of a key anywhere on the public surface. The one internal accessor, `ExportPrivateParameters`, is for custody code in the same assembly, which zeroes the scalar after use. The manager asks a store for material for one thing only: an export the player asked for.

**Concurrency.**

- The manager serializes every store call it makes, and every call to a store's signer (signing and disposal), under its lock, because `EcdsaPersonaSigner` and any future protected store are not thread-safe.
- The price: every manager member, listings included, waits while a store call runs. The in-memory doubles are instant; a store that reads files, unprotects keys or prompts would stall a frame thread that only wanted a listing. The key store increment must keep its calls short or split the manager's locking, and that is its decision.
- Codec calls run outside the lock, so a slow key derivation never stalls a listing. A codec is a function of its inputs and must tolerate concurrent calls.

What the seams do **not** decide, on purpose:

- whether a backup carries the label: the caller supplies a label on restore, so the format may carry one or not;
- the secret policy (K5);
- the container's layout, KDF, cipher and parameters (D2's open items);
- how a real store protects keys at rest (K2, K3, K9).

The coherence rule for an inspection (a version of 1 or more when one was read, 0 when malformed) is a rule of this in-memory model, not a format decision.

## 5. Security review of this increment

| Concern | Finding |
|---|---|
| Accidental persona correlation | Records, leases and material print the slot or a placeholder, never the identity or the label. Slots are random and differ per installation. The only place two personas meet is the manager's list. Exception messages name rules and carry no identifiers; a test scans them for `psn_`, `slot_`, labels, secrets and hex runs. |
| Private key exposure | No public member returns a platform key, `ECParameters` or key bytes; the only byte-producing members are the two backup ones, which produce a codec's container by contract. No public member takes a platform key either. Material is a checked key pair held in a platform key object this assembly created; exported and copied scalars are zeroed; every material and store signer the manager handles is disposed on every path. A revoked lease keeps its store signer until the caller disposes it. What a disposed platform key leaves in process memory is not claimed. |
| Inconsistent or malformed keys | Scalar range and length checked in managed code before import; public point derived by the platform from the scalar alone and compared with the recorded one; held scalar compared with the one given. Platform differences (a reducing CNG, a padding OpenSSL) cannot let a bad pair through. |
| Orphaned custody | Identity checked before the store commits; nothing refuses after; the store contract forbids replacing and requires atomic commits; the manager never deletes. `[updated 2026-09-29: atomic up to the durable write only. A verification that fails after it can leave an unrecorded envelope, which the wiring must detect and report (L12 in DecisionRegister.md).]` |
| Backup handling | Explicit, coherent `Supported` required before the secret is used; one private copy inspected and opened; duplicates never overwrite. |
| Signing for the wrong persona | A lease signs only while its persona is the selection it was opened under, only inputs naming that persona, and only through a store signer that still reports that persona's key, all checked under the manager's lock. |
| Unintended logging | The assembly logs nothing and references no logging. `ToString` on every type is safe to log. Labels are never included in any text the assembly produces. |
| Global state | None. Every manager is an instance with its own list, selection and lock; the test doubles are instances too. |
| Mutation of Plate models | Impossible by construction: the assembly cannot reference the plugin, and nothing in it names a Plate, a profile, a character or a binding, verified by a test over every type and member including private ones. |
| Thread safety | One lock serializes every manager operation, its store calls, and lease signatures and disposal; codec calls run outside it and a codec must be a function of its inputs; listings are snapshots; records are immutable. Deterministic tests hold a signature, a lease disposal and a restore commit in flight and show what waits for them; concurrency tests drive creates, selects, restores and lease signatures from several threads. A slow store would stall every member (section 4). |
| Insecure backup assumptions | No format, no fake cipher, no plaintext file. The test double's bytes are a random handle into test memory and are checked to contain no key, identity or secret. The docs say the seams are shapes, not security. |
| Premature public APIs | The surface is the minimum for the required operations plus the two seams, listed in a fixture, and marked provisional. Records, leases, results and key material cannot be constructed outside the assembly. |
| Unnecessary dependencies | None: BCL and `AetherFrame.Protocol`. No NuGet package, no Dalamud, no JSON, no file system, no network; the last three checked in the compiled assembly's type references. |

Two honest limits, neither introduced by this increment: a .NET string that held a secret before `PersonaBackupSecret` copied it (an input field's, say) cannot be zeroed from here; and a full memory dump taken while material is in memory can contain the key, as it can for any process holding one.

## 6. Verification

From the repository root, with `DALAMUD_HOME` set as for any plugin build:

```
dotnet build AetherFrame.slnx -c Release
dotnet test AetherFrame.Personas.Tests/AetherFrame.Personas.Tests.csproj -c Release --no-build
```

The Build workflow (`build.yml`) runs the suite on windows-2022 and ubuntu-24.04 after the protocol suite, on every push to master and every pull request. The plugin package check in that workflow (`validate-package`) confirms the release package still holds exactly the three plugin files, since the plugin does not reference the new assembly.

The Release workflow (`release.yml`) is **not** changed. Whether the persona suite should also block a plugin release is a new question of the same kind as L9, tracked in the register as L11 and unresolved (section 7).

## 7. Decisions this increment leaves open

None of these is approved by this increment, and none has to be decided before it merges: the plugin does not reference the assembly and nothing signs outside tests. Each is recorded in the register ([DecisionRegister.md](DecisionRegister.md)), where it is decided. Since the owner's delegation of September 29, 2026, the autopilot decides unresolved items there (ROADMAP.md, section 5), except the D3 wording below, which changes the owner's own approval.

**Signer lease on a persona switch (register L10, unresolved).**

- **The question.** When the player switches personas while an operation holds a signer lease for the previous persona, may that operation still sign as the previous persona, or must it stop?
- **What the code does now, as an interim.** The lease is revoked by any change of the active selection (select another persona, or deselect) and never revives. The operation fails with `LeaseRevoked` and must start again under the new selection.
- **Why this interim.** It follows NETWORK1.md, system 1 ("must never sign for a persona that is not the active one"), and it fails closed.
- **The alternatives.**
  - Let an operation finish under the persona it started with, which would need that boundary amended.
  - Refuse or defer a switch while a lease is open.
- **What it affects.** Nothing player-facing exists yet: nothing signs outside tests. It must be decided before the preview wiring (NETWORK1.md, increment 9) lets a player start an operation that signs.
- **Where it is recorded.** In the register as L10 (section 5), unresolved.

**D9a, the persona display name (unresolved).** `PersonaLabel` implements the register's recommended option, a private local label only, so the model can be exercised. It is provisional, as the code and this document say. If D9a is decided otherwise, the label rule, `Create`'s and `RestoreBackup`'s label parameter, and `Rename` may change or go.

**L6, the protocol's provisional `IPersonaKeyProvider` and `FuturePolicy` (unresolved).**

- This increment neither uses, implements, moves nor removes either, and changes nothing in `AetherFrame.Protocol`.
- It adds a separate seam, `IPersonaKeyStore`, in the persona assembly, and a manager whose `TryOpenActiveSigner` answers with a typed availability and a revocable lease. That is one data point for L6's question about the provider's real shape: a synchronous getter for one active signer does not express several personas, an unavailable key, or revocation.
- It does not decide L6. If L6 keeps the provider in the protocol, an adapter over the manager would be needed; if it removes it, nothing here depends on it.
- `[updated 2026-09-29: L6 is APPROVED (Claude, under the owner's delegation of September 29, 2026). NETWORK1 increment 1 removed both IPersonaKeyProvider and FuturePolicy from the protocol, so nothing here needs an adapter. The persona assembly's seams are where key storage policy lives.]`
- L4 (a public-only key accepted by `EcdsaPersonaSigner` until the first `Sign`) is untouched and stays open. `PersonaKeyMaterial` can never be public-only, and a signer made by its `CreateSigner` always holds the private half. But `IPersonaKeyStore.OpenSigner` returns any `IPersonaSigner`, and the manager checks only the public key it reports, so a store could still hand out a public-only signer: the lease would open and its first signature would fail. Whether the store must return material for the manager to make the signer, or the manager should probe the signer, is for the key store's design, which is where the register places L4.

**L9 (the protocol suite in `release.yml`, unresolved) and the persona gate (register L11, unresolved).**

- The first version of this increment added the persona suite to `release.yml`. That would have extended an unresolved release policy without approval, so it was withdrawn; only `build.yml` runs the suite.
- **The proposed change, to approve or refuse under L11:** add a "Test the persona foundation" step to `release.yml` after "Test the protocol", running `dotnet test AetherFrame.Personas.Tests/AetherFrame.Personas.Tests.csproj --configuration Release --no-build`.
- Approving it means a persona test failure blocks a plugin release even though the plugin does not use the assembly. Refusing it keeps release gating as it is today. The register scopes L9 to the protocol suite; the persona gate is a new question of the same kind, and L9 itself is unchanged. It is tracked as its own register entry, L11, and L9 is not widened.

**D3's wording.** The owner's D3 instruction to this increment included a point that the register's approved text does not quote: distinct personas remain independent unless the user deliberately associates them. The model enforces it: no operation associates two personas. The register now records the clause under D3 as wording the approved text does not quote, and leaves the approved text exactly as approved. Adding the clause to it changes the owner's own approval, so only the owner can do that.

**Before any real key exists.** Every row of the register's G1 table other than the approved D2 and D3 must be approved: the D2 details, D9b, N3, N5, L6, K1, K2, K3, K6, K7, P1 and P2, plus K8 and K9 if persona features are pursued under Wine, Proton or macOS. The D2 details, with K5, also gate any `.afpersona` file written or restored outside tests ([DecisionRegister.md](DecisionRegister.md), section 1).

The recommended next increment is NETWORK1.md's increment 1, the protocol API tidy (N5, L6). It changes no signed bytes and is the cheapest moment for that change, because nothing depends on the API yet. After that, increment 2 (the draft marker and the name rule, N3 and D4) is the gate before any persistent key signs anything. The key store core (increment 5) would build on this assembly by implementing `IPersonaKeyStore` over protected files behind a fake protector first, and the backup codec (increment 6) by implementing `IPersonaBackupCodec`. Increment 5 may need to change the manager's locking (section 4) and settle L4 (above).

## 8. The independent audit and its corrections (2026-09-29)

An independent security audit of the first version found the multiple-persona architecture sound and the increment not ready to merge. Each finding and its correction:

| Finding | Correction |
|---|---|
| Key material did not check that the public point belongs to the private scalar, and trusted the platform to refuse an out-of-range scalar | Section 4, "Key material is a checked key pair": managed range and length check before import, point derived from the scalar alone and compared, held scalar compared |
| Key material kept the caller's `ECDsa`, which the caller could change after validation | No public constructor or member takes a platform key; the internal path copies through the checks and never retains or disposes the caller's object |
| A test accepted whatever a platform did with an out-of-range scalar | Replaced by platform-independent vectors that a missing managed check fails on every platform |
| A refused create (duplicate identity) or restore (store reporting another identity) left the key in the store with no record | Two-step custody: identity checked before `AddKey`, nothing refuses after it, no deletion path |
| A restore proceeded on anything but `UnsupportedVersion` or `Malformed`, including an undefined status, and failed on a null inspection | Explicit, coherent `Supported` required; incoherent inspections refused before the secret is used; the inspection type refuses incoherent values |
| The bytes inspected and the bytes opened could differ if the caller's buffer changed | One private copy, inspected and opened, zeroed afterwards |
| A lease opened for one persona kept signing after a switch, contrary to NETWORK1.md system 1 | Revoked on switch (interim, section 7); the store's signer is never exposed |
| `release.yml` gated releases on the persona suite while L9 is unresolved | Withdrawn; tracked as L11 (section 7) |
| Labels, L6 and L9 were not marked as unresolved where the increment touches them | Section 7 |

### The review of the corrections (2026-09-29)

An independent six-part review of the corrections (key validation, custody, the restore gate, selection and leases, test adequacy, and governance), with every finding checked by a separate adversarial verifier, found no violation of the audit's requirements. It confirmed smaller defects, corrected here:

| Finding | Correction |
|---|---|
| A store signer whose public key could not be read was not disposed | Disposed on every way out of `TryOpenActiveSigner` |
| A lease disposed the store's signer outside the manager's lock, contrary to the class's promise | Disposed under the lock; a test shows disposal waits for a signature in flight |
| `Supported` was the zero value of `PersonaBackupStatus`, so a status nobody set counted as supported | The enum starts at 1; 0 is refused as an incoherent inspection |
| A refusal from `Open`, after the secret was used, was reported as `UnsupportedVersion` or `Malformed`, which promise no secret was used | Reported as `CannotOpen` (or `InvalidKey`) |
| The range check read the caller's scalar, then imported a second copy | One copy, checked and imported |
| Tests would not have caught a missing lock around lease signing or disposal, a split duplicate check and commit on restore, inspection of the caller's buffer, three restore outcome mappings, a weakened length check, missing zeroing of a caller's scalars, a copy made without re-checking, an undisposed refused signer, a new overload or interface on the manager, or file access | A test for each, every one shown to fail against the regression it guards |
| The docs overstated L4, left the windows-2022 runner's acceptance of an inconsistent pair out, listed the G1 rows without the D2 details, placed the increment on a roadmap entry that does not exist, widened L9's scope in a heading, and did not say that a slow store stalls every manager member or that revocation does not release a signer | Sections 1, 3, 4, 5 and 7 |

It also recorded, without a correction here: a store that commits a different key than it was given loses a newly created persona (the store contract now forbids it, section 4), and the manager's single lock makes every member wait on a slow store (for the key store increment).
