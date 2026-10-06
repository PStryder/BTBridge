# Campaign internals: driving the strategic layer without the UI

These are research notes from the decompile of v1.9.1, for the campaign layer between missions. Abbreviations:
- `SGS` = `BattleTech/SimGameState.cs` (about 13.7k lines)
- `SIM` = `BattleTech.UI/SimGameInterruptManager.cs`

**Status: researched, not built.** Line numbers are approximate pointers into one decompile run. Anything marked *(untested)* was inferred from code, not seen in-game.

All calls run on Unity's main thread, through the bridge's existing pump.

## Principles

- **One queue for modals.** Every campaign interrupt (events, notifications, rewards, the quarterly report, mech placement) goes through `SimGameInterruptManager`. The bridge exposes "what's waiting" and answers it, so an agent never has to find a button.
- **Dismiss through the module's own handler, never the queue entry alone.** Closing only the entry leaves the UI module on screen. Several handlers also **apply game effects on close**:
  - the 'Mech Purchased Continue action is what puts the mech in a bay (`SGS:10138`);
  - `RewardsPopup.OnClose` is what adds reward items;
  - the quarterly report's Accept is what applies the per-quarter morale change.
- **Call the underlying logic, not UI confirmation dialogs.** `GenericPopupBuilder` popups (purchase/hire confirmations, break-contract warnings, retreat confirm) are outside the queue. Replicate their checks and call the method they guard.
- **Wait for async steps:**
  - contract generation (coroutine);
  - lance-configurator roster population;
  - star-map A* pathfinding (stepped per frame);
  - travel camera transitions;
  - loading curtains and scene loads;
  - event result display (0.2 s);
  - conversation responses (1 s).

## Time

- **`SGS.Update` (:1329) is the clock.** Each frame it goes through these gates in order:
  1. Resolves a completed contract once no interrupt is open.
  2. Takes a pending milestone contract.
  3. Stops if `!TimeMoving || TravelManager.InTransition`.
  4. Shows the next queued interrupt and stops (**no day passes while anything is queued**).
  5. Otherwise accumulates `Constants.Time.DayElapseTimeNormal` and calls `OnDayPassed()` (private, :1604).
- **`TimeMoving` (:914)** = `CurRoomState == SHIP && RoomManager.TimeMoving() && canTimeElapse`.
  - Play and pause with `SetTimeMoving(bool)` (:11895) or `StopPlayMode()`.
  - `PauseTimer`/`ResumeTimer` drive `canTimeElapse`, a separate gate. It may stay false after some contract actions until the next save; check it and resume if needed.
