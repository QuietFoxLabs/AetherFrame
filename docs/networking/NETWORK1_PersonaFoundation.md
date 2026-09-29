# NETWORK1: the persona management foundation

**Status (2026-09-28):** implemented and tested as a standalone assembly the plugin does not reference. It generates no persistent key, stores nothing, encrypts nothing, opens no connection and changes no plugin behaviour. It applies decision D3 and the principle of D2 as recorded in the networking decision register ([DecisionRegister.md](DecisionRegister.md), introduced with the planned boundaries in [NETWORK1.md](NETWORK1.md) by the NETWORK1 increment 0 documentation change, pull request #22); nothing in it depends on a decision that is still unresolved there.

## 1. What this increment is

NETWORK0 gave AetherFrame the grammar of a signed publication and a persona identity derived from a public key ([NETWORK0.md](NETWORK0.md)). The owner has since decided that an installation holds several independent personas which the player switches by hand (D3), and that a persona must be exportable as an encrypted, portable backup whose format is still to be approved (D2, in principle).

This increment is the smallest piece of NETWORK1 that those two decisions make safe to build: the **local model** of personas and the **seams** that the still-undecided pieces (protected key storage, the backup format) will plug into. It is the Dalamud-free core of the roadmap's vault increment without the vault: the records, the selection, the identity rules and the contracts, with keys living behind an interface that has only in-memory test implementations.

It is deliberately not:

- a key store: no key is written anywhere, protected by anything, or read from anywhere;
- a backup format: no bytes of any `.afpersona` container are defined, and no encryption is implemented or faked;
- a persistence schema: nothing is serialized, and the in-memory text form of a slot id decides nothing about a file;
- a plugin feature: `AetherFrame.csproj` is unchanged, the release package is the same three files, and no command or window exists;
- a change to the protocol: `AetherFrame.Protocol` is unchanged, its public surface and vectors included.

## 2. The model

Everything lives in `AetherFrame.Personas` (net10.0, BCL plus `AetherFrame.Protocol`, no NuGet packages), tested by `AetherFrame.Personas.Tests`. In the terms of [NETWORK1.md](NETWORK1.md), it is the Dalamud-free core of system 2 (the installation's independent personas and the one active selection) with custody of the keys themselves left to a later store, and the contracts of system 3 (portable encrypted backups) without their implementation.

| Type | Role |
|---|---|
| `PersonaManager` | The personas this installation holds and which one is active. Create, select, deselect, rename, list, open a signer for the active persona, export a backup, inspect and restore one. In memory only; every member is thread-safe; nothing is persisted, loaded or deleted. |
| `PersonaRecord` | What a listing shows: the local slot, the public key, the identity the protocol derives from it, and the private label. Immutable; no secret is reachable from it; `ToString` gives the slot only. |
| `PersonaSlotId` | A random local handle for a record, minted here and never derived from the persona. Logs, error text and future file names can name a slot without naming an identity, which is what keeps two mentions from being correlated. Different on every installation that holds the same persona. |
| `PersonaLabel` | The private label rule: trimmed, 1 to 64 UTF-16 code units, no control characters, nothing else rewritten. Never published, never logged by this assembly. |
| `IPersonaKeyStore` | The seam for key custody: make a fresh key for a slot, adopt a restored key, open a signer for one operation, open material for an export. No implementation in the product assembly. |
| `PersonaKeyMaterial` | A private key while it is in memory: a platform key object whose only public members are the public key and `CreateSigner`. Same-assembly custody code reads the parameters through an internal member and zeroes them. Refuses anything but a P-256 key with a private scalar in range. |
| `PersonaSignerLease` | A borrowed signer for one operation, bound to the persona it was opened for. Switching the active persona does not change what an open lease signs as. |
| `IPersonaBackupCodec` | The seam for the future `.afpersona` codec: inspect a container without a secret, write material under a secret, open a container under a secret. No implementation in the product assembly. |
| `PersonaBackupSecret` | The player's secret as characters zeroed on disposal, printed as a placeholder. Applies no policy: K5 is open. |
| `PersonaBackupInspection`, `PersonaBackupStatus`, `PersonaRestoreResult`, `PersonaRestoreStatus`, `PersonaSignerAvailability` | Typed outcomes, so a caller never has to parse a message. |
| `PersonaException`, `PersonaError` | Refusals that are programming or store faults (an unknown slot, a bad label, a duplicate identity from a store, a store handing out the wrong key). Messages name rules, never labels, identities or bytes. |

The public surface is listed in `AetherFrame.Personas.Tests/Fixtures/public-api.txt` and a test fails when it drifts; regenerate it on purpose with `AETHERFRAME_PERSONAS_REGENERATE_PUBLIC_API=1`. Like the protocol's, the whole surface is provisional until the plugin integrates it.

## 3. The rules the model enforces

Each rule is a test in `AetherFrame.Personas.Tests`.

- **Several independent personas.** Each has its own slot, key and identity; the store holds one key per slot; nothing links two personas but their presence in the same list.
- **One active persona, chosen by the player.** `Select` and `Deselect` are the only ways the selection changes. Creating a persona, restoring one, renaming one and opening a signer never select anything, not even the first persona created. Without a selection, the answer to "sign as the active persona" is `NoActivePersona`, and nothing is chosen on the player's behalf.
- **Switching changes only the selection.** The record list is the same objects in the same order, the store is not called, and a lease already open keeps signing as the persona it was opened for: an operation signs as the persona that was active when it began, and a lease is for one operation, disposed after it, never re-pointed at a persona selected meanwhile.
- **Personas stay independent.** No operation associates two personas, and nothing links two records but their presence in the same list. A deliberate association, if the owner ever wants one, would be an explicit feature and never a side effect of anything here.
- **An identity is unique.** A store that produces a key whose identity the installation already holds is refused with `DuplicateIdentity`, and the list is unchanged. On restore the same check reports `AlreadyPresent` and leaves the existing record, label, key and selection exactly as they were.
- **A missing key is a typed answer.** When the store cannot open the active persona's key, `TryOpenActiveSigner` answers `KeyUnavailable`; the selection and the records are untouched. A store that opens a key belonging to another persona is caught by comparing public keys, and refused.
- **No character, account or Plate can reach the model.** No public member takes a Content ID, a Guid, a character, a World or a Plate, and no type or member in the assembly, public or private, names one. The assembly references only the runtime and `AetherFrame.Protocol`; it cannot see the plugin's Plate, character-binding or configuration types, so it cannot read or rewrite them. Nothing in it is keyed by, or derived from, anything about the player.
- **No network, no files.** The assembly references no `System.Net`, file-system, process or JSON assembly. Every test runs with no connection and touches no user data, saved Plate or Dalamud configuration.
- **Identity is the protocol's.** A record's identity is `PersonaPublicKey.Id`, exactly as NETWORK0 derives it; a document signed through a lease verifies to that identity; a persona restored elsewhere has the same identity under a different slot.

## 4. What the seams promise, and what they do not

`IPersonaKeyStore` and `IPersonaBackupCodec` are shapes, not protection. Nothing about either interface makes a key safe or a backup secure; only a reviewed implementation can, and none exists. What the shapes do guarantee is where private material can and cannot go:

- A private key crosses a public boundary only as `PersonaKeyMaterial`, an object with no public accessor to the key bytes. There is no `byte[]` of a key anywhere on the public surface. The one internal accessor, `ExportPrivateParameters`, is for custody code in the same assembly, which zeroes the scalar after use.
- The manager asks a store for material for one thing only: an export the player asked for. Creation, selection, renaming, listing and signing never call it. Nothing exports on a schedule or on creation.
- A backup is inspected before any secret is presented to a codec. An unsupported version ("made by a newer AetherFrame") or malformed bytes are refused without the secret ever reaching the codec.
- A restore never overwrites: a persona already held is reported present, the material the codec produced is disposed, and the existing record is returned unchanged. A restored persona gets a fresh slot and is not selected.
- A wrong secret and damaged content are one outcome, `CannotOpen`, so a codec is not asked to distinguish them.
- The manager serializes every store call it makes under its lock, because `EcdsaPersonaSigner` and any future protected store are not thread-safe. Codec calls run outside the lock, so a slow key derivation never stalls a listing; a codec is a function of its inputs and must tolerate concurrent calls. A lease, once returned, is the caller's.

What the seams do **not** decide, on purpose: whether a backup carries the label (the caller supplies a label on restore, so the format may carry one or not); the secret policy (K5); the container's layout, KDF, cipher and parameters (D2's open items); whether material is validated for consistency between the private scalar and the public point (the store's duty before it trusts material it did not generate: `PersonaKeyMaterial` checks the curve, the on-curve point and the scalar's range, not that the two match, and the platforms differ on what they check at import). The platforms differ more than expected: given a private scalar above the group order, Windows 11 CNG and OpenSSL refuse the import, while the windows-2022 CI runner's CNG imports it and hands back a reduced scalar, so a key whose scalar and point no longer match can exist without any error. This is why the store must validate that the point equals the scalar times the base point itself, in its own code, before it trusts restored material, and why platform validation is never trusted alone (NETWORK1.md, safeguard 6).

## 5. Security review of this increment

| Concern | Finding |
|---|---|
| Accidental persona correlation | Records, leases and material print the slot or a placeholder, never the identity or the label. Slots are random and differ per installation. The only place two personas meet is the manager's list. Exception messages name rules and carry no identifiers; a test scans them for `psn_`, `slot_`, labels, secrets and hex runs. |
| Private key exposure | No public member returns a platform key, `ECParameters` or key bytes; the only byte-producing members are the two backup ones, which produce a codec's container by contract. `PersonaKeyMaterial` disposes a refused key, copies keys for signers and zeroes exported scalars. What a disposed platform key leaves in process memory is not claimed. |
| Unintended logging | The assembly logs nothing and references no logging. `ToString` on every type is safe to log. Labels are never included in any text the assembly produces. |
| Global state | None. Every manager is an instance with its own list, selection and lock; the test doubles are instances too. |
| Mutation of Plate models | Impossible by construction: the assembly cannot reference the plugin, and nothing in it names a Plate, a profile, a character or a binding, verified by a test over every type and member including private ones. |
| Thread safety | One lock serializes every manager operation including its store calls; codec calls run outside it and a codec must be a function of its inputs; listings are snapshots; records are immutable. Leases, signers and key material are documented as not thread-safe. A concurrency test drives creates, selects and signer opens from eight threads. |
| Insecure backup assumptions | No format, no fake cipher, no plaintext file. The test double's bytes are a random handle into test memory and are checked to contain no key, identity or secret. The docs say the seams are shapes, not security. |
| Premature public APIs | The surface is the minimum for the eight required operations plus the two seams, listed in a fixture, and marked provisional. Records, leases and results cannot be constructed outside the assembly. |
| Unnecessary dependencies | None: BCL and `AetherFrame.Protocol`. No NuGet package, no Dalamud, no JSON, no file system, no network. |

Two honest limits, neither introduced by this increment: a .NET string that held a secret before `PersonaBackupSecret` copied it (an input field's, say) cannot be zeroed from here; and a full memory dump taken while material is in memory can contain the key, as it can for any process holding one.

## 6. Verification

From the repository root, with `DALAMUD_HOME` set as for any plugin build:

```
dotnet build AetherFrame.slnx -c Release
dotnet test AetherFrame.Personas.Tests/AetherFrame.Personas.Tests.csproj -c Release --no-build
```

Both CI workflows run the suite on windows-2022 and ubuntu-24.04 after the protocol suite. The plugin package check in CI (`validate-package`) confirms the release package still holds exactly the three plugin files, since the plugin does not reference the new assembly.

## 7. What stays open, and what comes next

Nothing in this increment permits a real key to exist. Before one can, every G1 row of the register must be approved: D9b, N3, N5, L6, K1, K2, K3, K6, K7, P1 and P2, plus K8 and K9 if persona features are pursued under Wine, Proton or macOS; and the D2 details with K5 need security approval before any `.afpersona` file is written or restored outside tests ([DecisionRegister.md](DecisionRegister.md), section 1).

The recommended next increment is NETWORK1.md's increment 1, the protocol API tidy (N5, L6), which changes no signed bytes and is the cheapest moment for that change because nothing depends on the API yet. After that, increment 2 (the draft marker and the name rule, N3 and D4) is the gate before any persistent key signs anything. The key store core (increment 5) builds on this assembly by implementing `IPersonaKeyStore` over protected files behind a fake protector first, and the backup codec (increment 6) by implementing `IPersonaBackupCodec`; neither changes the manager.
