# battletech-ai

This project lets an AI agent play HBS BattleTech (2018) through the game's own logic, not by clicking the UI. It can:
- read your mechbay, storage and the live mechlab, and talk builds with you while you work;
- design, refit and repair mechs;
- run the campaign: contracts, missions, after-action and salvage, travel, events, pilots, the store, Argo upgrades, finances;
- command a lance in tactical combat, on your side or **as the enemy against you**;
- talk with you in-game through a chat overlay, and read the story text (mission chatter, conversations, cinematic subtitles).

```
BattleTech (Unity/Mono, vanilla 1.9.1 + ModTek)
 └─ BTBridge mod (C#, Harmony)    mod/BTBridge
     ├─ main-thread pump: postfix on UnityGameInstance.Update
     ├─ reads: company, mechbay, storage, live mechlab, campaign status, combat briefing, story text
     ├─ writes: refits and repairs, campaign actions, combat orders, skirmish saves
     ├─ combat: AITeam decision hook (stock AI suggests, agent decides)
     ├─ in-game chat overlay (IMGUI) and camera follow
     └─ HttpListener on 127.0.0.1:8787, JSON envelopes
        ⇅
 btai MCP server (Python)         server/
     ├─ catalog_* / check_mech_build: offline, from the game's JSON data
     ├─ game_* / campaign_* / sim_* / combat_* / story_* / overlay_*: live, via the bridge (76 tools)
     └─ btai-cheats-mcp: a separate, gated server for operator cheats (off by default)
```

The target is vanilla 1.9.1 with ModTek, with BTBridge as the only mod. Overhaul packs such as RogueTech or BTA (MechEngineer, CustomComponents, CustomAmmoCategories, CleverGirl) change mech construction and patch the same AI methods, so they aren't supported.

How the game works inside, and why the hooks sit where they do:
- [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md): mechbay, combat, and modding traps;
- [docs/CAMPAIGN_INTERNALS.md](docs/CAMPAIGN_INTERNALS.md): the campaign layer, with what the live tests found;
- [docs/CHEATS_DESIGN.md](docs/CHEATS_DESIGN.md): the gated operator cheat tier.

## Status

Everything below runs on a test career unless marked otherwise. "Verified" means seen working in-game, not just compiled.

| Piece | State |
|---|---|
| Offline catalog and build pre-check | working; passes every regular stock mech; each rule mutation-tested |
| Mechbay, storage, live mechlab reads | verified |
| Refits (armor, removal, move, install from storage) and live validation | verified; overweight builds are refused before anything is spent |
| Repair (structure and damaged components) | verified on a destroyed Vindicator and a damaged Centurion. **Destroyed components are scrapped, not replaced**: refit them from storage afterwards |
| Campaign: status, interrupts, events, time, travel, quarterly report | verified across ~60 in-game days and two jumps |
| Contracts: accept → lance → Begin Mission → mission dialogue → end screen → AAR → salvage | verified end to end with **no clicks** (three missions). Three priority picks assembled a whole Crab |
| Pilots (train, hire, dismiss), store (buy, sell), Argo upgrades, finances | verified |
| Combat with the agent commanding the player lance | verified (skirmish and three campaign missions) |
| Combat with the agent commanding the **enemy** | built; first live run pending |
| Live objectives, mission briefing text | verified |
| Mission dialogue (blocking) and radio chatter (non-blocking), transcript | verified, including multi-speaker scenes |
| Story text: campaign conversations and cinematic subtitles, spoiler-gated; cinematic skip | reads verified against the game data. Career mode has no story, so the seen-tracking and skip still need a Campaign save |
| Chat overlay: feed, history, channels, operator input | verified; the latest layout pass (HUD-matched width, objective-yellow text, Close button) is deployed and awaiting a look |
| Camera follows the acting unit | built; first live run pending |
| Operator cheat layer | built and unit-tested; not yet run in-game (needs the launch option) |

The combat **test driver** used for the plumbing tests isn't part of the repo. It's a fixed rule set: close in, accept the stock AI's positioning, shoot the best expected-damage target. It won all three missions it played but lost two pilots holding against two assault 'Mechs. Missions that matter should be played by the agent reasoning live, or by you.

