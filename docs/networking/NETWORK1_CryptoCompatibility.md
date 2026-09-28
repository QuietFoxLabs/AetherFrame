# NETWORK1: platform cryptography compatibility

**Status (2026-09-28): findings, not decisions.** This document records what is known about the cryptography NETWORK1 would rely on, on each platform FFXIV players use, and how that is known. Nothing here is implemented, and no persona key exists. What to do about these findings is recorded as unresolved owner decisions in [DecisionRegister.md](DecisionRegister.md).

Every finding carries one of five kinds of evidence. They are never merged:

| Kind | Meaning |
|---|---|
| **Tested on native Windows** | Executed on a Windows PC by the compatibility harness described in section 1 |
| **Tested on native Linux** | Executed by this repository's CI on ubuntu-24.04 |
| **Wine source** | Read in the implementation source of a named Wine version. This is **not** a runtime test, in either direction. |
| **Published report** | Another project's public report of running under Wine |
| **Needs Wine execution** | Not established by anything above |

Why Wine needs its own column: players on Linux (XIVLauncher.Core, including the Steam Deck) and on macOS (XIV on Mac) run the game and Dalamud inside Wine. Dalamud there uses the **Windows** build of .NET, whose cryptography calls Wine's `ncrypt.dll`, `bcrypt.dll` and `crypt32.dll`. A native Linux .NET result (OpenSSL) exercises none of that code. A passing Linux test therefore says nothing about Wine.

## 1. Tested on native Windows

- **The harness.** A disposable compatibility harness, not part of this repository. It uses synthetic keys only and never touches the game, Dalamud, plugin settings or user data.
- **Environment.**
  - Windows 11 Pro 10.0.26200, x64, 32 cores.
  - Native modules: `bcrypt.dll` 10.0.26100.1, `ncrypt.dll` 10.0.26100.1591, `crypt32.dll` 10.0.26100.9549.
  - .NET runtimes:
    - **10.0.0 from XIVLauncher's runtime folder**, the runtime Dalamud loads;
    - 10.0.12 (system);
    - a self-contained 10.0.12 build.
- **Result:** 74 checks, 0 failures, on each runtime. A separate-process restart test (write the key material under one runtime, re-import under the other) passed 7 of 7 in both directions.

