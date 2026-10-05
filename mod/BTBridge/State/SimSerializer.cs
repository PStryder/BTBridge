using System.Collections.Generic;
using System.Linq;
using BattleTech;

namespace BTBridge.State
{
    public static class SimSerializer
    {
        public static object Company(SimGameState sim) => new
        {
            name = sim.CompanyName,
            funds = sim.Funds,
            date = sim.CurrentDate.ToString("yyyy-MM-dd"),
            days_passed = sim.DaysPassed,
            mechtech_skill = sim.MechTechSkill,
            max_active_mechs = sim.GetMaxActiveMechs(),
        };

        public static object MechBay(SimGameState sim)
        {
            var queued = new HashSet<string>(
                sim.MechLabQueue.OfType<WorkOrderEntry_MechLab>().Select(w => w.MechID));
            return new
            {
                max_active_mechs = sim.GetMaxActiveMechs(),
                active = Bays(sim.ActiveMechs, queued),
                readying = Bays(sim.ReadyingMechs, queued),
                work_queue = sim.MechLabQueue.Select(WorkOrder).ToList(),
            };
        }

        private static List<Dictionary<string, object>> Bays(Dictionary<int, MechDef> bays, HashSet<string> queued) =>
            bays.OrderBy(kv => kv.Key).Select(kv =>
            {
                var d = MechSerializer.Mech(kv.Value);
                d["bay"] = kv.Key;
                d["has_pending_work"] = kv.Value != null && queued.Contains(kv.Value.GUID);
                return d;
            }).ToList();

        private static object WorkOrder(WorkOrderEntry w)
        {
            var mechLab = w as WorkOrderEntry_MechLab;
            return new
            {
                id = w.ID,
                type = w.Type.ToString(),
                description = w.Description,
                mech_guid = mechLab?.MechID,
                sub_entries = w.SubEntries?.Select(s => s.Description).ToList(),
            };
        }

        /// <summary>
        /// Storage as counts. Inventory lives in company stats keyed
        /// "Item.{ResourceType}.{id}[.DAMAGED]" (components and whole mechs) or
        /// "Item.MECHPART.{mechDefId}" (salvaged parts toward a complete mech).
        /// </summary>
        public static object Storage(SimGameState sim)
        {
            var components = new List<object>();
            var mechs = new List<object>();
            var parts = new List<object>();
            foreach (var key in sim.GetAllInventoryStrings())
            {
                int count = sim.CompanyStats.GetValue<int>(key);
                if (count < 1)
                {
                    continue;
                }
                var p = key.Split('.');
                if (p.Length < 3)
                {
                    continue;
                }
                string type = p[1], id = p[2];
                bool damaged = p.Length > 3 && p[3] == "DAMAGED";
                if (type == "MECHPART")
                {
                    var mechDef = sim.DataManager.MechDefs.Exists(id) ? sim.DataManager.MechDefs.Get(id) : null;
                    int max = mechDef?.Chassis?.MechPartMax ?? 0;
                    parts.Add(new
                    {
                        mech_id = id,
                        name = mechDef?.Description?.Name,
                        parts = count,
                        parts_needed = max > 0 ? max : sim.Constants.Story.DefaultMechPartMax,
                    });
                }
                else if (type == nameof(BattleTechResourceType.MechDef))
                {
                    var chassis = sim.DataManager.ChassisDefs.Exists(id) ? sim.DataManager.ChassisDefs.Get(id) : null;
                    mechs.Add(new { chassis_id = id, name = chassis?.Description?.Name, variant = chassis?.VariantName, tonnage = chassis?.Tonnage, count });
                }
                else
                {
                    components.Add(StoredComponent(sim, type, id, damaged, count));
                }
            }
            return new { components, mechs, mech_parts = parts };
        }

        private static object StoredComponent(SimGameState sim, string type, string id, bool damaged, int count)
        {
            MechComponentDef def = null;
            if (System.Enum.TryParse(type, out BattleTechResourceType rt) && sim.DataManager.Exists(rt, id))
            {
                def = sim.GetComponentDef(rt, id);
            }
            var d = new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = def?.Description?.UIName ?? def?.Description?.Name,
                ["type"] = def?.ComponentType.ToString() ?? type,
                ["count"] = count,
                ["damaged"] = damaged,
                ["tonnage"] = def?.Tonnage,
                ["slots"] = def?.InventorySize,
            };
            if (def is WeaponDef w)
            {
                d["weapon"] = MechSerializer.Weapon(w);
            }
            return d;
        }
    }
}
