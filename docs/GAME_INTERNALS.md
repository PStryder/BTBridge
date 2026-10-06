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

`EncounterLayerData.CreatePlayerOneTeam()` (private) builds `new AITeam(..., substitutingforHuman: true, isMultiplayer)` instead of a human `Team` only when **both** of these are true in `StreamingAssets/data/debug/settings.json`:

```json
"testToolsEnabled": true,
"playerOneIsAIControlled": true
```

**BTBridge doesn't do this,** because `TestToolsEnabled` touches 84 places: debug stats, AttackDirector effects, save structure. Instead, a prefix on `CreatePlayerOneTeam` returns `new AITeam("Player 1", color, Player1Guid, true, combat, substitutingforHuman: true, isMultiplayer: false)` with `FactionValue = GetPlayer1sMercUnitFactionValue()`.
- `substitutingforHuman` keeps `PlayerControlsTeam` true. Objective success and failure handling needs it.
- The method only runs for fresh missions, not for combat loaded from a save. Control modes are latched there.

Things that come with an AI-driven player team:
- **Behavior tree.** Units spawn with `DoNothingTree`; the real tree is only assigned at spawn when the team is already an AITeam. Fix: `team.SetBehaviorTree(BehaviorTreeIDEnum.CoreAITree)` before activation (prefix on `AITeam.TurnActorProcessActivation`). The tree id is the private field `BehaviorTree.behaviorTreeIDEnum`.
- **HUD.** `Team.TurnActorProcessActivation` still sets `IsActive`, so `CombatSelectionHandler` would let a human order the same units. Prefix `TrySelectActor` and `AutoSelectActor` to refuse.
- **Morale and inspiration.** `Team` blocks morale gain for `this is AITeam` unless `MoraleDef.CanAIBeInspired`, so an AI-driven player lance may get less morale. Not handled yet.

## Combat: agent decisions (verified in-game)

- **Activation flow.** One AITeam activation is one unit (`selectCurrentUnit`, private, which can be overridden with a postfix). After a move completes, `think()` asks again for the same unit.
  - The tree's `IsMovementAvailableForUnitNode` and `IsAttackAvailableForUnitNode` pick the stage. The AI never attacks when not interleaved, i.e. out of combat.
  - Out of combat, a non-sprint move auto-braces and ends the activation.
  - Sprinting forfeits the attack.
