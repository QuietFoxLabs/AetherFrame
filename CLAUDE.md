# AetherFrame: rules for Claude

Since September 29, 2026, Claude runs this project under the owner's delegation: it plans, decides OPEN items, builds, reviews, merges and releases to the testing channel. The owner tests in game. Read [ROADMAP.md](ROADMAP.md) and [docs/process/AUTOPILOT.md](docs/process/AUTOPILOT.md) before any task. Live GitHub and pull request state beats their snapshot tables.

- **Off limits:** `E:\Plugin development`, the old primary checkout with dirty work, stashes and worktrees. No edits there, and no git commands. One exception, permitted by the owner on September 29, 2026: a session whose working directory already is one of Claude's own worktrees under `E:\Plugin development\AetherFrame\.claude\worktrees\` may keep working in that worktree if it held no uncommitted changes before the session's own edits, on that worktree's own `claude/*` branch. There it may fetch, merge `origin/master`, build, test, commit and push with an explicit refspec (`git push origin HEAD:refs/heads/claude/<slug>`), and nothing else: never `git stash`, `reset`, `clean`, `switch`, `checkout` or `worktree` commands, never the primary checkout root or another worktree, and every new task starts in `E:\AetherFrameWork`. The deny rules in `.claude/settings.json` still block the old tree for sessions in `E:\AetherFrameWork`.
- **Where work happens:** the control checkout `E:\AetherFrameWork` stays clean on `master`. Do each task in a worktree under `.claude/worktrees/`, with one branch `claude/<slug>` and one pull request.
- **Git limits:** never force-push, rewrite pushed history, push to `master` directly, or delete branches, tags or releases.
- **Merging and releasing:** merge only through the merge gate in AUTOPILOT.md. Release only a build the owner passed in game.
- **Decisions:** record each one with its date, scope and rationale, marked "APPROVED (Claude, under the owner's delegation of September 29, 2026)". Never present a delegated decision as the owner's. CONFIRMED requirements and the owner's own approvals change only on the owner's word.
- **Owner only:** anything needing accounts, payments, credentials, secrets or repository settings. Ask in the Owner inbox issue.
- **Done means:**
  - 0 build warnings and every test suite passing;
  - CI green on the exact head and a clean independent review;
  - the tested commit named in the pull request, and ROADMAP.md updated;
  - an **In game** section listing what the owner should check.
- **Commits by the autopilot:** end with the trailer `Autopilot: AetherFrame` before `Co-Authored-By`.

## Building

```bash
export DALAMUD_HOME="$APPDATA/XIVLauncher/addon/Hooks/dev"
dotnet build AetherFrame.slnx -c Release
dotnet test AetherFrame.Tests/AetherFrame.Tests.csproj -c Release --no-build
```

Run every test project in the solution the same way, and the `validate-package` check as `.github/workflows/build.yml` does.

## Known pitfalls

- `core.autocrlf` is `true` here and on GitHub's Windows runners. Byte-compared fixtures are `-text`, so validate fixture changes from a fresh clone.
- Write C# files with the Write tool. Bash heredocs break on some single-quoted C# content.
- In Git Bash, prefix commands with `MSYS_NO_PATHCONV=1` when an argument starts with `.` (for example `git show origin/master:.github/...`).
- A GitHub draft release edited without its tag detaches from the tag, so always pass `--tag` to `gh release edit`.
- A fresh clone inside a deep scratchpad path can fail checkout on long paths. Use `git archive` there instead.
