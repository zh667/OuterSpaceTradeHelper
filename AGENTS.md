# Outer Space Trade Helper — working rules

In-game trade assistant mod for *Outer Space / 秩序：新曙光* (Steam 3080070). Licensed GPL-3.0.

## Branch, commit and merge workflow
(Same rules as `E:/projects/ScCsgoKnives/AGENTS.md`.)
- Before the first edit for a feature, fix or any other code-changing task, create a task branch from `main`. Never implement directly on `main`.
- Every commit follows Conventional Commits: `<type>(<optional scope>): <description>`, e.g. `feat(ui): sortable market table`, `fix(scan): keep stale markets untouched`. Keep commits focused.
- After the scoped changes are verified, commit and push the task branch. Report branch, commits, verification and remaining gaps.
- Merge into `main` only after the user explicitly approves merging that task. Approval to develop, commit or push is not merge approval.
- Text files are LF (`.gitattributes`). Git operations that rewrite files run on Windows only; the VPS copy is an editing workspace and uses no Git.
- Commit identity: `zh667 <128960749+zh667@users.noreply.github.com>`.

## Build and install
- Windows: `python tools/build.py` builds `bin/Release/TradeHelper.dll` against the installed game's `Outer Space_Data/Managed` (net472, no NuGet) and lays out `dist/TradeHelper`. `python tools/build.py install` also copies it to `<game>/mods/TradeHelper` (an existing install is backed up under `dist/replaced-*`). Game files are never modified.
- Mod layout: `Manifest/manifest.xml` + `v_common/Assemblies/TradeHelper.dll`; the entry type must be the global `ModPluginLauncher : BaseModLauncher`.
- When the game updates (`Assembly-CSharp.dll` changes), re-check every game member the mod calls before building.

## Correctness rules
- Accuracy over speed: an optimisation must not change any number the mod shows.
- A market that still owes settlement days (`pendingSettlementDays`) must not be touched by price functions that call `EnsureSettledForUse` (`GetBasePriceFactor`, `getPBase`, `getItemSum`, `GetItemsForUse`…). Such markets are priced from stored fields and labelled as estimates. Only the player's explicit catch-up/initialise actions advance markets.
- Never write into game objects except through the game's own entry points the player asked for.
- Test in game before calling a feature done; record what was and was not verified.
