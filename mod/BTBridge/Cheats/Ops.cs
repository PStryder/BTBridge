using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Logic;
using BTBridge.Sim;
using BTBridge.State;
using Newtonsoft.Json.Linq;

namespace BTBridge.Cheats
{
    public sealed class OpSpec
    {
        public string Summary;
        public object Before;
        public object ExpectedAfter;
        public List<string> SideEffects = new List<string>();
        public string Fingerprint;
    }

    public sealed class OpOutcome
    {
        public object Before;
        public object After;
    }

    /// <summary>
    /// OPERATOR CHEAT LAYER: the v1 operations. Each mirrors the native debug command named in its
    /// comment (SimGameState_Debug) and adds the integrity checks that command lacks.
    /// Describe() is side-effect free and produces the fingerprint a plan is bound to.
    /// </summary>
    public static class Ops
    {
        public static readonly string[] Names =
        {
            "add_funds", "add_component", "remove_component", "add_mech",
            "complete_mech_work", "complete_argo_upgrade", "heal_pilots",
        };

        private static BridgeException Bad(string message) => new BridgeException(400, message);

        private static T Guard<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (RuleException e)
            {
                throw Bad(e.Message);
            }
        }

        public static OpSpec Describe(SimGameState sim, string op, JObject a)
        {
            switch (op)
            {
                case "add_funds": return DescribeFunds(sim, a);
                case "add_component": return DescribeComponent(sim, a, removing: false);
                case "remove_component": return DescribeComponent(sim, a, removing: true);
                case "add_mech": return DescribeMech(sim, a);
                case "complete_mech_work": return DescribeMechWork(sim);
                case "complete_argo_upgrade": return DescribeArgo(sim);
                case "heal_pilots": return DescribeHeal(sim);
                default: throw Bad($"unknown cheat op '{op}' ({string.Join(" | ", Names)})");
            }
        }

        public static OpOutcome Execute(SimGameState sim, string op, JObject a)
        {
            switch (op)
            {
                case "add_funds": return ExecFunds(sim, a);
                case "add_component": return ExecComponent(sim, a, removing: false);
                case "remove_component": return ExecComponent(sim, a, removing: true);
                case "add_mech": return ExecMech(sim, a);
                case "complete_mech_work": return ExecMechWork(sim);
                case "complete_argo_upgrade": return ExecArgo(sim);
                case "heal_pilots": return ExecHeal(sim);
                default: throw Bad($"unknown cheat op '{op}'");
            }
        }

        // -- funds (SimDebug_AddFunds) ----------------------------------------------------------

        private static long Amount(JObject a) => a.Value<long?>("amount") ?? throw Bad("amount is required (negative removes)");

        private static OpSpec DescribeFunds(SimGameState sim, JObject a)
        {
            long amount = Amount(a);
            int after = Guard(() => CheatGuards.FundsAfter(sim.Funds, amount, sim.Constants.Story.MaximumDebt));
            return new OpSpec
            {
                Summary = $"{(amount > 0 ? "add" : "remove")} {Math.Abs(amount):N0} C-bills",
                Before = new { funds = sim.Funds },
                ExpectedAfter = new { funds = after },
                SideEffects = { "funds-gained and burndown statistics are left untouched (as the native debug command does)" },
                Fingerprint = "funds=" + sim.Funds,
            };
        }

        private static OpOutcome ExecFunds(SimGameState sim, JObject a)
        {
            long amount = Amount(a);
            Guard(() => CheatGuards.FundsAfter(sim.Funds, amount, sim.Constants.Story.MaximumDebt));
            int before = sim.Funds;
            sim.AddFunds((int)amount, CheatService.Source, updateBurndown: false, updateFundsGained: false);
            return new OpOutcome { Before = new { funds = before }, After = new { funds = sim.Funds } };
        }

        // -- components (SimDebug_AddItemToCompany) ---------------------------------------------

