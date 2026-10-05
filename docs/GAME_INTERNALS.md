# BattleTech internals: what we hook and why

These are notes from decompiling `Assembly-CSharp.dll` for game v1.9.1 (build 686R). Class and method names are exact. Line numbers aren't recorded because they shift between decompiler runs.

## Decompiling

```powershell
dotnet tool install -g ilspycmd --version 9.1.0.7988   # newer versions fail to install on SDK 9
$env:DOTNET_DefaultStackSize = "8000000"               # without this ilspycmd stack-overflows partway
$m = "F:\SteamLibrary\steamapps\common\BATTLETECH\BattleTech_Data\Managed"
ilspycmd -p -o <outdir> -r $m "$m\Assembly-CSharp.dll"   # ~5,650 files, ~2 minutes
```

## Runtime

- Unity 2018.4.2, Mono, .NET 4.x profile. The mod targets `net472` with C# 7.3.
- The game ships Harmony 1.2.0.1 (`0Harmony.dll`) and its own mod loader, `BattleTech.ModSupport.ModLoader`, which is off by default (PlayerPrefs `ModsEnabled`).
- The HBS loader and ModTek read the same `mod.json` fields: `Name`, `DLL`, `DLLEntryPoint`, `Settings`. Both call the entry point with parameters matched by name, so `Init(string modDir, string settingsJson)` works under either.
- `UnityGameInstance.Update` (private) runs every frame for the whole session: menus, campaign and combat. The bridge drains its main-thread queue from a postfix on it.
- Global access: `UnityGameInstance.BattleTechGame` returns a `GameInstance`, which has `.Simulation` (`SimGameState`), `.Combat` (`CombatGameState`) and `.DataManager`.

## Combat AI: the decision point

`AITeam.think()` runs every frame while the AI team is active:

1. If any invocations are pending, it publishes them and returns.
2. Otherwise it calls **`getInvocationForCurrentUnit()`** (private). That runs `currentUnit.BehaviorTree.Update()`, which produces an `OrderInfo`. **`makeInvocationFromOrders(unit, order)`** then turns the order into an `InvocationMessage`.
3. **If it returns null, the AI is "still thinking" and gets polled again next frame.** That is the hook for an external agent: a prefix returns null until the agent has answered.
4. The result goes to `Combat.MessageCenter.PublishMessage(invocation)`.

Watch out for `Float_MaxThinkSeconds`. Inside `getInvocationForCurrentUnit`, if planning runs past this limit the unit gives up and braces. A prefix that skips the original method also skips this check.

How `OrderType` maps to invocations:

| OrderType | Invocation |
|---|---|
| Move, SprintMove | `Pathing.UpdateAIPath(dest, lookAt, moveType)` then `AbstractActorMovementInvocation(unit, false)` |
| JumpMove | `MechJumpInvocation(mech, dest, rotation, false)` |
| Attack | `AttackInvocation(unit, target, weapons)` (`ventHeatBeforeAttack`). Melee uses `MechMeleeInvocation`, death-from-above uses `MechDFAInvocation` |
| MultiTargetAttack | `AttackInvocation` plus `AddSubInvocation(target, weapons)` |
| CalledShotAttack | `AttackInvocation(..., MeleeAttackType.NotSet, targetLocation)` |
| Brace, VentCoolant | `ReserveActorInvocation(unit, DONE, round)` |
| Stand / StartUp | `MechStandInvocation` / `MechStartupInvocation` |
| ActiveAbility, ActiveProbe, ClaimInspiration | `make*Invocation` helpers |

The human UI publishes the same invocation types (`SelectionState.PublishInvocation`). Invocations have `ToJSON`/`FromJSON` because multiplayer replicates them.

## Making the AI drive the player's team

`EncounterLayerData.CreatePlayerOneTeam()` builds `new AITeam(..., substitutingforHuman: true, isMultiplayer)` instead of a human `Team` only when **both** of these are true in `StreamingAssets/data/debug/settings.json`:

```json
"testToolsEnabled": true,
"playerOneIsAIControlled": true
```

`testToolsEnabled` turns on other debug behavior as well. Back up the original file before changing it.

## Campaign mechbay

