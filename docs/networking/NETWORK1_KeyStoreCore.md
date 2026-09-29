# NETWORK1 increment 5: the key store core

**Status (2026-09-29): implemented behind seams; no key exists outside tests.**
- `AetherFrame.Personas` gains a `Storage` namespace: the store core, the at-rest envelope, and two seams (a protector, a blob storage) that the plugin fills. No implementation of either seam ships: the only protector is the test fake, which protects nothing and says so.
- The plugin gains its first file under `Services/Network`: a directory-of-files storage, compiled only in the networking preview flavour (D9b, P2) and tested from the persona suite. Nothing wires it. **No persona key is written anywhere outside tests.** Gate G1 still holds: K3 (enablement), K9 (Wine), N3, N5, L6, P1 and the D2 details remain unresolved, and the Windows protector (increment 7) does not exist.
- Decisions this increment applies, under the owner's delegation: **K1, K2, K6, K7** ([DecisionRegister.md](DecisionRegister.md), "Decisions approved under the delegation"). It also settles L4 for this store.

## 1. What this increment is

The persona foundation ([NETWORK1_PersonaFoundation.md](NETWORK1_PersonaFoundation.md)) fixed the custody contract of `IPersonaKeyStore` and left every implementation to a later increment. This one implements it: `ProtectedPersonaKeyStore` keeps each persona's private scalar as a protected envelope in a storage the plugin supplies, protected by a protector the plugin supplies. It holds no key in memory and caches nothing private. Every open reads the envelope, asks the protector for the scalar, rebuilds the key through `PersonaKeyMaterial`'s checks, and zeroes the scalar.

What it is not: a protector. Protection at rest is the protector's claim, made per platform and reviewed per platform (K2 for native Windows, increment 7; K9 elsewhere). The store guarantees custody: exactly the key it was given, proven before anything is durable, never replaced, atomic, and never the caller's object.

## 2. The envelope (`AFPK`, version 1)

One envelope per slot, big-endian, exact (no trailing byte):

| Field | Bytes | Notes |
|---|---|---|
| magic `AFPK` | 4 | |
| version | 2 | 1 |
| slot | 16 | the slot's raw bytes; must be the slot the envelope is held under |
| protector id | 1 + n | length, then 1 to 64 lowercase ASCII letters, digits, dots and hyphens |
| public key | 65 | the uncompressed P-256 point, as the protocol encodes it |
| blob | 4 + n | length, then the protector's output, 1 to 4096 bytes |

The header (everything before the blob length) is the protector's **context**: the protector binds its blob to it, so a blob moved into another envelope (another slot, another public key, another protector) does not open. There is no checksum. Damage shows as an envelope that does not decode, a blob the protector refuses, or a scalar that does not belong to the recorded public key, and every one of those is "unavailable" to the store. The file name (in the plugin's storage) is the slot's text form plus `.afkey`; slots are random local handles, so no persona identity is in any name (NETWORK1.md, safeguard 7). The public key inside the envelope is public; the identity is a hash of it and appears nowhere.

## 3. The store against the contract

`IPersonaKeyStore`'s contract (foundation document, section 4) and what `ProtectedPersonaKeyStore` does for each rule:

- **It commits exactly the key it was given, and proves it before anything is durable.** `AddKey` exports the scalar, protects it under the envelope's header, builds the envelope, then decodes it, unprotects the blob, compares the scalar in fixed time and imports it through `PersonaKeyMaterial.Import` against the material's public key. Only then does it write. After the write it reads the storage back and compares byte for byte; a storage that holds something else is reported as `CustodyFailed`, never hidden.
- **It never replaces.** A slot that already holds an envelope is refused with `InvalidOperationException` before the scalar is exported, and the envelope is left as it was. The storage refuses a held slot too.
- **It is atomic.** Every refusal before the write leaves nothing; the write itself is the storage's atomic `WriteNew`; a failure there is `CustodyFailed` with the cause inside, and the storage's contract says nothing is held.
- **It never retains the caller's material.** The scalar it exported is zeroed in a `finally`; the material stays the caller's.
- **Unavailability is null, never an exception.** `OpenSigner` and `OpenKey` return null, with a one-line reason to the optional report sink, for: the empty slot, no envelope, an envelope that does not decode or names another slot, a protector other than this store's, a blob the protector cannot open or a protector that throws, a scalar that does not belong to the recorded public key, and a storage that throws. Reasons name the slot and never an identity, a label or key bytes.
- **L4, settled for this store.** Every signer comes from `PersonaKeyMaterial.CreateSigner`, which copies the key through the checks again and always holds the private half. A public-only signer cannot come from this store. The interface still allows another store to differ, so the manager's check of the public key stays.
- **The manager's lock.** The store is not thread-safe and the manager serializes its calls. Each call is one storage read or write and one protector call over a few hundred bytes, so the lock is held briefly. A protector that prompts or blocks is not allowed by `IPersonaKeyProtector`'s contract; this is the increment's decision on the foundation's open question (its section 4), and the manager's locking is unchanged.

