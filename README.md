# battletech-ai

This project lets an AI agent work with HBS BattleTech (2018): read your mechbay and storage, follow along while you build in the mechlab, design its own mechs and lances, and eventually play tactical combat turns.

```
BattleTech (Unity/Mono)
 └─ BTBridge mod (C#, Harmony)    mod/BTBridge
     ├─ main-thread pump: postfix on UnityGameInstance.Update
     ├─ state serializers: mechbay, storage, live mechlab, skirmish customs
     └─ HttpListener on 127.0.0.1:8787, JSON envelopes
        ⇅
 btai MCP server (Python)         server/
     ├─ catalog_* / check_mech_build: offline, from the game's JSON data
     └─ game_* / campaign_* / mechlab_* / skirmish_*: live, via the bridge
```

How the game works inside, and why the hooks sit where they do, is covered in [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md).

## Status

| Piece | State |
|---|---|
| Offline catalog: components, chassis, stock mechs | working and tested against the installed data |
| Offline build pre-check: tonnage, slots, hardpoints, armor, jump jets, ammo | working; passes every regular stock mech; each rule mutation-tested |
| MCP server (12 tools) | working; the live tools report clearly when the game isn't reachable |
| BTBridge mod: read-only endpoints | **compiles against the game DLLs; not yet run in-game** |
| Writes: skirmish save, campaign refit proposals | designed (see internals doc), not built |
| Combat agent hook | designed, not built |

## Build and test

```bash
# mod (builds against the installed game; override with -p:GameDir=...)
cd mod/BTBridge && dotnet build -c Release      # -> bin/Release/BTBridge.dll + mod.json

# server
cd server && uv run pytest -q
uv run btai-mcp                                  # stdio MCP server
```

Environment variables: `BATTLETECH_DIR` (game install, default `F:\SteamLibrary\steamapps\common\BATTLETECH`) and `BTBRIDGE_URL` (default `http://127.0.0.1:8787`).

## Installing the mod

The mod uses [ModTek](https://github.com/BattletechModders/ModTek) v4.5.1. ModTek is installed by hand, not through Vortex: extract `winhttp.dll`, `doorstop_config.ini` and `Mods/ModTek/` from the release zip into the game folder. The mod still loads under the game's built-in loader as well, since both read the same `mod.json` and call `Init(modDir, settingsJson)`.

```powershell
scripts\deploy.ps1                   # build, then junction Mods\BTBridge -> mod\BTBridge\bin\Release
scripts\deploy.ps1 -NoBuild          # just (re)create the junction
scripts\deploy.ps1 -Undeploy         # remove the junction only
scripts\deploy.ps1 -UninstallModTek  # remove the junction and ModTek
```

Because of the junction, a rebuild goes live on the next game launch. Close the game before building, since it locks the DLL. Logs go to `BTBridge.log` next to the DLL, and ModTek's own logs go to `Mods\.modtek\`. When ModTek loads, the main menu's version string reads `/W MODTEK`.

## Bridge API (v0.1, read-only)

Every response is `{"ok": true, "data": ...}` or `{"ok": false, "error": "..."}`. A 409 means the game is in the wrong state (for example, no campaign loaded). A 503 means the main thread didn't respond.

| Route | Returns |
|---|---|
| `GET /health` | bridge and game version; whether a campaign, combat or the mechlab is active |
| `GET /sim/company` | funds, date, MechTech skill, bay capacity |
| `GET /sim/mechbay` | mechs in active and readying bays with full loadouts and stats; the work queue |
| `GET /sim/storage` | component counts (damaged flagged separately), stored mechs, salvaged mech parts |
| `GET /mechlab/current` | the open build including unsaved edits, plus the game's validation errors |
| `GET /skirmish/custom` | saved custom skirmish mechs |
