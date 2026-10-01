# Releasing and submitting AetherFrame

How a version becomes a GitHub Release, and what AetherFrame needs for the official Dalamud plugin repository. Version numbers and tags follow [Versioning](Versioning.md).

Nothing described here happens on its own. The release workflow only ever creates a **draft**, the custom repository changes only when you run its publication workflow by hand, and submitting to Dalamud is a pull request made by hand.

## Distribution plan

1. **A public custom Dalamud repository, hosted from GitHub.** The intended route for players: one URL added in `/xlsettings`, then install and updates from `/xlplugins` like any other plugin. The releases stay GitHub Releases; the repository is one generated `pluginmaster.json` that points at them, published by a workflow you run by hand. How it works, how it is validated and how publishing runs: [CustomRepository](CustomRepository.md). Nothing is published there yet.
2. **GitHub Release ZIPs**, loaded as dev plugins, for testers until the repository is live.
3. **Official Dalamud repository.** Welcome, and prepared for below, but not required for distribution. If AetherFrame is accepted there at a higher version, Dalamud moves players over from the custom repository automatically.

Testers follow [Testing](Testing.md).

## Making a release

1. Finish the milestone on `master` as [Versioning](Versioning.md) describes, with `Version.props` set to the new version.
2. Move the `## [Unreleased]` notes in [CHANGELOG.md](../CHANGELOG.md) under `## [<version>] - <date>` and add its compare link. The release fails without that section, or with one over 16,384 characters (Dalamud's limit for the repository's changelog). Notes about code the release doesn't carry, such as the networking preview flavour's, can stay under `[Unreleased]`, as 0.1.7's did.
3. Regenerate the dry-run repository fixture for the new version and commit it with steps 1 and 2 ([command](../distribution/dry-run/README.md#regenerating)). The tooling tests fail while `distribution/dry-run/pluginmaster.json` describes another version or another CHANGELOG text, so the Build workflow and a tagged Release would fail without it, and a tag is never moved.
4. Optional dry run: **Actions → Release → Run workflow** on `master`. It builds, tests and checks the package and keeps it as a workflow artifact, without creating a release.
5. Tag and push the tag (only after the Build workflow passed on that commit):

   ```bash
   git tag -a v0.1.6 -m "AetherFrame 0.1.6"
   git push origin v0.1.6
   ```

6. The **Release** workflow runs. When it succeeds, a draft pre-release is waiting under **Releases**.
7. Download the ZIP from the draft and load it in game as a dev plugin ([Testing](Testing.md#from-a-github-release-zip-dev-plugin)). `/af version` should print the new version and the tag's commit.
8. Publish the draft by hand when you're happy with it, or delete it. Keep **Set as a pre-release** ticked for a release that goes to the testing channel first.
9. Put it in the custom repository: **Actions → Publish custom repository → Run workflow** on `master`, with the version and a channel (`testing` for a pre-release). Run it once with **publish** unticked to see the change, then with **publish** ticked, and approve it when the run pauses. The channel rules, promotion to stable and rollback: [CustomRepository](CustomRepository.md#publishing).

### What the workflow checks

[`.github/workflows/release.yml`](../.github/workflows/release.yml) runs on Windows, like players' machines:

| Step | Refuses to continue when |
|---|---|
| Tag check (tag pushes only) | the tag isn't `v` + the `Version.props` version, isn't annotated, doesn't point at the commit being built, or isn't on `master` |
| Restore | `packages.lock.json` is out of date (`--locked-mode`, as the official build would) |
| Build and test | the Release build or any test fails, including the checks on the built DLL and its manifest |
| Package check ([`New-ReleasePackage.ps1`](../.github/scripts/New-ReleasePackage.ps1)) | `latest.zip` holds anything other than `AetherFrame.dll`, `AetherFrame.json` and `AetherFrame.deps.json`; the manifest isn't `AetherFrame` at `<version>.0` with its installer fields; the DLL isn't `<version>.0`; or the CHANGELOG has no section for the version |
| Release tooling tests and staged package check (`AetherFrame.ReleaseTools validate-package`) | any rule in [CustomRepository](CustomRepository.md#release-validation-rules) fails: the DLL isn't x64 and built from the commit being released, a version or the Dalamud API level disagrees anywhere, the csproj no longer matches the packaged manifest, an entry name is unsafe, or `SHA256SUMS.txt` doesn't match |
| Repository metadata (`generate-repository`, `validate-repository`) | the custom repository entry can't be generated from the package, or doesn't validate. The result is kept in the artifact as `pluginmaster.json`; nothing is published |
| Draft release | a release for the tag already exists, draft or published. Nothing is ever replaced |

The draft gets the ZIP as `AetherFrame-<version>.zip`, a `SHA256SUMS.txt`, and the CHANGELOG section as its notes. The ZIP is exactly what DalamudPackager built. The workflow artifact also holds `package-summary.json` and the generated `pluginmaster.json` (stable and testing-exclusive shapes) for inspection.

The custom repository is published separately and by hand, from the published GitHub Release (step 9); that workflow downloads the release's own assets and checks them all again ([CustomRepository](CustomRepository.md#publishing)).

Only the draft job can write to the repository (`contents: write`), and it runs in the `release` environment. Adding yourself as a required reviewer of that environment (**Settings → Environments → release**) makes every draft wait for your approval too.

The workflow only runs for tags pushed after it exists, so the existing tags `v0.1.0`–`v0.1.4` are never built by it.

To run the package check locally after a Release build (PowerShell 7 or Windows PowerShell 5.1):

```powershell
./.github/scripts/New-ReleasePackage.ps1 -Version 0.1.6 -Destination dist
```

Then the full rule set and the repository metadata, with the release tooling:

```bash
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- validate-package --package dist/AetherFrame-0.1.6.zip --config distribution/repository.json --version-props Version.props --csproj AetherFrame/AetherFrame.csproj --changelog CHANGELOG.md --checksums dist/SHA256SUMS.txt --commit "$(git rev-parse HEAD)"
```

[CustomRepository](CustomRepository.md#building-and-validating-a-release-locally) has the rest of the commands.

## Official Dalamud repository

Checked against the official sources on 2026-09-25:

- [DalamudPluginsD17 README](https://github.com/goatcorp/DalamudPluginsD17#readme): approval and technical criteria, submission steps
- [The Submission Process](https://dalamud.dev/plugin-publishing/submission), [Plugin Restrictions](https://dalamud.dev/plugin-publishing/restrictions), [The Approval Process](https://dalamud.dev/plugin-publishing/approval-process)
- [AI Usage Policy](https://dalamud.dev/plugin-publishing/ai-policy)
- [Setting Plugin Metadata](https://dalamud.dev/plugin-development/plugin-metadata), [Technical Considerations](https://dalamud.dev/plugin-development/technical-considerations)
- The live [plugin master](https://kamori.goats.dev/Plugin/PluginMaster): API level 15 is current, and no plugin is called AetherFrame

Re-read them before submitting. They change.

### Required for the testing track

Every new plugin is submitted to `testing/live`, so this is the list for the first submission.

| Requirement | AetherFrame |
|---|---|
| Public Git repository that clones over HTTP without authentication | Done. `QuietFoxLabs/AetherFrame` is public |
| Latest `Dalamud.NET.Sdk` | Done. `Dalamud.NET.Sdk/15.0.0`, the newest on NuGet |
| `.csproj` and `packages.lock.json` committed after a Release build | Done. `dotnet restore --locked-mode` passes |
| Manifest `Name`, `Author`, `Punchline`, `Description` | Done, set in `AetherFrame.csproj` and written by DalamudPackager |
| Version not based on a timestamp or build counter | Done. `Version.props` only |
| Dalamud Windowing API for windows | Done. Every window is a `Window` in one `WindowSystem` |
| Clean install, working main and settings buttons | Done. The installer's main and settings buttons both open My Plates. Confirm on a clean install |
| Follows the Plugin Restrictions | Done. No server communication, no combat, no automation, no other players' account IDs. The own character's Content ID is a local file key and is stripped from exports |
| `icon.png` in the D17 `images/` folder, 1:1, 64–512 px | 512 × 512 file ready (`AetherFrame/images/icon.png`). It is AI-generated, so a hand-made replacement is recommended before submitting. See [Artwork](#artwork-and-ai-disclosure) |
| AI-generated assets disclosed in the plugin description | Done. The description names the AI-generated icon and the AI-assisted Celestial Dream and Celestial Sakura artwork |
| AI use level disclosed in the PR description | **Copilot**. Draft below |
| `manifest.toml` in `testing/live/AetherFrame/` with `repository`, `commit`, `owners`, `project_path` | Draft in [`dalamud-submission/manifest.toml`](dalamud-submission/manifest.toml). `commit` is filled in at submission |
| One plugin per PR, from its own branch | At submission |
| Acceptable Use Policy, Terms of Service, Code of Conduct | Read and accept at submission |

The approval team also reviews the code informally and checks that the plugin works and doesn't upload personal data. Nothing leaves the player's machine.

### Required for stable

Moving from testing to stable means copying the manifest folder from `testing/live/` to `stable/` in a new PR. No version bump or new commit is needed. Stable builds should be "relatively bug-free" and "properly supported", so AetherFrame's own bar is:

- a testing period with the reported bugs fixed or triaged, and no open data-loss or crash issues;
- a stable changelog in `manifest.toml`;
- someone answering issues.

### Recommended but optional

- **Installer preview images**: up to five, `image1.png`–`image5.png`, PNG, at most 730 × 380. See [Installer artwork](#installer-artwork).
- **A hand-made icon.** Strongly recommended before the D17 submission. The current icon is AI-generated, and the AI Usage Policy prefers even a crude hand-drawn icon to an AI-generated one; the team may ask for one. Disclosing the AI icon meets the rule, so this isn't a hard blocker.
- **A `changelog` in `manifest.toml`**, so it shows in the installer. The PR description is used otherwise, but isn't shown in the installer.
- **`CategoryTags`** in the csproj, to help the installer group the plugin.
- Asking in the Dalamud Discord's developer channels before submitting.
- Starting the PR description with `nofranz` to write the Discord announcement yourself.

## Installer artwork

| Image | Required | Size | Format | Current state |
|---|---|---|---|---|
| `icon.png` | Yes | Square, 64 × 64 to 512 × 512 | PNG | 512 × 512 RGBA, in `AetherFrame/images/`. AI-generated; replace with a hand-made icon before submitting |
| `image1.png`–`image5.png` | No | At most 730 × 380 | PNG | None yet |

The images live in the D17 folder (`testing/live/AetherFrame/images/`), not in this repository. The csproj's `IconUrl` is only used outside the official repository, for dev and custom-repository installs.

A good preview set would be the four README screenshots in `docs/screenshots/`, each scaled to fit 730 × 380 (the shape is close to the Plate Viewer and Advanced Editor captures). Screenshots that show Celestial Sakura art are covered by the description's disclosure. Take new captures rather than cropping, if names or other personal details are visible.

## Artwork and AI disclosure

The AI Usage Policy asks for two separate disclosures:

- **Assets, to players**, in the plugin description. `AetherFrame.csproj`'s `Description` says that the plugin icon is AI-generated and that the Celestial Dream and Celestial Sakura Components use AI-assisted artwork. The README says the same.
- **Code, to reviewers**, as a level in the PR description. The intended level is **Copilot**: AI implements while I plan, decide, review and test. The README's development note says the same.

| Asset | Origin | Disclosure |
|---|---|---|
| Celestial Sakura (7 pieces) | Created with AI assistance. Shipped byte for byte, each with embedded C2PA Content Credentials | Description and README. Credentials verified present in all seven files |
| Celestial Dream *Astrolabe Pivot* | Created with AI assistance. The runtime copy is resampled, so it carries no credentials | Description and README |
| Plugin icon | Generated with ChatGPT, then refined. The file carries no provenance metadata | Description and README. **A hand-made replacement is recommended before the D17 submission.** When it lands, drop the icon from the description and from `ReleaseMetadataTests` |
| Fonts | PT Sans, PT Serif, Cousine under the SIL OFL 1.1 | `Fonts/THIRD-PARTY-FONT-LICENSES.txt` |
| Font library | 101 Google Fonts families, each under the SIL OFL 1.1 or the Apache License 2.0 (`tools/fonts/library.json` records each file's source and checksum) | `Fonts/Library/THIRD-PARTY-FONT-LICENSES.txt` |

### Draft PR description

```markdown
Adds AetherFrame to testing/live.

AetherFrame lets players design character Plates: an Adventure Plate-style Basic Editor, a freeform
Advanced Editor, a local My Plates library, and a Plate Viewer (/af view). Everything is local:
no networking, no account, no data collection. Plates are shared only as files the player exports.

Source: https://github.com/QuietFoxLabs/AetherFrame
Changelog: https://github.com/QuietFoxLabs/AetherFrame/blob/master/CHANGELOG.md

## AI usage disclosure

Level: Copilot. AI writes most of the implementation and helps with code review. I decide what gets
built and how it works, review the changes, and test every release in game myself.

Assets: the plugin icon is AI-generated (ChatGPT, then refined), and the bundled Celestial Dream
and Celestial Sakura Component artwork is AI-assisted. The plugin description says so. The
Celestial Sakura files keep their C2PA Content Credentials.
```

Copilot is the intended level. Change it only if it no longer describes how AetherFrame is made. If the icon has been replaced by then, update the Assets paragraph.