| Area | Operations checked | Result |
|---|---|---|
| .NET `ECDsa` (CNG through NCrypt: `ECDsaWrapper` over `ECDsaCng`, Microsoft Software Key Storage Provider, `ECDSA_P256`, ephemeral, plaintext export allowed) | P-256 generation; public and private export (parameters, ECPrivateKey, PKCS#8, SubjectPublicKeyInfo); import of Q only, D and Q, D only (Q derived by the platform), PKCS#8, ECPrivateKey; SHA-256 P1363 signing; verification | passed |
| NCrypt called directly | open provider, create and finalize `ECDSA_P256`, key properties (group `ECDSA`, name `ECDSA_P256`, length 256), public export, sign and verify, public import with and without the curve-name parameter list | passed |
| BCrypt `ECDSA_P256` called directly, without NCrypt | generate, export public and private blobs, import them, sign (always 64 bytes), verify; cross-verification with .NET in both directions and with an independent managed verifier | passed |
| Other primitives | SHA-256, random numbers, HMAC-SHA256, AES-GCM (GCM specification test cases 2 and 14); PBKDF2-HMAC-SHA256 through `Rfc2898DeriveBytes`, through `BCryptDeriveKeyPBKDF2` called directly, and as PBKDF2 over HMAC. All three PBKDF2 routes give identical known-answer output. | passed |
| DPAPI (`CryptProtectData`, CurrentUser scope, UI forbidden, with entropy) | round trip through a direct call and through `ProtectedData`, which interoperate. `ProtectedData` loaded from XIVLauncher's own WindowsDesktop.App 10.0.0. Wrong entropy, missing entropy and a tampered blob are refused (Win32 error 13). | passed |
| AetherFrame.Protocol | `SignedDocumentCodec.Sign` and `Verify` with `EcdsaPersonaSigner` and with a BCrypt-backed `IPersonaSigner`; both committed personas derived identically three ways; every stored vector verified; the maximal vector rebuilt from its construction rule; 77 of 77 rejected vectors refused with their stated code; both server-obligation vectors | passed |
| Invalid material | Off-curve points, x+p encodings, D = 0, D = n, D = 2²⁵⁶−1 and D/Q mismatch are refused by the platform. BCrypt refuses off-curve imports unless told not to validate (`BCRYPT_NO_KEY_VALIDATION`), so the protocol's own managed checks must stay in front, as they do. The high-S twin of a signature is accepted by the platform and refused by the protocol. | as expected |

Measured on Windows:

| Operation | .NET through NCrypt | BCrypt direct |
|---|---|---|
| Generate a key | 0.38–0.41 ms | 0.042 ms |
| Sign | 0.083–0.091 ms | 0.054 ms |
| Import a public key and verify | 0.30 ms | 0.080 ms |

| Other operation | Time |
|---|---|
| PBKDF2-SHA256, 600,000 iterations | 47–49 ms |
| PBKDF2-SHA256, 1,000,000 iterations | 77–92 ms |
| DPAPI protect / unprotect | 0.10 ms / 0.06 ms |

Other measurements:
- About half of all platform signatures are high-S, and the platform never normalises them.
- 0.3–0.8 % of signatures have a leading zero byte in r or s; every one was still exactly 64 bytes.

Not covered: the harness is a separate process. Behaviour inside the game process and under Dalamud's plugin load context was not exercised.

## 2. Tested on native Linux

Coverage is the repository's protocol suite in CI, not the harness:
- runs 36446719752 (`dcd56f9`) and 36474945050 (`6384db6`);
- ubuntu-24.04, image 20260920.314.1;
- .NET 10.0.12 with OpenSSL;
- 153 of 153 each (NETWORK0.md, section 13).

This covers native Linux .NET key generation, private and public import, P1363 signing and verification, and the committed vectors. Native Linux is not a platform players run Dalamud on; this result matters for the future backend and for CI, not for Wine.

## 3. Wine behaviour inferred from implementation sources

These are statements about source code, not runtime results. Versions examined:
- upstream Wine 9.0, 10.0, 10.8, 11.0, 11.17 and master at 4e819f05 (2026-09-25);
- wine-staging at the same versions;
- XIVLauncher.Core's Wine build (goatcorp/wine-xiv-git);
- Proton branches proton_9.0 to proton_11.0;
- XIV on Mac's winecx.

The ncrypt and crypt32 limits at tag wine-10.8 were checked twice, independently.

**Which Wine each launcher uses:**
- **XIVLauncher.Core:** Stable and Beta use `wine-xiv-staging-fsync-git-10.8` (`WineStable.cs` in goatcorp/XIVLauncher.Core). Its build scripts leave the crypto DLLs as upstream 10.8. The binaries were not inspected.
- **XIV on Mac 5.5.1:** uses winecx, which is based on Wine 11.17.
- **Proton:** its ncrypt source matches upstream 11.0.
- **The .NET runtime:** XIVLauncher.Core runs Dalamud on the Windows .NET runtime inside the Wine prefix, with no crypto DLL overrides.

**Wine's `ncrypt.dll`**, the provider .NET's `ECDsa` uses on Windows:
- **Key creation.** `NCryptCreatePersistedKey` accepts only `"RSA"` in 9.0 to 11.0, including 10.8 (`dlls/ncrypt/main.c`, "Algorithm not handled", NTE_NOT_SUPPORTED 0x80090029). That covers XIVLauncher.Core and every Proton branch.
  - `"ECDSA_P256"` creation was added in commit 0a13e4af4b, first shipped in 11.1.
  - Those keys lack the "Algorithm Group" and "Algorithm Name" properties .NET reads, so .NET's own creation path is expected to fail there too. That is inferred, not traced to the end.
- **Key import.** `NCryptImportKey` accepts only RSA key blobs in every version examined, up to master. It rejects any parameter list, and .NET sends one for named curves.
- **What that breaks.** On Windows, .NET imports every EC public or private key through `NCryptImportKey`, and `AetherFrame.Protocol` imports the signer's public key for every `SignedDocumentCodec.Verify` and for the self-verification inside every `Sign`. So the protocol's current verification path is expected to fail under every Wine version examined, and to report it as `InvalidKey`.

**Wine's `bcrypt.dll`** (not used by .NET for ECDSA, but callable directly):
- **ECDSA exists.** `ECDSA_P256` generate, export, import (X, Y and D together), sign and verify are implemented in every version examined.
- **The GnuTLS dependency.**
  - Up to 11.10, and in Proton, this goes through the host's libgnutls, loaded at runtime. If it doesn't load, asymmetric keys and symmetric encryption are unavailable.
  - From 11.11 (and so XIV on Mac 5.5.1) bcrypt uses bundled SymCrypt and needs no GnuTLS.
  - Whether libgnutls loads in the XIVLauncher.Core Flatpak on Steam Deck, in distribution installs, and in the Steam Linux Runtime is **not established**.
- **A quirk before 11.11:** a signature-size query answers 32 instead of 64. A caller must always allocate 64 bytes.
- **PBKDF2.** `BCryptDeriveKeyPBKDF2` is implemented in every version examined and needs no GnuTLS. .NET's `Rfc2898DeriveBytes.Pbkdf2` doesn't call it on Windows 10 and later, though. It uses the `BCRYPT_PBKDF2_ALG_HANDLE` pseudo-handle, which Wine only added in 11.3 (commit 2b98230e53). So `Rfc2898DeriveBytes.Pbkdf2` is expected to fail on 10.8 and 11.0.
- **Other primitives.** AES-GCM needs GnuTLS before 11.11. SHA-256 and random numbers are available in every version examined.

**Wine's `crypt32.dll` DPAPI** (identical from Wine 8.5 to master, in Proton and in winecx):
- `CryptProtectData` derives a 3DES key from SHA-1 over:
  - the Windows user name (the Unix login under Wine);
  - a constant in Wine's public source ("I'm hunting wabbits");
  - a salt stored inside the blob;
  - the optional entropy.
- It ignores the protection flags.
- Blobs use a Wine-only format.

It round-trips, but anyone holding the blob and the user name can decrypt it. It is obfuscation, not protection. See section 5.

## 4. Wine behaviour reported by others

Other projects' reports, each consistent with section 3. They are other people's measurements, not this project's:

| Report | Environment | Observed | Relation to section 3 |
|---|---|---|---|
| [twelvehouse/ChatAnywhere#3](https://github.com/twelvehouse/ChatAnywhere/issues/3) (2026-05-13) | Debian 13, XIVLauncher-managed Wine "beta (10.8)", Dalamud CLR 10.0.0 | `CryptographicException` 0xC1000008 in `Pbkdf2Implementation.FillKeyDerivation` | Matches the missing PBKDF2 pseudo-handle. The report's own explanation (a missing `BCryptDeriveKeyPBKDF2`) does not match the Wine source, where that function exists. |
| [XeldarAlz/FFXIV-Aetherphone#52](https://github.com/XeldarAlz/FFXIV-Aetherphone/pull/52) (2026-07-23) | XLCore 1.4.0, wine-xiv 10.8, .NET 10 | P-256 ECDH key creation fails with 0x80090029; fixed with BouncyCastle | Matches NCrypt rejecting EC key creation; ECDSA goes through the same code path |
| [lkosson/profak#122](https://github.com/lkosson/profak/issues/122) (2026-04-13) | wine64, not FFXIV | EC PKCS#8 import fails with 0x80090027 | Matches NCrypt rejecting non-RSA imports |
| [BCD1210/soju#16](https://github.com/BCD1210/soju/pull/16) (2026-09-01) | Wine, version not stated | NCrypt persisted-key calls return 0x80090029 | Matches NCrypt's limited key support |

## 5. What this means for key storage

- **Native Windows.** DPAPI in CurrentUser scope works, as tested. What it protects against:
  - a copied key file read by another Windows account;
  - a key file copied off the machine.

  What it does not protect against: code running as the same user, including other Dalamud plugins in the game process.
- **Under Wine, DPAPI must not be presented as protection** (section 3, from source). Blobs also can't move between Windows and Wine, because the formats differ.
- **Any Wine key store would need independent verification.** If persona features are ever offered there, they need a separately verified storage alternative, and it must be run under the target Wine builds before it is relied on. Options:
  - a passphrase-wrapped store, whose PBKDF2 must avoid the missing pseudo-handle (for example by calling `BCryptDeriveKeyPBKDF2` directly) and whose AES-GCM needs GnuTLS before Wine 11.11;
  - an explicit "not protected on this system" state, which would be a policy decision.
- **The backup design needs the same care.** A backup format built on `Rfc2898DeriveBytes` plus `AesGcm` would fail at the PBKDF2 step under XIVLauncher.Core's Wine 10.8 (section 3). The direct `BCryptDeriveKeyPBKDF2` route gives byte-identical output (tested on Windows), so choosing it would not change a file format.

## 6. Selecting implementations

- **Recommendation (not decided): choose implementations by capability tests, not by operating system labels.**
  - A check that the process is running under Wine (for example `Util.IsWine()`) says nothing about which operations work: Wine 10.8 and Wine 11.17 differ, and GnuTLS may or may not load.
  - A Windows machine with broken CNG would pass an operating-system check and still fail.
- **What a capability test looks like.** Run the exact chain the plugin would use, once per session, off the framework thread, with a throwaway key, never a persona key: generate, export, import, sign, protocol `Verify`, protect and unprotect, derive a key. Enable persona features only if the whole chain passes.
- **When a check fails,** report the missing capability distinctly from the protocol's `InvalidKey` (which today would be the symptom of an NCrypt import failure) and leave every local feature untouched.
- **Guardrail:** hand-written implementations of cryptographic algorithms (signing, key derivation, encryption, and verification in production) are **not** approved as a way to keep the plugin package at three files or to avoid a dependency. The harness contains a PBKDF2-over-HMAC routine and uses the tests' managed P-256 verifier. Both exist only to cross-check platform results. Any future move of an algorithm into our own code, or any new cryptography dependency, is a separate owner decision with its own review (DecisionRegister.md, K7).

## 7. Needs actual Wine execution

None of the following is established:

- Every Wine, Proton and macOS row above, as a runtime result.
- Whether libgnutls loads in the XIVLauncher.Core Flatpak (Steam Deck), in distribution installs and in the Steam Linux Runtime.
- Which exceptions .NET actually throws under Wine for each failure. Some exception types could escape the protocol's `ProtocolException` contract.
- Whether .NET's key creation fails on Wine 11.1 and later at the missing key properties.
- How GnuTLS and SymCrypt (with minimal validation) handle invalid points. This is harmless to the protocol, which validates first.
- The user name Wine reports under each launcher, which decides whether Wine DPAPI blobs survive a user or machine change.
- CrossOver, Whisky and Kegworks.
- Whether the shipped wine-xiv and XIV on Mac binaries match the source that was read.
- Behaviour inside the game process under Dalamud.

The minimum runs before any Wine-specific implementation:
1. XIVLauncher.Core Stable (Wine 10.8) on a Linux desktop;
2. the XIVLauncher.Core Flatpak on a Steam Deck;
3. XIV on Mac 5.5.1.

Each run should capture Wine's `+bcrypt,+ncrypt,+crypt` debug channels and the GnuTLS load message. A tester package of the harness exists outside the repository for this purpose.
