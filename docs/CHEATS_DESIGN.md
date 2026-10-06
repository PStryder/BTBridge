# Operator cheat layer: design and build plan

**Status: v1 built (BTBridge `Cheats/`, `Logic/CheatRules.cs`, `btai-cheats-mcp`); unit- and mutation-tested; not yet run in-game.** Research is against the v1.9.1 decompile. Class and method names are exact. Line numbers are approximate.

## Purpose and boundary

An operator (the human) can explicitly tell the agent things like "give the company 250M C-bills", "put these three mechs in my bays" or "add two PPCs to storage". The agent carries the request out through the game's own state-changing methods.

The **boundary**:
- Cheats are a separate authority tier.
- Ordinary campaign tools never gain cheat behavior.
- The agent never uses a cheat because it judges that cheating would help. Cheats are for explicit operator requests only.

That separation shows up in code (its own namespace and files), in the API (`/cheat/*` routes, `cheat_*` tools, a separate MCP server), in the logs (`[CHEAT]` lines and a dedicated audit file), and in the save (a visible marker on the company).

## 1. What the game already provides

`BattleTech/SimGameState_Debug.cs` is HBS's own debug console layer: about 60 `[ScriptBinding]` commands, all static and shipped in the release build. Each v1 cheat copies the matching method rather than writing to fields.

| Need | Native path | Notes |
|---|---|---|
| Add/remove C-bills | `sim.AddFunds(int, string sourceID, bool updateBurndown, bool updateFundsGained)`, as `SimDebug_AddFunds` does with `("DEBUG_FUNDS", false, false)` | With `false, false` the money stays out of the burndown and funds-gained stats, so the earnings history stays honest. Funds is an `int` company stat: cap at about 2.1B. **Game over** if funds < `Constants.Story.MaximumDebt` at the next day tick, so floor any removal there. |
| Add a component to storage | `sim.AddItemStat(id, typeof(WeaponDef or AmmunitionBoxDef or HeatSinkDef or JumpJetDef or UpgradeDef), damaged)`, as in `SimDebug_FinalizeInventoryItem` | The debug path first resolves and loads the def with a `LoadRequest` (`SimDebug_RequestItemResource`). In a loaded campaign all defs are already in `DataManager`, but check `DataManager.Exists`. The resource lookup uses `filterByOwnership: true`, so DLC you don't own is refused. |
| Remove a component | `sim.RemoveItemStat(id, Type, damaged)` | Check the count first (`GetItemCount`), so the stat never goes negative. |
| Add a whole mech | `sim.AddMech(int idx, MechDef, bool active, bool forcePlacement, bool displayMechPopup, string header)`. The debug path instead queues `QueueMechPlacementPopup(m, immediate: true)`. | See the integrity notes below. |
| Add mech parts (salvage-style) | `sim.AddMechPart(id)` | Completing a set adds the mech through `AddMech`, with a popup. |
| Finish all mechlab work now | `SimDebug_CompleteMechTasks`: for each `MechLabQueue` entry, `PayCost(GetRemainingCost())`, then `UpdateMechLabWorkQueue(passDay: false)` | Runs the normal completion handlers (`ML_InstallComponent` and the rest), so inventory and armor reconcile exactly as on a natural finish. |
| Heal pilots now | `SimDebug_HealMechWarriors`: `MedBayQueue.PayCost(GetRemainingCost())`, then `RefreshInjuries()` | |
| Finish the Argo upgrade now | No direct debug command. The natural path is `UpdateArgoUpgrades(passDay:true)` (private): `PayCost(DailyUpgradeValue)`, and when paid, `CompleteArgoUpgrade`, `RemoveWorkQueueEntry`, the "Work Order Complete" notification, then `CancelArgoUpgrade(false)` | Pay the remaining cost, then invoke the private `UpdateArgoUpgrades(true)` by reflection (an extra payment on an already-paid entry is harmless). That keeps the native completion, notification included. |
| Grant every Argo upgrade | `SimDebug_ApplyAllArgoUpgrades`: `AddArgoUpgrade(def)` for each, then `ApplyArgoUpgrades()` | Too blunt for v1. |
| Pilot XP | `pilot.AddExperience(0, "Cheating", amount)` (`GiveAllPilotsXP`) | Use source `"BTBRIDGE_CHEAT"`. Later stage. |
| Reputation | `SimDebug_AddRep` → `sim.AddReputation(faction, val, modifyEnemies, source)` / `SetReputation(faction, val, op, source)` | Later stage. |
| Any company stat or tag | `SimDebug_SetStat`, `SimDebug_AddTag` | Too general to expose. Only through specific, named ops. |
| Game constants | `SimDebug_SetSimGameConstant(group, name, value)` mutates `sim.Constants` in memory | Global and not saved, and it changes rules for everyone (for example per-operation tech points). Don't expose it. |