        private sealed class ComponentArgs
        {
            public string Id;
            public int Count;
            public bool Damaged;
            public MechComponentDef Def;
            public Type Type;
        }

        private static ComponentArgs ParseComponent(SimGameState sim, JObject a)
        {
            string id = a.Value<string>("id") ?? throw Bad("id is required");
            var def = BuildSpec.ResolveComponent(sim.DataManager, id) ?? throw Bad($"unknown component '{id}'");
            if (!sim.DataManager.ContentPackIndex.IsResourceOwned(id))
            {
                throw Bad($"'{id}' belongs to content you don't own");
            }
            return new ComponentArgs
            {
                Id = id,
                Count = a.Value<int?>("count") ?? 1,
                Damaged = a.Value<bool?>("damaged") ?? false,
                Def = def,
                Type = SimGameState.GetTypeFromComponent(def),
            };
        }

        private static int Count(SimGameState sim, ComponentArgs c) =>
            sim.GetItemCount(c.Id, c.Type, c.Damaged ? SimGameState.ItemCountType.DAMAGED_ONLY : SimGameState.ItemCountType.UNDAMAGED_ONLY);

        private static OpSpec DescribeComponent(SimGameState sim, JObject a, bool removing)
        {
            var c = ParseComponent(sim, a);
            int have = Count(sim, c);
            Guard(() =>
            {
                CheatGuards.ComponentCount(c.Count, removing, have);
                return 0;
            });
            string label = $"{c.Count}x {(c.Damaged ? "damaged " : "")}{c.Def.Description.UIName ?? c.Def.Description.Name} ({c.Id})";
            return new OpSpec
            {
                Summary = (removing ? "remove " : "add ") + label + (removing ? " from storage" : " to storage"),
                Before = new { id = c.Id, damaged = c.Damaged, in_storage = have },
                ExpectedAfter = new { id = c.Id, damaged = c.Damaged, in_storage = have + (removing ? -c.Count : c.Count) },
                Fingerprint = $"item:{c.Id}:{c.Damaged}={have}",
            };
        }

        private static OpOutcome ExecComponent(SimGameState sim, JObject a, bool removing)
        {
            var c = ParseComponent(sim, a);
            int before = Count(sim, c);
            Guard(() =>
            {
                CheatGuards.ComponentCount(c.Count, removing, before);
                return 0;
            });
            for (int i = 0; i < c.Count; i++)
            {
                if (removing)
                {
                    sim.RemoveItemStat(c.Id, c.Type, c.Damaged);
                }
                else
                {
                    sim.AddItemStat(c.Id, c.Type, c.Damaged);
                }
            }
            return new OpOutcome
            {
                Before = new { id = c.Id, damaged = c.Damaged, in_storage = before },
                After = new { id = c.Id, damaged = c.Damaged, in_storage = Count(sim, c) },
            };
        }

        // -- mechs (SimDebug_FinalizeInventoryItem MechDef branch, via AddMech) ---------------------

        private sealed class MechArgs
        {
            public MechDef Def;
            public bool ToBay;
            public int Bay = -1;
        }

        private static List<int> Occupied(SimGameState sim) => sim.ActiveMechs.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();

        private static MechArgs ParseMech(SimGameState sim, JObject a)
        {
            string id = a.Value<string>("mech_def_id") ?? throw Bad("mech_def_id is required");
            if (!sim.DataManager.MechDefs.TryGet(id, out var def))
            {
                throw Bad($"unknown mech '{id}'");
            }
            // AddMech silently returns for unowned content; check up front instead.
            var owned = sim.DataManager.ContentPackIndex;
            if (!owned.IsResourceOwned(def.Description.Id) || !owned.IsResourceOwned(def.Chassis.Description.Id) || !owned.IsResourceOwned(def.Chassis.PrefabIdentifier))
            {
                throw Bad($"'{id}' belongs to content you don't own");
            }
            string destination = a.Value<string>("destination") ?? "bay";
            if (destination != "bay" && destination != "storage")
            {
                throw Bad("destination must be bay or storage");
            }
            var m = new MechArgs { Def = def, ToBay = destination == "bay" };
            if (m.ToBay)
            {
                m.Bay = Guard(() => CheatGuards.Bay(a.Value<int?>("bay"), sim.GetMaxActiveMechs(), Occupied(sim), sim.GetFirstFreeMechBay()));
            }
            return m;
        }