- **Ace Pilot.** `AbstractActor.CanMoveAfterShooting` lets a unit fire first and still move. `OrderSequence.ConsumesActivation` stays false, so the next decision is a move stage with `fired = true`. Seen in-game: a Locust fired, then sprinted to 7 evasion pips.
- **The think clock.** `planningStartTime` is set only at activation start. `Float_MaxThinkSeconds` covers the whole activation, animations included, and when it runs out the unit braces. Restart it while the agent deliberates, via reflection on the private field.
- **Returning null is safe.** The stock tree itself returns "running" for many frames. In-game, decisions sat open for minutes with no side effects.
- **Capturing the suggestion.** Postfix `getInvocationForCurrentUnit`, keep its result, and return null. Prefix `makeInvocationFromOrders` (private) to record the `OrderInfo`, which describes the suggestion.
  - `AbstractActorMovementInvocation` copies its waypoints at construction, so a held suggestion stays valid even if other paths are computed afterwards.
  - Building the agent's own orders through `makeInvocationFromOrders` (via reflection) keeps the game's exact semantics. That includes the guards that turn an illegal move or attack into a brace.
  - A team-wide reserve (`ReserveActorInvocation` whose `targetGUID` is the team's GUID) is left to the stock AI.
- **Movement grids are sparse lattices.** `PathNodeGrid.GetValidPathNodeAt(pos, maxCost)` is an exact-cell lookup and misses almost any arbitrary point. Snap to the nearest of `getGrid(moveType).GetSampledPathNodes()` with `IsValidDestination` and `CostToThisNode` in `[0, budget)`, as the movement UI does (`GetClosestPathNode`).
  - Budgets are `MaxWalkDistance`, `MaxSprintDistance` and `MaxBackwardDistance`.
  - Check `Pathing.ArePathGridsComplete` first.
  - Jumps: snap with `HexGrid.GetClosestPointOnGrid` and `MapMetaData.GetLerpedHeightAt`, then check `JumpPathing.IsValidLandingSpot(pos, allActors)`. `MechJumpInvocation` doesn't validate.
- **Line of sight isn't line of fire.** `VisibilityLevel.LOSFull` (sensors) can hold while `Combat.LOS.GetLineOfFire(...)` is `LOFBlocked` for the weapon mounts. `Weapon.WillFireAtTarget` requires the unit's own LOS plus LOF, in arc and in range. Seen in-game: a Locust on a ridge saw an enemy at 89 m and couldn't fire, so the stock AI chose melee.
- **Weapon uids are per unit** (`"0"`, `"1"`, `"3"`...), so the same uid list is valid on several mechs. Orders must name their unit.
- **The influence map.** `BehaviorTree.influenceMapEvaluator.WorkspaceEvaluationEntries[0..firstFreeWorkspaceEvaluationEntryIndex)` holds the AI's scored positions.
  - Each entry has `Position`, `Angle`, `GetBestMoveType()`, `GetHighestAccumulator()`, and per-factor `ValuesByFactorName` (`RegularValue * RegularWeight`).
  - The list is reused, so re-check each entry against the live grids.
  - Out of combat, the tree produces only one unscored candidate.
- **Fog of war.** `team.VisibilityCache.VisibilityToTarget(actor).VisibilityLevel` gives a team's view. `Blip*` levels mean position only. `PreviouslyDetectedEnemyUnits` has no stored position, so BTBridge records its own last sighting every 30 frames.
- **Also useful:**
  - `HitLocation.GetAttackDirection(attackPos, target)`: which face of the target a shot lands on.
  - `MapMetaData.GetPriorityDesignMaskAtPos(pos)`: the terrain or cover `DesignMaskDef`, with to-hit, damage-taken, stability and heat-sink modifiers.
  - `AbstractActor.InitiativeToString(phase)`: the number shown on the initiative bar (stored values are inverted).

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

- **Don't use `MechValidationLevel.MechLab` for builds that didn't come through the UI.** It skips inventory slots, hardpoints, allowed locations and the one-EW/one-prototype limits, because the mechlab's drag-and-drop makes those placements impossible. Use `Full`. Confirmed in-game: an AC/20 in the head passes `MechLab` apart from tonnage and ammo, and fails `Full` on slots and hardpoints.
- The mechlab won't save a build that has `ValidManifest`, `Overweight`, `WeaponsMissing`, `InvalidInventorySlots`, `InvalidHardpoints`, `InvalidJumpjets` or `StructureDestroyed` errors. `Underweight`, `AmmoMissing`, `AmmoUnneeded` and `StructureDamaged` are only warnings.

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

Gotchas, all confirmed in-game:
- **Components on starting mechs, and on newly acquired ones, have no `SimGameUID`.** `MechBayPanel` assigns UIDs just before opening the mechlab. If a removal or move order is built without one, `CreateComponentInstallWorkOrder` makes up a fresh UID that matches nothing on the mech. The game charges for the order and then logs `ML_InstallComponent ... had an invalid mechComponentID, skipping` at completion. Fix: assign any missing UIDs first.
- A move is a removal (`newLocation = None`) followed by an install of the **same UID**. `ML_InstallComponent` keeps the removed part in `WorkOrderComponents` when a later sub-entry references it, so the part never passes through storage.
- Copying with `new MechComponentRef(other)` keeps the UID but **drops `Def`**. `CreateComponentInstallWorkOrder` reads `.Def`, so restore it with `SetComponentDef`.
- Removals and armor changes cost 0 tech points in vanilla (`UninstallTechPoints`, `ArmorInstall*` = 0), so they complete as soon as they're queued. Removed parts show up in storage right away, and only the installs wait. Removals still cost the install fee in C-bills.
- Cancelling a queued order in the Argo's work queue refunds it in full (`CancelWorkOrder`), including orders created over the bridge.
- A weapon's hardpoint slot is the number of weapons already in that location (`MechLabLocationWidget`).

## Skirmish custom mechs and lances (writes)

`ActiveOrDefaultSettings.CloudSettings.CustomUnitsAndLances` is a `SkirmishUnitsAndLances` object with:
- `GetValidMechs()`
- `AddOrUpdateMechDef`, `RemoveMechDef`
- `AddOrUpdateLanceDef`, `RemoveLanceDef`

Persist changes with `ActiveOrDefaultSettings.SaveUserSettings()`. Custom mechs carry the `unit_custom` tag. See `SkirmishMechBayPanel.OnMechLabConfirm`, `SaveMech` and `SaveLance`.

- `GetMechDef(id)` throws a NullReferenceException when the id is missing. Check `ContainsMechDef(id)` first.
- Skirmish pilots (tag `pilot_release_skirmish`) are only loaded when the skirmish mechbay opens. To load them without the UI: `dm.CreateLoadRequest(cb, true)`, then `AddAllOfTypeBlindLoadRequest(BattleTechResourceType.PilotDef, true)`, then `ProcessRequests()`. The load is asynchronous.
- A lance is `new LanceDef(description(cost = sum of mech Description.Cost), 0, TagSet("lance_type_custom", "lance_release", "lance_bracket_skirmish", GetLanceBracketTag(value)), units)`.

## Data on disk

`BattleTech_Data/StreamingAssets/data/{weapon,ammunitionBox,ammunition,heatsinks,jumpjets,upgrades,chassis,mech,lance,...}`.
- The game's parser tolerates trailing commas, and a few shipped files rely on that.
- **DLC content isn't in the loose JSON.** Heavy Metal, Flashpoint and Urban Warfare items live in `data/assetbundles/{heavymetal,flashpoint,urbanwarfare,shadowhawkdlc}`.
- Mechs tagged `BLACKLISTED` are special or story builds, and some of them are deliberately illegal.
