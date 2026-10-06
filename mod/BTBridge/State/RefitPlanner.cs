using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.UI;
using BTBridge.Bridge;
using BTBridge.Patches;
using HBS;
using Localize;

namespace BTBridge.State
{
    /// <summary>
    /// Campaign refits, built from the same work-order primitives the mechlab uses.
    ///
    /// Preview diffs a mech's current loadout against a target spec and builds the
    /// WorkOrderEntry_MechLab the mechlab would have built (removals first, then
    /// installs, then armor), without touching the company. Apply re-checks that
    /// nothing changed since the preview and then commits it exactly the way
    /// MechBayPanel.OnMechLabComplete does.
    ///
    /// Component moves are a removal (to None) plus an install of the same SimGameUID:
    /// SimGameState.ML_InstallComponent keeps a removed part in WorkOrderComponents
    /// when a later sub-entry references it, instead of returning it to storage.
    /// </summary>
    public static class RefitPlanner
    {
        private sealed class Plan
        {
            public string Id;
            public string MechGuid;
            public string Fingerprint;
            public WorkOrderEntry_MechLab WorkOrder;
            public Dictionary<string, int> FromStorage;
            public int CBills;
            public DateTime Created;
        }

        private static readonly Dictionary<string, Plan> Plans = new Dictionary<string, Plan>();
        private static int nextPlan;

        public static object Preview(SimGameState sim, string mechRef, BuildSpec spec)
        {
            var mech = FindActiveMech(sim, mechRef, out int bay);
            if (spec.ChassisId != mech.ChassisID)
            {
                throw new BridgeException(400, $"spec chassis {spec.ChassisId} does not match {mech.Description.Name} ({mech.ChassisID}); a refit cannot change chassis");
            }
            var dm = sim.DataManager;
            var problems = new List<string>();
            EnsureComponentUids(sim, mech);
            if (sim.GetWorkOrderEntryForMech(mech) != null)
            {
                problems.Add("this mech already has queued mechlab work; finish or cancel it first");
            }

            // --- component diff ---------------------------------------------------
            var current = mech.Inventory.Where(c => !c.IsFixed).ToList();
            var unmatched = new List<MechComponentRef>(current);
            var pending = new List<SpecItem>();
            var kept = new List<MechComponentRef>();
            foreach (var item in spec.Items)
            {
                var same = unmatched.FirstOrDefault(c => c.ComponentDefID == item.Id && c.MountedLocation == item.Location);
                if (same != null)
                {
                    unmatched.Remove(same);
                    kept.Add(same);
                }
                else
                {
                    pending.Add(item);
                }
            }
            var moves = new List<KeyValuePair<MechComponentRef, SpecItem>>();
            var installs = new List<SpecItem>();
            foreach (var item in pending)
            {
                var movable = unmatched.FirstOrDefault(c => c.ComponentDefID == item.Id);
                if (movable != null)
                {
                    unmatched.Remove(movable);
                    moves.Add(new KeyValuePair<MechComponentRef, SpecItem>(movable, item));
                }
                else
                {
                    installs.Add(item);
                }
            }
            var removals = unmatched;

            // --- storage check ----------------------------------------------------
            var fromStorage = installs.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Count());
            var missing = new List<object>();
            foreach (var kv in fromStorage)
            {
                var def = installs.First(i => i.Id == kv.Key).Def;
                int have = sim.GetItemCount(kv.Key, SimGameState.GetTypeFromComponent(def), SimGameState.ItemCountType.UNDAMAGED_ONLY);
                if (have < kv.Value)
                {
                    missing.Add(new { id = kv.Key, name = def.Description.UIName, needed = kv.Value, in_storage = have });
                }
            }
            if (missing.Count > 0)
            {
                problems.Add("not enough parts in storage (see missing_parts)");
            }

            // --- work order -------------------------------------------------------
            string name = mech.Description.Name;
            var order = new WorkOrderEntry_MechLab(WorkOrderType.MechLabGeneric, "MechLab-BaseWorkOrder",
                Strings.T("Modify 'Mech - {0}", name), mech.GUID, 0,
                Strings.T(sim.Constants.Story.GeneralMechWorkOrderCompletedText, name));
            var steps = new List<object>();

            foreach (var c in removals.Concat(moves.Select(m => m.Key)))
            {
                order.AddSubEntry(sim.CreateComponentInstallWorkOrder(mech.GUID, MechBuilder.Copy(c), ChassisLocations.None, c.MountedLocation));
            }
            foreach (var c in removals)
            {
                steps.Add(new { action = "remove_to_storage", id = c.ComponentDefID, name = c.Def?.Description?.UIName, from = c.MountedLocation.ToString() });
            }

            var slots = new MechBuilder.HardpointCounter();
            foreach (var c in kept)
            {
                slots.Count(c);
            }
            var finalInventory = mech.Inventory.Where(c => c.IsFixed).Select(MechBuilder.Copy).ToList();
            finalInventory.AddRange(kept.Select(MechBuilder.Copy));

