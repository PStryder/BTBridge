# battletech-ai

This project lets an AI agent work with HBS BattleTech (2018). It can:
- read your mechbay and storage, and follow along while you build in the mechlab;
- design and refit mechs and lances;
- command a lance in tactical combat, on your side or the enemy's.

It works by hooking the game's own logic, not by clicking the UI. Next up: the campaign layer (contracts, travel, events, company management).

```
BattleTech (Unity/Mono, vanilla + ModTek)
 └─ BTBridge mod (C#, Harmony)    mod/BTBridge
     ├─ main-thread pump: postfix on UnityGameInstance.Update
     ├─ state: mechbay, storage, live mechlab, skirmish customs, combat briefing
     ├─ writes: refit work orders, skirmish saves, combat orders
     ├─ combat: AITeam decision hook (stock AI suggests, agent decides)
     └─ HttpListener on 127.0.0.1:8787, JSON envelopes
        ⇅
 btai MCP server (Python)         server/
     ├─ catalog_* / check_mech_build: offline, from the game's JSON data
     └─ game_* / campaign_* / mechlab_* / skirmish_* / combat_*: live, via the bridge
```

The target is vanilla 1.9.1 with ModTek, and BTBridge as the only mod. Overhaul packs such as RogueTech or BTA (MechEngineer, CustomComponents, CustomAmmoCategories, CleverGirl) change mech construction and patch the same AI methods, so they aren't supported.

How the game works inside, and why the hooks sit where they do, is covered in [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md).

## Status

| Piece | State |
|---|---|
| Offline catalog: components, chassis, stock mechs | working and tested against the installed data |
| Offline build pre-check: tonnage, slots, hardpoints, armor, jump jets, ammo | working; passes every regular stock mech; each rule mutation-tested |
| MCP server (31 tools) | working; the live tools report clearly when the game isn't reachable |
| BTBridge mod: read endpoints | verified in-game (career): company, mechbay, storage, live mechlab |
| Writes: live validation, campaign refit preview/apply, skirmish mechs/lances | verified in-game: armor, removal, move, install-from-storage refits complete correctly |
| Combat: decision hook, accept/move/attack/brace orders | verified in-game (skirmish, agent commanding the player lance): waits indefinitely, move→attack stages, melee, indirect fire, Ace Pilot shoot-then-move |
| Combat: move snapping, unit-guarded answers, reachable query | built after the first skirmish; compiles, not yet run in-game |
| Combat: side briefing, standing orders, activation order, enemy-side control | built; compiles, not yet run in-game |
| Campaign layer: contracts, travel, time/events, pilots, store | being researched |

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

## Bridge API (v0.1)

Every response is `{"ok": true, "data": ...}` or `{"ok": false, "error": "..."}`. A 409 means the game is in the wrong state (for example, no campaign loaded). A 503 means the main thread didn't respond.

| Route | Returns |
|---|---|
| `GET /health` | bridge and game version; whether a campaign, combat or the mechlab is active |
| `GET /sim/company` | funds, date, MechTech skill, bay capacity |
| `GET /sim/mechbay` | mechs in active and readying bays with full loadouts and stats; the work queue |
| `GET /sim/storage` | component counts (damaged flagged separately), stored mechs, salvaged mech parts |
| `GET /mechlab/current` | the open build including unsaved edits, plus the game's validation errors |
| `GET /sim/mech?bay=N` | one active mech plus its editable `spec` |
| `POST /mech/validate` | `{mechdef}`: the game's validator at `Full` level, plus the built mech's stats |
| `POST /sim/refit/preview` | `{mech, mechdef}`: the step list, cost, missing parts, validation and a `plan_id`. Changes nothing |
| `POST /sim/refit/apply` | `{plan_id}`: commits like the mechlab's Confirm. Refuses if anything changed since the preview |
| `GET /skirmish/custom` | saved custom skirmish mechs and lances |
| `GET /skirmish/pilots` | skirmish pilots. The first call starts loading them |
| `POST /skirmish/mechs`, `DELETE /skirmish/mechs?id=` | save a validated custom mech / delete one |
| `POST /skirmish/lances`, `DELETE /skirmish/lances?id=` | save a lance (1 to 4 `{mech_id, pilot_id}`) / delete one |

Every build-taking route uses the game's mechdef shape: `{"ChassisID", "Locations": [{"Location", "AssignedArmor", "AssignedRearArmor"}], "inventory": [{"ComponentDefID", "MountedLocation"}]}`. Fixed equipment is left out because the chassis supplies it.

### Combat routes

| Route | Returns |
|---|---|
| `GET/POST /combat/control` | who commands each side, `{player, enemy, decision_timeout_seconds}`. Applies from the next mission start |
| `GET /combat/briefing?side=` | the whole board from one side's visibility: initiative bar, own units with terrain and a per-enemy engagement matrix, contacts, last-seen lost contacts |
| `GET /combat/state?side=` | compact board summary |
| `GET /combat/decision` | the open decision: unit, stage, side, the stock AI's suggestion, top influence-map candidates, per-weapon hit chances, any `standing_order_error` |
| `POST /combat/decision` | `{id, unit, order}`. `order.action` is `accept`, `move`, `attack` or `brace`. `unit` must match the decision's unit |
| `GET/POST /combat/orders` | standing orders for the round: per unit an optional move and attack, `sequence` and `on_invalid` |
| `GET /combat/reachable?move=&x=&z=&limit=` | reachable points for the deciding unit, nearest first to a focus point |
| `GET /combat/history` | the last 50 decisions and how they were resolved |

Control modes:
- **player:** `Human` (vanilla, but the board is still readable for advice), `Agent`, or `BuiltinAI` (the stock AI plays your lance).
- **enemy:** `StockAI` or `Agent` (every AI team hostile to the player).

An agent-commanded player lance can't be selected in the HUD.

### Playing a combat round as the agent

1. `combat_briefing` once per round to read the board: turn order, engagements, cover.
2. `combat_set_orders` to plan every unit: move, attack and activation sequence.
3. `combat_wait_for_decision`. Planned units act instantly, so a decision only stays open when a plan no longer fits (`standing_order_error`) or a unit had no orders. Answer it with `combat_decide`.

Snags found in-game, in more detail in [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md):
- Movement grids are sparse, so move orders snap to the nearest reachable node within 25 m.
- Line of sight isn't line of fire. A unit on a ridge can see a target it can't shoot.
- Weapon uids repeat across units, which is why answers must name their unit.
