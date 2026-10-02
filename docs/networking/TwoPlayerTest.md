# The two-player sharing test

NETWORK2's acceptance test ([NETWORK2.md](NETWORK2.md), section 2), as a checklist for the owner and each tester. Its tester kit is "N2-11's tester kit" in [DecisionRegister.md](DecisionRegister.md).

Player A is the owner. Player B is a tester. With more testers, each one is a player B, and any two can check viewing between them.

## Before the test

`[updated 2026-10-02: since "Releases carry sharing" (DecisionRegister.md) a tester installs the release from the custom repository's testing channel instead of a kit, and the server has been open to everyone since October 1, 2026 ("Opening the alpha" in DecisionRegister.md; Runbook.md, step 8), so step 1 below and the allowlist line under Afterwards apply only while it is closed again. Steps 1 to 3 stay for a closed server or a build no release carries.]`

**The owner:**
1. Collect each tester's Lodestone character page address, and send them to Claude in chat. Claude replies with one command that adds them to the server's allowlist (decision C8). Paste it into the droplet's console, where it prints how many ids the allowlist now holds.
2. Ask Claude for a tester kit. Claude builds it from the newest `master` that has passed in your own game, and stages it as `E:\AetherFrame Test Builds\<date> <commit> tester kit\AetherFrame-tester-kit-<commit>.zip`.
3. Give each tester the zip, by a channel you trust. It holds its own install steps in `How to install.txt`.
4. Start the Lodestone relay on your PC (`E:\AetherFrame Test Builds\Lodestone relay\Start relay.cmd`), and keep its window open while anyone links a character. Viewing doesn't need it.

**Each tester** follows `How to install.txt`: Windows only, any other AetherFrame disabled, the test build added under Dev Plugin Locations, and `/af version` showing "network preview". `[updated 2026-10-02: since 26aed03 (0.1.8 and later) the line ends with "[sharing]"; the October 1 builds still said "[network preview]".]`

## The checks

Tick each one for each pair of players. Each check names the step of NETWORK2's section 2 it covers.

| # | Who | Do | Expect | Step |
|---|---|---|---|---|
| 1 | B | Before turning sharing on, right-click A's character | No **View AetherFrame Plate** item. My Plates' **Sharing** offers nothing to search | 1 |
| 2 | B | Open **Sharing**, read the consent screen, agree, and turn sharing on | A one-time code. The screen said what others see, who can see it, what the server keeps, and how to stop | 2 |
| 3 | B | Paste the code into the Lodestone Character Profile, then the page's address into AetherFrame, and press **Check** | The check passes. The code can then be deleted from the profile | 3 |
| 4 | B | Wait a moment after the check passes | A small window says your Active Plate is shared, then closes by itself. My Plates marks it **Shared** | 4 |
| 5 | A | Right-click B's character in the world or the party list, and choose **View AetherFrame Plate** | B's Active Plate, read-only, as it looks in B's own Profile View. Nothing new in A's Library | 5 |
| 6 | A | **Find a player's Plate**: B's full name and World | The same Plate | 5 |
| 7 | B | Edit the Active Plate and save it. Then A presses **Refresh** | A sees the new version | 6 |
| 8 | B | Make another Plate Active. Then A refreshes | The small window says it is shared, and A sees that Plate | 6 |
| 9 | A | **Hide this player**, then reopen B's Plate from the menu | "You hid this player's Plate", with nothing looked up. **Show their Plate again** brings it back | C5 |
| 10 | A | **Report...** with any reason | "Reported. Thank you." The owner sees it in `admin reports` | C5 |
| 11 | B | Turn sharing off. Then A refreshes | "No AetherFrame Plate to show". `admin characters` no longer lists B | 7 |
| 12 | Both | Use My Plates, both editors and Profile View, with the game's network or the server unreachable | Everything local works, and every Plate, Template and binding is as before | 8 |

Swap A and B and repeat checks 5 to 11, so each player both shares and views.

`[updated 2026-10-02: checks 4 and 8 follow the owner's direction ("Sharing a Plate as soon as it is Active" in DecisionRegister.md): nothing is shown before a Plate is shared any more. The results below were taken before it.]`

## Afterwards

- Each tester turns sharing off before removing the test build, so the server keeps nothing of theirs.
- The owner tells Claude the result in chat or in the [Owner inbox](https://github.com/QuietFoxLabs/AetherFrame/issues/25): which checks passed, and for any that didn't, what was done and what was seen. A tester's `/xllog` lines for AetherFrame help, and never hold a name, a World, a code or a key.
- Testers can be taken off the allowlist afterwards. Claude gives the command.

## Results

### October 1, 2026: passed

**Set-up:**
- **Plugin:**
  - The owner's PC ran preview build `02468e6` (`master` after [#79](https://github.com/QuietFoxLabs/AetherFrame/pull/79)) from `E:\AetherFrame Test Build\`; dalamud.log shows it loaded at 14:55 UTC.
  - The second PC ran tester kit `0c96b18` (`master` after [#78](https://github.com/QuietFoxLabs/AetherFrame/pull/78)), SHA-256 `902faca7...a0eb`.
  - #79 only removed code nothing reached (Clean Preview), so the two builds behave the same.
- **Server:** `1a8527a` at `plates.aetherframe.dev`, with the Lodestone relay on the owner's PC.
- **Players:** the owner (A) and a second player (B), each on their own PC, both of which the owner controlled. The tester is named nowhere here.

**Results, as the owner reported them in chat:**

| # | Result |
|---|---|
| 1 | Not reported on its own. |
| 2 to 4 | Passed for B. B's first checks failed only because B wasn't yet on the allowlist (`check:allowlist` in the server log); once added, the check passed and B shared a Plate. |
| 5 | Passed both ways: A saw B's Plate, and B saw A's. |
| 6 | Not reported on its own between the two players. The search had passed for A's own Plate before the test, with build `0c96b18`. |
| 7 | Passed: A changed and shared A's Plate, and B saw the update. |
| 8 | Not reported on its own. |
| 9 to 11 | Hide, report and opting out worked in game. The server side (`admin reports`, and `admin characters` after opting out) wasn't reported on its own. |
| 12 | Passed with the server stopped (`docker compose stop server`) instead of the network: everything local worked, sharing said the server couldn't be reached, and both recovered once it started again. |

**What the test taught:**
- **Add the tester before their first check.** A check before the allowlist holds their id fails with the same "check didn't pass" as any other cause (C2), and spends one of the hour's 10 checks. So run the allowlist command first, and confirm it ends in the expected number of ids, before a tester presses Check.