        private static int StoredCount(SimGameState sim, MechDef def) =>
            sim.GetItemCount(def.Description.Id, typeof(MechDef), SimGameState.ItemCountType.ALL);

        private static OpSpec DescribeMech(SimGameState sim, JObject a)
        {
            var m = ParseMech(sim, a);
            var occupied = Occupied(sim);
            return new OpSpec
            {
                Summary = $"add {m.Def.Description.UIName ?? m.Def.Description.Name} ({m.Def.Description.Id}) to " + (m.ToBay ? $"bay {m.Bay}" : "storage"),
                Before = new { occupied_bays = occupied, stored = StoredCount(sim, m.Def) },
                ExpectedAfter = m.ToBay ? (object)new { bay = m.Bay, mech = m.Def.Description.Name } : new { stored = StoredCount(sim, m.Def) + 1 },
                SideEffects =
                {
                    "a fresh copy of the stock loadout (new GUID); its components get SimGameUIDs when first refitted",
                    "COMPANY_MechsAdded statistic +1 and SimGameMechAddedMessage, as for any acquired mech",
                    m.ToBay ? "placed directly; no notification interrupt" : "stored as a mech; readying it is a normal mechlab task",
                },
                Fingerprint = $"bays={string.Join(",", occupied.Select(b => b.ToString()).ToArray())};stored={StoredCount(sim, m.Def)};max={sim.GetMaxActiveMechs()}",
            };
        }

        private static OpOutcome ExecMech(SimGameState sim, JObject a)
        {
            var m = ParseMech(sim, a);
            var before = new { occupied_bays = Occupied(sim), stored = StoredCount(sim, m.Def) };
            // Never hand the shared DataManager def to the company.
            var mech = new MechDef(m.Def, sim.GenerateSimGameUID());
            if (m.ToBay)
            {
                sim.AddMech(m.Bay, mech, active: true, forcePlacement: true, displayMechPopup: false);
                if (!sim.ActiveMechs.TryGetValue(m.Bay, out var placed) || placed != mech)
                {
                    throw new BridgeException(500, "the game did not place the mech (content ownership or bay state)");
                }
            }
            else
            {
                sim.AddMech(-1, mech, active: false, forcePlacement: true, displayMechPopup: false);
            }
            sim.RoomManager?.RefreshTimeline(refreshInjuries: false);
            return new OpOutcome
            {
                Before = before,
                After = new { occupied_bays = Occupied(sim), stored = StoredCount(sim, m.Def), mech_guid = mech.GUID, bay = m.ToBay ? (int?)m.Bay : null },
            };
        }

        // -- finish now: mechlab (SimDebug_CompleteMechTasks) --------------------------------------

        private static List<object> Queue(SimGameState sim) =>
            sim.MechLabQueue.Select(w => (object)new { id = w.ID, description = w.Description, remaining = w.GetRemainingCost() }).ToList();

        private static string QueueFingerprint(SimGameState sim) =>
            "queue=" + string.Join(";", sim.MechLabQueue.Select(w => w.ID + ":" + w.GetRemainingCost()).ToArray());

