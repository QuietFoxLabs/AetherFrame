# NETWORK1: the persona management foundation

**Status (2026-09-29):** implemented and tested as a standalone assembly the plugin does not reference, corrected after an independent security audit (section 8). It generates no persistent key, stores nothing, encrypts nothing, opens no connection and changes no plugin behaviour. It applies decision D3 and the principle of D2 as recorded in the networking decision register ([DecisionRegister.md](DecisionRegister.md), merged with the planned boundaries in [NETWORK1.md](NETWORK1.md) by the NETWORK1 increment 0 documentation change, pull request #22). Where it had to choose behaviour that an unresolved decision governs, it says so and names the decision (section 7); nothing here approves anything.

## 1. What this increment is

NETWORK0 gave AetherFrame the grammar of a signed publication and a persona identity derived from a public key ([NETWORK0.md](NETWORK0.md)). The owner has since decided that an installation holds several independent personas which the player switches by hand (D3), and that a persona must be exportable as an encrypted, portable backup whose format is still to be approved (D2, in principle).

This increment is the smallest piece of NETWORK1 that those two decisions make safe to build: the **local model** of personas and the **seams** that the still-undecided pieces (protected key storage, the backup format) will plug into. It is the Dalamud-free core of the roadmap's vault increment without the vault: the records, the selection, the identity rules and the contracts, with keys living behind an interface that has only in-memory test implementations.

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
| `PersonaSignerLease` | A borrowed signer for one operation. It signs only as its persona, and only while that persona is still the selection it was opened under; a switch revokes it (section 3, and section 7 for the owner decision this awaits). It never hands out the store's own signer. |
| `IPersonaBackupCodec` | The seam for the future `.afpersona` codec: inspect a container without a secret, write material under a secret, open a container under a secret. No implementation in the product assembly. |
| `PersonaBackupSecret` | The player's secret as characters zeroed on disposal, printed as a placeholder. Applies no policy: K5 is open. |
| `PersonaBackupInspection` | What inspection found: a defined status and a version coherent with it (1 or more when a version was read, 0 when the container is malformed). The constructor refuses anything else. |
| `PersonaBackupStatus`, `PersonaRestoreResult`, `PersonaRestoreStatus`, `PersonaSignerAvailability` | Typed outcomes, so a caller never has to parse a message. |
| `PersonaException`, `PersonaError` | Refusals that are programming, store or codec faults (an unknown slot, a bad label, a duplicate identity from a store, a store handing out the wrong key, a codec giving no coherent inspection, a revoked lease). Messages name rules, never labels, identities or bytes. |

The public surface is listed in `AetherFrame.Personas.Tests/Fixtures/public-api.txt` and a test fails when it drifts; regenerate it on purpose with `AETHERFRAME_PERSONAS_REGENERATE_PUBLIC_API=1`. Like the protocol's, the whole surface is provisional until the plugin integrates it.

## 3. The rules the model enforces

Each rule is a test in `AetherFrame.Personas.Tests`.

- **Several independent personas.** Each has its own slot, key and identity; the store holds one key per slot; nothing links two personas but their presence in the same list.
- **One active persona, chosen by the player.** `Select` and `Deselect` are the only ways the selection changes. A table of every other public member of the manager, each in its succeeding and failing forms (create, a duplicate create, a failed commit, rename, list, open a signer, export, inspect, restore, a duplicate restore, an unsupported, malformed or undecodable backup, a wrong secret, a failed select), is run with and without a selection, and none of them changes it. The table is checked against the manager's public members by reflection, so a new member fails the test until it is classified. Without a selection, the answer to "sign as the active persona" is `NoActivePersona`, and nothing is chosen on the player's behalf.
- **Switching changes only the selection, and revokes open leases.** The record list is the same objects in the same order and the store is not called. A lease opened before the switch stops signing: its signer refuses with `LeaseRevoked`, and stays revoked if the first persona is selected again. Selecting the persona that is already active is not a switch and revokes nothing. This follows NETWORK1.md, system 1: nothing signs for a persona that is not the active one. It is the interim policy; the owner decision it awaits is in section 7.
- **A lease signs only as its persona.** Its signer checks, under the manager's lock and for every signature, that the lease is open and current, that the signing input names its persona, and that the store's signer still reports that persona's key; the store's signer never leaves the lease. A concurrency test switches personas while a lease signs and shows a revoked lease never signs again.
- **Personas stay independent.** No operation associates two personas, and nothing links two records but their presence in the same list. A deliberate association, if the owner ever wants one, would be an explicit feature and never a side effect of anything here.
- **An identity is unique, and checked before anything is committed.** A new key's identity, and a restored key's, is compared with every persona held before the store is asked to keep it. A duplicate from a store is refused with `DuplicateIdentity`; a duplicate restore reports `AlreadyPresent` and leaves the existing record, label, key and selection exactly as they were. Neither reaches the store.
- **No orphaned keys.** Nothing refuses a create or a restore after the store has committed the key, so a refusal never leaves a key in the store without a record (section 4).
- **A missing key is a typed answer.** When the store cannot open the active persona's key, `TryOpenActiveSigner` answers `KeyUnavailable`; the selection and the records are untouched. A store that opens a key belonging to another persona, or that holds a different key than it was given, is caught by comparing public keys at every use, and refused.
- **No character, account or Plate can reach the model.** No public member takes a Content ID, a Guid, a character, a World or a Plate, and no type or member in the assembly, public or private, names one. The assembly references only the runtime and `AetherFrame.Protocol`; it cannot see the plugin's Plate, character-binding or configuration types, so it cannot read or rewrite them. Nothing in it is keyed by, or derived from, anything about the player.
- **No network, no files.** The assembly references no `System.Net`, file-system, process or JSON assembly. Every test runs with no connection and touches no user data, saved Plate or Dalamud configuration.
- **Identity is the protocol's.** A record's identity is `PersonaPublicKey.Id`, exactly as NETWORK0 derives it; a document signed through a lease verifies to that identity; a persona restored elsewhere has the same identity under a different slot.

## 4. What the seams promise, and what they do not

`IPersonaKeyStore` and `IPersonaBackupCodec` are shapes, not protection. Nothing about either interface makes a key safe or a backup secure; only a reviewed implementation can, and none exists. What the shapes do guarantee is where private material can and cannot go, and in what state it may be accepted.

**Key material is a checked key pair.** `PersonaKeyMaterial` accepts a private scalar and a recorded public key only after four checks, in this order:

1. The scalar is exactly 32 big-endian bytes from 1 to n − 1, checked in managed code, in time that does not depend on the value, **before any platform import**.
2. The platform derives the public point from the scalar alone. No point is given to the import, so no platform can accept a pair by trusting a point it was handed. This uses .NET's `ECDsa` import, the platform's own implementation; no elliptic-curve arithmetic is written here.
3. The derived point must equal the recorded one (fixed-time comparison; the protocol's own on-curve check also runs).
4. The scalar the platform then holds must equal the scalar given.

The platform key that passes is one this type created and shares with nobody. There is no public way to hand in an `ECDsa`: the internal path that reads one copies it through the four checks, never retains the caller's object and never disposes it, so a caller that later imports another key into its object, generates a new one in it or disposes it cannot reach the material. Every copy (`Copy`, `CreateSigner`) goes through the same checks again.

The platforms differ at step 1, which is why no platform is trusted with it (NETWORK1.md, safeguard 6):

| Platform | What it does with a bad scalar | How this is known |
|---|---|---|
| Windows 11 Pro, CNG | Refuses D = 0, D = n, D = 2²⁵⁶ − 1 and a D/Q mismatch | The native Windows harness, [NETWORK1_CryptoCompatibility.md](NETWORK1_CryptoCompatibility.md), section 1 |
| windows-2022 GitHub runner, CNG | Imported a scalar above the order and handed back a reduced scalar | Observed in CI only (run 36502675177, the first run of this pull request); not measured on a local machine |
| Linux, OpenSSL 3.0.13, .NET 10.0.12 | Refuses D = 0, n, n + 1, 2²⁵⁶ − 1 and a D/Q mismatch; derives Q from D alone; **accepts a 31-byte D**, reading it as a smaller number | Measured in this increment's Linux container on 2026-09-29 (not a platform players run Dalamud on) |

So a scalar a platform would reduce or pad, and a scalar whose point is someone else's, are refused on every platform by the managed checks, with no platform error involved. The tests say so directly: every non-canonical vector (zero, n, n + 1, n + 2 and 2²⁵⁶ − 1, and lengths of 0, 31, 33 and 64 bytes) is paired with the point its reduced value would give, and is refused with no inner platform exception; `n + 1` with the base point, which a reducing platform would find consistent, is refused; the boundaries 1 and n − 1 are accepted with G and −G; a scalar with another key's point, with its own point negated, with the base point, and the neighbouring scalar with its point, are refused.

**Custody is taken in two steps, and nothing refuses after the second.**

- `GenerateKey` makes a key the store is able to hold but does not hold; a restored key comes from the codec, also unheld.
- The manager checks the key's identity against every persona it holds.
- Only then does `AddKey` commit a copy of it under a fresh slot, and the record is added. Nothing after `AddKey` returns can refuse the operation.

`AddKey`'s contract binds every implementation:

- **It never replaces.** A slot already held is refused and its key is left as it was.
- **It is atomic.** When it throws, nothing is held under the slot.
- **It never retains the caller's material.** The caller disposes that whether `AddKey` returns or throws.

So a duplicate create or restore never reaches the store, and a store failure leaves no record and, from a store that honours the contract, no key. The manager has no way to delete a key, so cleaning up after a failed operation can never remove a valid persona's. No rollback is therefore needed or performed. A store that breaks atomicity (commits, then throws) is not hidden: its exception reaches the caller unchanged, and a test pins that behaviour. Ownership is uniform:

- material or a signer a store returns is the caller's to dispose;
- material a caller passes in stays the caller's;
- every material the manager is handed is disposed on every path, including a codec or store exception.

**A restore opens only what it inspected, and only when inspection says so.**

- The manager copies the caller's bytes once, and that private copy is what the codec inspects and then opens. A buffer another thread changes meanwhile cannot change what is opened, and the copy is zeroed afterwards. A test overwrites the caller's buffer with another persona's backup during inspection, and the persona restored is the one inspected.
- The secret reaches the codec only after inspection returned a coherent `Supported` result. No result, an undefined status, or a version that contradicts its status is refused with `InvalidBackupInspection`, and `InspectBackup` refuses it the same way. `UnsupportedVersion` and `Malformed` stop the restore with their own outcomes. None of these presents the secret.
- A restore never overwrites: a persona already held is reported present, the material the codec produced is disposed, and the existing record is returned unchanged. A restored persona gets a fresh slot and is not selected.
- A wrong secret and damaged content are one outcome, `CannotOpen`, so a codec is not asked to distinguish them. A codec that opens nothing yields `InvalidKey`.

**Only an export reaches for material.** A private key crosses a public boundary only as `PersonaKeyMaterial`, which has no public accessor to the key bytes; there is no `byte[]` of a key anywhere on the public surface. The one internal accessor, `ExportPrivateParameters`, is for custody code in the same assembly, which zeroes the scalar after use. The manager asks a store for material for one thing only: an export the player asked for.

**Concurrency.**

- The manager serializes every store call it makes, and every signature made through a lease, under its lock, because `EcdsaPersonaSigner` and any future protected store are not thread-safe.
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
| Private key exposure | No public member returns a platform key, `ECParameters` or key bytes; the only byte-producing members are the two backup ones, which produce a codec's container by contract. No public member takes a platform key either. Material is a checked key pair held in a platform key object this assembly created; exported and copied scalars are zeroed; every material the manager handles is disposed on every path. What a disposed platform key leaves in process memory is not claimed. |
| Inconsistent or malformed keys | Scalar range and length checked in managed code before import; public point derived by the platform from the scalar alone and compared with the recorded one; held scalar compared with the one given. Platform differences (a reducing CNG, a padding OpenSSL) cannot let a bad pair through. |
| Orphaned custody | Identity checked before the store commits; nothing refuses after; the store contract forbids replacing and requires atomic commits; the manager never deletes. |
| Backup handling | Explicit, coherent `Supported` required before the secret is used; one private copy inspected and opened; duplicates never overwrite. |
| Signing for the wrong persona | A lease signs only while its persona is the selection it was opened under, only inputs naming that persona, and only through a store signer that still reports that persona's key, all checked under the manager's lock. |
| Unintended logging | The assembly logs nothing and references no logging. `ToString` on every type is safe to log. Labels are never included in any text the assembly produces. |
| Global state | None. Every manager is an instance with its own list, selection and lock; the test doubles are instances too. |
| Mutation of Plate models | Impossible by construction: the assembly cannot reference the plugin, and nothing in it names a Plate, a profile, a character or a binding, verified by a test over every type and member including private ones. |
| Thread safety | One lock serializes every manager operation, its store calls and lease signatures; codec calls run outside it and a codec must be a function of its inputs; listings are snapshots; records are immutable. Concurrency tests drive creates, selects, restores and lease signatures from several threads. |
| Insecure backup assumptions | No format, no fake cipher, no plaintext file. The test double's bytes are a random handle into test memory and are checked to contain no key, identity or secret. The docs say the seams are shapes, not security. |
| Premature public APIs | The surface is the minimum for the required operations plus the two seams, listed in a fixture, and marked provisional. Records, leases, results and key material cannot be constructed outside the assembly. |
| Unnecessary dependencies | None: BCL and `AetherFrame.Protocol`. No NuGet package, no Dalamud, no JSON, no file system, no network. |

Two honest limits, neither introduced by this increment: a .NET string that held a secret before `PersonaBackupSecret` copied it (an input field's, say) cannot be zeroed from here; and a full memory dump taken while material is in memory can contain the key, as it can for any process holding one.

## 6. Verification

From the repository root, with `DALAMUD_HOME` set as for any plugin build:

```
dotnet build AetherFrame.slnx -c Release
dotnet test AetherFrame.Personas.Tests/AetherFrame.Personas.Tests.csproj -c Release --no-build
```

The Build workflow (`build.yml`) runs the suite on windows-2022 and ubuntu-24.04 after the protocol suite, on every push to master and every pull request. The plugin package check in that workflow (`validate-package`) confirms the release package still holds exactly the three plugin files, since the plugin does not reference the new assembly.

The Release workflow (`release.yml`) is **not** changed: whether a failing networking suite should block a plugin release is L9, which is unresolved (section 7).

## 7. Decisions this increment needs from the owner

None of these is approved by this increment. Each is stated so it can be decided.

**Signer lease on a persona switch (new; not in the register yet).**

- **The question.** When the player switches personas while an operation holds a signer lease for the previous persona, may that operation still sign as the previous persona, or must it stop?
- **What the code does now, as an interim.** The lease is revoked by any change of the active selection (select another persona, or deselect) and never revives. The operation fails with `LeaseRevoked` and must start again under the new selection.
- **Why this interim.** It follows NETWORK1.md, system 1 ("must never sign for a persona that is not the active one"), and it fails closed.
- **The alternatives.**
  - Let an operation finish under the persona it started with, which would need that boundary amended.
  - Refuse or defer a switch while a lease is open.
- **What it affects.** Nothing player-facing exists yet: nothing signs outside tests. It must be decided before the preview wiring (NETWORK1.md, increment 9) lets a player start an operation that signs.
- **Where to record it.** In the register as an unresolved item, or as part of D3's scope.

**D9a, the persona display name (unresolved).** `PersonaLabel` implements the register's recommended option, a private local label only, so the model can be exercised. It is provisional, as the code and this document say. If D9a is decided otherwise, the label rule, `Create`'s and `RestoreBackup`'s label parameter, and `Rename` may change or go.

**L6, the protocol's provisional `IPersonaKeyProvider` and `FuturePolicy` (unresolved).**

- This increment neither uses, implements, moves nor removes either, and changes nothing in `AetherFrame.Protocol`.
- It adds a separate seam, `IPersonaKeyStore`, in the persona assembly, and a manager whose `TryOpenActiveSigner` answers with a typed availability and a revocable lease. That is one data point for L6's question about the provider's real shape: a synchronous getter for one active signer does not express several personas, an unavailable key, or revocation.
- It does not decide L6. If L6 keeps the provider in the protocol, an adapter over the manager would be needed; if it removes it, nothing here depends on it.
- L4 (a public-only key accepted by `EcdsaPersonaSigner` until the first `Sign`) is also untouched in the protocol. Inside this assembly it cannot arise, because material refuses a key without a private scalar and every signer is made from checked material.

**L9, whether networking tests gate plugin releases (unresolved).**

- The first version of this increment added the persona suite to `release.yml`. That would have extended an unresolved release policy without approval, so it was withdrawn; only `build.yml` runs the suite.
- **The proposed change, for the owner to approve or refuse:** add a "Test the persona foundation" step to `release.yml` after "Test the protocol", running `dotnet test AetherFrame.Personas.Tests/AetherFrame.Personas.Tests.csproj --configuration Release --no-build`.
- Approving it means a persona test failure blocks a plugin release even though the plugin does not use the assembly. Refusing it keeps release gating as it is today. L9's existing question (whether the protocol suite should stay in `release.yml`) is the same kind of decision and is unchanged.

**D3's wording.** The owner's D3 instruction to this increment included a point that the register's approved text does not quote: distinct personas remain independent unless the user deliberately associates them. The model enforces it: no operation associates two personas. The register's approved text is left exactly as approved; whether to add the clause there is the owner's call.

**Before any real key exists.** Every G1 row of the register must be approved: D9b, N3, N5, L6, K1, K2, K3, K6, K7, P1 and P2, plus K8 and K9 if persona features are pursued under Wine, Proton or macOS. The D2 details with K5 need security approval before any `.afpersona` file is written or restored outside tests ([DecisionRegister.md](DecisionRegister.md), section 1).

The recommended next increment is NETWORK1.md's increment 1, the protocol API tidy (N5, L6). It changes no signed bytes and is the cheapest moment for that change, because nothing depends on the API yet. After that, increment 2 (the draft marker and the name rule, N3 and D4) is the gate before any persistent key signs anything. The key store core (increment 5) builds on this assembly by implementing `IPersonaKeyStore` over protected files behind a fake protector first, and the backup codec (increment 6) by implementing `IPersonaBackupCodec`; neither changes the manager.

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
| `release.yml` gated releases on the persona suite while L9 is unresolved | Withdrawn; presented for approval in section 7 |
| Labels, L6 and L9 were not marked as unresolved where the increment touches them | Section 7 |
