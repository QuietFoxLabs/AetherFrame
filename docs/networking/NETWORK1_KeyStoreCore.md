# NETWORK1 increment 5: the key store core

**Status (2026-09-29): implemented behind seams; no key exists outside tests.**
- `AetherFrame.Personas` gains a `Storage` namespace: the store core, the at-rest envelope, and two seams (a protector, a blob storage) that the plugin fills. No implementation of either seam ships: the only protector is the test fake, which protects nothing and says so. `[updated 2026-09-29: NETWORK2's N2-4 adds the Windows DPAPI protector, compiled only in the preview flavour; nothing wires it yet.]`
- The plugin gains its first file under `Services/Network`: a directory-of-files storage, compiled only in the networking preview flavour (D9b, P2) and tested from the persona suite. Nothing wires it. **No persona key is written anywhere outside tests.** Gate G1 still holds: K3 (enablement), K9 (Wine), N3, N5, L6, P1 and the D2 details remain unresolved, and the Windows protector (increment 7) does not exist. `[updated 2026-09-29: it does now, as NETWORK2's N2-4, and K3 is decided; nothing wires either before N2-5.]` `[updated 2026-10-02: wired by NETWORK2's N2-5b (the persona session) and N2-9b (a key per sharing character), so keys are written in the sharing build, under Network\Personas\keys\ in the plugin's configuration directory.]`
- Decisions this increment applies, under the owner's delegation: **K1, K2, K6, K7** ([DecisionRegister.md](DecisionRegister.md), "Decisions approved under the delegation"). It also settles L4 for this store.

## 1. What this increment is

The persona foundation ([NETWORK1_PersonaFoundation.md](NETWORK1_PersonaFoundation.md)) fixed the custody contract of `IPersonaKeyStore` and left every implementation to a later increment. This one implements it: `ProtectedPersonaKeyStore` keeps each persona's private scalar as a protected envelope in a storage the plugin supplies, protected by a protector the plugin supplies. It holds no key in memory and caches nothing private. Every open reads the envelope, asks the protector for the scalar, rebuilds the key through `PersonaKeyMaterial`'s checks, and zeroes the scalar.

What it is not: a protector. Protection at rest is the protector's claim, made per platform and reviewed per platform (K2 for native Windows, increment 7; K9 elsewhere). The store guarantees custody: exactly the key it was given, proven before anything is durable, never replaced, nothing held after a refusal before the write, and never the caller's object. The one case it cannot undo, a verification that fails after a durable write, is stated in section 3.

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
- **It is atomic up to the write, and honest after it.** Every refusal before the write leaves nothing; the write itself is the storage's atomic `WriteNew`; a failure there is `CustodyFailed` with the cause inside, and the storage's contract says nothing is held. Once `WriteNew` has returned, the store has no way to take the write back (no delete, K6). So the read-back after it is tried up to three times, 50 and then 100 ms apart, because a file an antivirus scanner or an indexer has just opened can refuse a read for a moment; bytes that differ are never retried. If the read-back still fails, `AddKey` throws `CustodyFailed` saying the envelope may stay under the slot. The manager adds no record and never reuses that slot, so the envelope is a key file no record names. A crash between the key write and a persisted record would leave the same kind of file. The register tracks it as L12: the wiring (increment 9) must detect such files and report them. Both independent reviews found this case, which an earlier draft of this document called atomic.
- **It never retains the caller's material.** The scalar it exported is zeroed in a `finally`; the material stays the caller's.
- **Unavailability is null, never an exception.** `OpenSigner` and `OpenKey` return null, with a one-line reason to the optional report sink, for: the empty slot, no envelope, an envelope that does not decode or names another slot, a protector other than this store's, a blob the protector cannot open or a protector that throws, a scalar that does not belong to the recorded public key, and a storage that throws. Reasons name the slot and never an identity, a label or key bytes.
- **L4, settled for this store.** Every signer comes from `PersonaKeyMaterial.CreateSigner`, which copies the key through the checks again and always holds the private half. A public-only signer cannot come from this store. The interface still allows another store to differ, so the manager's check of the public key stays.
- **The manager's lock.** The store is not thread-safe and the manager serializes its calls. Opening a key is one storage read and one protector call; adding one is a read, a write, a read-back (retried at most twice), and two protector calls, all over a few hundred bytes, plus one flush to disk in the plugin's file storage. So the lock is held briefly. A protector that prompts or blocks is not allowed by `IPersonaKeyProtector`'s contract; this is the increment's decision on the foundation's open question (its section 4), and the manager's locking is unchanged.