        private static OpSpec DescribeMechWork(SimGameState sim)
        {
            if (sim.MechLabQueue.Count == 0)
            {
                throw Bad("no mechlab work is queued");
            }
            if (sim.RoomManager?.MechBayRoom != null && sim.RoomManager.MechBayRoom.mechLabOpen)
            {
                throw new BridgeException(409, "close the mechlab first");
            }
            return new OpSpec
            {
                Summary = $"finish all {sim.MechLabQueue.Count} queued mechlab work order(s) now",
                Before = new { queue = Queue(sim) },
                ExpectedAfter = new { queue = new object[0] },
                SideEffects = { "the normal completion handlers run: installs, removals to storage, armor, repairs" },
                Fingerprint = QueueFingerprint(sim),
            };
        }

        private static OpOutcome ExecMechWork(SimGameState sim)
        {
            var before = Queue(sim);
            foreach (var entry in sim.MechLabQueue.ToList())
            {
                if (!entry.IsCostPaid())
                {
                    entry.PayCost(entry.GetRemainingCost());
                }
            }
            sim.UpdateMechLabWorkQueue(passDay: false);
            sim.RoomManager?.RefreshTimeline(refreshInjuries: false);
            return new OpOutcome { Before = new { queue = before }, After = new { queue = Queue(sim) } };
        }

        // -- finish now: Argo upgrade (UpdateArgoUpgrades completion path) -----------------------

        private static OpSpec DescribeArgo(SimGameState sim)
        {
            var entry = sim.CurrentUpgradeEntry ?? throw Bad("no Argo upgrade is being built");
            return new OpSpec
            {
                Summary = $"finish the Argo upgrade '{entry.Description}' now",
                Before = new { building = entry.Description, remaining = entry.GetRemainingCost() },
                ExpectedAfter = new { building = (string)null },
                SideEffects = { "the normal completion runs: upgrade applied, stats added, 'Work Order Complete' notification queued" },
                Fingerprint = $"argo={entry.ID}:{entry.GetRemainingCost()}",
            };
        }

        private static OpOutcome ExecArgo(SimGameState sim)
        {
            var entry = sim.CurrentUpgradeEntry ?? throw Bad("no Argo upgrade is being built");
            var before = new { building = entry.Description, remaining = entry.GetRemainingCost() };
            entry.PayCost(entry.GetRemainingCost());
            // The private daily step completes a paid entry: CompleteArgoUpgrade, timeline, notification.
            Reflect.Call(sim, "UpdateArgoUpgrades", true);
            return new OpOutcome { Before = before, After = new { building = sim.CurrentUpgradeEntry?.Description, completed = sim.CurrentUpgradeEntry == null } };
        }

        // -- finish now: medbay (SimDebug_HealMechWarriors) ----------------------------------------

        private static List<object> Injured(SimGameState sim)
        {
            var pilots = new List<Pilot>();
            if (sim.Commander != null)
            {
                pilots.Add(sim.Commander);
            }
            pilots.AddRange(sim.PilotRoster);
            return pilots.Where(p => p.Injuries > 0 || !p.CanPilot).Select(p => (object)new { callsign = p.Callsign, injuries = p.Injuries, can_pilot = p.CanPilot }).ToList();
        }

        private static OpSpec DescribeHeal(SimGameState sim)
        {
            var injured = Injured(sim);
            if (injured.Count == 0)
            {
                throw Bad("no pilot is injured");
            }
            return new OpSpec
            {
                Summary = $"finish medbay recovery for {injured.Count} pilot(s) now",
                Before = new { injured },
                ExpectedAfter = new { injured = new object[0] },
                Fingerprint = "medbay=" + (sim.MedBayQueue == null ? "none" : sim.MedBayQueue.GetRemainingCost().ToString()) + ";injured=" + injured.Count,
            };
        }

        private static OpOutcome ExecHeal(SimGameState sim)
        {
            var before = Injured(sim);
            if (sim.MedBayQueue != null && !sim.MedBayQueue.IsCostPaid())
            {
                sim.MedBayQueue.PayCost(sim.MedBayQueue.GetRemainingCost());
            }
            sim.RefreshInjuries();
            return new OpOutcome { Before = new { injured = before }, After = new { injured = Injured(sim) } };
        }
    }
}
