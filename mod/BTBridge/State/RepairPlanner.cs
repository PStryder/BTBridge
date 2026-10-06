using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using Localize;
using UnityEngine;

namespace BTBridge.State
{
    /// <summary>
    /// Battle-damage repair, mirroring the mech bay's Repair button (MechBayPanel.OnRepairMech):
    /// internal structure per damaged location, and damaged components repaired in place. Destroyed
    /// components get the same re-install order the game queues. Armor needs no order: the game
    /// restores it as the structure under it is repaired. Refits don't do any of this.
    /// </summary>
    public static class RepairPlanner
    {
        private static readonly ChassisLocations[] Locations =
        {
            ChassisLocations.Head, ChassisLocations.CenterTorso, ChassisLocations.LeftTorso, ChassisLocations.RightTorso,
            ChassisLocations.LeftLeg, ChassisLocations.RightLeg, ChassisLocations.LeftArm, ChassisLocations.RightArm,
        };

        public static object Repair(SimGameState sim, string mechRef, bool confirm)
        {
            var mech = RefitPlanner.FindActiveMech(sim, mechRef, out int bay);
            var existing = sim.GetWorkOrderEntryForMech(mech);
            var entries = new List<WorkOrderEntry>();
            var structure = new List<object>();
            var components = new List<object>();
            var destroyed = new List<string>();
            int days = 0, cbills = 0;

            foreach (var loc in Locations)
            {
                var have = mech.GetLocationLoadoutDef(loc);
                var max = mech.GetChassisLocationDef(loc);
                if (MechValidationRules.MechStructureUnderMaintenance(loc, MechValidationLevel.MechLab, existing)
                    || have.CurrentInternalStructure >= max.InternalStructure)
                {
                    continue;
                }
                int count = Mathf.RoundToInt(Mathf.Max(0f, max.InternalStructure - have.CurrentInternalStructure));
                var e = sim.CreateMechRepairWorkOrder(mech.GUID, loc, count);
                entries.Add(e);
                days += e.GetCost();
                cbills += e.GetCBillCost();
                structure.Add(new { location = loc.ToString(), from = have.CurrentInternalStructure, to = max.InternalStructure });
            }

            foreach (var c in mech.Inventory)
            {
                if (string.IsNullOrEmpty(c.SimGameUID))
                {
                    c.SetSimGameUID(sim.GenerateSimGameUID());
                }
                if (MechValidationRules.MechComponentUnderMaintenance(c, MechValidationLevel.MechLab, existing))
                {
                    continue;
                }
                if (c.DamageLevel == ComponentDamageLevel.Destroyed)
                {
                    var e = sim.CreateComponentInstallWorkOrder(mech.GUID, c, ChassisLocations.None, c.MountedLocation);
                    entries.Insert(0, e);
                    days += e.GetCost();
                    cbills += e.GetCBillCost();
                    destroyed.Add($"{c.MountedLocation}: {c.Def?.Description?.Name}");
                }
                else if (c.DamageLevel != ComponentDamageLevel.Functional && c.DamageLevel != ComponentDamageLevel.Installing)
                {
                    var e = sim.CreateComponentRepairWorkOrder(c, isOnMech: true);
                    entries.Add(e);
                    days += e.GetCost();
                    cbills += e.GetCBillCost();
                    components.Add(new { location = c.MountedLocation.ToString(), component = c.Def?.Description?.Name, damage = c.DamageLevel.ToString() });
                }
            }

            days = Math.Max(1, Mathf.CeilToInt((float)days / sim.MechTechSkill));
            var preview = new
            {
                mech = mech.Description.Name,
                bay,
                needed = entries.Count > 0,
                structure,
                components,
                destroyed_components = destroyed,
                destroyed_note = destroyed.Count > 0
                    ? "the game's repair re-queues destroyed components as installs; to replace them with different parts, refit instead"
                    : null,
                cbills = entries.Count > 0 ? cbills : 0,
                days = entries.Count > 0 ? days : 0,
                funds = sim.Funds,
                armor_note = "armor is restored by the game as the structure under it is repaired",
            };
            if (!confirm || entries.Count == 0)
            {
                return new { preview, queued = false, next = entries.Count > 0 ? "repeat with confirm=true to queue it" : null };
            }
            if (sim.Funds < cbills)
            {
                throw new BridgeException(409, $"repair costs {cbills:n0} C-bills; the company has {sim.Funds:n0}");
            }

            bool isNew = existing == null;
            var order = existing ?? new WorkOrderEntry_MechLab(WorkOrderType.MechLabGeneric, "MechLab-BaseWorkOrder",
                Strings.T("Modify 'Mech - {0}", mech.Description.Name), mech.GUID, 0,
                Strings.T(sim.Constants.Story.GeneralMechWorkOrderCompletedText, mech.Description.Name));
            if (isNew)
            {
                order.SetMechDef(mech);
            }
            foreach (var e in entries)
            {
                order.AddSubEntry(e);
            }
            if (isNew)
            {
                sim.MechLabQueue.Add(order);
                sim.InitializeMechLabEntry(order, 0);
            }
            sim.UpdateMechLabWorkQueue(passDay: false);
            Log.Info($"repair queued for {mech.Description.Name}: {entries.Count} steps, {cbills} C-bills, {days} day(s)");
            return new { preview, queued = true, next = "run time to complete it (sim_run_time)" };
        }
    }
}