Error values: `PersonaError.CustodyFailed` is new, for a protector or storage that refused, or an envelope that did not prove to hold the key. Its message names the rule, never a value.

## 4. The seams, and what they do not decide

**`IPersonaKeyProtector`.** `Id` (written into every envelope; a change of format is a new id), `Protect(secret, context)`, `Unprotect(blob, context)` returning null when it cannot open. Called only through the store, one call at a time; never keeps, logs or prompts. It decides nothing about *how* a platform protects: that is K2's target for Windows (DPAPI, CurrentUser, UI forbidden, entropy from the context) and K9's open question elsewhere.

**`IPersonaKeyBlobStorage`.** `Read(slot)` (exactly what is held, or null) and `WriteNew(slot, blob)` (atomic, durable, exactly the bytes or nothing, never replaces). Nothing deletes. It decides nothing about *where*: the plugin's storage is a directory of files; the tests' is a dictionary.

**What the assembly still does not do.** It touches no file, no environment and no network; its boundary tests still hold it to the same namespaces as before (`System.IO` is not among them). The seams are how a file reaches it.

## 5. The plugin's directory storage

`AetherFrame/Services/Network/Personas/PersonaKeyFileStorage.cs`, the first file under the plugin's networking folders, which a player build does not compile (`AetherFrame.csproj` removes them outside the preview flavour; the boundary tests hold the player DLL to no type in their namespaces). It is plain .NET over `System.IO`, so the persona test project compiles it from source and tests it on every build.

`WriteNew`: create the directory; refuse if the final file exists; delete any stale `.afkey.tmp` (never trusted); write the temporary file with create-new semantics, flush to disk, read it back and compare; move it into place without overwriting. So the final name only ever holds bytes that were verified, or nothing. `Read`: the file's bytes, null when absent (a temporary file alone is not a key), an `IOException` for a file larger than any envelope (64 KiB). Nothing deletes a key.

**Durability, one gap before wiring.** The file's bytes reach the disk before the move, but the move is not written through, so a power loss just after `WriteNew` returns can leave the key only in the temporary file, which is never trusted. That falls short of the storage contract's "durable once it returns". Nothing persists a record yet, so no key can be lost this way today. Before increment 9 persists one, the move must be written through (`MoveFileEx` with `MOVEFILE_WRITE_THROUGH` on Windows), as the register's K2 entry requires. Also before then: the inner `IOException` of a failure carries full paths, including the Windows profile name, so the wiring must log the error kind, not the inner exception's text. `[updated 2026-09-30: applied by NETWORK2's N2-5b: the move is written through (WrittenThroughMove.MoveNew), and the persona session logs error kinds only, never an exception's text.]`

Not decided here: the directory. Increment 9's wiring names it (the target is the plugin's own configuration directory, `Network/Personas/keys/`), and nothing creates it before then.

## 6. Verification

Run locally on 2026-09-29 with `DALAMUD_HOME` set as CI does, in the order of `build.yml`:

