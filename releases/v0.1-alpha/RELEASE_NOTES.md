BTBridge **v0.1 alpha**: the first packaged build of the battletech-ai game mod.

It lets an AI agent work with HBS BattleTech (2018) through the game's own logic: read your company and the live mechlab, refit and repair, run the campaign (contracts, missions, after-action and salvage, travel, events, pilots, store, Argo), command a lance in combat on your side or **as the enemy against you**, and talk with you through an in-game chat overlay.

**These zips are the game mod only.** To connect an agent (Claude or any MCP client), clone the repository and run the MCP server in `server/`: see the [README](https://github.com/PStryder/battletech-ai#readme).

## Downloads

| File | For |
|---|---|
| `BTBridge-v0.1-alpha-windows.zip` | Windows (tested) |
| `BTBridge-v0.1-alpha-macos.zip` | macOS (**untested**: testers wanted) |

Both contain the same `BTBridge.dll`; only the install guide differs. Checksums are in `SHA256SUMS.txt`.

## Requirements

- BattleTech **1.9.1**, vanilla (no RogueTech, BTA or other overhaul packs)
- [ModTek v4.5.1](https://github.com/BattletechModders/ModTek/releases)

## Status

- **Verified on Windows:** a full campaign loop with no clicks (three contracts, from Begin Mission through salvage), refits and repairs, travel and events, the chat overlay.
- **Built but not yet run live:** the agent commanding the enemy against a human, the camera follow, and the operator cheat tier (off by default, and only available with a launch option).
- **macOS:** the code is portable, but it has never been run on a Mac.

## Known limits

- Alpha: expect rough edges, and back up your saves.
- Combat control modes reset when the game restarts.
- The agent plays at reasoning speed (about 10–30 s per unit).

Report problems with logs at https://github.com/PStryder/battletech-ai/issues