            foreach (var m in moves)
            {
                var moved = MechBuilder.Copy(m.Key);
                moved.SetData(m.Value.Location, slots.Take(m.Value.Def, m.Value.Location), moved.DamageLevel, moved.IsFixed);
                order.AddSubEntry(sim.CreateComponentInstallWorkOrder(mech.GUID, moved, m.Value.Location, ChassisLocations.None));
                finalInventory.Add(moved);
                steps.Add(new { action = "move", id = m.Key.ComponentDefID, name = m.Value.Def.Description.UIName, from = m.Key.MountedLocation.ToString(), to = m.Value.Location.ToString() });
            }
            foreach (var item in installs)
            {
                var added = MechBuilder.NewRef(item.Def, item.Location, slots.Take(item.Def, item.Location), dm, sim.GenerateSimGameUID());
                order.AddSubEntry(sim.CreateComponentInstallWorkOrder(mech.GUID, added, item.Location, ChassisLocations.None));
                finalInventory.Add(added);
                steps.Add(new { action = "install_from_storage", id = item.Id, name = item.Def.Description.UIName, to = item.Location.ToString() });
            }

            // --- armor -------------------------------------------------------------
            var target = new MechDef(mech);
            target.SetInventory(finalInventory.ToArray());
            foreach (var loc in MechSerializer.Locations)
            {
                var have = mech.GetLocationLoadoutDef(loc);
                var max = mech.Chassis.GetLocationDef(loc);
                float front = spec.FrontFor(loc, have.AssignedArmor);
                float rear = spec.RearFor(loc, have.AssignedRearArmor);
                if (front < 0 || front > max.MaxArmor || (MechBuilder.HasRear(loc) && (rear < 0 || rear > max.MaxRearArmor)))
                {
                    problems.Add($"{loc}: armor {front}/{rear} outside 0..{max.MaxArmor}/{max.MaxRearArmor}");
                    continue;
                }
                int diff = (int)Math.Abs(front - have.AssignedArmor) + (int)Math.Abs(rear - have.AssignedRearArmor);
                if (diff > 0)
                {
                    order.AddSubEntry(sim.CreateMechArmorModifyWorkOrder(mech.GUID, loc, diff, (int)front, (int)rear));
                    steps.Add(new { action = "armor", location = loc.ToString(), front = new { from = have.AssignedArmor, to = front }, rear = MechBuilder.HasRear(loc) ? new { from = have.AssignedRearArmor, to = rear } : null });
                }
                var t = target.GetLocationLoadoutDef(loc);
                t.AssignedArmor = t.CurrentArmor = front;
                t.AssignedRearArmor = t.CurrentRearArmor = rear;
            }

            // --- validation and cost ---------------------------------------------------
            var errors = MechBuilder.Validate(target, dm);
            bool blocked = MechBuilder.IsBlocked(errors);
            if (blocked)
            {
                problems.Add("the resulting build fails the game's mechlab validation (see validation_errors)");
            }
            int cbills = order.GetCBillCostForIncompleteTasks();
            int techPoints = order.SubEntries.Sum(s => s.GetCost());
            int days = Math.Max(0, (int)Math.Ceiling(techPoints / (double)Math.Max(1, sim.MechTechSkill)));
            if (cbills > sim.Funds)
            {
                problems.Add($"costs {cbills:N0} C-bills but the company has {sim.Funds:N0}");
            }

            string planId = null;
            if (problems.Count == 0 && order.SubEntryCount > 0)
            {
                planId = "refit-" + (++nextPlan);
                PrunePlans();
                Plans[planId] = new Plan
                {
                    Id = planId, MechGuid = mech.GUID, Fingerprint = Fingerprint(mech), WorkOrder = order,
                    FromStorage = fromStorage, CBills = cbills, Created = DateTime.UtcNow,
                };
            }
            return new
            {
                plan_id = planId,
                can_apply = planId != null,
                no_changes = order.SubEntryCount == 0,
                mech = new { bay, guid = mech.GUID, name },
                steps,
                cost = new { cbills, days, tech_points = techPoints, mechtech_skill = sim.MechTechSkill, funds_after = sim.Funds - cbills },
                problems,
                missing_parts = missing,
                validation_errors = errors,
                result = MechSerializer.Mech(target),
            };
        }

