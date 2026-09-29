# AetherFrame's custom Dalamud repository

How AetherFrame reaches players without the official Dalamud plugin repository: a public custom repository, hosted from this GitHub repository, that Dalamud's plugin installer reads like any other. This page covers the format, the tooling that produces and checks it, how a release is validated and published, and how to undo a bad release.

**Status:** the publication system is implemented and tested, and **nothing is published to the repository yet**. The repository URL is approved ([Permanent repository URL](#permanent-repository-url)), but the `plugin-repository` branch doesn't exist, so the address answers 404 and no player can add it. v0.1.6 passed its in-game smoke test and its GitHub Release is published; the first publication waits for the owner's settings below ([First publication](#first-publication), [Decisions still open](#decisions-still-open)).

Principles this is built on: everything is local first, nothing in the plugin phones home, and the custom repository adds no networking to the plugin itself. Dalamud does the downloading, from GitHub Releases, exactly as it does for every plugin. GitHub stays the public source and release host. Approval into the official Dalamud repository is welcome but not required. No account or backend is involved in installing AetherFrame.

## In plain language

For the project owner. Everything below this section is the detail.

**What players will do.** Type `/xlsettings` in game, open **Experimental**, paste one address under **Custom Plugin Repositories**, and save. AetherFrame then shows up in `/xlplugins` next to every other plugin, with an **Install** button, and later updates itself the way other plugins do. That address is the *repository URL*. Once anyone has used it, it can never change: Dalamud only accepts updates from the exact address a plugin was installed from.

**What a release is.** A release is a version number (`0.1.6`), a section in `CHANGELOG.md` for it, and a Git tag `v0.1.6`. Pushing the tag makes GitHub build the plugin, run every test, check the package, and prepare a **draft** release that only maintainers can see. You download the ZIP from the draft, try it in game, and press **Publish** when it is good. Nothing reaches players before that button, and nothing reaches the plugin installer before the next step.

**What the repository is.** One small text file, `pluginmaster.json`, that says: this is AetherFrame, this is its version, this is what changed, and the ZIP is here (a link into the GitHub release you published). Dalamud reads that file. The tooling in this repository writes it from the release itself, so the file can't disagree with the ZIP, and refuses to write it when anything is off.

**How a release reaches the installer.** After publishing the GitHub Release, you run one more thing by hand: **Actions → Publish custom repository → Run workflow**, with the version and a channel. It checks the release from scratch, shows you exactly what would change, and waits for your approval. When you approve, it updates `pluginmaster.json` on its own branch, and within about five minutes players' installers see the new version. With **publish** unticked it only shows you what would happen.

**Undo.** If a release turns out bad, you run the same workflow with the previous good version and **rollback** ticked. Players who already updated keep the bad version until a fixed release with a higher number follows; Dalamud never downgrades. The source code, the tags and the old releases are never deleted or moved.

**Testing builds.** Dalamud has a built-in idea of testing builds: players who tick **Get plugin testing builds** and opt into AetherFrame's testing see a newer *testing* version before everyone else. The repository can carry a stable version and a testing version at the same time. The first release through the repository, 0.1.6, is planned as testing-only: only players with testing builds on see AetherFrame at all, until you promote a release to stable.

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

Every field below is written by `generate-repository` and `prepare-publication`. Nothing else is ever emitted, and a file with any other key fails validation.

| Field | Value | Comes from |
|---|---|---|
| `Author`, `Name`, `Punchline`, `Description`, `Tags`, `CategoryTags`, `IconUrl`, `ImageUrls`, `RepoUrl`, `ApplicableVersion`, `MinimumDalamudVersion`, `LoadRequiredState`, `LoadSync`, `LoadPriority`, `CanUnloadAsync`, `AcceptsFeedback`, `FeedbackMessage` | as built | the `AetherFrame.json` inside the release ZIP, which DalamudPackager writes from `AetherFrame.csproj` |
| `InternalName` | `AetherFrame` | the ZIP's manifest, which must equal `distribution/repository.json` |
| `AssemblyVersion` | `MAJOR.MINOR.PATCH.0` | the DLL inside the ZIP (which must equal the manifest, the file name, `Version.props` and the tag) |
| `DalamudApiLevel` | `15` | `distribution/repository.json`, which must equal the manifest, the DLL's Dalamud reference and `Dalamud.NET.Sdk` in the csproj |
| `Changelog` | the version's section | `CHANGELOG.md`, `## [MAJOR.MINOR.PATCH] - YYYY-MM-DD`; a publication reads it from the release's own tagged commit |
| `LastUpdate` | Unix seconds | a publication: the newest GitHub publish time of the releases the entry describes. `generate-repository`: `--last-update`, given explicitly (CI dry runs use the release commit's date). Never "now" |
| `DownloadLinkInstall`, `DownloadLinkUpdate`, `DownloadLinkTesting` | `https://github.com/QuietFoxLabs/AetherFrame/releases/download/v<version>/AetherFrame-<version>.zip` | the template in `distribution/repository.json` and the version |
| `IsHide` | `false` | fixed; a bad release is rolled back, not hidden |
| `TestingAssemblyVersion`, `TestingDalamudApiLevel`, `TestingChangelog`, `IsTestingExclusive` | see [Channels](#channels-stable-and-testing) | the testing package, when there is one |

The generated file for the current version, with download links pointed at a non-resolving `.invalid` host, is committed as [`distribution/dry-run/pluginmaster.json`](../distribution/dry-run/pluginmaster.json). It is the exact JSON Dalamud would read, apart from that host.

## Channels: stable and testing

Dalamud's model is one entry per plugin with a stable slot and an optional testing slot. Three shapes make sense, and both `generate-repository` and a publication produce only these:

| Shape | `generate-repository` | Who sees what |
|---|---|---|
| Stable only | `--stable-package <zip>` | Everyone sees and installs the stable version. |
| Stable plus testing | `--stable-package <zip> --testing-package <newer zip>` | Everyone sees the stable version. Players with testing builds on who opt into AetherFrame get the testing version instead. The testing version must be newer, or Dalamud ignores it. |
| Testing-exclusive | `--testing-exclusive --testing-package <zip>` | Only players with **Get plugin testing builds** on see the plugin at all; installing it opts them into its testing. Both slots carry the same version, and both are needed: an exclusive install downloads the testing slot. |

A publication asks for one thing: put version X in the testing or the stable channel. What that does depends on what is published now (`PublicationPlan`):

| Request | Published now | Result |
|---|---|---|
| testing X | nothing | testing-exclusive X |
| testing X | stable S (X newer) | stable S, testing X |
| testing X | testing-exclusive T, or stable S with testing T (X newer than T) | the same, with X in place of T |
| stable X | nothing | stable X |
| stable X | testing-exclusive T, or stable S with testing T | stable X; T stays for testers if it is newer than X, and is dropped otherwise |
| stable X | stable S (X newer) | stable X |

And the rules behind the table:

- **A first pre-release is testing-exclusive.** With no stable version yet, the testing channel is the whole repository, and only players with testing builds see AetherFrame.
- **Publishing to testing never touches stable.** A published stable version stays until a stable request replaces it.
- **Testing never goes below stable.** Players with testing builds always get at least the stable version, so a testing version older than stable is refused, and naming the stable version itself clears the testing slot (a rollback, below).
- **Stable takes only full releases.** The GitHub Release must not be marked as a pre-release. Promoting a release means two deliberate acts: edit the GitHub Release and untick **Set as a pre-release**, then run the workflow with the stable channel. Testing takes either kind.
- **No channel moves to an older version without rollback.** Dalamud never downgrades players who already updated, so an older version is refused unless the request says rollback; a rollback that would not move the channel back is refused too. Asking for what a channel already serves writes nothing.
- **The repository never becomes empty.** No request removes the last published version.

## What lives where

| Path | Purpose |
|---|---|
| [`distribution/repository.json`](../distribution/repository.json) | The explicit facts the metadata is derived from: internal name, API level, source repository URL, the repository URL players add, and the download URL template. Reviewed by hand; never derived from the build machine. The repository URL also decides the branch a publication writes. |
| [`distribution/dry-run/`](../distribution/dry-run/README.md) | The generated fixture for the current version and how to regenerate it. |
| [`distribution/plugin-repository/README.md`](../distribution/plugin-repository/README.md) | The README the publication branch carries next to `pluginmaster.json`, copied byte for byte. |
| [`tools/AetherFrame.ReleaseTools/`](../tools/AetherFrame.ReleaseTools/) | The tool: `validate-package`, `generate-repository`, `validate-repository`, `checksums`, `verify-checksums`, `plan-publication`, `prepare-publication`. Plain .NET, no packages, no Dalamud reference, no network, no AppData. |
| [`tools/AetherFrame.ReleaseTools.Tests/`](../tools/AetherFrame.ReleaseTools.Tests/) | Its tests, on packages, assemblies and GitHub-shaped release descriptions they build themselves in temporary directories, plus checks of the committed publication workflow. |
| [`.github/scripts/New-ReleasePackage.ps1`](../.github/scripts/New-ReleasePackage.ps1) | Stages `dist/` from DalamudPackager's `latest.zip`: the named ZIP, `SHA256SUMS.txt` and `release-notes.md`. Unchanged since 0.1.5; the tool re-checks its output with the full rule set. |
| [`.github/scripts/custom-repository.sh`](../.github/scripts/custom-repository.sh) | The git and GitHub side of a publication: reads the published file, fetches releases, writes the one commit. It decides nothing; the tool does. |
| [`.github/workflows/build.yml`](../.github/workflows/build.yml) | Every push and pull request: build, tests, tooling tests, the package check on the build's own `latest.zip`, and ShellCheck on the publication script. |
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | Tags and manual dry runs: the above plus staging, the staged-package check, repository metadata generation and a draft release (tags only). |
| [`.github/workflows/publish-custom-repository.yml`](../.github/workflows/publish-custom-repository.yml) | Publishing, by hand only: see [Publishing](#publishing). |
| branch `plugin-repository` (not created yet) | The live `pluginmaster.json` and the README, and nothing else, written only by the publication workflow. |

The live `pluginmaster.json` never lives on `master`. It has its own branch, `plugin-repository`, served as `https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json`, the approved permanent address ([Permanent repository URL](#permanent-repository-url)). That keeps generated data out of the source history, gives it its own history for auditing, and lets the workflow write it with the built-in `GITHUB_TOKEN`. The first publication creates the branch.

## Building and validating a release locally

From the repository root. Windows PowerShell 5.1 runs the staging script; the tool needs only the .NET 10 SDK.

```bash
dotnet restore AetherFrame.slnx --locked-mode
dotnet build AetherFrame.slnx --configuration Release --no-restore
dotnet test AetherFrame.Tests/AetherFrame.Tests.csproj --configuration Release --no-build
dotnet test tools/AetherFrame.ReleaseTools.Tests/AetherFrame.ReleaseTools.Tests.csproj --configuration Release --no-build
```

```powershell
./.github/scripts/New-ReleasePackage.ps1 -Version 0.1.6 -Destination dist
```

```bash
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- validate-package --package dist/AetherFrame-0.1.6.zip --config distribution/repository.json --version-props Version.props --csproj AetherFrame/AetherFrame.csproj --changelog CHANGELOG.md --checksums dist/SHA256SUMS.txt --commit "$(git rev-parse HEAD)" --summary dist/package-summary.json
```

```bash
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- generate-repository --config distribution/repository.json --changelog CHANGELOG.md --last-update "$(git log -1 --format=%cI HEAD)" --stable-package dist/AetherFrame-0.1.6.zip --output dist/pluginmaster.json
```

Add `--dry-run` instead of `--output` to print the document without writing anything. `--tag v0.1.6` adds the tag check. `validate-repository --repository <file> --config distribution/repository.json [--stable-package <zip>] [--testing-package <zip>] [--changelog CHANGELOG.md]` checks an existing file. `checksums --output SHA256SUMS.txt <files>` and `verify-checksums --checksums SHA256SUMS.txt` write and check checksum files.

Every command prints one line per check (`[ OK ]` or `[FAIL]`) and ends with `Package OK`, `Repository metadata OK`, `Checksums OK`, `Plan OK`, `Publication prepared`, `Nothing to publish` or `FAILED: n check(s) failed`. Exit codes: 0 all checks passed, 1 a check failed, 2 wrong usage. A run that fails writes nothing.

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

**The repository document** (`validate-repository`, also run on everything `generate-repository` and `prepare-publication` write)
- A JSON array with exactly one object, with known keys only.
- The entry's identity, API level, source URL and `ApplicableVersion` equal the configuration; versions are canonical `MAJOR.MINOR.PATCH.0` text; `LastUpdate` is Unix seconds between 2020 and 2100; `IsHide` is false; load and feedback flags are present.
- Every download link is exactly what the configured template gives for that version (https on a dotted host name, default port, no query, no user information, written in canonical form, ending in the package file name).
- The testing slot is coherent: absent entirely, a newer version with `TestingDalamudApiLevel` and its own link, or a testing-exclusive entry with one version, one link and one changelog.
- With the packages at hand: the entry describes exactly them, field by field (a testing-exclusive entry is compared with its package whether it is given as the stable or the testing package). With `--changelog`: its changelog fields are the current sections.

**A published release** (`prepare-publication`, for every release a publication describes, including one that is already published and stays)
- The GitHub Release is published: not a draft, it has a publish time, and its page is `https://github.com/QuietFoxLabs/AetherFrame/releases/tag/v<version>`. The workflow fetches it from GitHub's by-tag endpoint, which does not return drafts at all; the tool checks the `draft` flag again.
- It has exactly two assets, `AetherFrame-<version>.zip` and `SHA256SUMS.txt`, uploaded, each with GitHub's `sha256:` digest, and served from the addresses the entry links to.
- The downloads are exactly those bytes (size and SHA-256 equal GitHub's digest), and `SHA256SUMS.txt` lists the ZIP's actual hash.
- The tag `v<version>` is annotated, and `master` contains the commit it points at.
- `Version.props` at the tagged commit sets the version, and the package passes every rule above with `--version-props`, `--csproj` and `--changelog` read from the tagged commit, `--checksums`, `--tag`, and `--commit` set to the tagged commit: the DLL was built from exactly that commit.
- In the stable slot: the release is a full release, not a pre-release.
- Recorded, not required: whether GitHub marks the release immutable.

What is not enforced: byte-identical ZIPs across builds. The DLL itself is reproducible (`DotNet.ReproducibleBuilds`), but DalamudPackager stamps each ZIP entry with the build time, so two builds of one commit differ in the ZIP bytes. The repository metadata is independent of that: it is byte-identical across builds of one version.

## Checksums

`New-ReleasePackage.ps1` writes `dist/SHA256SUMS.txt` in `sha256sum` format (`<64 lowercase hex>  <file name>`, LF line ends), listing the release ZIP. The Release workflow attaches it to the draft release next to the ZIP, and the release notes repeat the hash. The tool's `checksums` command writes the same format for any set of files, sorted by name so the output is deterministic, and `verify-checksums` checks a file against the files beside it. `validate-package --checksums` requires the package's line to match.

`package-summary.json`, written by `validate-package --summary`, records the package's SHA-256 and the SHA-256 of each of the three files inside it, including `AetherFrame.dll`, so a DLL loaded in game can be traced to a release with `Get-FileHash`.

Players verify a download with `Get-FileHash .\AetherFrame-0.1.6.zip` (PowerShell) or `sha256sum -c SHA256SUMS.txt`. GitHub also publishes a `digest` for every release asset; a publication requires that digest, the downloaded bytes and `SHA256SUMS.txt` to agree before it describes a release.

## CI

Nothing in CI publishes anything or needs a secret beyond the built-in token.

**Build and test** (`build.yml`, every push and pull request to `master`, Windows and Linux, `contents: read`): locked restore is implied by the SDK, Release build of the whole solution, the plugin's tests against the built DLL, the tooling's tests (among them the publication rules, the checks on the committed publication workflow, and the check that the build's own `latest.zip` regenerates the committed fixture), `validate-package` on that `latest.zip` with `--version-props`, `--csproj`, `--changelog` and `--commit`, and on Linux, ShellCheck on the publication script. Pull requests from forks run with read-only permissions and no secrets, and the tool only ever reads the checkout. Pushes to `master` run it too: those runs belong to the merge commit, not to the pull request, so the pull request's checks list does not show them; they are under **Actions** and on the commit.

**Release** (`release.yml`, tag pushes and **Run workflow**, Windows): tag check (tags only), `dotnet restore --locked-mode`, Release build, both test suites, `New-ReleasePackage.ps1`, `validate-package` on the staged ZIP with `--checksums`, `--commit`, `--tag` (tags only) and `--summary`, then `generate-repository` in the stable and the testing-exclusive shapes, `validate-repository` and `verify-checksums`. Everything lands in the `AetherFrame-<version>` artifact for 30 days: the ZIP, `SHA256SUMS.txt`, `release-notes.md`, `package-summary.json`, `pluginmaster.json`, `pluginmaster.testing-exclusive.json`. The step summary shows the checksums. The `draft-release` job (tags only, `contents: write`, `release` environment) still attaches only the ZIP and `SHA256SUMS.txt` to a draft pre-release and never replaces an existing release.

**Publish custom repository** (`publish-custom-repository.yml`): by hand only. [Publishing](#publishing).

## Publishing

`publish-custom-repository.yml` puts a published GitHub Release into the testing or the stable channel, or rolls a channel back. It is the only way the served file changes.

1. You publish the GitHub Release (the draft the Release workflow made), by hand.
2. You run **Actions → Publish custom repository → Run workflow** on `master`, by hand.
3. **prepare** (read-only) reads the published `pluginmaster.json`, plans the change, fetches and verifies every release the new file will describe, generates the file, validates it against those releases, and shows the change in the run summary.
4. With **publish** ticked, **publish** waits for approval in the `custom-repository` environment.
5. **publish** derives everything again from scratch, requires it to be exactly what prepare showed, and pushes one commit to `plugin-repository`.
6. `raw.githubusercontent.com` serves the new file; Dalamud installers see it within about five minutes.

### The workflow

It has one trigger, `workflow_dispatch`: no push, pull request, release, schedule or other trigger starts it, it never runs for pull requests, and it doesn't use `pull_request_target`.

| Input | Values | Meaning |
|---|---|---|
| `version` | `MAJOR.MINOR.PATCH` | The release to publish. Its GitHub Release must be published; a draft is always refused. |
| `channel` | `testing` (default), `stable` | The channel to put it in ([Channels](#channels-stable-and-testing)). |
| `rollback` | off (default), on | Allow moving the channel to an older version than it serves now. |
| `publish` | off (default), on | Off: a dry run that checks and prepares everything and writes nothing. On: publish, after approval. |

Both jobs run on `ubuntu-24.04`, and nothing runs twice on different systems. The tool is cross-platform .NET, bash, git and the GitHub CLI are native there, and a Linux checkout keeps the files' LF line ends. Build and test keeps testing the tool on Windows and Linux.

### Jobs and permissions

- The workflow's default token permission is `contents: read`.
- **prepare** runs with it: the GitHub API and release downloads, read-only. Checkout keeps no credentials.
- **publish** is the only job with `contents: write`, and asks for nothing else. It runs only when **publish** is ticked, prepare found a change, and the run comes from `master`; and it runs in the `custom-repository` environment, which is where the approval comes from ([Protecting the repository](#protecting-the-repository)). The token reaches git for the one push, through the GitHub CLI's credential helper, and is never written to disk.
- No job asks for `issues`, `pull-requests`, `packages`, `actions`, `id-token`, `pages` or any other permission.
- Every action is pinned to a commit: `actions/checkout` v5.1.0, `actions/setup-dotnet` v5.4.0, `actions/upload-artifact` v4.6.2, the versions the other workflows' `@v5` and `@v4` resolve to today.
- Inputs reach the shell only as environment variables, never spliced into a script, and each is validated before it is used: the version and channel by the tool, the rollback flag by the script. `PublicationWorkflowTests` fail the build if any of this changes.

### One reviewed, atomic write

- Nothing is written until every check has passed. The tool writes its output directory whole or not at all.
- The publish job does not reuse prepare's files. It derives everything again from scratch and requires three things to match what the run summary showed: the branch is still at the commit prepare read, `pluginmaster.json` is byte-identical, and so is the publication record (`summary.json`, which names every release's id, publish time and package SHA-256). The file alone would not be enough: it names each release's download address, not its hash, so a different build of the same release gives the same file. Whatever changed in between (a release, an asset, the branch) stops the publication.
- The commit is built with git plumbing, and its tree holds exactly `pluginmaster.json` and `README.md`. No source, build output, ZIP, secret or temporary file can reach the branch, whatever else is in the checkout.
- It is pushed as a plain fast-forward, never forced: git refuses the push if the branch moved since it was read, and for the first publication if the branch appeared meanwhile. A failed run leaves the published file exactly as it was.
- After the push, the job checks that the branch points at the new commit and that the pushed file has the approved SHA-256.

### One run at a time

The workflow's concurrency group, `publish-custom-repository`, never cancels a run in progress, so only one publication runs at a time and none stops halfway. A run waiting for approval holds the group, and GitHub keeps at most one more run pending behind it (a newer one replaces it). Approve, reject or cancel a waiting run so it doesn't block the next; GitHub ends a run that has waited 30 days.

### What a run produces

- **The run summary**: before and after, the old and new `pluginmaster.json` SHA-256, the fields that change, and every release checked, with its page, id, publish time, kind, tagged commit and package hash. This is what you review before approving.
- **The artifact** `custom-repository-<run id>` (30 days): `branch/pluginmaster.json`, `branch/README.md`, `summary.json`, `report.md`, `commit-message.txt`.
- **When published, the commit on `plugin-repository`**. Its message is the permanent record: what was requested, the state before and after, the previous and new `pluginmaster.json` SHA-256, the previous commit, each release's URL, id, publish time, kind, immutability, tagged commit and package hash, and the workflow run. The publish job's summary names the new commit.

Nothing in the record comes from players' data or the development machine: releases, hashes, versions and GitHub addresses only.

### Running it outside Actions

The script works anywhere with git, the GitHub CLI logged in, and the .NET 10 SDK, which is also how it was tested. From a fresh clone of `master` (it fetches tags and branches into the clone it runs in):

```bash
dotnet build tools/AetherFrame.ReleaseTools --configuration Release
RELEASE_TOOLS=tools/AetherFrame.ReleaseTools/bin/Release/net10.0/AetherFrame.ReleaseTools.dll PUBLICATION_BRANCH=plugin-repository DEFAULT_BRANCH=master GITHUB_REPOSITORY=QuietFoxLabs/AetherFrame VERSION=0.1.6 CHANNEL=testing ROLLBACK=false bash .github/scripts/custom-repository.sh prepare ../publication-preview
```

`prepare` only reads, fetches and prepares; `../publication-preview/publication/report.md` shows what a run would change. `publish` and `commit` push, and belong to [Manual emergency recovery](#manual-emergency-recovery).

## Rollback

A bad release is undone by publishing a known good state with the same workflow, which verifies that state again and records it like any publication. Not by reverting publication commits by hand, force-pushing `plugin-repository`, moving or deleting tags, deleting releases, or rewriting `master`. (Reverting a *hand edit* of the branch is different: see [Failure recovery](#failure-recovery).)

Run the workflow with the good version, the channel, and **rollback** ticked (then **publish**, and approve):

| Situation | version | channel | Result |
|---|---|---|---|
| A bad testing version while stable S is published | S | testing | stable S only: testers get S again |
| A bad testing version T, with an earlier testing version T0 still newer than stable | T0 | testing | stable S, testing T0 |
| A bad testing-exclusive version, with an earlier one T0 | T0 | testing | testing-exclusive T0 |
| A bad stable version, with a good earlier stable S0 | S0 | stable | stable S0; a newer testing version stays |

- A rollback regenerates the file from the verified releases, with every check a publication runs, rather than restoring old bytes. Rolling back to what was published before gives back the earlier file byte for byte.
- The release rolled back to must still be a published GitHub Release (a full release for stable).
- A rollback can't take an update back. Dalamud never downgrades, so players who already updated keep the bad version until a fixed, higher version is published (0.1.7 after a bad 0.1.6). A rollback stops the bad version from reaching more players.
- Nothing removes the last published version. If the only published version is bad, such as the first, testing-exclusive 0.1.6, publish a fixed higher version as soon as possible.
- Record what happened in `CHANGELOG.md` under the fixed version.

## Failure recovery

A failed run writes nothing; the published file stays as it was. What the error means:

| The run says | Why | What to do |
|---|---|---|
| GitHub has no published release for `v<version>` | The release is still a draft, or the version is wrong | Publish the GitHub Release first, or check the version |
| `published`: the release is a draft | Same, caught by the tool | Same |
| `full release`: marked as a pre-release | A pre-release was asked for the stable channel | To promote it, untick **Set as a pre-release** on the GitHub Release, then run again |
| `plan`: … is older than the … the channel serves now | An accidental downgrade | Check the version; tick **rollback** only if you mean it |
| `plan`: nothing to roll back / only moves a channel to an older version | **rollback** ticked for something that is not one | Untick it |
| `published file: …` | The published `pluginmaster.json` was edited by hand or damaged | Find the edit in the branch's history (`git log origin/plugin-repository`), undo it with `git revert` and a normal push (owner only, never force-push), then run again |
| … is at X, not at the Y the approved run prepared from | The branch changed while the run waited for approval | Run the workflow again and review the new result |
| The pluginmaster.json, or the publication record, derived now is not the one the approved run showed | A release or its assets changed while the run waited | Run again and review the new result |
| The push was refused | The branch moved between the last check and the push | Run again |
| Publishing runs only from master | The run was started from another branch | Start it from `master` |
| The run waits and nothing happens | The `custom-repository` environment requires approval | **Review deployments** on the run page: approve or reject |
| Dalamud still shows the old version | `raw.githubusercontent.com` caches the file for up to five minutes | Wait, then compare the served file's SHA-256 (`curl -sS <repository URL> \| sha256sum`) with the new one in the run summary |

## Manual emergency recovery

Only when GitHub Actions is unavailable and a publication or rollback can't wait. The owner, from a fresh clone of `master`, with the GitHub CLI logged in and the .NET 10 SDK:

1. Run `prepare` exactly as in [Running it outside Actions](#running-it-outside-actions), with the version, channel and `ROLLBACK` you need, into a new directory `<work>`.
2. Read `<work>/publication/report.md`. `<work>/outputs` holds `base` (the branch commit it was prepared from, or `none`) and `sha256`.
3. `PUBLICATION_BRANCH=plugin-repository EXPECTED_SHA256=<sha256> bash .github/scripts/custom-repository.sh commit <work>/publication <base>`. It writes the same single commit the workflow would, from the same two files, and pushes it as a fast-forward. With the rulesets below, the push needs the owner's bypass.
4. Confirm in game: `/xlplugins` lists the version within a few minutes. If not, compare the served file with the committed one.

Never edit `pluginmaster.json` by hand and never force-push the branch: a hand-edited file stops every later publication until it is reverted.

## What must never be committed

- Tokens, personal access tokens, deploy keys, or any secret. The workflows need none beyond GitHub's built-in token, and the tool never reads one.
- Local paths, user names, or anything from `%AppData%`. The tool fails a package whose manifest or `deps.json` contains a local path, and its summaries carry file names, releases and hashes only.
- `dist/`, `bin/`, `obj/`, or any built ZIP. They are ignored by Git.
- The live `pluginmaster.json` on `master`. It lives on `plugin-repository` only; `distribution/dry-run/pluginmaster.json` is a fixture with non-resolving links.
- A hand edit on `plugin-repository`. Only the workflow writes it.
- A changed `pluginMasterUrl` in `distribution/repository.json` once anyone uses the repository. Changing it orphans every installed copy.

## Permanent repository URL

`https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json`

The owner approved `https://raw.githubusercontent.com/richhiiee/AetherFrame/refs/heads/plugin-repository/pluginmaster.json` on 2026-09-27. On 2026-09-29 the owner moved the repository to the QuietFoxLabs organization and said to use it from then on, so the address above replaced it. Installations made from the `richhiiee` address keep working only through GitHub's redirect for transferred repositories (checked 2026-09-29: the old address answers 200 with the same file). That redirect ends if a repository named `AetherFrame` is ever created or forked under `richhiiee`.

It is `pluginMasterUrl` in `distribution/repository.json`, and the publication writes the branch it names, `plugin-repository`. It is not active yet: the branch doesn't exist until the first publication, and until then the address answers 404. Once players use it, it never changes. Dalamud offers a plugin's updates only from the exact address it was installed from, so every installation made from this address depends on it for good. `DistributionTests` fails if the configured address changes, so a change can only ever be deliberate.

**This is the GitHub compatibility endpoint.** A shorter, branded address, such as `https://repo.aetherframe.app/pluginmaster.json`, may be introduced later. If that happens:

- This address stays `pluginMasterUrl`, and the publication keeps writing the branch it serves. It remains the endpoint of every installation made from it, for as long as that is practical.
- The branded address must serve the same file as this one, for example by redirecting to it or as a copy written by the same publication, so that both always offer the same versions. Check that Dalamud's installer reads it before announcing it.
- Players who installed from this address keep getting updates without doing anything. Moving an installed copy to the branded address means reinstalling it from there.
- Players should add one of the two addresses, not both.
- A branded address, once announced, is permanent too, for the same reason.

The branded address is not configured anywhere, and no DNS or hosting exists for it.

Why this address (checked 2026-09-27, against the live service):

- **How GitHub serves it.** `raw.githubusercontent.com` serves a branch's file as `text/plain; charset=utf-8`, with `Cache-Control: max-age=300`, through a CDN. Dalamud reads the body as JSON whatever the content type, so `text/plain` is fine. A new publication reaches every player within about five minutes; so does a rollback.
- **The `refs/heads/` form.** `…/AetherFrame/<ref>/pluginmaster.json` and `…/AetherFrame/refs/heads/<branch>/pluginmaster.json` serve the same file, but the short form accepts tags as well as branches (`…/AetherFrame/v0.1.6/Version.props` answers 200), so a tag ever named `plugin-repository` would make a short address ambiguous. The approved `refs/heads/` form can only mean the branch.
- **Availability.** The same GitHub service that hosts the release ZIPs, which Dalamud downloads anyway. If GitHub is down, installs fail either way.
- **GitHub Pages** (`https://quietfoxlabs.github.io/AetherFrame/pluginmaster.json`) would serve `application/json` and needs Pages switched on and a deployment step: either Pages building from the branch, or a deployment job with `pages: write` and `id-token: write`. That is more moving parts, and it is no more reliable: it is the same GitHub, and it is tied to the same account and repository names. Only a custom domain, such as the branded address above, makes an address independent of GitHub.
- **Permanence.** The address contains the owner name, the repository name and the branch name. Never rename any of them; don't rely on GitHub's redirects for this. The owner's move to QuietFoxLabs (above) is the one exception, and installations from the old address now depend on that redirect.

## Protecting the repository

None of this is set up yet: the repository has no rulesets and no branch protection, and the `custom-repository` environment doesn't exist. These are owner actions in the GitHub settings. The repository is public, so all of them are available on GitHub Free.

**1. The `custom-repository` environment. Required before the first publication.** Settings → Environments → **New environment**, name `custom-repository`:
- **Required reviewers**: add `richhiiee`. Leave **Prevent self-review** unticked: you start the runs yourself, and with it ticked nobody could approve them.
- **Deployment branches and tags**: **Selected branches and tags**, add the branch `master`. A copy of the workflow on any other branch can then never use the environment.
- **Allow administrators to bypass configured protection rules**: untick it, so every publication waits for an explicit approval.
- No wait timer, no secrets, no variables.

If the environment is missing when a run with **publish** ticked reaches the publish job, GitHub creates it without any protection and the job runs without waiting. That's why **publish** is off by default: dry runs never touch the environment.

Optionally, give the existing `release` environment (the Release workflow's draft job) the same required reviewer.

**2. Rulesets.** Settings → Rules → Rulesets.
- **`master`** (New branch ruleset; target: the default branch; enforcement: Active): **Restrict deletions**; **Block force pushes**; **Require a pull request before merging** with 0 required approvals; **Require status checks to pass** with `build (windows-2022)` and `build (ubuntu-24.04)`. Put Repository admin on the bypass list, so that you can't lock yourself out.
- **`plugin-repository`** (New branch ruleset; target: include by pattern `plugin-repository`): **Restrict deletions**, **Block force pushes**, **Require linear history**. The workflow's pushes satisfy all three. Don't add **Require signed commits** (the workflow's commits are unsigned) or **Restrict updates**, unless you have checked that the workflow can still push (GitHub Actions itself must then be on the bypass list).
- **Release tags** (New tag ruleset; target: include by pattern `v*`): **Restrict updates** (a release tag can never be moved), **Restrict deletions**, **Block force pushes**. Creating new tags stays allowed. A publication refuses a release whose DLL wasn't built from the tag's commit, so a moved tag could never be published anyway; this stops the move itself.

**3. Immutable releases.** Settings → Releases → **Enable release immutability**. Once a release is published, its assets can't be modified or deleted and its tag can't be moved or deleted; its title, notes and pre-release flag can still be edited, so promotion to stable still works. GitHub's documentation says it applies only to future releases. v0.1.6 was published before it was turned on, so it isn't covered, and its publication record will say `not immutable`; turning it on now covers the releases after it. `gh api repos/QuietFoxLabs/AetherFrame/releases/tags/<tag> --jq .immutable` shows whether a release is immutable, and every publication record says so too. The trade-off: a bad release's ZIP can never be removed or replaced. Nothing here depends on doing that.

Sources: GitHub Docs, "Immutable releases" (`/code-security/supply-chain-security/understanding-your-software-supply-chain/immutable-releases`) and "Preventing changes to your releases" (`/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes`), read 2026-09-27.

## First publication

The order for v0.1.6, whose in-game smoke test has passed. Nothing reaches the plugin installer before step 5.

1. **Set up the protections**: at least the `custom-repository` environment; ideally also the rulesets and immutable releases ([Protecting the repository](#protecting-the-repository)).
2. **Have the workflow on `master`**: it runs only from there, so the pull request that adds it must be merged first.
3. **The GitHub Release** "AetherFrame 0.1.6": done. It was published on 2026-09-27T23:58:31Z as a pre-release, with its two assets unchanged (`AetherFrame-0.1.6.zip`, SHA-256 `6c6e708b…ad23`). For later releases: open the draft on GitHub, keep **Set as a pre-release** ticked for the testing channel, and press **Publish release**.
4. **Dry run**: **Actions → Publish custom repository → Run workflow**, branch `master`, version `0.1.6`, channel `testing`, **rollback** and **publish** unticked. The run summary should say *Publish 0.1.6 to testing*, *nothing published → testing-exclusive 0.1.6*, and list v0.1.6 as a pre-release with tagged commit `76e53963…` and package SHA-256 `6c6e708b…`.
5. **Publish**: the same inputs with **publish** ticked. When the run pauses, check its summary says the same as the dry run, then **Review deployments → custom-repository → Approve and deploy**. The publish job writes the first commit of `plugin-repository`, which creates the branch.
6. **Check what is served**: a few minutes later, `curl -sS https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json | sha256sum` gives the SHA-256 in the run summary.
7. **Install**: in game, `/xlsettings` → Experimental: tick **Get plugin testing builds**, add `https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/refs/heads/plugin-repository/pluginmaster.json` under Custom Plugin Repositories, enable it, save. Remove the AetherFrame dev plugin location first; Dalamud can't load both. `/xlplugins` lists AetherFrame 0.1.6; install it, and check `/af version` says 0.1.6 and commit `76e5396`.
8. **Confirm updates**: the next release goes through the same steps, and the installed copy should be offered it. Only then tell other players the URL, and update `README.md` and `docs/Testing.md` with it.

## Security review

The infrastructure was reviewed as though an attacker or an accidental bad release were trying to get through it. Findings and the resulting rules:

- **Malicious ZIP paths.** Entry names are checked for traversal, absolute paths, drive letters, backslashes, folders, directory entries, control characters, duplicates and case collisions before any entry is read. Only the three expected names pass.
- **Decompression size.** Entries are read with limits (64 MiB, 1 MiB) enforced during decompression, not from the declared size alone; at most 64 entries are considered.
- **Version confusion.** The version must agree between the DLL, the file version, the informational version, the manifest, `deps.json`, the ZIP file name, `Version.props`, the tag and the repository entry. Any disagreement fails.
- **API level drift.** The configured API level must match the manifest, the DLL's Dalamud reference and the `Dalamud.NET.Sdk` major version.
- **Stale or substituted artifacts.** The DLL's embedded commit must equal the commit being released, the project file must match the packaged manifest, and a publication validates the *published* asset, downloaded from the release, never a workflow artifact.
- **Draft releases.** Refused twice: the workflow reads releases from GitHub's by-tag endpoint, which does not return drafts, and the tool refuses a release whose `draft` flag is set, which has no publish time, or whose address isn't the tagged release page.
- **Release asset and checksum trust.** Every asset is downloaded and hashed; GitHub's digest, the download and `SHA256SUMS.txt` must agree, and the release may carry nothing but the ZIP and `SHA256SUMS.txt`. The root of trust is the annotated tag on `master` and the commit the DLL says it was built from; checksums bind the bytes to that.
- **Tag trust.** The tag is fetched from GitHub as it is there, must be annotated and on `master`, and the DLL must have been built from its commit, so a moved tag fails. The tag ruleset keeps tags from moving at all.
- **Published file trust.** The current `pluginmaster.json` must pass every structural check, and only its versions are used. Everything in a new file is regenerated from verified releases. A damaged or hand-edited file stops the publication.
- **URL manipulation.** Download links come only from the reviewed template plus the version; every URL must be absolute https on a dotted host name and the default port, with no user information, query or fragment, and written in canonical form: its text is the address a client requests, so no `.` or `..` segment can make a link that reads as `github.com/QuietFoxLabs/AetherFrame/...` fetch from another repository. A download link must end in the package file name. Templates without a per-version part are refused. The release's own asset addresses must equal the links the entry will carry. The publication branch is derived from `pluginMasterUrl` and must be a branch root of this repository, never `master` or `main`.
- **JSON injection.** Text from the csproj (description, punchline) is serialized by System.Text.Json; a description containing quotes, braces, control characters or field-like text round-trips as text (tested). The reader rejects unknown and duplicate keys in the manifest, the configuration, the tag facts and each repository entry; `deps.json` is only searched for the project's own entries; a GitHub release description is read field by field with type checks and a size limit.
- **Workflow injection.** Inputs reach the shell only as environment variables; no `${{ }}` expression appears in any script (tested); the version, channel and rollback flag are validated before use; branch names are fixed or come from the reviewed configuration and are checked against a strict pattern.
- **Path traversal.** Release files are stored under names made from the configuration and the version, never from GitHub's asset names, and the tool reads only those.
- **Time-of-check to time-of-use.** The publish job derives everything again right before the push and requires the branch commit, the file and the whole publication record to match what was approved; the push only fast-forwards from that commit. What remains is the few seconds between the final verification and the push, during which a release asset could still be swapped; immutable releases close that too.
- **Concurrency and partial writes.** One run at a time, never cancelled halfway. A publication is one commit and one ref update; the branch serves either the old or the new file, never a mixture.
- **Accidental local path publication.** The manifest and `deps.json` may not contain local paths; summaries and publication records contain file names, releases and hashes only; `dist/` is ignored.
- **Secrets.** No workflow uses a secret; the tool reads no environment variables and never reads `%AppData%`. The publication's token is never written to disk, and reaches git only for the push.
- **GitHub Actions scope.** All workflows default to `contents: read`. Only the draft-release job (tags only, `release` environment) and the publication's publish job (by hand, `master` only, `custom-repository` environment) have `contents: write`. Pull requests run read-only with no secrets, and never start a release or a publication.
- **Supply chain.** The tool has no NuGet dependencies; its test project uses the same test packages at the same versions as the plugin's tests. Both projects have lock files and are covered by `dotnet restore --locked-mode`. The publication workflow pins every action to a commit. CI runs ShellCheck on the publication script.
- **Reproducibility.** The repository metadata is byte-identical across builds of one version (tested against differing commits and ZIP bytes), and a publication is byte-identical across runs with the same releases (tested). `LastUpdate` is never "now".
- **Command injection.** The tool launches no processes and evaluates nothing; file paths given on the command line are used as paths only.

Residual risks, accepted and stated:

- Anyone with write access can change `master`, and so the workflow, or push to `plugin-repository` with a workflow of their own. Today that is only the owner. The environment's branch rule and required reviewer, and the rulesets, narrow it. If collaborators with write access are ever added, consider a deploy key held only as a `custom-repository` environment secret, and a `plugin-repository` ruleset that lets only that key update the branch.
- A missing environment is created by GitHub without protection (see above); **publish** defaults to off for that reason.
- The repository must stay public: Dalamud fetches the file and the ZIPs anonymously.

Not addressed, deliberately: signing the ZIP or the metadata. Dalamud has no signature check for custom repositories; GitHub's TLS, release asset digests and `SHA256SUMS.txt` are what players and the publication can verify.

## Decisions still open

The repository URL is decided ([Permanent repository URL](#permanent-repository-url)). Still open:

1. **The `custom-repository` environment** with the owner as required reviewer and `master` as its only branch. Needed before the first publication.
2. **Rulesets** for `master`, `plugin-repository` and release tags ([Protecting the repository](#protecting-the-repository)).
3. **Immutable releases** for the releases after 0.1.6, which was published without it.
4. **When to promote to stable.** 0.1.6 starts testing-exclusive; stable stays empty until a release is deliberately promoted.
5. **A branded short address**, later and optional. It must serve the same file, and the GitHub address stays the compatibility endpoint ([Permanent repository URL](#permanent-repository-url)).
6. **A Dalamud API level change.** The configuration has one API level for both slots, so the first publication after an API change must replace both slots with releases built for the new level. Plan it when it comes.
7. **Folding `New-ReleasePackage.ps1` into the tool.** Today the script stages `dist/` and the tool re-checks it; one implementation would be simpler once the tool has run in CI for a few releases.
8. **Player-facing text.** `README.md` and `docs/Testing.md` describe the custom repository only in outline until the URL is live; the install steps and the address belong there then.