## Build and test

```bash
# mod (builds against the installed game; override with -p:GameDir=...)
cd mod/BTBridge && dotnet build -c Release      # -> bin/Release/BTBridge.dll + mod.json

# mod tests (.NET Framework 4.7.2): pure rules, plus checks against the installed game's assemblies
cd mod/BTBridge.Tests && dotnet test

# server
cd server && uv run pytest -q
uv run btai-mcp                                  # stdio MCP server
```

Environment variables: `BATTLETECH_DIR` (game install, default `F:\SteamLibrary\steamapps\common\BATTLETECH`) and `BTBRIDGE_URL` (default `http://127.0.0.1:8787`).

Two test suites guard against the mistakes that stopped the mod from starting during development:
- **Patch targets:** every `[HarmonyPatch]` must resolve to exactly one game method, and every hook parameter must name a real parameter of it. Harmony binds by name, and either mistake throws inside `PatchAll`, so the whole mod fails to load.
- **No creating singletons:** HBS's `LazySingletonBehavior<T>.Instance` *creates* the singleton if the game hasn't yet. Reading `UIManager` that way during startup hung the game on a black screen. Only `Ui/GameUi.cs` may touch it, behind `HasInstance`.

## Installing the mod

The mod uses [ModTek](https://github.com/BattletechModders/ModTek) v4.5.1, installed by hand rather than through Vortex: extract `winhttp.dll`, `doorstop_config.ini` and `Mods/ModTek/` from the release zip into the game folder. The mod also loads under the game's built-in loader, since both read the same `mod.json` and call `Init(modDir, settingsJson)`.

```powershell
scripts\deploy.ps1                   # build, then junction Mods\BTBridge -> mod\BTBridge\bin\Release
scripts\deploy.ps1 -NoBuild          # just (re)create the junction
scripts\deploy.ps1 -Undeploy         # remove the junction only
scripts\deploy.ps1 -UninstallModTek  # remove the junction and ModTek
```

Because of the junction, a rebuild goes live on the next game launch. Close the game before building, since it locks the DLL. Logs:
- `BTBridge.log` next to the DLL;
- `Mods\.modtek\ModTek.log` and `Mods\.modtek\battletech_log.txt` (the game's own log, where Unity exceptions land).

When ModTek loads, the main menu's version string reads `/W MODTEK`.

### macOS and Linux (untested)

Nothing here has been run on a Mac or Linux yet. The code is portable: the mod is platform-neutral .NET with no Windows APIs, and finds game files through Unity's own paths. These are the pieces adapted for other platforms:
- **ModTek:** install its macOS/Linux files (the ones skipped in the Windows install) and launch through its script, as ModTek's own instructions describe. On Apple Silicon the game runs under Rosetta.
- **Deploy:** `scripts/deploy.sh` does what `deploy.ps1` does, with a symlink instead of a junction:
  ```bash
  scripts/deploy.sh                      # build, then link Mods/BTBridge -> mod/BTBridge/bin/Release
  scripts/deploy.sh --no-build           # just (re)create the link
  scripts/deploy.sh --undeploy           # remove the link only
  scripts/deploy.sh --game-dir "<path>"  # if the game isn't in the usual Steam folder
  ```
  It refuses to build while the game is running, and won't delete anything that isn't its own link. Set `BTBRIDGE_MODS_DIR` if ModTek's Mods folder isn't `<game dir>/Mods`.
- **Building:** the project finds the game assemblies in either layout (`BattleTech_Data/Managed`, or `BattleTech.app/Contents/Resources/Data/Managed` on macOS). A DLL built on Windows should also work as-is.
- **The MCP server's offline catalog** finds the game data in either layout too. Its default install location follows the OS (`~/Library/Application Support/Steam/...` on macOS, `~/.local/share/Steam/...` on Linux); `BATTLETECH_DIR` can point at the Steam folder or straight at `BattleTech.app`.

## Bridge API (v0.1)

Every response is `{"ok": true, "data": ...}` or `{"ok": false, "error": "..."}`. Status codes:
- **409:** the game is in the wrong state (no campaign loaded, a popup waiting, a mission still loading, or a mech write during combat).
- **503 with `executed: false`:** the main thread didn't pick the request up in time. It was cancelled and **did not run**, so retrying is safe.
- **504 with `executed: null`:** it started but didn't finish in time. Its outcome is unknown, so read the state before retrying.
- **403:** the request came from a web browser, or used an unexpected `Host`. The bridge only serves local programs such as the MCP server.

Plans are bound to the load that made them: a refit plan from before a save reload or a campaign switch is refused. Mech writes (refit apply, repair) are refused while a mission or its contract resolution owns the mechs.

### Company and mechs

| Route | Returns |
|---|---|
| `GET /health` | bridge and game version; whether a campaign, combat or the mechlab is active |
| `GET /sim/company` | funds, date, MechTech skill, bay capacity |
| `GET /sim/mechbay` | mechs in active and readying bays with full loadouts and stats; the work queue |
| `GET /sim/storage` | component counts (damaged flagged separately), **stored complete mechs**, salvaged mech parts |
| `GET /mechlab/current` | the open build including unsaved edits, plus the game's validation errors |
| `GET /sim/mech?bay=N` | one active mech plus its editable `spec` |
| `POST /mech/validate` | `{mechdef}`: the game's validator at `Full` level, plus the built mech's stats |
| `POST /sim/refit/preview` | `{mech, mechdef}`: steps, cost, missing parts, validation and a `plan_id`. Changes nothing |
| `POST /sim/refit/apply` | `{plan_id}`: commits like the mechlab's Confirm. Refuses if anything changed since the preview; a plan is single-use |
| `POST /sim/repair` | `{mech, confirm}`: battle-damage repair as the mech bay's Repair button does it. Previews cost and days; `confirm: true` queues it. Lists destroyed components, which the repair **scraps** |
| `GET /skirmish/custom`, `GET /skirmish/pilots` | saved custom skirmish mechs and lances; skirmish pilots |
| `POST /skirmish/mechs`, `DELETE /skirmish/mechs?id=` | save a validated custom mech / delete one |
| `POST /skirmish/lances`, `DELETE /skirmish/lances?id=` | save a lance (1 to 4 `{mech_id, pilot_id}`) / delete one |

Every build-taking route uses the game's mechdef shape: `{"ChassisID", "Locations": [{"Location", "AssignedArmor", "AssignedRearArmor"}], "inventory": [{"ComponentDefID", "MountedLocation"}]}`. Fixed equipment is left out because the chassis supplies it.

### Campaign

| Route | Purpose |
|---|---|
| `GET /sim/status` | whether the campaign is idle, with blockers, room, date, funds, unread operator messages, and any running time / launch / travel job |
| `GET/POST /sim/interrupt` | read and answer whatever is waiting: events (plain text, options, results), notifications, quarterly report, rewards, mech placement, Flashpoint and Heavy Metal popups, conversations |
| `POST /sim/time` | `{days}` or `{until_event: true}`, optional `day_seconds`; `{stop: true}` stops |
| `GET /sim/contracts`, `POST /sim/contracts/accept`, `POST /sim/contracts/launch` | list contracts with description and the player's objectives; accept with negotiated pay/salvage; drop with `[{bay, pilot}]` |
| `GET/POST /sim/aar` | after-action stages; priority salvage picks (all or nothing, capped) |
| `GET /sim/starmap?jumps=`, `GET/POST /sim/travel` | neighbouring systems; route preview, then `confirm` to travel |
| `GET /sim/pilots`, `POST /sim/pilots/train`, `GET /sim/hiring`, `POST /sim/pilots/hire`, `POST /sim/pilots/dismiss` | barracks (training previews first); hiring hall, with `mrb_allows` per pilot |
| `GET /sim/store?shop=`, `GET /sim/store/sellable`, `POST /sim/store/buy`, `POST /sim/store/sell` | system, faction and black-market shops. Selling is all or nothing |
| `GET /sim/argo`, `POST /sim/argo/upgrade`, `GET /sim/finances`, `GET /sim/flashpoints`, `POST /sim/flashpoints/accept` | ship upgrades, money and morale, reputation, flashpoints |

Several of these mirror a screen that does its own checks. Where the underlying game method doesn't validate anything, the bridge repeats the checks: hiring (roster, MRB, funds), limited-stock purchases, Argo requirements, salvage caps, event double-submits. Popups block transactions, as they do in the UI. See [docs/CAMPAIGN_INTERNALS.md](docs/CAMPAIGN_INTERNALS.md).

### Missions, dialogue and story

| Route | Purpose |
|---|---|
| `GET /combat/mission` | briefing waiting/ready, the contract's text, **live objectives** with status and progress, mission over and result |
| `POST /combat/begin` | press Begin Mission once loading completes |
| `GET /combat/dialog`, `POST /combat/dialog/continue` | the blocking story line on screen (speaker, text); advance it |
| `GET /dialog/transcript?limit=&since=&repeats=` | every dialogue line shown: `dialog` (blocking) and `radio` (voiced side-panel chatter), with speaker and `times_seen`. Repeats are dropped unless asked for. Also written to `dialog_log.jsonl` |
| `POST /combat/withdraw`, `POST /combat/exit` | retreat; leave the end screen |
| `GET /story/conversations`, `GET /story/conversation?id=` | the campaign's scripted conversations, with speakers and branching responses |
| `GET /story/cinematics`, `GET /story/cinematic?video=` | cinematics in story order; subtitle text (the game's subtitle files name no speakers, and 8 of 17 have none) |
| `GET /sim/video`, `POST /sim/video/skip` | a cinematic is playing; skip it the way Escape does |

Story routes are **spoiler-gated**: only material already seen in this install (recorded when it plays) unless `spoilers=true`. Objectives the player isn't meant to see are never listed: the encounter's internal objective list includes the AI's hidden ones, so the bridge uses the list the combat HUD shows, and skips objectives that haven't triggered yet.

### Combat

| Route | Returns |
|---|---|
| `GET/POST /combat/control` | who commands each side, `{player, enemy, decision_timeout_seconds}`. Applies when the next mission builds its teams; in memory only, so set it again after a restart |
| `GET /combat/briefing?side=` | the whole board from one side's visibility: initiative bar, own units with terrain and a per-enemy engagement matrix, contacts, last-seen contacts, objectives |
| `GET /combat/state?side=` | compact board summary |
| `GET /combat/decision` | the open decision: unit, stage, side, the stock AI's suggestion, top influence-map candidates, per-weapon hit chances, any `standing_order_error`, waiting dialogue/briefing flags |
| `POST /combat/decision` | `{id, unit, order}`. `order.action` is `accept`, `move`, `attack` or `brace`. `unit` must match the decision's unit |
| `GET/POST /combat/orders` | standing orders for the round: per unit an optional move and attack, `sequence` and `on_invalid`. All or nothing: a rejected batch leaves the previous plan unchanged |
| `GET /combat/reachable?move=&x=&z=&limit=` | reachable points for the deciding unit, nearest first to a focus point |
| `GET /combat/history` | the last 50 decisions and how they were resolved |

Control modes:
- **player:** `Human` (vanilla, but the board is still readable for advice), `Agent`, or `BuiltinAI` (the stock AI plays your lance).
- **enemy:** `StockAI` or `Agent` (every AI team hostile to the player).

An agent-commanded player lance can't be selected in the HUD.

**Playing a round as the agent:**
1. `combat_briefing` once per round to read the board: turn order, engagements, cover, objectives.
2. `combat_set_orders` to plan every unit: move, attack and activation sequence.
3. `combat_wait_for_decision`. Planned units act instantly, so a decision stays open only when a plan no longer fits (`standing_order_error`) or a unit had no orders. Answer with `combat_decide`. The wait also returns early for operator messages, mission dialogue and the Begin Mission screen.

**Fog of war:** every combat view is built from what the agent's side can see. A sensor contact shows its position, and its kind (mech, vehicle, turret) only at type-level sensor returns; never its name, facing or loadout. Default move facing only considers detected enemies, and `face_unit` refuses a unit the side no longer detects.

Snags found in-game, in more detail in [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md):
- Movement grids are sparse, so move orders snap to the nearest reachable node within 25 m.
- Line of sight isn't line of fire. A unit on a ridge can see a target it can't shoot.
- Weapon uids repeat across units, which is why answers must name their unit.

### In-game chat overlay

The agent puts short messages on screen with `overlay_say(type, text)` (`POST /overlay/say`):
- **Placement:** a rolling feed of up to 5 messages on the right edge, **as wide as the combat HUD's objectives panel** (measured from the live panel in combat). Each fades after 12 s.
- **Look:** bold text on a dark, near-opaque backing. The agent's own words (commentary, decisions) are in the game's primary-objective yellow, read from the game's UI constants; warnings red-orange; system light grey; your messages a soft green.
- **History:** the last 20 messages, **Ctrl+Shift+H** or its **Close** button. The header says how many messages your channel settings hide. Also written to `overlay_log.jsonl`.
- **Four channels:** `commentary`, `decision`, `warning`, `system`. **Ctrl+Shift+O** switches each on or off live; **only commentary is on by default**. Choices persist in PlayerPrefs, and only the player can change them; there is no route for it.
- **Text:** plain only (markup isn't interpreted), control characters stripped, at most 280 characters and 3 lines, about 1 per second with bursts of 5.
- **Speaker labels** come from the mod, not the model: `[YOUR LANCE]`, `[OPFOR]`, `[CAMPAIGN]`.
- **Hidden plans:** when the agent commands the enemy, its `decision` messages are **held until that unit has acted** (a toggle turns this off).
- **Non-blocking:** its own IMGUI layer; no game popups, nothing waits on it. Clicks over an open overlay panel don't reach the game, so pressing Close can't select or move a mech.

**Camera follow:** as an agent- or AI-driven unit's turn opens, the camera pans to it, the way selecting it would. It never pans to an enemy unit your side can't fully see, so an agent-commanded OpFor isn't revealed. Toggle in the Ctrl+Shift+O panel.

**Talking to the agent:** **Ctrl+Shift+T** opens a full-width input bar along the bottom of the screen, in every scene. **Enter** sends; **Esc** cancels.
- Your message echoes in the feed as `[YOU]`, always visible regardless of channel toggles.
- It goes into an inbox only this box can write to, so the agent can't fabricate operator messages.
- The agent reads it with `overlay_inbox` and acknowledges with `overlay_ack`. Unread messages also ride along on `sim_status` and every combat decision response.

**While you type, game input is suspended:** BTInput's action sets are disabled and restored on close, and a postfix on `DebugConsole.IsHidden` makes the combat key handler stand down (the gate HBS's own console uses). **Known quirks:** a few spots read Unity's `Input` directly; while typing, Space/Esc can skip a combat dialog line, and any key can dismiss a turn-event banner or skip a skippable camera sequence.

### Operator cheat layer (off by default)

A separate, gated tier for explicit operator requests such as "give the company 250M C-bills" or "put an Atlas in bay 3". [docs/CHEATS_DESIGN.md](docs/CHEATS_DESIGN.md) has the design. Three layers gate it:
1. **Capability (restart required):** launch with `--btbridge-allow-cheats` (Steam launch options), or set `"AllowCheats": true` in `mod.json`. Without it, the `/cheat/*` routes don't exist.
2. **Arming (human only):** **Ctrl+Shift+F9** opens a window (15 minutes by default), with a red banner while armed. **Ctrl+Shift+F10** opens the settings panel. No API can arm or reconfigure.
3. **A separate MCP server:** `btai-cheats-mcp`. Register it only if you want it:
   ```json
   "battletech-cheats": { "command": "uv", "args": ["run", "--directory", "server", "btai-cheats-mcp"] }
   ```

Every cheat is preview → execute, with a single-use plan bound to the campaign, this load of it, and the before-state. Each execution writes `cheats_audit.jsonl` with before/after values, the quoted operator request, and whether the save was dirty (the next autosave makes it persistent). The campaign gets a visible marker. Cheats are **refused outright in Ironman**.

v1 operations: add/remove C-bills, add/remove components, add a mech to a bay or storage, and finish mechlab work, the Argo upgrade, or medbay recovery now.