Error values: `PersonaError.CustodyFailed` is new, for a protector or storage that refused, or an envelope that did not prove to hold the key. Its message names the rule, never a value.

## 4. The seams, and what they do not decide

**`IPersonaKeyProtector`.** `Id` (written into every envelope; a change of format is a new id), `Protect(secret, context)`, `Unprotect(blob, context)` returning null when it cannot open. Called only through the store, one call at a time; never keeps, logs or prompts. It decides nothing about *how* a platform protects: that is K2's target for Windows (DPAPI, CurrentUser, UI forbidden, entropy from the context) and K9's open question elsewhere.

**`IPersonaKeyBlobStorage`.** `Read(slot)` (exactly what is held, or null) and `WriteNew(slot, blob)` (atomic, durable, exactly the bytes or nothing, never replaces). Nothing deletes. It decides nothing about *where*: the plugin's storage is a directory of files; the tests' is a dictionary.

**What the assembly still does not do.** It touches no file, no environment and no network; its boundary tests still hold it to the same namespaces as before (`System.IO` is not among them). The seams are how a file reaches it.

## 5. The plugin's directory storage

`AetherFrame/Services/Network/Personas/PersonaKeyFileStorage.cs`, the first file under the plugin's networking folders, which a player build does not compile (`AetherFrame.csproj` removes them outside the preview flavour; the boundary tests hold the player DLL to no type in their namespaces). It is plain .NET over `System.IO`, so the persona test project compiles it from source and tests it on every build.

`WriteNew`: create the directory; refuse if the final file exists; delete any stale `.afkey.tmp` (never trusted); write the temporary file with create-new semantics, flush to disk, read it back and compare; move it into place without overwriting. So the final name only ever holds bytes that were verified, or nothing. `Read`: the file's bytes, null when absent, an `IOException` for a file larger than any envelope (64 KiB). Nothing deletes a key.

Not decided here: the directory. Increment 9's wiring names it (the target is the plugin's own configuration directory, `Network/Personas/keys/`), and nothing creates it before then.

## 6. Verification

Run locally on 2026-09-29 with `DALAMUD_HOME` set as CI does, in the order of `build.yml`:

| Check | Result |
|---|---|
| Solution build | 0 warnings, 0 errors |
| AetherFrame.Personas.Tests | 303 passed (252 on master; 51 new: the envelope, the store, the directory storage, the boundary tests with the widened byte-producer list) |
| AetherFrame.Tests / ReleaseTools.Tests / Protocol.Tests | 3183 / 468 / 153 passed; the player DLL holds no type in `AetherFrame.Services.Network`, `Hosting.Network` or `Windows.Network` |
| validate-package | Package OK, the same three entries, "plugin flavour: player build, no networking code" |
| Preview flavour | 0 warnings with `TreatWarningsAsErrors`; the directory storage compiles under the plugin's settings; the 6 boundary tests pass in preview mode; the player package is untouched afterwards |
| Public surface | `Fixtures/public-api.txt` regenerated on purpose: `PersonaError.CustodyFailed`, the two seams and `ProtectedPersonaKeyStore` are the only additions |

What the tests pin: the round trip through the envelope and the protector; that the plain scalar is not in an envelope and the public key is; that every scalar the protector hands back is zeroed; that a held slot is refused and left as it was; that a protector which throws, produces an unusable blob, or opens its own blob to another scalar leaves nothing held; that a storage which throws, cannot be read, or holds something other than what it was given is reported and holds nothing usable; that every kind of damage, a blob moved between slots or under another public key, another protector's envelope, a locked protector and a storage failure are all null with a reason naming the slot and nothing else; the manager end to end over the store (create, select, sign and verify, unavailable key, backup export); and for the directory storage, exact bytes under the slot's name alone, refusal of a held or foreign file, a stale temporary file replaced and none left behind, and nothing held when the directory cannot be made.

## 7. Security review

To be recorded: an independent security-focused review of the store, the envelope, the storage and the four register entries, whose concurrence each entry needs.

## 8. What comes next

- Increment 7: the Windows DPAPI protector (K2's target) and the capability probe (K3), each with its own review; until then no protector exists outside tests.
- The persisted persona registry (records, labels and the active slot; the manager takes none at construction) before increment 9's wiring.
- Increment 6: the backup codec, which needs the D2 details and K5.
