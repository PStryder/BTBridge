using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.UI;
using BTBridge.Bridge;
using BTBridge.Logic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BTBridge.Sim
{
    /// <summary>
    /// The mission loop: list, negotiate + accept, lance + launch, withdraw, mission end, after-action
    /// and salvage. Launch keeps the game's own flow (StartLanceConfiguration -> configurator ->
    /// confirm) and only substitutes the lance, via a prefix on CreateLanceConfiguration.
    /// </summary>
    public static class Contracts
    {
        public const string EmployerTeam = "ecc8d4f2-74b4-465d-adf6-84445e5dfc230";
        public const string TargetTeam = "be77cadd-e245-4240-a93e-b99cc98902a5";

        /// <summary>The lance to hand the configurator; read by the CreateLanceConfiguration prefix.</summary>
        public static LanceConfiguration PendingLance;

        private static string job;
        private static bool generating;
        private static object lastResult;
        // By reference: Contract.GUID can be null (found in the career test, where a GUID guard skipped the pick).
        private static Contract salvageFinalizedFor;

        public static object JobView() => job;

        // -- listing ---------------------------------------------------------------------------

        public static object List(SimGameState sim)
        {
            var system = sim.CurSystem ?? throw new BridgeException(409, "no current system");
            if (!system.InitialContractsFetched)
            {
                if (!generating)
                {
                    generating = true;
                    system.GenerateInitialContracts(() => generating = false);
                }
                return new { generating = true, note = "contracts are being generated; retry in a moment" };
            }
            var list = sim.GetAllCurrentlySelectableContracts();
            return new
            {
                system = system.Name,
                selected = sim.SelectedContract?.Name,
                contracts = list.Select((c, i) => Summary(sim, c, i)).ToList(),
            };
        }

        private static object Summary(SimGameState sim, Contract c, int index)
        {
            var o = c.Override;
            var employer = c.GetTeamFaction(EmployerTeam);
            var target = c.GetTeamFaction(TargetTeam);
            return new
            {
                index,
                name = c.Name,
                type = c.ContractTypeValue?.Name,
                difficulty_half_skulls = o?.GetUIDifficulty(),
                employer = employer?.FriendlyName,
                employer_gives_reputation = employer?.DoesGainReputation,
                target = target?.FriendlyName,
                pay_base = c.InitialContractValue,
                salvage_potential = c.SalvagePotential,
                can_negotiate = c.CanNegotiate,
                fixed_terms = c.CanNegotiate ? null : new { pay = o.negotiatedSalary, salvage = o.negotiatedSalvage },
                can_configure_lance = c.CanLanceConfigure,
                lance_limits = new
                {
                    max_units = o?.maxNumberOfPlayerUnits,
                    lance_tonnage = new { min = o?.lanceMinTonnage, max = o?.lanceMaxTonnage },
                    slot_tonnage_min = o?.mechMinTonnages,
                    slot_tonnage_max = o?.mechMaxTonnages,
                },
                map = c.mapName,
                biome = c.ContractBiome.ToString(),
                travel_only = o?.travelOnly,
                target_system = c.TargetSystem,
                priority = c.IsPriorityContract,
                story = c.IsStoryContract,
                flashpoint = c.IsFlashpointContract,
                expires_in_days = c.UsingExpiration ? (int?)c.ExpirationTime : null,
                meets_reputation = sim.ContractUserMeetsReputation(c),
                description = c.ShortDescription,
                objectives = BTBridge.Combat.Objectives.PlayerObjectives(c),
            };
        }

        private static Contract Find(SimGameState sim, int index, string name)
        {
            var list = sim.GetAllCurrentlySelectableContracts();
            if (index < 0 || index >= list.Count)
            {
                throw new BridgeException(404, "no contract at that index; list again");
            }
            var c = list[index];
            if (!string.IsNullOrEmpty(name) && c.Name != name)
            {
                throw new BridgeException(409, $"contract {index} is now '{c.Name}', not '{name}'; list again");
            }
            return c;
        }

        // -- accept ----------------------------------------------------------------------------

        public static object Accept(SimGameState sim, int index, string name, float? pay, float? salvage)
        {
            RequireQuiet(sim);
            var c = Find(sim, index, name);
            if (!sim.ContractUserMeetsReputation(c))
            {
                throw new BridgeException(400, "your reputation does not allow this contract");
            }
            if (sim.HasTravelContract && sim.ActiveTravelContract != c)
            {
                throw new BridgeException(409, "a travel contract is active; accepting another would break it (not supported)");
            }
            NegotiatedTerms terms;
            if (c.CanNegotiate)
            {
                try
                {
                    var employer = c.GetTeamFaction(EmployerTeam);
                    terms = Negotiation.Resolve(pay ?? 0.5f, salvage, employer != null && employer.DoesGainReputation);
                }
                catch (RuleException e)
                {
                    throw new BridgeException(400, e.Message);
                }
            }
            else
            {
                terms = new NegotiatedTerms { Pay = c.Override.negotiatedSalary, Salvage = c.Override.negotiatedSalvage };
            }
            sim.SetSelectedContract(c, c.Override.travelOnly);
            c.SetNegotiatedValues(terms.Pay, terms.Salvage);
            Log.Info($"contract accepted: '{c.Name}' pay {terms.Pay:0.##} salvage {terms.Salvage:0.##}");
            if (c.Override.travelOnly)
            {
                // Routes to the target system and starts the trip (async).
                sim.PrepareBreadcrumb(c);
                job = $"travelling for contract '{c.Name}'";
                return new { accepted = true, travel = true, terms, next = "run time until arrival, then answer the arrival interrupt" };
            }
            job = $"contract '{c.Name}' accepted; awaiting lance";
            return new { accepted = true, travel = false, terms, next = "POST /sim/contracts/launch with your lance" };
        }

        // -- lance + launch -------------------------------------------------------------------

        public sealed class UnitChoice
        {
            public int? Bay;
            public string Pilot;
        }

        public static object Launch(SimGameState sim, List<UnitChoice> units)
        {
            RequireQuiet(sim);
            var c = sim.SelectedContract ?? throw new BridgeException(409, "no contract selected; accept one first");
            // The configurator is the Command Center's widget (CompleteLanceConfigurationPrep shows it
            // through CmdCenterRoom), and the UI only ever reaches it from that room.
            if (sim.CurRoomState != DropshipLocation.CMD_CENTER)
            {
                sim.SetSimRoomState(DropshipLocation.CMD_CENTER);
            }
            if (!c.CanLanceConfigure)
            {
                // The game fills the lance itself for these.
                sim.StartLanceConfiguration();
                job = $"launching '{c.Name}' (game-assigned lance)";
                return new { launching = true, lance = "assigned by the contract" };
            }
            var o = c.Override;
            if (units == null || units.Count < 1 || units.Count > Math.Min(4, o.maxNumberOfPlayerUnits))
            {
                throw new BridgeException(400, $"a lance needs 1 to {Math.Min(4, o.maxNumberOfPlayerUnits)} units");
            }
            var mechs = new List<MechDef>();
            var lance = new LanceConfiguration();
            var usedPilots = new HashSet<string>();
            for (int slot = 0; slot < units.Count; slot++)
            {
                var u = units[slot];
                if (!u.Bay.HasValue || !sim.ActiveMechs.TryGetValue(u.Bay.Value, out var mech) || mech == null)
                {
                    throw new BridgeException(400, $"slot {slot}: no mech in bay {u.Bay}");
                }
                if (mechs.Contains(mech))
                {
                    throw new BridgeException(400, $"slot {slot}: {mech.Description.Name} is already in the lance");
                }
                if (!MechValidationRules.ValidateMechCanBeFielded(sim, mech))
                {
                    throw new BridgeException(400, $"slot {slot}: {mech.Description.Name} cannot be fielded (damaged, in maintenance or invalid)");
                }
                float min = o.mechMinTonnages != null && slot < o.mechMinTonnages.Length ? o.mechMinTonnages[slot] : -1f;
                float max = o.mechMaxTonnages != null && slot < o.mechMaxTonnages.Length ? o.mechMaxTonnages[slot] : -1f;
                if (!MechValidationRules.MechTonnageWithinRange(mech, min, max))
                {
                    throw new BridgeException(400, $"slot {slot}: {mech.Description.Name} ({mech.Chassis.Tonnage}t) is outside {min}-{max}t");
                }
                var pilot = FindPilot(sim, u.Pilot) ?? throw new BridgeException(400, $"slot {slot}: no pilot '{u.Pilot}'");
                if (!usedPilots.Add(pilot.GUID))
                {
                    throw new BridgeException(400, $"slot {slot}: {pilot.Callsign} is already in the lance");
                }
                if (!pilot.CanPilot)
                {
                    throw new BridgeException(400, $"slot {slot}: {pilot.Callsign} is injured or unavailable");
                }
                mechs.Add(mech);
                lance.AddUnit(Combat.CombatControl.Player1Guid, mech, pilot.pilotDef);
            }
            if (!MechValidationRules.LanceTonnageWithinRange(mechs, o.lanceMinTonnage, o.lanceMaxTonnage))
            {
                throw new BridgeException(400, $"lance tonnage {mechs.Sum(m => m.Chassis.Tonnage)}t is outside {o.lanceMinTonnage}-{o.lanceMaxTonnage}t");
            }
            PendingLance = lance;
            sim.StartLanceConfiguration();
            job = $"launching '{c.Name}': waiting for the lance configurator";
            Log.Info($"launch requested for '{c.Name}' with {units.Count} unit(s)");
            return new { launching = true, units = units.Count };
        }

        private static Pilot FindPilot(SimGameState sim, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            var all = new List<Pilot> { sim.Commander };
            all.AddRange(sim.PilotRoster);
            return all.FirstOrDefault(p => p != null && (p.GUID == key || string.Equals(p.Callsign, key, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Frame tick: confirm the configurator once it is ready (it populates asynchronously).</summary>
        public static void Tick()
        {
            if (PendingLance == null)
            {
                return;
            }
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var lc = sim?.RoomManager?.CmdCenterRoom?.lanceConfigBG?.LC;
            if (lc == null || !sim.RoomManager.CmdCenterRoom.lanceConfigOpen || !lc.Initialized)
            {
                return;
            }
            try
            {
                // The Deploy button's final step, after its warning popups and VO.
                lc.ContinueConfirmAudioCallback(null, AkCallbackType.AK_EndOfEvent, null);
                job = "mission launching";
                Log.Info("lance configurator confirmed; mission launching");
            }
            catch (Exception e)
            {
                Log.Error("lance confirm failed", e);
                job = "launch failed: " + e.Message;
                PendingLance = null;
            }
        }

        /// <summary>Called when combat starts: the substituted lance has been used.</summary>
        public static void OnCombatStarted()
        {
            if (PendingLance != null)
            {
                PendingLance = null;
                job = "in mission";
            }
        }

        private static void RequireQuiet(SimGameState sim)
        {
            if (UnityGameInstance.BattleTechGame?.Combat != null)
            {
                throw new BridgeException(409, "in combat");
            }
            if (sim.InterruptQueue.IsOpen || sim.InterruptQueue.HasQueue)
            {
                throw new BridgeException(409, "an interrupt is waiting; resolve it first");
            }
            if (sim.RoomManager?.MechBayRoom != null && sim.RoomManager.MechBayRoom.mechLabOpen)
            {
                throw new BridgeException(409, "close the mechlab first");
            }
        }

        // -- in mission ----------------------------------------------------------------------------

        public static object Withdraw(CombatGameState combat)
        {
            if (combat.ActiveContract != null && (combat.ActiveContract.IsPriorityContract || combat.ActiveContract.IsStoryContract))
            {
                throw new BridgeException(400, "this mission does not allow withdrawal");
            }
            var layer = UnityEngine.Object.FindObjectOfType<EncounterLayerData>();
            bool goodFaith = layer != null && layer.IsGoodFaithEffort;
            combat.MessageCenter.PublishMessage(new MissionRetreatMessage(goodFaith));
            Log.Info($"withdrawal ordered (good faith effort: {goodFaith})");
            return new { withdrawing = true, good_faith_effort = goodFaith };
        }

        private static Briefing BriefingScreen()
        {
            var b = UnityEngine.Object.FindObjectOfType<Briefing>();
            return b != null && b.Visible ? b : null;
        }

        /// <summary>
        /// The pre-mission briefing's "Begin Mission" button (found in the first career test). The game
        /// waits on it after loading; BeginPlaying does nothing until loading is complete.
        /// </summary>
        public static object BeginMission()
        {
            var b = BriefingScreen() ?? throw new BridgeException(409, "the mission briefing is not showing");
            string state = Reflect.Get(b, "loadingState")?.ToString();
            if (state != "Complete")
            {
                throw new BridgeException(409, $"the mission is still loading ({state}); retry shortly");
            }
            b.BeginPlaying();
            Log.Info("mission begun from the briefing screen");
            return new { begun = true };
        }

        public static object MissionEnd(CombatGameState combat)
        {
            var screen = UnityEngine.Object.FindObjectOfType<CombatHUDMissionEnd>();
            var briefing = BriefingScreen();
            return new
            {
                briefing_waiting = briefing != null,
                briefing_ready = briefing != null && Reflect.Get(briefing, "loadingState")?.ToString() == "Complete",
                mission = BTBridge.Combat.Objectives.Briefing(combat.ActiveContract),
                objectives = BTBridge.Combat.Objectives.Live(combat),
                mission_over = combat.TurnDirector.IsMissionOver,
                result = combat.TurnDirector.IsMissionOver ? combat.TurnDirector.TheMissionResult.ToString() : null,
                end_screen_visible = screen != null && screen.Visible,
            };
        }

        public static object ExitMission()
        {
            var screen = UnityEngine.Object.FindObjectOfType<CombatHUDMissionEnd>();
            if (screen == null || !screen.Visible)
            {
                throw new BridgeException(409, "the mission end screen is not showing yet");
            }
            screen.ReceiveButtonPress("Exit");
            return new { exiting = true };
        }

        // -- after action --------------------------------------------------------------------------

        private static MissionResults Results() => UnityEngine.Object.FindObjectOfType<MissionResults>();

        public static object Aar(SimGameState sim)
        {
            var mr = Results();
            if (mr == null)
            {
                return new { open = false, last_result = lastResult };
            }
            var contract = Reflect.Get(mr, "contract") as Contract;
            string state = Reflect.Get(mr, "currState")?.ToString();
            bool salvage = state == "SALVAGE";
            return new
            {
                open = true,
                state,
                contract = contract?.Name,
                outcome = contract?.State.ToString(),
                money = contract?.MoneyResults,
                experience = contract?.ExperienceEarned,
                salvage_picks_allowed = contract?.FinalPrioritySalvageCount,
                salvage_total = contract?.FinalSalvageCount,
                potential_salvage = salvage && contract != null ? Potential(contract).Select(s => new
                {
                    id = s.Description?.Id,
                    name = s.Description?.Name,
                    type = s.Type.ToString(),
                    damaged = s.Damaged,
                    count = s.Count,
                }).ToList() : null,
                answer = salvage
                    ? "POST {\"salvage\": [{\"id\", \"damaged\"}, ...]} (up to salvage_picks_allowed)"
                    : "POST {\"action\": \"continue\"}",
            };
        }

        private static List<SalvageDef> Potential(Contract c) => c.GetPotentialSalvage() ?? new List<SalvageDef>();

        public static object AarContinue(SimGameState sim, JArray picks)
        {
            var mr = Results() ?? throw new BridgeException(409, "no after-action report is open");
            var contract = Reflect.Get(mr, "contract") as Contract;
            string state = Reflect.Get(mr, "currState")?.ToString();
            switch (state)
            {
                case "MISSIONOUTCOME":
                    ((AAR_ContractResults_Screen)Reflect.Get(mr, "contractScreen")).OnCompleted();
                    return new { advanced = "contract results" };
                case "LANCERESULTS":
                    ((AAR_UnitsResult_Screen)Reflect.Get(mr, "unitsScreen")).OnCompleted();
                    return new { advanced = "lance results" };
                case "SALVAGE":
                    return FinishSalvage(mr, contract, picks);
                default:
                    throw new BridgeException(409, $"after-action report is in state {state}; retry shortly");
            }
        }

        private static object FinishSalvage(MissionResults mr, Contract contract, JArray picks)
        {
            var screen = (AAR_SalvageScreen)Reflect.Get(mr, "salvageScreen");
            List<SalvageDef> chosen = new List<SalvageDef>();
            // With no priority picks the screen confirms (and finalizes) on its own.
            if (contract.FinalPrioritySalvageCount >= 1 && !ReferenceEquals(salvageFinalizedFor, contract))
            {
                var potential = Potential(contract);
                var wanted = (picks ?? new JArray()).Select(p => new SalvagePick
                {
                    Id = p.Value<string>("id"),
                    Damaged = p.Value<bool?>("damaged") ?? false,
                }).ToList();
                List<int> indices;
                try
                {
                    indices = Salvage.Match(potential.Select(s => new SalvageOption { Id = s.Description?.Id, Damaged = s.Damaged, Count = s.Count }).ToList(),
                        wanted, contract.FinalPrioritySalvageCount);
                }
                catch (RuleException e)
                {
                    throw new BridgeException(400, e.Message);
                }
                foreach (int i in indices)
                {
                    chosen.Add(new SalvageDef(potential[i]) { Count = 1 });
                }
                // FinalizeSalvage appends and does not cap: exactly once per contract.
                salvageFinalizedFor = contract;
                contract.FinalizeSalvage(chosen);
            }
            lastResult = new
            {
                contract = contract.Name,
                outcome = contract.State.ToString(),
                money = contract.MoneyResults,
                experience = contract.ExperienceEarned,
                priority_salvage = chosen.Select(s => s.Description?.Name).ToList(),
            };
            screen.OnCompleted();
            Log.Info($"after-action complete for '{contract.Name}': {chosen.Count} priority salvage pick(s)");
            job = null;
            return new { completed = true, result = lastResult, next = "the campaign resumes; check /sim/status" };
        }
    }
}