**Don't use `DEBUG_AllowDebug`.** `SimGameState.AllowDebug` is **serialized into the save** (`save.AllowDebug`). When true, it runs `SimGameState_Debug.SimDebugUpdate()` every frame, which enables HBS's own hotkey cheats (F5–F12 and `-`). Turning it on would quietly open a second, ungated cheat channel that persists in the campaign.

### Build and repair time (supplemental)

How the game paces work:
- **Mechlab:** each day `UpdateMechLabWorkQueue` pays **`MechTechSkill`** tech points into the *first* queued work order (`MechLabQueue[0].PayCost(MechTechSkill)`). Days ≈ remaining tech points ÷ MechTechSkill.
  - Per-operation tech points (`Constants.MechLab.*InstallTechPoints`, `ArmorInstallTechPoints`, `UninstallTechPoints`) are baked into each work order when it is created.
- **Argo upgrades:** each day `UpdateArgoUpgrades` pays **`UpgradeValue`** (company stat, `DailyUpgradeValue`) against the upgrade's `TechCost`.
- **Medbay:** paced by **`MedTechSkill`** (company stat).
- Argo upgrades raise these stats additively through `SetCompanyStat` when installed (`AddArgoUpgrade` / `ApplyArgoUpgrades`). The stats live in the save.

