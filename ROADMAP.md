# AetherFrame roadmap

Repository: [richhiiee/AetherFrame](https://github.com/richhiiee/AetherFrame). Verified September 29, 2026, at 11:31 UTC. This is a repository handoff, not authorization to push, merge, publish, release, or change the primary checkout.

**Evidence rules.** CONFIRMED means an explicit owner decision or an agreed project requirement. VERIFIED means repository content or GitHub state was checked. OPEN means undecided or not verified. RECOMMENDATION means a proposed next step, not an approved decision. Implemented, merged, released, reviewed, and tested in game are separate states.

Product requirements below preserve the September 2026 Product and Technical Specification and the owner's later statements in AetherFrame Dev. Repository and PR state take precedence over older status reports. The [networking decision register](https://github.com/richhiiee/AetherFrame/blob/3920aed702b3049508de5117b0ae68b9859ad4ec/docs/networking/DecisionRegister.md) is authoritative for networking approvals: D3 is approved; D2 is approved in principle; the other listed decisions remain OPEN. Code, research, and recommendations do not approve them.

## 1. Goal and scope

AetherFrame is a Final Fantasy XIV Dalamud plugin for creating, preserving, organizing, duplicating, and intentionally sharing complete character Plates.

**Basic mode feels like FFXIV. Advanced mode removes the restrictions.** Both editors operate on the same saved Plate. The creative core includes multiple independent Plates and variants, a local Plate Library, text and typography, images and backgrounds, reusable Templates, safe import and export, and accurate previews.

Editing, Plates, Templates, images, the Library, import, and export must work without an account or backend. Online features are optional for the player and must never hold the only copy of their content. Exporting a file does not publish it.

**CONFIRMED release requirement:** the owner subsequently required working networking before inviting external beta testers, because showing Plates to other players is integral to the product. This changes the older plan that deferred all networking beyond initial testing. It does not remove the requirement for an independent local core. **OPEN:** the exact sharing, viewing, and backend feature set required for that beta. An existing published alpha is not evidence that this beta gate has been met.

Lightweight RP information remains optional. Full RP profiles, social networks, appearance management, live posing, and native portrait manipulation are outside the core. Avoid duplicating Penumbra, Glamourer, Brio, Anamnesis, Absolute Roleplay, RPHub, Glance, HaselTweaks, and Photobooth.

## 2. Current status: done, in progress, and known bugs

### Verified repository snapshot

| Item | Verified state |
| --- | --- |
| Default branch | `master` at `3920aed702b3049508de5117b0ae68b9859ad4ec`; Windows and Ubuntu build checks passed. |
| Published version | [v0.1.6](https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.6), published September 27, 2026, marked prerelease; product documentation calls it Alpha. A separate v0.1.5 release remains a draft. |
| Custom repository | `plugin-repository` exists at `e85692416ec20151fbda9b76788c4e2ef83db763`. Its [manifest](https://github.com/richhiiee/AetherFrame/blob/e85692416ec20151fbda9b76788c4e2ef83db763/pluginmaster.json) serves v0.1.6 with `IsTestingExclusive: true`. Older statements that it has never been published are stale. |
| PR #21 | [Plate Library reliability and data preservation](https://github.com/richhiiee/AetherFrame/pull/21): OPEN, draft, not merged. Head `bb37acce8ef452106d91f5e87dcda0b7715de898`; Windows and Ubuntu passed. |
| PR #22 | [NETWORK1 Increment 0 documentation](https://github.com/richhiiee/AetherFrame/pull/22): MERGED September 29 at 00:24:47 UTC as `3920aed`. PR head `24e9edd1b3c9a2a07cfc63f1f2b9337e0f25a242` passed both platforms. Six networking documents changed; no implementation or protocol bytes changed. |
| PR #23 | [Persona management foundation](https://github.com/richhiiee/AetherFrame/pull/23): OPEN, draft, not merged. Head `1b2dcf47a10b2d5c73e60d90c25cd812840afc0b`; Windows and Ubuntu passed. Includes #22 through a merge from master. |
| Primary local checkout | `E:\Plugin development`, branch `claude/celestial-dream-v1`, commit `4749b9ac8bbc3f6d6374337bf7209b54915175b9`, with 19 dirty entries. It is not the current remote master. Treat its artwork, component work, untracked files, backups, and stashes as protected work. |

PR #21 was receiving commits during verification. Recheck its head before continuing. CI results belong to the recorded commit, not automatically to later commits. This handoff did not run a new build, execute game tests, or perform a fresh independent security audit.

### Done

1. **Released local product:** My Plates; Basic and Advanced editors; manual save; duplicate, rename, delete, search, and Active Plate selection; Plate Viewer and Clean Preview; procedural and bundled graphical Components; local Templates; managed images; validated `.aetherframe` import and export. These are documented in the [versioned README](https://github.com/richhiiee/AetherFrame/blob/3920aed702b3049508de5117b0ae68b9859ad4ec/README.md) and [changelog](https://github.com/richhiiee/AetherFrame/blob/3920aed702b3049508de5117b0ae68b9859ad4ec/CHANGELOG.md).
2. **Released v0.1.6 hardening:** archive and document validation, bounded font sizes, safer save and import lifecycle handling, preservation of newer configuration and asset metadata, and Content ID log redaction. This is implemented and released; a complete recorded run of the manual acceptance checklist remains OPEN.
3. **Merged, unreleased NETWORK0 foundation:** isolated `AetherFrame.Protocol`, canonical binary encoding, signed document verification, synthetic identity tests, and hostile input tests. It does not connect to a server and the plugin does not reference it. Protocol v1 remains DRAFT. Its current P256 and signature behavior is an implementation baseline, not approval of every future cryptographic or product policy.
4. **Merged NETWORK1 planning:** #22 records boundaries, platform evidence, decision gates, D3 approval, and D2 approval in principle. It does not implement networking.
5. **Distribution infrastructure:** release validation and custom repository publication tooling exist, and the testing channel is populated. Official Dalamud repository submission or acceptance is not established.

### In progress

**PR #21:** reliability remediation remains isolated in its draft branch. Since the earlier `c724cd6` audit, it adds byte level encoding detection, preservation before overwriting invalid bytes, protection of bindings and manual order, recovery copies that receive their final name only after completion, failed move rollback, package fixes, and regression tests. `bb37acc` adds historical document encoding tests. Do not carry the old U+FFFD rejection regression forward as an unfixed current implementation claim: the new reader distinguishes authored U+FFFD from invalid bytes and does not use encoding alone to choose an older backup. The PR description and reliability report still describe parts of the earlier behavior and need reconciliation.

**PR #23:** standalone `AetherFrame.Personas` and tests implement multiple independent personas, explicit selection, key custody contracts, and backup contracts. The amended implementation addresses key pair validation, orphaned key custody, inspection before using secrets, copying restore input, and signer lease revocation. There is no persistent key store, backup format, encryption implementation, plugin integration, or networking. Labels and signer lease policy remain provisional. Adding the persona suite to the release workflow was withdrawn; the build workflow runs it. See the [current increment document](https://github.com/richhiiee/AetherFrame/blob/1b2dcf47a10b2d5c73e60d90c25cd812840afc0b/docs/networking/NETWORK1_PersonaFoundation.md).

**Verified CI evidence:** #23's Windows job passed 2739 plugin, 439 release tooling, 153 protocol, and 226 persona tests, with zero build warnings and Package OK ([run](https://github.com/richhiiee/AetherFrame/actions/runs/36559291387)). #21 at `bb37acc` passed 2917 plugin, 439 release tooling, and 153 protocol tests on both platforms, with zero build warnings and Package OK ([run](https://github.com/richhiiee/AetherFrame/actions/runs/36561609661)). These runs build GitHub's merge previews for the recorded PR heads: `12f03e9` for #23 and `0c54caa` for #21. They do not test #21 and #23 combined.

### Known bugs and verification gaps

1. **Merged baseline defect, fix still in #21:** if preserving damaged bytes in Recovery fails, a later write can replace those bytes after loading an older backup. The preservation guard and partial copy tests are in the unmerged PR. Master and the published alpha do not yet contain that fix.
2. **Documentation defects:** #21's report and PR description lag its remediation; #23's PR description still gives the original 96 persona tests and earlier workflow behavior. Master documentation also lags the actual custom repository publication. Use exact commits and live checks for status.
3. **OPEN manual verification:** current #21 acceptance in FFXIV, the complete [v0.1.6 checklist](https://github.com/richhiiee/AetherFrame/blob/3920aed702b3049508de5117b0ae68b9859ad4ec/docs/ManualAcceptance-0.1.6.md), and review closure of amended #21 and #23. Passing CI does not establish these. Earlier reports concern earlier heads.
4. **OPEN residual reliability findings:** the [existing reliability report](https://github.com/richhiiee/AetherFrame/blob/bb37acce8ef452106d91f5e87dcda0b7715de898/docs/reliability/PlateLibraryReliability.md) records configuration recovery, unknown element ordering after downgrade, missing backup recovery notices, concurrent game clients sharing storage, and prerequisites for image cleanup. Reconfirm each against the chosen implementation before calling it a current defect or assigning a fix. The reliability report's D numbers are unrelated to the networking register's D numbers.
5. **Known limits:** no autosave or version history, no automatic image cleanup, and no implemented online sharing. These are not promises already delivered. Native Linux CI is not proof of compatibility inside Dalamud under Wine, Proton, or macOS.

## 3. Milestones and acceptance criteria

The first six milestones preserve the baseline product sequence. Implemented features should be verified and refined, not rebuilt because an old checklist still calls them future work. Acceptance criteria restate the agreed scope; an OPEN result is not a completion claim.

| Milestone | Status and acceptance criteria |
| --- | --- |
| 1. Adventure Plate Classic and editors | Implemented. Basic sections, portrait image, themes, and preview work on the same document as Advanced. Opening Basic does not mutate a Plate; Advanced positioning survives; reset, undo, revert, and save are explicit. Complete game acceptance: OPEN. |
| 2. Plate Library | Implemented. Create, preview, edit, rename, duplicate, delete, reorder, search, and choose Active Plate. Duplicate has a new Guid; opening or importing does not silently activate. Failure to make a thumbnail never prevents saving. |
| 3. Assets and persistence | Implemented in part, with continuing hardening in #21. Versioned documents and migrations preserve content; images are local; image and cache limits are bounded; unavailable character identity leaves the Library usable. Damaged originals must be preserved before replacement. Automatic asset cleanup remains disabled until its preservation prerequisites are satisfied. |
| 4. Portable Plate packages | Implemented. Validate a staged `.aetherframe` archive, preview compatibility, import as a new Plate with safe asset identities, and never overwrite or activate existing work implicitly. Hostile archives are refused and failure preserves existing content. |
| 5. Templates | Implemented. Adventure Plate Classic, Blank Canvas, and saved local Templates create independent Plates; editing the result never changes its Template. No account required. |
| 6. Onboarding and release readiness | Partial. Installation, commands, testing guidance, issue templates, and release tooling exist. Complete runtime, input, DPI, controller, migration, accessibility, and performance verification is OPEN. Official repository readiness is a later goal. |
| NETWORK0. Protocol foundation | Merged, DRAFT. Isolated protocol and tests, typed refusal of hostile input, unchanged plugin behavior, no server or real identity storage. Final wire format freeze remains OPEN. |
| NETWORK1. Local identity and publishing preparation | Planning merged; persona foundation in #23. Complete only after applicable decisions are approved, protected storage and encrypted backup are reviewed, and the planned local publishing preparation works without changing the creative core. NETWORK1 itself adds no HTTP, backend, discovery, share links, or sharing UI. |
| Working sharing before external beta | CONFIRMED owner gate; implementation and detailed acceptance OPEN. Users must be able to show their Plates to others intentionally while retaining local ownership and offline editing. Backend, transport, remote rendering, and interaction details require decisions before a complete beta checklist can be written. |

## 4. Agreed architecture and coding rules

1. **One local document.** `ProfileDocument` represents a complete Plate; Guid identity is separate from character identity. Basic and Advanced share it and the logical renderer. Keep logical canvas coordinates independent of viewport pixels.
2. **Local character binding.** Use `IPlayerState.ContentId` only for local bindings stored separately from Plates. Character name and home World are descriptive metadata. If identity is unavailable, postpone binding and keep the Library usable. Never publish Content IDs or derive personas from them.
3. **Persistence and preservation.** Small global settings use `IPluginConfiguration`; important structured JSON uses `IReliableFileStorage`; images and generated thumbnails use ordinary file storage. Manual save, persistent state comparison for dirty tracking, schema versions, migration tests, and memory only undo are the agreed baseline. Preserve unknown and newer data. Do not enable asset cleanup without protecting unsaved references, backups, Recovery, and incomplete scans.
4. **Assets and rendering.** Use managed image copies with Guid filenames and `AssetId` references. Replacing an image preserves its transforms. Keep caches bounded and expensive file, image, package, and network work away from frame rendering. The baseline recommends asynchronous CPU thumbnail composition; do not treat that recommendation alone as proof of the current implementation.
5. **Import is data.** Packages must never run code, invoke the shell, install dependencies, fetch remote URLs, write arbitrary paths, alter unrelated settings, or silently replace a Plate. Bound archive size, entries, JSON complexity, document elements, image dimensions, and decoded memory before committing.
6. **Input and platform.** Prefer supported Dalamud APIs and thin adapters. Read key transitions during `IFramework.Update`, queue editor actions, consume only handled shortcuts, and disable editor shortcuts during text input. Respect UI scaling and hiding. Test game behavior in game.
7. **Separate optional systems.** Local persistence must not acquire persona IDs, remote profile IDs, or publication state. NETWORK1 separates signing, protected keys, encrypted backups, local Plates, optional publishing, and backend obligations. Publishing preparation reads saved Plates and managed images without mutating originals. It must not make local code depend on identity availability.
8. **Privacy.** Prefer intentional sharing and explicit target lookup. No silent telemetry, automatic nearby scraping, public Content IDs, alt correlation, exact location, location history, public last seen, named viewers or downloaders, public social graphs, or reputation scores. Target lookup interaction and identity verification remain OPEN.
9. **Security gates.** No persistent private key outside tests before G1 approvals. No real `.afpersona` writing or restoration before D2 technical security approval. No signed byte change without explicit approval and matching specification and vector updates. Protocol v1 remains DRAFT until explicitly frozen. No approval exists for custom cryptography or a new cryptographic dependency merely to satisfy packaging constraints.
10. **Implementation workflow.** Claude Code handles substantial implementation. Continue the same Claude chat for a small follow up. For a new milestone or major subsystem, **start a new Claude chat**. Perplexity Deep Research handles research; Gemini is for targeted independent review. The requested repository based workflow makes these records the handoff instead of relying on copied chat reports.
11. **Change discipline.** Use incremental milestones, minimal dependencies, safe migrations, and appropriate tests. Keep protected local work separate from new implementation. Verify Git status before destructive actions; preserve stashes, backups, and authored data. Do not push unless asked. This roadmap grants no merge, release, publication, cleanup, or primary checkout modification permission.

Current verified build context is .NET 10, Dalamud SDK/API 15, and an x64 plugin. The release package currently contains `AetherFrame.dll`, `AetherFrame.deps.json`, and `AetherFrame.json`. Whether and how networking code enters that package is OPEN under D9b; preserve validation of historical packages when that decision is implemented.

## 5. Key decisions and one line rationales

| Confirmed decision | Rationale |
| --- | --- |
| Local creative work remains independent of accounts and services. | Users retain usable, authoritative copies of their work. |
| Basic and Advanced edit one Plate through a shared renderer. | Familiar editing and freeform control preserve the same creation. |
| Plates, Templates, character bindings, and personas have distinct roles. | Reuse and identity changes must not mutate or correlate unrelated content. |
| Import creates a new Plate; activation and publishing are explicit. | Receiving or opening content must not silently change what the player presents. |
| Manual save and memory only undo are the baseline. | Save behavior stays explicit; autosave and history are separate OPEN product choices. |
| Privacy favors deliberate sharing and target initiated viewing. | The product must not become passive player tracking. |
| D3: multiple independent personas, manually switched, with at most one active for identity operations. | Players control which identity an operation uses without automatic character, account, or Content ID binding. |
| D3: switching personas changes neither saved Plates nor publishing. | Identity selection remains separate from content ownership and consent. |
| D2, in principle: encrypted portable `.afpersona` backups restore the same identity on another computer without a hosted account. | Players can recover identity while plaintext private key exports remain prohibited. |
| Working networking is required before external beta invitations. | Showing a finished Plate to others is part of the owner's intended beta experience. |
| Prefer integration with established tools and eventual official Dalamud compatibility. | Keep AetherFrame focused and avoid unnecessary unsafe subsystems. |
| Move continuity into the repository with Claude implementing. | Someone can continue from recorded decisions and evidence without a chat relay. |

D2 does not approve an encryption scheme, password policy, derivation parameters, recovery warnings, or implementation. D3 does not approve labels, storage, supported platforms, user interface, or the interim lease revocation policy. These remain OPEN.

## 6. Deferred features in planned order

**Confirmed ordering constraints:** stabilize the local product; retain NETWORK0 before dependent identity and publishing implementation; approve each decision gate before crossing it; deliver working sharing before external beta. Precise prioritization within the remaining work is OPEN.

The baseline's later feature order was:

1. Lightweight RP summary and hooks.
2. Penumbra IPC.
3. Glamourer IPC.
4. Optional native portrait experiments.
5. Optional online accounts.
6. Private sharing.
7. Target profile lookup.
8. Optional Brio scene experiments.

That is a historical order, not an unchanged execution queue. The later beta requirement promotes working sharing; it does not approve accounts or make the other items beta requirements. **OPEN:** a revised total order and whether target lookup belongs in the first sharing release. Do not schedule RP, IPC, or portrait experiments ahead of beta sharing solely because of the old list.

For NETWORK1, the repository records this **nonbinding recommendation**: API tidy; draft marker and name rule; plugin boundary skeleton; publication index, outbox, and snapshot builder; key store core; backup codec; Windows protection and capability probe; image preparation; preview wiring; Wine/Proton/macOS measurements; acceptance and independent review. Each increment is conditional on its register gates. The persona foundation in #23 implements part of this plan out of sequence; it does not approve the remaining increments.

Trash restoration UI, richer organization and previews, additional visual sets and typography, autosave/history, cleanup activation, and a shorter repository URL are future ideas or deferred concerns. Their detailed scope and priority are OPEN. Do not add folders, favorites, archives, or complex tags without evidence of need.

## 7. Open questions

Every item below is **OPEN** unless an owner approval is subsequently recorded with its date and exact scope.

| Gate or area | OPEN questions |
| --- | --- |
| Beta scope | What exact publish, view, retract, sharing, and lookup experience constitutes working networking? What runtime and usability evidence is required before invitations? |
| G1: persistent keys | D9b packaging; N3 draft marker; N5 document API; L6 provider and policy API; K1 algorithm choice; K2 storage; K3 platform enablement; K6 rotation; K7 implementation sources; P1 publication index; P2 preview enablement. K8 verification and K9 storage also apply if pursuing Wine support. |
| D2 technical approval | Backup encryption, container format, password rules, derivation parameters, recovery warnings, and implementation, including K5; K4 governs backup or acknowledgement before real publishing. |
| G2: snapshots and images | D4 name rules; D5 image digest and metadata policy; D8 whether a metadata only schema reaches players; D9a persona labels; I1 supported image policy. |
| G3: freeze or real server | D1 retraction semantics; D6 signed envelopes versus server checked content; D7 deployment binding; K4 and K5; N1 identifier scope; N7 consumer text handling. |
| G4: backend | N2 replay retention; N6 clock behavior for retractions; S1 request proofs; S2 tombstones; S3 lost keys and expiry; S4 persona revocation; I2 server image processing. Hosting, domains, provider, cost, and operational policy are not selected here. |
| #23 follow ups | Signer lease behavior during a persona switch; D3's additional deliberate association clause; provisional labels; L9 networking suites as release gates. The current revocation behavior is an interim implementation. |
| Other protocol items | L2 conformance coverage for a second implementation; L4 public only signer acceptance; L8 signing context specification; local package photo metadata under I3. |
| Local reliability | Resolution and current applicability of the residual findings in section 2; any future autosave, history, cleanup, or concurrent client storage design. |
| Acceptance and integration | Fresh review and game evidence for the amended draft PRs; combined integration of #21 and #23 with current master. Separate green PR runs do not prove the combined result. |
| Workflow | Exact permissions and automation for Claude running from the repository. No unattended push, merge, release, or deployment policy is established by this handoff. |

## 8. Next five small PR sized tasks and acceptance criteria

**RECOMMENDATION, not new owner decisions.** This is a proposed breakdown of existing closeout work and the already documented NETWORK1 increments. Recheck current heads first and skip work already completed. Tasks 1 and 2 are follow ups to their existing draft PRs. Tasks 3 through 5 are blocked on the named approvals; this document does not grant them.

1. **Reconcile #21's reliability report and acceptance evidence.** Claude Code: continue the same Claude chat handling #21. Keep the scope to documentation and any narrowly missing regression evidence. Acceptance: report and changelog describe the actual byte decoding, backup selection, Recovery copy, and failed move behavior; results name the tested head; old `c724cd6` findings are mapped to fixes or OPEN residuals; the current game checklist accounts for invalid bytes versus authored U+FFFD and the backup context of the installed build. Record actual manual results or leave them OPEN. Do not mark the draft ready solely because CI passed.
2. **Reconcile #23's provisional decisions and review evidence.** Claude Code: continue the same Claude chat handling #23. Acceptance: record the lease policy as OPEN in the decision register without inventing approval; document provisional labels, the D3 wording gap, and L9; align the handoff with the amended implementation and 226 test baseline; preserve the lack of real keys, storage, backup format, networking, and plugin reference. Record independent review results against the amended head or leave review closure OPEN.
3. **NETWORK1 increment 1: protocol API tidy.** Claude Code: **start a new Claude chat** for this increment. Prerequisites: owner decisions N5 and L6 recorded. Acceptance: apply only the approved API changes; update affected consumers and public API fixtures; keep signed bytes and committed vectors identical; pass protocol and persona tests where applicable; add no plugin integration or persistent keys. If the proposal is declined, close this task without implementing it.
4. **NETWORK1 increment 2a: distinguish draft signed documents.** Claude Code: **start a new Claude chat** for increment 2. Prerequisite: N3 approved. Acceptance: implement only the approved draft marker; update specification, vectors, and independent checker together; prove rejection across incompatible draft and final markers; keep protocol status DRAFT and add no persistent identity. Exact marker values remain OPEN until approved.
5. **NETWORK1 increment 2b: enforce the approved remote name rule.** Claude Code: continue the same Claude chat for increment 2, in a separate small PR. Prerequisite: D4 approved and task 4's chosen base established. Acceptance: implement exactly the approved Unicode, length, and byte rules; add boundary and adversarial cases; update vectors, specification, and independent checker; preserve local Plate naming and file formats. Do not silently adopt the register's recommended limits as approved policy.

The next action is to reconcile the two existing draft PRs at their current heads. Persistent key storage, real backups, backend work, and beta invitations follow only when their respective decisions and acceptance evidence are complete.