| Check | Result |
|---|---|
| Solution build | 0 warnings, 0 errors |
| AetherFrame.Personas.Tests | 310 passed (252 on master; 58 new: the envelope, the store, the directory storage, the boundary tests with the widened byte-producer list, and the 7 the reviews asked for) |
| AetherFrame.Tests / ReleaseTools.Tests / Protocol.Tests | 3183 / 468 / 155 passed (155 since #32); the player DLL holds no type in `AetherFrame.Services.Network`, `Hosting.Network` or `Windows.Network` |
| validate-package | Package OK, the same three entries, "plugin flavour: player build, no networking code" |
| Preview flavour | 0 warnings with `TreatWarningsAsErrors`; the directory storage compiles under the plugin's settings; the 6 boundary tests pass in preview mode; the player package is untouched afterwards |
| Public surface | `Fixtures/public-api.txt` regenerated on purpose: `PersonaError.CustodyFailed`, the two seams and `ProtectedPersonaKeyStore` are the only additions |

What the tests pin: the round trip through the envelope and the protector; that the plain scalar is not in an envelope and the public key is; that every scalar the protector hands back is zeroed; that a held slot is refused and left as it was; that a protector which throws, produces an unusable blob, or opens its own blob to another scalar leaves nothing held; that a storage which throws before holding anything, or cannot be read before the write, is reported and holds nothing; that a read-back which fails once after the write is retried and the key is held, while one that keeps failing, bytes that differ, or a storage that throws after holding are reported as `CustodyFailed` with the envelope left under the unrecorded slot (L12); that every kind of damage, a blob moved between slots or under another public key, another protector's envelope, a locked protector and a storage failure are all null with a reason naming the slot and nothing else; the manager end to end over the store (create, select, sign and verify, unavailable key, backup export); and for the directory storage, exact bytes under the slot's name alone, refusal of a held or foreign file, a stale temporary file replaced and none left behind, and nothing held when the directory cannot be made.

## 7. Security review

Two reviewers with no shared context examined `0c1cf96` on September 29, 2026: a general reviewer over the whole diff, and a security-focused reviewer over the store, the envelope, the storage and the four register entries. Both found the same blocking issue: the "atomic" claim failed when the read-back after a durable write failed. It is fixed as section 3 describes, with tests. Nothing else blocked.

The security reviewer **concurred** with K1, K2, K6 and K7 and with L4 as settled here; each register entry records its concurrence. It checked DPAPI's behaviour against Microsoft's documentation, which led to the qualification about roaming profiles and domain backup keys in K2, and to K7's explicit exemption for the existing managed validation checks. It also mutated the code 17 ways. The tests caught the ones that matter, and two gaps it found are now tested: the moved-blob test, which decoded the second envelope after planting over it, and a lone temporary file read as a key. The mutations the tests still don't catch are these:
- skipping the zeroing of the exported scalar in `AddKey`;
- a fixed-time comparison that always passes (the import check after it still refuses a wrong scalar);
- the file storage skipping its own temporary-file compare or flush, or overwriting on the move while keeping its check that the final file doesn't exist, which only fault injection could observe;
- narrowing the catches in `OpenSigner` and `OpenKey` back to `PersonaException` (no test can make the platform or the protocol refuse a key the managed checks already accepted);
- removing the pause between read-back retries (timing only).

Its recheck of the fix ran 10 more mutations, 8 on the new code and 2 repeats of the first round. Three survived, all listed above: the pause between retries, the narrowed catches and the skipped zeroing of the exported scalar.

Its non-blocking notes are recorded where they apply: the write-through move and the log text (section 5), `CryptUnprotectData`'s output zeroed before `LocalFree` and the deprecated prompt structure (K2, for increment 7), and exceptions that could escape `OpenSigner` and `OpenKey` (now caught, section 3).

## 8. What comes next

- Increment 7: the Windows DPAPI protector (K2's target) and the capability probe (K3), each with its own review; until then no protector exists outside tests. `[updated 2026-09-29: implemented by NETWORK2's N2-4, in the preview flavour only; nothing wires it before N2-5.]`
- The persisted persona registry (records, labels and the active slot; the manager takes none at construction) before increment 9's wiring. With it: detecting and reporting key files that no record names (L12), and the write-through move (section 5). `[updated 2026-09-30: the registry and L12's audit are NETWORK2's N2-5a, in the library (P3 in DecisionRegister.md); the registry file and the write-through move are N2-5b's.]`
- Increment 6: the backup codec, which needs the D2 details and K5.