| Option | How | Persistence | Recommendation |
|---|---|---|---|
| **A. Finish now (one-shot)** | Native `CompleteMechTasks` / `HealMechWarriors` / Argo pay-and-complete | The results persist (they're normal completions); nothing about pacing changes | **v1.** Clean, native, and auditable as "these N orders finished". |
| **B. Session speed multiplier** | Harmony postfix on `UpdateMechLabWorkQueue(passDay)` / `UpdateArgoUpgrades` / `UpdateInjuries` that pays `(multiplier − 1) ×` the daily value extra into the same entry | **Not saved.** Ends when disarmed or on restart | v2 if wanted. Reversible and leaves the save untouched apart from faster completions. |
| **C. Permanent stat change** | `companyStats.ModifyStat("BTBRIDGE_CHEAT", 0, "MechTechSkill", Int_Add, delta)` (likewise `UpgradeValue`, `MedTechSkill`) | **Saved**; stacks with Argo upgrades | Only on an explicit "permanently" request. Audit the delta so a matching reverse op can undo it. |
| D. Change constants | `SetSimGameConstant` | Session; global | Not recommended: it changes the game's rules, not this campaign's resources. |

## 2. Gating model

Options compared:

| Model | What it stops | Weakness |
|---|---|---|
| Restart-time capability only (mod.json or `--btbridge-allow-cheats`) | Cheats can't exist in normal play; small attack surface | Once enabled, the agent could cheat at any moment of the session |
| In-session arm/disarm only | Cheats only during an explicit window | The routes always exist; arming over the API is something the agent can do itself |
| **Both (recommended)** | Normal play: nothing to call. Cheat sessions: only while a human has armed | Two steps for the operator, which is acceptable for a deliberate tier |

**Recommended, layered** (each layer is independent):

1. **Capability: restart required.**
   - BTBridge reads `--btbridge-allow-cheats` from `Environment.GetCommandLineArgs()` (Steam launch options) **or** `Settings.AllowCheats` in `mod.json`. Both are read once in `Init`.
   - When off, the cheat routes are **not registered**: `Routes.Build(cheatsEnabled)` leaves them out, so a call gets a 404 like any unknown path. No cheat code paths run, and the overlay and hotkey listener aren't created.
   - Recommendation: document the command-line flag as the primary switch. It's set in Steam, visible, and separate from files the agent routinely edits.
2. **Arming: human-only, in-session, no restart.**
   - **There is no arm route.** Arming is a hotkey read by BTBridge's frame pump through `UnityEngine.Input`, for example **Ctrl+Shift+F9**. That avoids HBS's debug keys (F5–F12, `-`) and works whether or not those are enabled.
   - Arming opens a window with an expiry (default 15 minutes, configurable) and an optional operation budget.
   - **Disarming** is allowed both ways: the hotkey, or `POST /cheat/disarm`. Disarming is the safe direction.
   - Automatic disarm on: expiry, budget used up, leaving the campaign (main menu or loading a save), and game exit (the state is in memory only).
   - While armed, an on-screen **overlay** (our own `MonoBehaviour.OnGUI` label, not a game popup) shows `BTBridge CHEATS ARMED · 12:47 left · 3 ops`, so the human always knows the tier is open.
3. **MCP: a separate server.**
   - The cheat tools live in their own MCP server (`btai-cheats`, a separate entry point). The ordinary `btai-mcp` contains **no** `cheat_*` tools, and a test enforces that.
   - The operator registers `btai-cheats` only when they want it.
   - In Claude Code's normal permission modes, each `btai-cheats` call is then a visible approval prompt. **Note:** this session runs in bypass-permissions mode, where that prompt doesn't appear. So layer 2 (human arming) is the gate we rely on.
4. **Explicit operator intent, enforced as far as it can be:**
   - Every preview takes a required **`operator_request`**: the operator's words, quoted. It's validated as non-empty and at least a minimum length, and written to the audit log. This doesn't prove intent, but it makes every cheat traceable to a claimed instruction, and an unrequested cheat stands out in review.
   - Cheats run only while **armed**, which only the human can do. That is the hard part: an agent that decided to cheat on its own would find the routes refusing (`403 cheats are not armed`).
   - The server instructions and tool docstrings state the policy. They're the soft layer, not the only one.
   - **Residual risk, stated honestly:** the agent runs with shell access on the same machine, so no in-process mechanism is unbreakable. It could edit `mod.json`, or synthesize keystrokes with computer-use tools. The design makes misuse take deliberate circumvention that is visible in the transcript and the logs; it doesn't make misuse impossible.

## 3. API and tools

Routes exist only when the capability is on:

| Route | Purpose |
|---|---|
| `GET /cheat/status` | capability, armed state, time and operations left, Ironman flag, the campaign's cheat marker |
| `POST /cheat/preview` | `{op, args, operator_request}` → validates and resolves defs; returns the plan (before values, intended after values, side effects) and a single-use **`plan_id`**. Changes nothing |
| `POST /cheat/execute` | `{plan_id}` → re-checks armed state and preconditions, mutates through the native methods, audits, and returns the before/after values |
| `POST /cheat/disarm` | closes the window |
| `GET /cheat/audit?limit=` | recent audit entries |

Ops for v1: `add_funds {amount}` (negative removes, floored at the debt limit), `add_component {id, count, damaged}`, `remove_component {id, count, damaged}`, `add_mech {mech_def_id, destination: "bay" | "storage", bay?}`, `complete_mech_work {}`, `complete_argo_upgrade {}`, `heal_pilots {}`.

MCP tools (`btai-cheats` server):
- `cheat_status`, `cheat_disarm`, `cheat_audit`
- `cheat_add_funds(amount, operator_request)`
- `cheat_add_component(id, count, operator_request, damaged=False)`, `cheat_remove_component(...)`
- `cheat_add_mech(mech_def_id, operator_request, destination="bay", bay=None)`
- `cheat_complete_mech_work(operator_request)`, `cheat_complete_argo_upgrade(operator_request)`, `cheat_heal_pilots(operator_request)`

Each convenience tool calls **preview, then execute** inside the MCP server, returning one result that holds the plan and the outcome. There's no second confirmation step, as you asked: the operator's request plus the human arming are the authorization. A `dry_run=True` argument returns only the preview, for "show me what this would do".

## 4. Duplicate-execution safety

These reuse the refit-plan and event-guard lessons:
- **Single-use plans.** `execute(plan_id)` consumes the plan. Replaying the same `plan_id` returns the **original result** (the ledger keeps it) and changes nothing. Retries of the same execution are therefore idempotent.
- **Plans expire** after 2 minutes, or as soon as anything they depend on changes: funds, the target bay, item counts, the queue contents. Each plan stores a fingerprint, as `RefitPlanner` does, and execution re-checks it.
- **Plans are bound to the campaign and to this load of it** (operator requirement, 2026-10-06). Each plan records:
  - **`campaign_id`** = `SimGameState.InstanceGUID`, a per-campaign GUID that survives save/load;
  - **`load_epoch`**, a BTBridge counter that goes up on every `SimGameState.Rehydrate` (any save load) and whenever a different `SimGameState` instance appears (new career, main menu round trip);
  - the **before-state fingerprint** above.

  Execute refuses with `409 campaign or load changed since preview` if the campaign or the epoch differs, so a plan previewed before a reload or campaign switch can never run against another state. It refuses with `409 state changed since preview` if the fingerprint differs.
- **A new request needs a new preview.** "Give me another 10M" is a new plan by design; an accidental double execute is not.
- The ledger is in memory and also mirrored to the audit file, so a duplicate is visible after the fact.

## 5. State integrity per operation

- **Funds:**
  - `AddFunds` with `updateBurndown:false, updateFundsGained:false`, so the earnings stats aren't polluted.
  - Guard against int overflow.
  - Refuse a removal that would push funds below `MaximumDebt`, which triggers game over at the next day tick.
- **Components:**
  - The def must exist in `DataManager` and be owned (DLC).
  - Typed `AddItemStat` / `RemoveItemStat` calls. A removal must not exceed the count, and damaged and intact items are separate stats.
  - Cap the count per operation (for example 50).
- **Mechs:**
  - **Copy the def:** `new MechDef(DataManager.MechDefs.Get(id), sim.GenerateSimGameUID())`. Never hand the shared DataManager instance to the company.
  - Run `ContentPackIndex.IsResourceOwned` on the mech, the chassis and the prefab *before* calling. `AddMech` otherwise returns silently.
  - **Bay:** must be `< GetMaxActiveMechs()` and empty. `AddMech(active:true)` **overwrites an occupied bay** (it only logs an error).
  - Use `forcePlacement:true, displayMechPopup:false` so no interrupt is queued.
  - Storage destination: `AddMech(-1, mech, active:false, ...)` stores it as a `MechDef` item. Readying it later is a normal work order, which takes days unless `complete_mech_work` is used.
  - Side effects: `COMPANY_MechsAdded` +1 and `SimGameMechAddedMessage`. These are normal for any acquired mech.
  - Fresh mechs' components have no `SimGameUID` (the refit gotcha). Our refit planner already assigns them.
- **Work and medbay completion:** only native completion handlers. Refuse while the mechlab is open, during a save, or during combat.
- **Argo completion:** refuse when nothing is building.
- **All operations:**
  - Main thread only, through the bridge pump.
  - Refuse when not idle: interrupt open, saving, travel transition, or in combat.
  - Refresh the affected UI if it's open (mechbay / timeline: `RoomManager.RefreshTimeline`).

## 6. Audit

- **`BTBridge.log`:** every attempt is logged with a `[CHEAT]` prefix: preview, execute, refusal, disarm and arm.
- **`cheats_audit.jsonl`** (next to the DLL, append-only), one JSON line per executed operation:
  - `utc`, `arm_session`, `plan_id`, `op`, `args`, `operator_request`
  - campaign identity: company name, game date, days passed, Ironman flag
  - **before/after** values for exactly what was touched: funds; the item stat counts per id/damaged; the bay contents (mech name and GUID); the queue entries completed (ids, descriptions, remaining cost before)
  - `result`
- **Marker in the campaign.** On the first executed cheat, add the company tag `btbridge_cheats_used` and increment a company stat `BTBRIDGE_CheatOps`. Both are saved, so a cheated campaign says so honestly. No event or requirement in the data references them; they're inert. This could be made configurable, but the default is on.

## 7. Saves and Ironman

- **No forced save in normal campaigns.** The operator keeps "reload my last save" as an undo. The game saves at its usual points, and the cheat marker goes with the next one.
- **Save-state awareness** (operator requirement, 2026-10-06). The game autosaves for many unrelated reasons (contract accepted or completed, arrival, events, the quarterly report), so "reload is undo" lasts only until the next save of any kind. BTBridge tracks saves:
  - a prefix on `TriggerSaveNow(reason, ...)` records the requested reason;
  - a postfix on `SimGameState.Dehydrate` marks the moment the campaign state was actually written into a save, whatever the path, manual saves included.

  Every executed cheat's audit record carries:
  - **`save_state`**: `clean` if no cheat was unsaved before this one, `dirty` if earlier cheats were also still unsaved; plus `last_save_utc` and `last_save_reason`;
  - **`persistence`**: `"unsaved: the next save of any kind (including an autosave) will make this permanent; reload a save from before last_save_utc to undo"`.

  When the next `Dehydrate` happens, BTBridge appends a **`persisted`** audit record listing every plan id that became permanent, with the save reason and time.

  `GET /cheat/status` shows the unsaved cheat count and the last save, so the operator can decide to reload before the game autosaves.
- An optional `save_after: true` on execute calls `TriggerSaveNow(SaveReason.MANUAL, QUEUE_IF_NEEDED)` for operators who want it locked in.
- **Ironman** (`sim.IsIronmanCampaign`): the single-slot autosave would make cheats irreversible, so cheats are **refused outright** in Ironman campaigns. There is no override setting.
  - Both preview and execute return `403 cheats are not available in Ironman campaigns`.
  - Arming still works, so the overlay can say why.

## 8. Tests

Pure logic, in `BTBridge.Logic`, tested in `BTBridge.Tests` and mutation-checked like the campaign rules:
- **Gate state machine:**
  - Capability off means nothing is ever armed.
  - Arming opens a window; expiry and the operation budget close it.
  - Disarm always works; leaving the campaign disarms.
  - Executing while disarmed is refused.
- **Plan ledger:**
  - Single use, and replay returns the original result.
  - Expiry; a fingerprint mismatch is rejected.
  - A different campaign id or load epoch is rejected.
- **Save tracker:**
  - `clean` → `dirty` transitions.
  - Persistence records list exactly the cheats executed since the previous save.
  - A save with no unsaved cheats records nothing.
- **Value guards:** funds overflow, the debt floor, count caps, removal ≤ count, bay range and occupancy.
- **Audit records:** required fields present; before/after captured.
- **Route registration:** `Routes.Build(cheatsEnabled:false)` contains no `/cheat/` paths, and `Build(true)` contains exactly the documented set. Building routes doesn't touch the game, so this runs in the test project.

Python:
- `btai-mcp` exposes no `cheat_*` tools; `btai-cheats` exposes exactly the documented set.
- Every cheat tool sends `operator_request` and fails without it.
- Convenience tools do preview then execute, and `dry_run` stops after preview.

In-game (in the combined test session, once built):
- Funds add/remove with before/after values; the burndown and earnings stats unchanged.
- A component stored and visible in storage.
- A mech into an empty bay; an occupied bay refused; storage destination works.
- Completing a refit early still reconciles storage.
- Argo completion produces the normal notification.
- Hotkey arm and disarm, with the overlay; expiry; a refusal when disarmed.
- Ironman refused, with no override.

## 9. Staged build plan

0. **Gate and infrastructure:** capability detection (args + mod.json), conditional route registration, `/cheat/status`, audit writer, campaign marker, tests.
1. **Arming:** hotkey listener in the frame pump, the overlay, expiry and budget, auto-disarm hooks, `/cheat/disarm`, tests.
2. **Plans:** preview/execute ledger with fingerprints, then **funds** and **components** (add/remove), tests.
3. **Mechs:** add to a bay or to storage, with copy, ownership and bay checks; mech parts.
4. **Time and work:** complete mechlab work, complete the Argo upgrade, heal pilots, all on the native paths.
5. **The `btai-cheats` MCP server** and its tests, plus docs. The ordinary server is untouched.
6. **Later, opt-in:** a session speed multiplier (option B); permanent tech-stat deltas (option C) with a reverse op; pilot XP/injuries; reputation; mech instant-repair (queue repair orders the normal way, then complete them).

## Operator decisions

1. **Decided (2026-10-06):** arming hotkey **Ctrl+Shift+F9**; default window **15 minutes**; no operation budget by default.
   - **The hotkey, window and budget are configurable only by the operator,** through `mod.json` (`CheatArmHotkey`, `CheatArmMinutes`, `CheatArmMaxOps`, read at startup) or an **in-game settings panel**.
   - **There is no bridge route to read-write or change them.** `/cheat/status` reports the current values read-only, so the agent can't widen its own window.
   - The panel is part of the BTBridge overlay (`OnGUI`), opened with **Ctrl+Shift+F10**. It offers window length (−/+), operation budget, and "press a key to rebind".
   - Panel changes are stored in `PlayerPrefs` under `BTBridge.Cheat.*` and take priority over `mod.json`. "Reset" clears them back to `mod.json`.
   - The panel and its hotkey exist only when the cheat capability is on.

2. **Decided (2026-10-06):** the campaign marker is **on by default**: company tag `btbridge_cheats_used` plus the `BTBRIDGE_CheatOps` counter, added on the first executed cheat.

3. **Decided (2026-10-06):** cheats are **refused outright in Ironman campaigns**, with no override setting.
4. **Decided (2026-10-06):** **build speed in v1 is option A only**: finish now, for mechlab work, the Argo upgrade and the medbay.
   - Option B (session multiplier) and option C (permanent stat deltas) stay in the "later, opt-in" stage.

All design questions are settled; the layer is ready to build when the operator says so.