`SimGameState`:
- `ActiveMechs` and `ReadyingMechs`: `Dictionary<int bay, MechDef>`.
- `GetAllInventoryStrings()`: company stat keys. `CompanyStats.GetValue<int>(key)` gives the count. Keys look like `Item.{ResourceType}.{id}[.DAMAGED]` or `Item.MECHPART.{mechDefId}`.
- `GetAllInventoryItemDefs()` returns one `MechComponentRef` per distinct item, with no counts. `GetAllInventoryMechDefs()` returns stored chassis and mech parts.
- `MechLabQueue` is a `List<WorkOrderEntry>`. The other fields used are `Funds`, `MechTechSkill`, `GetMaxActiveMechs()`, `CompanyName` and `CurrentDate`.

`MechLabPanel` (one instance, reused for campaign and skirmish):
- Every open path goes through `LoadMech(MechDef)`. Closing goes through `ExitMechLab()`.
- **`CreateMechDef()` snapshots the build in progress with no side effects.** `ValidateLoadout()` also repaints the UI alerts, so don't use it for reads.
- Useful public fields: `originalMechDef`, `activeMechDef`, `baseWorkOrder`, `dataManager`, `IsSimGame`, `Modified`.

Validation: **`MechValidationRules.ValidateMechDef(MechValidationLevel, DataManager, MechDef, WorkOrderEntry_MechLab)`** is static and needs no UI. It returns `Dictionary<MechValidationType, List<Text>>`.

Stats bars: `MechStatisticsRules.Calculate{Tonnage,CBillValue,Firepower,HeatEfficiency,Durability,Movement,Range,Melee}Stat(mechDef, ref cur, ref max)`. `CalculateTonnage` reports max = 100 for the UI bar, so take the real cap from `Chassis.Tonnage`.

Tonnage formula: `Chassis.InitialTonnage + sum(assigned armor) / (ARMOR_PER_TENTH_TON * 10) + sum(component tonnage)`, with `ARMOR_PER_TENTH_TON = 8` (80 armor per ton).

### Campaign refit pipeline (writes)

The mechlab builds a `WorkOrderEntry_MechLab` named "MechLab-BaseWorkOrder" with these sub-entries:
- `Sim.CreateComponentInstallWorkOrder(mechGuid, componentRef, newLocation, previousLocation)`. A `newLocation` of `None` means removal.
- `Sim.CreateMechArmorModifyWorkOrder(mechGuid, location, armorDiff, front, rear)`.
- Repair entries.

On confirm, `MechBayPanel.OnMechLabComplete(entries, nickname, refund)`:
- `Sim.MechLabQueue.Add(entry)` for each entry.
- `Sim.InitializeMechLabEntry(entry, refund)`. This charges funds and moves items out of storage (`MoveWorkOrderItemsToQueue`).
- `Sim.UpdateMechLabWorkQueue(passDay: false)`.
- `TriggerIronManSave()` runs in `MechLabPanel.DoConfirmRefit`.

## Skirmish custom mechs and lances (writes)

`ActiveOrDefaultSettings.CloudSettings.CustomUnitsAndLances` is a `SkirmishUnitsAndLances` object with:
- `GetValidMechs()`
- `AddOrUpdateMechDef`, `RemoveMechDef`
- `AddOrUpdateLanceDef`, `RemoveLanceDef`

Persist changes with `ActiveOrDefaultSettings.SaveUserSettings()`. Custom mechs carry the `unit_custom` tag. See `SkirmishMechBayPanel.OnMechLabConfirm`, `SaveMech` and `SaveLance`.

## Data on disk

`BattleTech_Data/StreamingAssets/data/{weapon,ammunitionBox,ammunition,heatsinks,jumpjets,upgrades,chassis,mech,lance,...}`.
- The game's parser tolerates trailing commas, and a few shipped files rely on that.
- **DLC content isn't in the loose JSON.** Heavy Metal, Flashpoint and Urban Warfare items live in `data/assetbundles/{heavymetal,flashpoint,urbanwarfare,shadowhawkdlc}`.
- Mechs tagged `BLACKLISTED` are special or story builds, and some of them are deliberately illegal.