- **Advancing N days:**
  1. Make sure the room is SHIP, the queue is empty and `canTimeElapse` is true.
  2. Optionally lower `DayElapseTimeNormal` (it's mutable; keep it ≥ 0.1 s).
  3. Call `SetTimeMoving(true)`.
  4. A postfix on `OnDayPassed` counts days and stops at N.
  - "Until next event" needs nothing extra: every interrupt's `Render()` calls `StopPlayMode()`.
  - Don't call `OnDayPassed(n > 0)`. It skips travel, events and milestones.
- **`OnDayPassed` does, in order:** travel step, daily event roll (`QueueEventTest`), injuries, temp results, mechlab queue, Argo upgrades, game-over check, contract expiry, system day, pilot XP, quarter rollover (`DeductQuarterlyFunds` + financial report), flashpoint and reputation days.

## Interrupts (the highest-risk area)

- **Access:** `sim.InterruptQueue` exposes `IsOpen` (private `curPopup != null`), `HasQueue`, `ContainsPopupOfType` and `GetHighestPriority`. Read `curPopup`/`popups` by reflection to get the type and `parameters`.
- **Order:** sorted by `InterruptType` value, then uid: Loss, GenericPopup, PauseNotification, MechPlacement, EventTest, EventPopup, … AutoSave(99). A Loss popup clears the queue.
- **Display is blocked while:** a popup is open, a travel transition is running, a conversation is on, a video is playing, character creation or credits are up, a save is in progress, **or `MechBayRoom.mechLabOpen`**. A mechlab left open stalls everything.
- **Enumerating visible modules:** `UIManager.Find(pred)` (public). Walk it collecting every visible module; that also catches popups outside the queue.
- **Generic fallback:** each module's public `HandleEnterKeypress()` / `HandleEscapeKeypress()`.

| Interrupt | Resolve with | Watch out |
|---|---|---|
| AutoSave, EventTest, FlashpointMilestoneCheck | they close themselves | Save sets `Saving` and pauses the timer |
| PauseNotification, TravelContractNotification | `PauseNotification` module: Enter = primary, Escape = secondary or primary. Actions sit in `parameters[4]` (primary) and `parameters[6]` (secondary) | **'Mech added: the action places the mech.** Arrival: primary = Visit Store (switches room); prefer Continue. Breadcrumb arrival = Proceed / Not Yet |
| GenericPopup, MechwarriorHasDied | `GenericPopup`: buttons are private; Enter = last button, Escape = first or cancel | "Memorial Wall" switches room; pick Confirm |
| EventPopup | `SGEventPanel` (see Events) | |
| FinancialReport | `SetExpenditureLevel(level, updateMorale: true)` then `SGCaptainsQuartersStatusScreen.Dismiss()` | `Dismiss()` alone skips the quarterly morale change |
| MechPlacementPopup (bays full) | store: `UnreadyMech(-1, mech)`, or for a chassis `AddMech(-1, …, active:false, forcePlacement:true, displayMechPopup:false)`, then the private `Close()`; or replicate `ConfirmStoreMech`/`ConfirmScrapMech` | No keyboard fallback |
| RewardsPopup | `OnClose()` / `ReceiveButtonPress("Complete")` once the private `allShopDefItems != null` (loads async) | **Items are added on close** |
| FlashpointEnteredSystem | `SG_FlashpointInfoPopup.OnConfirm()` / `OnCancel()` | |
| FlashpointEnd, FlashpointsExist, NewStarmapTech, HeavyMetal, CareerModeEnd, Win, Loss | top module's `HandleEnterKeypress()` / `ReceiveButtonPress` | Loss returns to the main menu |
| Conversation | see Events | |

## Events

- **Data:** `SimGameEventDef` → `Options[]`, each a `SimGameEventOption`. An option has:
  - `Description.Name` (the button text) and `.Details` (requirement text);
  - `RequirementList`;
  - `ResultSets[]`, weighted, picked with `NetworkRandom`. Each set holds `Results[]`: stats, tags, actions, forced events, temporary results.
- **Text:** interpolate with `Interpolator.Interpolate(text, sim.Context)`. The tracker sets `TargetMechWarrior` and similar objects on the context.
- **Capture the pending event** with a postfix on `SGEventPanel.SetEvent(evt, scope, tracker, entry)`.
- **To answer:**
  1. Check `sim.MeetsRequirements(opt.RequirementList)` yourself (`OnOptionSelected` doesn't).
  2. Call `panel.OnOptionSelected(opt)` **once**. Results are applied immediately, then a 0.2 s coroutine switches to the result view, and a second call inside that window applies the results again.
  3. Wait for the result state, then call `panel.Dismiss()`.
- **Readable outcome:** `sim.BuildSimGameResults(set.Results, sim.Context)`.
- **Conversations** (story): `sim.ConversationManager.IsOn`. Poll `IsInputLocked()` and the private `isAnimating`, then:
  - `SelectResponse(link.index)` when enabled responses exist (`sim.DialogPanel.Responses`, or the private `responseData`);
  - otherwise `InputContinue()`.
  - The conversation ends by itself.

## Contracts: the mission loop

- **List:** `sim.GetAllCurrentlySelectableContracts()` (:5181), the same list the Command Center shows: flashpoint contract, global contracts, system contracts, breadcrumbs, and an arrived travel contract.
  - If `!CurSystem.InitialContractsFetched`, call `CurSystem.GenerateInitialContracts(cb)` and wait for the callback.
  - Filter on `sim.ContractUserMeetsReputation(c)`; accepting doesn't recheck it.
- **Fields:**

  | Data | Source |
  |---|---|
  | Name | `Name` |
  | Type | `ContractTypeValue`, `GetContractTypeString(sim)` |
  | Difficulty | `Override.GetUIDifficulty()` in half-skulls |
  | Employer / target | `GetTeamFaction("ecc8d4f2-74b4-465d-adf6-84445e5dfc230")` / `("be77cadd-e245-4240-a93e-b99cc98902a5")` |
  | Pay | `InitialContractValue`; max via `GetScaledCBillValue` |
  | Salvage | `SalvagePotential` (+ `ContractFloorSalvageBonus`) |
  | Reputation | `GetMaxPossibleReputation` |
  | Negotiation / lance locks | `CanNegotiate`, `CanLanceConfigure` |
  | Lance limits | `Override.maxNumberOfPlayerUnits`, `lanceMin/MaxTonnage`, `mechMin/MaxTonnages[4]` |
  | Map | `mapName`, `ContractBiome` |
  | Expiry | `UsingExpiration`, `ExpirationTime` |
  | Flags | `IsPriorityContract`, `IsStoryContract`, `IsFlashpointContract`, `Override.travelOnly`, `TargetSystem` |
- **Negotiate and accept** *(untested)*:
  1. `sim.SetSelectedContract(c, c.Override.travelOnly)`.
  2. `c.SetNegotiatedValues(pay, salvage)`. Fractions: the sum should be ≤ 1, and the reputation share = 1 − sum (only for employers that gain reputation; otherwise salvage = 1 − pay). For non-negotiable contracts use `Override.negotiatedSalary` / `negotiatedSalvage`.
  3. `travelOnly` → `sim.PrepareBreadcrumb(c)` (async route). Otherwise go to lance and launch.
  - If a travel contract is already active, accepting another breaks it (reputation penalty). Report it rather than doing it silently.
- **Lance and launch** *(untested; option B preferred)*:
  - **B (keeps the game flow):** prefix `LanceConfiguratorPanel.CreateLanceConfiguration` to return the agent's lance. Call `sim.StartLanceConfiguration()`, wait for `LC.Initialized`, then call the public `LC.ContinueConfirmAudioCallback(null, AkCallbackType.AK_EndOfEvent, null)`.
    - `OnContractReady` calls `FillContractLance`, which re-reads `CreateLanceConfiguration`.
    - This also covers forced, story and flashpoint contracts, and skips the warning popups.
  - **A (bypass):** build a `LanceConfiguration` (`AddUnit(Player1Guid, mechDef, pilotDef)`) into `c.Lances`, save, then `sim.StartContract(c)`. Not for forced or no-configure contracts.
  - **Validation to replicate:**
    - per-slot and lance tonnage (`MechValidationRules.MechTonnageWithinRange` / `LanceTonnageWithinRange`);
    - `ValidateMechCanBeFielded(sim, mech)`;
    - `Pilot.CanPilot` (no injuries, no timeout);
    - no duplicates;
    - `maxNumberOfPlayerUnits`;
    - `Contract.Accept()` throws if the lance is invalid.
- **In mission:**
  - **No deployment-zone choice** in vanilla.
  - **Withdraw:** `combat.MessageCenter.PublishMessage(new MissionRetreatMessage(FindObjectOfType<EncounterLayerData>().IsGoodFaithEffort))`. The UI hides it for priority, story and skirmish missions.
  - **Mission end:** `CombatHUDMissionEnd.ReceiveButtonPress("Exit")` loads `MissionResultLauncher`. An end dialogue (`InterruptDialogSequence`) may need advancing first.
- **After-action:** `MissionResults.AdvanceAARState()` steps through outcome, lance results and salvage. Get the module with `FindObjectOfType` or a postfix on `Init`.
  - Contract results: `AAR_ContractResults_Screen.OnCompleted()`. Unit results: `AAR_UnitsResult_Screen.OnCompleted()`.
  - **Salvage:**
    - Items: `contract.GetPotentialSalvage()` (stacked `SalvageDef`: Id, Damaged, Count, ComponentType).
    - Picks allowed: `FinalPrioritySalvageCount` (≤ 8).
    - Submit once: `contract.FinalizeSalvage(picks)`, then `AAR_SalvageScreen.OnCompleted()`.
    - `FinalizeSalvage` doesn't cap the number of picks and appends on every call, so enforce the cap and call it only once.
  - **Back in the sim**, `ResolveCompleteContract` applies funds, reputation, XP, salvage, lost mechs and dead pilots. Read them from the contract:
    - `State`, `MoneyResults`, reputation results, `ExperienceEarned`
    - `MissionObjectiveResultList`, `PlayerUnitResults`, `KilledPilots`, `LostMechs`, `SalvageResults`
    - or hook `SimGameContractCompleteMessage`.

## Navigation

- **Data:** `sim.StarSystems`, `CurSystem`, and `Starmap.GetSystemByID(id)` → `StarSystemNode` with `.AdjacentSystems` and `.Cost` (refuel days).
  - System: owner, tags, jump distance, `TravelRequirements`, and shops (`CanUseSystemStore` / `CanUseFactionStore` / `CanUseBlackMarketStore`).
  - Reachability: `Starmap.CanTravelToNode(id)`; `sim.GetTravelRestrictions()` gives the reasons.
- **Cost and time:**
  - `JumpShipCost` per jump, waived on a travel contract.
  - Days = `DistanceToJumpship()` + Σ node `Cost` + destination `JumpDistance`.
- **Travel** *(untested)*:
  1. `Starmap.SetSelectedSystem(id)`, then wait for routing (`StarSystemRouted` / `PotentialPath`).
  2. Check requirements, funds ≥ `ProjectedTravelCost`, and that there's no travel contract to break.
  3. `Starmap.SetActivePath()`, then `sim.SetSimRoomState(DropshipLocation.SHIP)` (required: travel steps defer unless the room is SHIP).
  4. Run time (see Time). Each travel state change animates, so wait for `!TravelManager.InTransition`.
  5. Arrival queues a PauseNotification, a flashpoint popup, or the breadcrumb Proceed / Not Yet.

## Company management

- **Pilots:**
  - Roster: `sim.PilotRoster`, `Commander`. `Pilot` has Gunnery/Piloting/Guts/Tactics, `UnspentXP`, injuries, `CanPilot` and abilities.
  - **Spending XP** (copy `SGBarracksAdvancementPanel.SetTempPilotSkill`), one pip at a time:
    1. `def = pilot.ToPilotDef(true)` and refresh abilities.
    2. Add the newly unlocked abilities via `sim.AbilityTree[stat][level]` + `CanPilotTakeAbility`.
    3. `np = new Pilot(def, guid, true)`, then `np.SpendExperience(0, "Advancement", GetLevelCost(level))` and `np.ModifyPilotStat_Barracks(0, "Advancement", stat, level+1)`.
    4. `sim.UpgradePilot(np)`.
    - Primary abilities are permanent (2 primaries + 1 specialist), so confirm with the player.
  - **Hire:** `CurSystem.AvailablePilots`, priced with `GetPurchaseCostAfterReputationModifier(GetMechWarriorHiringCost(def))`. `CurSystem.HirePilot(def)` does **no checks**, so redo them: in system, roster below max, MRB and morale gates, funds.
  - **Fire:** `DismissPilot`. **Medbay** is automatic.
- **Store:**
  - Shops: `CurSystem.SystemShop` / `FactionShop` / `BlackMarketShop`, inventory in `.ActiveInventory`, prices from `GetPrice(item, type, ThisShopType)`.
  - **Buy:** `shop.Purchase(id, IsInfinite ? Normal : Special, item.Type)`. **Check funds first:** a `Special` purchase lowers stock before checking money.
    - A bought mech queues 'Mech Purchased, and it lands in a bay **only when that notification is dismissed**. If the bays are full, a MechPlacementPopup follows.
  - **Sell:** `shop.SellInventoryItem(item)`, one unit at a time (list sellables with `GetAllInventoryShopItems()`). Mech parts can't be sold.
- **Finances:**
  - `Funds`, `Morale`, `GetExpenditures()`, `DayRemainingInQuarter`.
  - Expense level (`EconomyScale`): set once per quarter through the report, using `SetExpenditureLevel(level, true)`. Setting it elsewhere with `true` changes morale again.
  - Reputation: `GetReputation(faction)`. MRB: `GetCurrentMRBLevel()`. Game over: `Funds < Constants.Story.MaximumDebt`.
- **Argo upgrades:**
  - Defs: `DataManager.ShipUpgradeDefs`.
  - Available when not owned, not in progress, and prerequisites owned (`HasShipUpgrade(RequiredModules)`).
  - Price: `ceil(PurchaseCost * ArgoUpgradeCostMultiplier)`.
  - `sim.QueueArgoUpgrade(u)` builds **one at a time**; queueing another cancels and refunds the current one. Enforce `MeetsRequirements(u.Requirements)`, which the UI ignores.
- **Flashpoints:**
  - `AvailableFlashpoints`, `ActiveFlashpoint`; accept with `SetActiveFlashpoint(fp)`.
  - Milestone choices arrive as ordinary EventPopups.
  - Milestone contracts come through `PendingMilestoneContract` and go into the normal lance/launch flow.

## Idle detection

The campaign is waiting for the player when all of these hold:
- `UXAttached && HasSimShipBeenSet && !Saving && Game.Combat == null`
- `CompletedContract == null && PendingMilestoneContract == null`
- `!InterruptQueue.IsOpen && !HasQueue`
- `!ConversationManager.IsOn && !VideoPlayerActive`, character creation not active, `Credits == null`
- `!TravelManager.InTransition && !TimeMoving`
- `!MechBayRoom.mechLabOpen`
- no visible `PauseNotification`, `GenericPopup`, `SGEventPanel` or `MechPlacementPopup` (via `UIManager.Find`)

Debounce for a frame or two. Then branch on `CurRoomState` and `TravelState`.

## Build plan

1. **The interrupt layer first.** Everything else stalls without it:
   - `GET /sim/status`: the idle predicate plus what is waiting.
   - `GET /sim/interrupt`: the waiting interrupt, typed and readable.
   - `POST /sim/interrupt`: answer it through the module's own handler.
   - Events and conversations, with the double-apply guard.
2. **Time:** `POST /sim/time {days | until: "event"}`.
3. **The contract loop:** list, accept (negotiated), set lance and launch (option B), withdraw, mission end, salvage, results.
4. **Navigation:** systems, route preview, travel.
5. **Company:** pilots (XP, hire, fire), store (buy and sell), Argo upgrades, finances.
6. **Flashpoints.**
