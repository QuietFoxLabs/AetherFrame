# Dry-run repository fixture

**Generated test data. Not a repository anyone should add to Dalamud.**

`pluginmaster.json` here is exactly what `AetherFrame.ReleaseTools generate-repository` writes for the current version, with one difference: every download link points at `https://dry-run.invalid/...` instead of `https://github.com/QuietFoxLabs/AetherFrame/releases/download/...`. The `.invalid` top-level domain never resolves, so this file can't install anything even if it were served by mistake. Everything else (names, description, tags, versions, API level, load flags, changelog) is what Dalamud would read from the real repository.

Use it to see what Dalamud will consume before anything is published. The full explanation of the format and of every field is in [docs/CustomRepository.md](../../docs/CustomRepository.md).

## Regenerating

The fixture is checked by the tooling tests: it must describe the version in `Version.props`, validate against `distribution/repository.json`, carry the current `CHANGELOG.md` section, and be byte-for-byte what the tool generates from the plugin's own Release build. After a version bump, or after editing the released version's CHANGELOG section or the manifest fields in `AetherFrame.csproj`, regenerate it from the repository root:

```bash
dotnet build AetherFrame.slnx --configuration Release
dotnet run --project tools/AetherFrame.ReleaseTools --configuration Release --no-build -- generate-repository --config distribution/repository.json --changelog CHANGELOG.md --last-update "$(git log -1 --format=%cI HEAD)" --stable-package AetherFrame/bin/x64/Release/AetherFrame/latest.zip --download-url-template "https://dry-run.invalid/QuietFoxLabs/AetherFrame/releases/download/v{version}/{package}" --output distribution/dry-run/pluginmaster.json
```

`--last-update` is the release instant the entry shows; for the fixture it is the commit date of the version's tag (`git log -1 --format=%cI v0.1.6`). Any fixed instant works, as long as it is given explicitly: the tool never uses the current time, so the output stays reproducible.
