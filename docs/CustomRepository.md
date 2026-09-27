# AetherFrame's custom Dalamud repository

How AetherFrame reaches players without the official Dalamud plugin repository: a public custom repository, hosted from this GitHub repository, that Dalamud's plugin installer reads like any other. This page covers the format, the tooling that produces and checks it, how a release is validated, how publishing will work, and how to undo a bad release.

**Status:** the infrastructure is implemented and tested, and nothing is published. Publishing is a separate decision (see [Decisions still open](#decisions-still-open)). Until then the Release workflow only produces the repository metadata as a workflow artifact.

Principles this is built on: everything is local first, nothing in the plugin phones home, and the custom repository adds no networking to the plugin itself. Dalamud does the downloading, from GitHub Releases, exactly as it does for every plugin. GitHub stays the public source and release host. Approval into the official Dalamud repository is welcome but not required.

## In plain language

For the project owner. Everything below this section is the detail.

**What players will do.** Type `/xlsettings` in game, open **Experimental**, paste one address under **Custom Plugin Repositories**, and save. AetherFrame then shows up in `/xlplugins` next to every other plugin, with an **Install** button, and later updates itself the way other plugins do. That address is the *repository URL*. Once anyone has used it, it can never change: Dalamud only accepts updates from the exact address a plugin was installed from.

**What a release is.** A release is a version number (`0.1.6`), a section in `CHANGELOG.md` for it, and a Git tag `v0.1.6`. Pushing the tag makes GitHub build the plugin, run every test, check the package, and prepare a **draft** release that only maintainers can see. You download the ZIP from the draft, try it in game, and press **Publish** when it is good. Nothing reaches players before that button.

**What the repository is.** One small text file, `pluginmaster.json`, that says: this is AetherFrame, this is its version, this is what changed, and the ZIP is here (a link into the GitHub release you published). Dalamud reads that file. The tooling in this repository writes it from the release itself, so the file can't disagree with the ZIP, and refuses to write it when anything is off.

**What happens when publishing is switched on.** Pressing **Publish** on a release will, in the future, run one more automated step that updates `pluginmaster.json`. A few minutes later, players' installers show the new version. Until that step exists, updating the file is a manual step described [below](#manual-emergency-recovery).

**Undo.** If a release turns out bad, the file is pointed back at the previous version (one Git revert), and a fixed release with a higher number follows. Players who already updated keep the bad version until the fix; Dalamud never downgrades. The source code, the tags and the old releases are never deleted.

**Testing builds.** Dalamud has a built-in idea of testing builds: players who tick **Get plugin testing builds** and opt into AetherFrame's testing see a newer *testing* version before everyone else. The repository can carry a stable version and a testing version at the same time. Whether to use that from the first release is one of the open decisions.

## How Dalamud custom repositories work

Verified against the Dalamud source at the version installed on the development machine (15.0.3.5, commit `e81744f6`): `Dalamud/Plugin/Internal/Types/PluginRepository.cs`, `PluginManifest.cs`, `Manifest/RemotePluginManifest.cs` and `PluginManager.cs`.

- A custom repository is a URL that returns a JSON **array** of plugin entries (the "plugin master"). Dalamud fetches it with a 20-second timeout and `Cache-Control: no-cache`, and deserializes it with Newtonsoft.Json into `RemotePluginManifest` records. An entry needs at least `InternalName`, `Name` and `AssemblyVersion` or it is dropped.
- To install, Dalamud downloads `DownloadLinkInstall` (or `DownloadLinkTesting` for the testing version), extracts the ZIP, and requires `<InternalName>.dll` and `<InternalName>.json` in it. The packaged manifest's `InternalName` and `AssemblyVersion` must equal the entry's, and the package must not carry a `WorkingPluginId`. Otherwise the install fails.
- An entry is shown only when its API level is at least the current one minus one (the previous level is listed as outdated), and it can only be installed or updated at exactly the current API level (`DalamudApiLevel`, or `TestingDalamudApiLevel` for the testing version).
- Updates are offered for an installed plugin only from entries with the same `InternalName` **from the same repository URL the plugin was installed from** (`InstalledFromUrl`), or from the official repository, and only when the candidate version is greater than the installed one. Dalamud never downgrades.
- A third-party repository may not carry an `InternalName` that exists in the official repository (compared ignoring case): such entries are dropped with a warning. If AetherFrame is one day accepted into the official repository at a higher version, players who installed from the custom repository are updated from the official one automatically, and the custom entry becomes inert. Dalamud makes that comparison on every reload, and while the official repository fails to load it marks every custom repository as failed too, so AetherFrame can be missing from `/xlplugins` for reasons unrelated to this repository.
- Testing: the testing version is used only when the player has **Get plugin testing builds** on and has opted into this plugin's testing, `TestingDalamudApiLevel` is present and current, and `TestingAssemblyVersion` is greater than `AssemblyVersion`. An `IsTestingExclusive` entry is invisible to players without **Get plugin testing builds**; installing it downloads the testing slot and opts the player into AetherFrame's testing automatically, and its updates are offered only while **Get plugin testing builds** stays on.
- `DownloadLinkUpdate` exists in the format and is set to the install link; Dalamud 15.0.3.5 downloads installs and updates from `DownloadLinkInstall`, or `DownloadLinkTesting` for a testing version.

### The entry AetherFrame publishes

Every field below is written by `generate-repository`. Nothing else is ever emitted, and a file with any other key fails validation.

| Field | Value | Comes from |
|---|---|---|
| `Author`, `Name`, `Punchline`, `Description`, `Tags`, `CategoryTags`, `IconUrl`, `ImageUrls`, `RepoUrl`, `ApplicableVersion`, `MinimumDalamudVersion`, `LoadRequiredState`, `LoadSync`, `LoadPriority`, `CanUnloadAsync`, `AcceptsFeedback`, `FeedbackMessage` | as built | the `AetherFrame.json` inside the release ZIP, which DalamudPackager writes from `AetherFrame.csproj` |
| `InternalName` | `AetherFrame` | the ZIP's manifest, which must equal `distribution/repository.json` |
| `AssemblyVersion` | `MAJOR.MINOR.PATCH.0` | the DLL inside the ZIP (which must equal the manifest, the file name, `Version.props` and the tag) |
| `DalamudApiLevel` | `15` | `distribution/repository.json`, which must equal the manifest, the DLL's Dalamud reference and `Dalamud.NET.Sdk` in the csproj |
| `Changelog` | the version's section | `CHANGELOG.md`, `## [MAJOR.MINOR.PATCH] - YYYY-MM-DD` |
| `LastUpdate` | Unix seconds | `--last-update`, given explicitly (CI uses the release commit's date); never "now" |
| `DownloadLinkInstall`, `DownloadLinkUpdate`, `DownloadLinkTesting` | `https://github.com/richhiiee/AetherFrame/releases/download/v<version>/AetherFrame-<version>.zip` | the template in `distribution/repository.json` and the version |
| `IsHide` | `false` | fixed; a bad release is rolled back, not hidden |
| `TestingAssemblyVersion`, `TestingDalamudApiLevel`, `TestingChangelog`, `IsTestingExclusive` | see [Channels](#channels-stable-and-testing) | the testing package, when there is one |

The generated file for the current version, with download links pointed at a non-resolving `.invalid` host, is committed as [`distribution/dry-run/pluginmaster.json`](../distribution/dry-run/pluginmaster.json). It is the exact JSON Dalamud would read, apart from that host.

## Channels: stable and testing

Dalamud's model is one entry per plugin with a stable slot and an optional testing slot. `generate-repository` produces the three shapes that make sense:

| Shape | Command | Who sees what |
|---|---|---|
| Stable only | `--stable-package <zip>` | Everyone sees and installs the stable version. |
| Stable plus testing | `--stable-package <zip> --testing-package <newer zip>` | Everyone sees the stable version. Players with testing builds on who opt into AetherFrame get the testing version instead. The testing version must be newer, or Dalamud ignores it. |
| Testing-exclusive | `--testing-exclusive --testing-package <zip>` | Only players with **Get plugin testing builds** on see the plugin at all; installing it opts them into its testing. Both slots carry the same version, and both are needed: an exclusive install downloads the testing slot. |

Proposed use, pending the owner's decision: a GitHub Release published as a **pre-release** fills the testing slot and keeps the current stable version; a release published as a full release becomes the new stable version and clears the testing slot. Until publishing is automated, the operator picks the shape by hand.

## What lives where

| Path | Purpose |
|---|---|
| [`distribution/repository.json`](../distribution/repository.json) | The explicit facts the metadata is derived from: internal name, API level, source repository URL, the repository URL players add, and the download URL template. Reviewed by hand; never derived from the build machine. |
| [`distribution/dry-run/`](../distribution/dry-run/README.md) | The generated fixture for the current version and how to regenerate it. |
| [`tools/AetherFrame.ReleaseTools/`](../tools/AetherFrame.ReleaseTools/) | The tool: `validate-package`, `generate-repository`, `validate-repository`, `checksums`, `verify-checksums`. Plain .NET, no packages, no Dalamud reference, no network, no AppData. |
| [`tools/AetherFrame.ReleaseTools.Tests/`](../tools/AetherFrame.ReleaseTools.Tests/) | Its tests, on packages and assemblies they build themselves in temporary directories. |
| [`.github/scripts/New-ReleasePackage.ps1`](../.github/scripts/New-ReleasePackage.ps1) | Stages `dist/` from DalamudPackager's `latest.zip`: the named ZIP, `SHA256SUMS.txt` and `release-notes.md`. Unchanged since 0.1.5; the tool re-checks its output with the full rule set. |
| [`.github/workflows/build.yml`](../.github/workflows/build.yml) | Every push and pull request: build, tests, tooling tests, and the package check on the build's own `latest.zip`. |
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | Tags and manual dry runs: the above plus staging, the staged-package check, repository metadata generation and a draft release (tags only). |

The live `pluginmaster.json` will not live on `master`. It belongs on its own branch (proposed: `plugin-repository`, holding only that file and a README), served as `https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/pluginmaster.json`. That keeps generated data out of the source history, gives it its own history for rollback, and lets the future publish workflow write it with the built-in `GITHUB_TOKEN`.

## Building and validating a release locally

From the repository root. Windows PowerShell 5.1 runs the staging script; the tool needs only the .NET 10 SDK.

```bash
dotnet restore AetherFrame.slnx --locked-mode
dotnet build AetherFrame.slnx --configuration Release --no-restore
dotnet test AetherFrame.Tests/AetherFrame.Tests.csproj --configuration Release --no-build
dotnet test tools/AetherFrame.ReleaseTools.Tests/AetherFrame.ReleaseTools.Tests.csproj --configuration Release --no-build
```

```powershell
./.github/scripts/New-ReleasePackage.ps1 -Version 0.1.5 -Destination dist
```

```bash
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- validate-package --package dist/AetherFrame-0.1.5.zip --config distribution/repository.json --version-props Version.props --csproj AetherFrame/AetherFrame.csproj --changelog CHANGELOG.md --checksums dist/SHA256SUMS.txt --commit "$(git rev-parse HEAD)" --summary dist/package-summary.json
```

```bash
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- generate-repository --config distribution/repository.json --changelog CHANGELOG.md --last-update "$(git log -1 --format=%cI HEAD)" --stable-package dist/AetherFrame-0.1.5.zip --output dist/pluginmaster.json
```

Add `--dry-run` instead of `--output` to print the document without writing anything. `--tag v0.1.5` adds the tag check. `validate-repository --repository <file> --config distribution/repository.json [--stable-package <zip>] [--testing-package <zip>] [--changelog CHANGELOG.md]` checks an existing file. `checksums --output SHA256SUMS.txt <files>` and `verify-checksums --checksums SHA256SUMS.txt` write and check checksum files.

Every command prints one line per check (`[ OK ]` or `[FAIL]`) and ends with `Package OK`, `Repository metadata OK`, `Checksums OK` or `FAILED: n check(s) failed`. Exit codes: 0 all checks passed, 1 a check failed, 2 wrong usage. A run that fails writes nothing.

`dist/` is ignored by Git.

## Release validation rules

`validate-package` refuses a package when any of these fail. All failures are reported, not just the first.

**The file**
- Exists, is not empty, and is a ZIP archive whose directory and entries .NET can read; a damaged archive or an unsupported compression method is a failed check.
- Is named `latest.zip` (DalamudPackager's output) or `AetherFrame-MAJOR.MINOR.PATCH.zip`; a versioned name must match the DLL's version.

**The entries**
- At most 64 entries; every name is a plain, relative, flat file name: no `..`, no `.` segment, no leading `/`, no drive letter, no backslash, no folder, no directory entry, no control characters, at most 255 characters.
- No two entries with the same name, and none that differ only by case.
- Exactly `AetherFrame.dll`, `AetherFrame.json` and `AetherFrame.deps.json`. Anything else fails and is named by kind: debug symbols, source files, test assemblies, local configuration, user data paths, development-only files.
- Size limits: 64 MiB for the DLL, 1 MiB for each JSON file, checked against the declared size and again while decompressing.

**The DLL**
- A .NET assembly named `AetherFrame`, x64 (PE32+, `AMD64`), IL only, not 32-bit.
- Assembly version `MAJOR.MINOR.PATCH.0` and file version equal to it.
- Informational version `MAJOR.MINOR.PATCH+<40-character commit id>`: the SDK writes it when building from a Git checkout, so a build from a source archive or with a stale version never passes.
- References `Dalamud` at a major version equal to the configured API level.

**The manifest (`AetherFrame.json`)**
- Strict JSON: no comments, trailing commas, duplicate keys or unknown keys. Keys Dalamud writes into installed plugins (`WorkingPluginId`, `InstalledFromUrl`, `Disabled`, `Testing`, `ScheduledForDeletion`) fail with an explanation.
- `InternalName`, `AssemblyVersion`, `DalamudApiLevel` and `RepoUrl` equal the configuration and the DLL.
- `Name`, `Author`, `Punchline`, `Description` are non-empty; `ApplicableVersion` is `any`; `IconUrl` and `ImageUrls` are plain https URLs (at most five images); tags are non-empty and distinct.
- Contains nothing that looks like a local path (drive letters, UNC paths, Unix home directories).

**`AetherFrame.deps.json`**
- Valid JSON that names `AetherFrame/<version>` as the project library under its runtime target, and contains no local paths.

**Agreement**
- With `--version-props`, `--tag` and `--commit`: the version equals `Version.props`, the tag is `v<version>`, and the DLL was built from that commit.
- With `--csproj`: the project uses `Dalamud.NET.Sdk/<API level>.x.y`, and its `Name`, `Author`, `Punchline`, `Description`, `RepoUrl`, `IconUrl` and `Tags` equal the packaged manifest (a package built from other sources fails).
- With `--changelog`: `CHANGELOG.md` has a dated, non-empty section for the version.
- With `--checksums`: `SHA256SUMS.txt` lists the package with its actual SHA-256.

**The repository document** (`validate-repository`, also run on everything `generate-repository` writes)
- A JSON array with exactly one object, with known keys only.
- The entry's identity, API level, source URL and `ApplicableVersion` equal the configuration; versions are canonical `MAJOR.MINOR.PATCH.0` text; `LastUpdate` is Unix seconds between 2020 and 2100; `IsHide` is false; load and feedback flags are present.
- Every download link is exactly what the configured template gives for that version (https on a dotted host name, default port, no query, no user information, written in canonical form, ending in the package file name).
- The testing slot is coherent: absent entirely, a newer version with `TestingDalamudApiLevel` and its own link, or a testing-exclusive entry with one version, one link and one changelog.
- With the packages at hand: the entry describes exactly them, field by field (a testing-exclusive entry is compared with its package whether it is given as the stable or the testing package). With `--changelog`: its changelog fields are the current sections.

What is not enforced: byte-identical ZIPs across builds. The DLL itself is reproducible (`DotNet.ReproducibleBuilds`), but DalamudPackager stamps each ZIP entry with the build time, so two builds of one commit differ in the ZIP bytes. The repository metadata is independent of that: it is byte-identical across builds of one version.

## Checksums

`New-ReleasePackage.ps1` writes `dist/SHA256SUMS.txt` in `sha256sum` format (`<64 lowercase hex>  <file name>`, LF line ends), listing the release ZIP. The Release workflow attaches it to the draft release next to the ZIP, and the release notes repeat the hash. The tool's `checksums` command writes the same format for any set of files, sorted by name so the output is deterministic, and `verify-checksums` checks a file against the files beside it. `validate-package --checksums` requires the package's line to match.

`package-summary.json`, written by `validate-package --summary`, records the package's SHA-256 and the SHA-256 of each of the three files inside it, including `AetherFrame.dll`, so a DLL loaded in game can be traced to a release with `Get-FileHash`.

Players verify a download with `Get-FileHash .\AetherFrame-0.1.5.zip` (PowerShell) or `sha256sum -c SHA256SUMS.txt`. GitHub also publishes a `digest` for every release asset; the future publish workflow compares it with `SHA256SUMS.txt` before touching the repository.

## CI today: the dry run

Nothing in CI publishes anything, updates the repository, or needs a secret beyond the built-in token.

**Build and test** (`build.yml`, every push and pull request to `master`, Windows and Linux, `contents: read`): locked restore is implied by the SDK, Release build of the whole solution, the plugin's tests against the built DLL, the tooling's tests against the build's own `latest.zip` (including the check that it regenerates the committed fixture), and `validate-package` on that `latest.zip` with `--version-props`, `--csproj`, `--changelog` and `--commit`. Pull requests from forks run with read-only permissions and no secrets, and the tool only ever reads the checkout.

**Release** (`release.yml`, tag pushes and **Run workflow**, Windows): tag check (tags only), `dotnet restore --locked-mode`, Release build, both test suites, `New-ReleasePackage.ps1`, `validate-package` on the staged ZIP with `--checksums`, `--commit`, `--tag` (tags only) and `--summary`, then `generate-repository` in the stable and the testing-exclusive shapes, `validate-repository` and `verify-checksums`. Everything lands in the `AetherFrame-<version>` artifact for 30 days: the ZIP, `SHA256SUMS.txt`, `release-notes.md`, `package-summary.json`, `pluginmaster.json`, `pluginmaster.testing-exclusive.json`. The step summary shows the checksums. The `draft-release` job (tags only, `contents: write`, `release` environment) still attaches only the ZIP and `SHA256SUMS.txt` to a draft pre-release and never replaces an existing release.

## The future publish workflow

Designed here, not implemented. Nothing below runs until it is added deliberately, after the owner's decisions.

**Trigger.** The `release` event with `types: [published]`: it fires when a draft is published (as a release or a pre-release) and when a release is created already published. `prereleased` does not fire for pre-releases published from drafts, so `published` is the one type to use. The workflow must ignore drafts (`github.event.release.draft == false`) and refuse to run for anything but a tag matching `v<Version.props version>`. `GITHUB_SHA` is the tagged commit.

**Flow.**

1. Check out the tagged commit; verify the tag is annotated, on `master`, and equals `v<Version.props>` (the same checks the Release workflow runs).
2. Download the published release's assets `AetherFrame-<version>.zip` and `SHA256SUMS.txt` from the release itself (not from a workflow artifact), and require GitHub's asset `digest` to equal the `SHA256SUMS.txt` line and a fresh SHA-256 of the download. What players will download is what gets described.
3. Run `validate-package` on that ZIP with `--version-props`, `--csproj`, `--changelog`, `--checksums`, `--commit $GITHUB_SHA` and `--tag`.
4. Fetch the current `pluginmaster.json` from the `plugin-repository` branch and `validate-repository` it (a corrupted current file stops everything).
5. Decide the shape from `github.event.release.prerelease`: a full release becomes the new stable version (the testing slot is cleared); a pre-release keeps the current stable version and fills the testing slot with this release. The current stable package is downloaded by the link in the current `pluginmaster.json` and verified against its own release's `SHA256SUMS.txt` before it is reused.
6. `generate-repository` with `--last-update` set to the release's `published_at`; then `validate-repository` against both packages and `CHANGELOG.md`.
7. Commit `pluginmaster.json` to `plugin-repository` with a message naming the version and the release URL, and push. No force-push; the concurrency group `publish` serializes runs. Nothing is written when any earlier step failed.
8. Within a few minutes (GitHub's raw content cache), Dalamud installers see the new version.

**Permissions.** Only the publish job gets `contents: write`, and only to push to `plugin-repository`. Everything else runs with `contents: read`. The built-in `GITHUB_TOKEN` is enough; no personal access token, deploy key or other long-lived secret is needed. The job runs in a `publish` environment with the owner as a required reviewer, so every repository update waits for one more approval after the release itself.

**Protections.** Branch protection (or a ruleset) on `plugin-repository` allowing pushes only from GitHub Actions and the owner; no deletion; linear history. Release publishing itself is restricted to maintainers by GitHub. The workflow never runs on pull requests, never uses `pull_request_target`, and pins actions by major version as the existing workflows do. Every input is validated before the single write.

## Rollback procedure

A bad release is undone at the repository, never by rewriting source history or moving tags.

1. On the `plugin-repository` branch, revert the commit that introduced the bad version (`git revert <sha>`, then push). `pluginmaster.json` now describes the previous version again. Run `validate-repository` on it first; the file is small enough to read.
2. Players who have not updated see the previous version; those who already updated keep the bad version, because Dalamud never downgrades. A fix must ship as a **higher** version (`0.1.7` after a bad `0.1.6`) through the normal release path.
3. Optionally mark the bad GitHub Release as a pre-release or add a note to it. Deleting its ZIP asset stops any remaining installs of that version, and the tag and the commits stay where they are.
4. Record what happened in `CHANGELOG.md` under the fixed version.

For a testing-slot problem, revert the same way: the previous stable entry (without the testing slot) is restored, and opted-in testers stop being offered the bad testing version.

## Manual emergency recovery

When the publish workflow does not exist yet, or GitHub Actions is unavailable, the repository can be produced and updated by hand from any machine with Git and the .NET 10 SDK.

1. Download `AetherFrame-<version>.zip` and `SHA256SUMS.txt` from the published GitHub Release, and check the hash (`Get-FileHash` or `sha256sum -c`).
2. Check out the release's tag and build the tool: `git checkout v<version>` and `dotnet build tools/AetherFrame.ReleaseTools --configuration Release`.
3. `validate-package --package <zip> --config distribution/repository.json --version-props Version.props --csproj AetherFrame/AetherFrame.csproj --changelog CHANGELOG.md --checksums SHA256SUMS.txt --commit "$(git rev-parse HEAD)" --tag v<version>`. `--commit` is what ties the downloaded DLL to the tag you checked out; without it any build of the same version passes.
4. `generate-repository --config distribution/repository.json --changelog CHANGELOG.md --last-update <the release's publish time, ISO 8601 with a zone> --stable-package <zip> --output pluginmaster.json` (add `--testing-package` or `--testing-exclusive` for the other shapes).
5. `validate-repository --repository pluginmaster.json --config distribution/repository.json --stable-package <zip> --changelog CHANGELOG.md`.
6. Check out `plugin-repository`, replace `pluginmaster.json`, commit with the version in the message, and push. Never force-push that branch.
7. Confirm in game: `/xlplugins` should list the version within a few minutes. If it does not, fetch the served URL in a browser and compare it with the committed file.

## What must never be committed

- Tokens, personal access tokens, deploy keys, or any secret. The workflows need none beyond GitHub's built-in token, and the tool never reads one.
- Local paths, user names, or anything from `%AppData%`. The tool fails a package whose manifest or `deps.json` contains a local path, and its summary carries file names only.
- `dist/`, `bin/`, `obj/`, or any built ZIP. They are ignored by Git.
- The live `pluginmaster.json` on `master`. It lives on `plugin-repository` only; `distribution/dry-run/pluginmaster.json` is a fixture with non-resolving links.
- A changed `pluginMasterUrl` in `distribution/repository.json` once anyone uses the repository. Changing it orphans every installed copy.

## Security review

The infrastructure was reviewed as though an attacker or an accidental bad release were trying to get through it. Findings and the resulting rules:

- **Malicious ZIP paths.** Entry names are checked for traversal, absolute paths, drive letters, backslashes, folders, directory entries, control characters, duplicates and case collisions before any entry is read. Only the three expected names pass.
- **Decompression size.** Entries are read with limits (64 MiB, 1 MiB) enforced during decompression, not from the declared size alone; at most 64 entries are considered.
- **Version confusion.** The version must agree between the DLL, the file version, the informational version, the manifest, `deps.json`, the ZIP file name, `Version.props`, the tag and the repository entry. Any disagreement fails.
- **API level drift.** The configured API level must match the manifest, the DLL's Dalamud reference and the `Dalamud.NET.Sdk` major version.
- **Stale or substituted artifacts.** The DLL's embedded commit must equal the commit being released (`--commit $GITHUB_SHA`), the project file must match the packaged manifest, and the future publish flow validates the *published* asset by digest rather than trusting a workflow artifact.
- **URL manipulation.** Download links come only from the reviewed template plus the version; every URL must be absolute https on a dotted host name and the default port, with no user information, query or fragment, and written in canonical form: its text is the address a client requests, so no `.` or `..` segment can make a link that reads as `github.com/richhiiee/AetherFrame/...` fetch from another repository. A download link must end in the package file name. Templates without a per-version part are refused.
- **JSON injection.** Text from the csproj (description, punchline) is serialized by System.Text.Json; a description containing quotes, braces, control characters or field-like text round-trips as text (tested). The reader rejects unknown and duplicate keys in the manifest, the configuration and each repository entry; `deps.json` is only searched for the project's own entries.
- **Accidental local path publication.** The manifest and `deps.json` may not contain local paths; the summary contains file names only; `dist/` is ignored.
- **Secrets.** No workflow step uses a secret; the tool reads no environment variables and never reads `%AppData%`.
- **GitHub Actions scope.** Both workflows default to `contents: read`; only the draft-release job (tags only, `release` environment) has `contents: write`. Pull requests run read-only with no secrets. All inputs to `run` steps come from GitHub-controlled values (`github.sha`, `github.ref_name`) or from `Version.props` after a regex check, and are passed through environment variables, not interpolated into scripts.
- **Supply chain.** The tool has no NuGet dependencies; its test project uses the same test packages at the same versions as the plugin's tests. Both projects have lock files and are covered by `dotnet restore --locked-mode`, so a changed dependency fails the Release workflow.
- **Reproducibility.** The repository metadata is byte-identical across builds of one version (tested against differing commits and ZIP bytes). `LastUpdate` is always an explicit input.
- **Command injection.** The tool launches no processes and evaluates nothing; file paths given on the command line are used as paths only.

Not addressed, deliberately: signing the ZIP or the metadata. Dalamud has no signature check for custom repositories; GitHub's TLS, release asset digests and `SHA256SUMS.txt` are what players and the publish flow can verify.

## Decisions still open

1. **Repository URL.** Proposed: a `plugin-repository` branch in this repository, served as `https://raw.githubusercontent.com/richhiiee/AetherFrame/plugin-repository/pluginmaster.json`. Alternatives: a GitHub Pages URL (`https://richhiiee.github.io/AetherFrame/pluginmaster.json`) or a separate repository. The choice is permanent once players use it; it is stored in `distribution/repository.json`.
2. **Channel mapping.** Whether a pre-release feeds the testing slot and a full release the stable slot, and whether the first custom-repository release (0.1.6) is stable, stable plus testing, or testing-exclusive for a closed test.
3. **Approvals.** Whether the `release` and future `publish` environments get a required reviewer (recommended: the owner).
4. **Branch protection** for `plugin-repository` once it exists.
5. **Folding `New-ReleasePackage.ps1` into the tool.** Today the script stages `dist/` and the tool re-checks it; one implementation would be simpler once the tool has run in CI for a few releases.
6. **Player-facing text.** `README.md` and `docs/Testing.md` describe the custom repository only in outline until the URL is live; the install steps for players belong there then.