        public static object Apply(SimGameState sim, string planId)
        {
            if (string.IsNullOrEmpty(planId) || !Plans.TryGetValue(planId, out var plan))
            {
                throw new BridgeException(404, $"no refit plan '{planId}' (plans live until the game restarts; preview again)");
            }
            if (MechLabTracker.Live != null)
            {
                throw new BridgeException(409, "close the mechlab before applying a refit");
            }
            var mech = sim.ActiveMechs.Values.FirstOrDefault(m => m != null && m.GUID == plan.MechGuid)
                ?? throw new BridgeException(409, "the mech is no longer in an active bay");
            if (Fingerprint(mech) != plan.Fingerprint)
            {
                throw new BridgeException(409, "the mech changed since the preview; preview again");
            }
            if (sim.GetWorkOrderEntryForMech(mech) != null)
            {
                throw new BridgeException(409, "the mech already has queued mechlab work");
            }
            foreach (var kv in plan.FromStorage)
            {
                var def = BuildSpec.ResolveComponent(sim.DataManager, kv.Key);
                if (sim.GetItemCount(kv.Key, SimGameState.GetTypeFromComponent(def), SimGameState.ItemCountType.UNDAMAGED_ONLY) < kv.Value)
                {
                    throw new BridgeException(409, $"storage no longer has {kv.Value}x {kv.Key}; preview again");
                }
            }
            if (plan.CBills > sim.Funds)
            {
                throw new BridgeException(409, $"not enough funds ({sim.Funds:N0} < {plan.CBills:N0})");
            }
            // Every removal or move must name a component actually on the mech; otherwise the game
            // skips the step at completion ("had an invalid mechComponentID") after charging for it.
            var onMech = new HashSet<string>(mech.Inventory.Select(c => c.SimGameUID));
            foreach (var step in plan.WorkOrder.SubEntries.OfType<WorkOrderEntry_InstallComponent>())
            {
                if (step.DesiredLocation == ChassisLocations.None && !onMech.Contains(step.ComponentSimGameUID))
                {
                    throw new BridgeException(500, $"plan step '{step.Description}' references component {step.ComponentSimGameUID}, which is not on the mech; refusing to apply");
                }
            }

            // Mirrors MechBayPanel.OnMechLabComplete + MechLabPanel.DoConfirmRefit.
            sim.MechLabQueue.Add(plan.WorkOrder);
            sim.InitializeMechLabEntry(plan.WorkOrder, 0);
            sim.UpdateMechLabWorkQueue(passDay: false);
            var ui = LazySingletonBehavior<UIManager>.Instance;
            ui.StartCoroutine(sim.RoomManager.DelayedRefreshTimeline(0.1f));
            sim.TriggerIronManSave();
            Plans.Remove(planId);
            Log.Info($"applied {planId} to {mech.Description.Name} ({mech.GUID}): {plan.WorkOrder.SubEntryCount} steps, {plan.CBills} C-bills");
            return new { applied = true, plan_id = planId, mech_guid = mech.GUID, cbills_charged = plan.CBills, funds = sim.Funds, work_queue_entries = sim.MechLabQueue.Count };
        }

        internal static MechDef FindActiveMech(SimGameState sim, string mechRef, out int bay)
        {
            if (string.IsNullOrEmpty(mechRef))
            {
                throw new BridgeException(400, "mech is required (bay number or mech GUID)");
            }
            if (int.TryParse(mechRef, out bay))
            {
                if (sim.ActiveMechs.TryGetValue(bay, out var byBay) && byBay != null)
                {
                    return byBay;
                }
                throw new BridgeException(404, $"no mech in active bay {bay}");
            }
            foreach (var kv in sim.ActiveMechs)
            {
                if (kv.Value != null && kv.Value.GUID == mechRef)
                {
                    bay = kv.Key;
                    return kv.Value;
                }
            }
            throw new BridgeException(404, $"no active mech with GUID {mechRef}");
        }

        /// <summary>
        /// Starting and newly acquired mechs can carry components with no SimGameUID. The mechbay
        /// assigns them just before opening the mechlab (MechBayPanel, before mechLab.SetData);
        /// work orders find components by UID, so do the same before planning.
        /// </summary>
        private static void EnsureComponentUids(SimGameState sim, MechDef mech)
        {
            foreach (var c in mech.Inventory)
            {
                if (string.IsNullOrEmpty(c.SimGameUID))
                {
                    c.SetSimGameUID(sim.GenerateSimGameUID());
                }
            }
        }

        /// <summary>Everything a plan depends on: component UIDs and placements, armor.</summary>
        private static string Fingerprint(MechDef mech)
        {
            var parts = mech.Inventory
                .Select(c => $"{c.SimGameUID}|{c.ComponentDefID}|{c.MountedLocation}|{c.DamageLevel}")
                .OrderBy(s => s)
                .Concat(MechSerializer.Locations.Select(l =>
                {
                    var d = mech.GetLocationLoadoutDef(l);
                    return $"{l}:{d.AssignedArmor}/{d.AssignedRearArmor}";
                }));
            return string.Join(";", parts.ToArray());
        }

        private static void PrunePlans()
        {
            foreach (var old in Plans.Values.Where(p => DateTime.UtcNow - p.Created > TimeSpan.FromHours(1)).Select(p => p.Id).ToList())
            {
                Plans.Remove(old);
            }
        }
    }
}
