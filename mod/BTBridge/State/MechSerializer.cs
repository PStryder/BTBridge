using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;

namespace BTBridge.State
{
    /// <summary>
    /// Flattens game defs into plain dictionaries. Never hand game objects to the JSON
    /// serializer directly: they are cyclic and drag in Unity types.
    /// </summary>
    public static class MechSerializer
    {
        public static readonly ChassisLocations[] Locations =
        {
            ChassisLocations.Head, ChassisLocations.CenterTorso, ChassisLocations.LeftTorso, ChassisLocations.RightTorso,
            ChassisLocations.LeftArm, ChassisLocations.RightArm, ChassisLocations.LeftLeg, ChassisLocations.RightLeg,
        };

        public static Dictionary<string, object> Mech(MechDef mech)
        {
            if (mech == null)
            {
                return null;
            }
            var chassis = mech.Chassis;
            var inventory = mech.Inventory ?? new MechComponentRef[0];
            var d = new Dictionary<string, object>
            {
                ["id"] = mech.Description?.Id,
                ["guid"] = mech.GUID,
                ["name"] = mech.Description?.Name,
                ["chassis_id"] = mech.ChassisID,
                ["variant"] = chassis?.VariantName,
                ["chassis_name"] = chassis?.Description?.Name,
                ["weight_class"] = chassis?.weightClass.ToString(),
                ["max_tonnage"] = chassis?.Tonnage,
                ["tags"] = mech.MechTags?.ToArray(),
                ["armor"] = new { assigned = mech.MechDefAssignedArmor, current = mech.MechDefCurrentArmor, max = SafeFloat(() => mech.MechDefAbsoluteMaxArmor) },
                ["structure"] = new { current = mech.MechDefCurrentStructure, max = SafeFloat(() => mech.MechDefMaxStructure) },
                ["jump_jets"] = new { installed = inventory.Count(c => c.ComponentDefType == ComponentType.JumpJet), max = chassis?.MaxJumpjets },
                ["heat_sinks"] = new { installed = inventory.Count(c => c.ComponentDefType == ComponentType.HeatSink), chassis_base = chassis?.Heatsinks },
                ["stats"] = Stats(mech),
                ["locations"] = Locations.Select(loc => Location(mech, loc)).ToList(),
                ["inventory"] = inventory.Select(Component).ToList(),
            };
            return d;
        }

        /// <summary>
        /// The build in the editable spec format accepted by validate / refit / skirmish save.
        /// Fixed equipment is omitted: the chassis supplies it.
        /// </summary>
        public static object Spec(MechDef mech) => mech == null ? null : new
        {
            ChassisID = mech.ChassisID,
            Locations = Locations.Select(loc =>
            {
                var l = mech.GetLocationLoadoutDef(loc);
                return new { Location = loc.ToString(), AssignedArmor = l.AssignedArmor, AssignedRearArmor = l.AssignedRearArmor };
            }).ToList(),
            inventory = (mech.Inventory ?? new MechComponentRef[0])
                .Where(c => !c.IsFixed)
                .Select(c => new { ComponentDefID = c.ComponentDefID, MountedLocation = c.MountedLocation.ToString() })
                .ToList(),
        };

        public static Dictionary<string, object> Location(MechDef mech, ChassisLocations loc)
        {
            var loadout = mech.GetLocationLoadoutDef(loc);
            var def = mech.Chassis?.GetLocationDef(loc);
            var installed = (mech.Inventory ?? new MechComponentRef[0]).Where(c => c.MountedLocation == loc).ToList();
            return new Dictionary<string, object>
            {
                ["location"] = loc.ToString(),
                ["damage"] = loadout?.DamageLevel.ToString(),
                ["armor"] = new { assigned = loadout?.AssignedArmor, current = loadout?.CurrentArmor, max = def?.MaxArmor },
                ["rear_armor"] = HasRear(loc)
                    ? new { assigned = loadout?.AssignedRearArmor, current = loadout?.CurrentRearArmor, max = def?.MaxRearArmor }
                    : null,
                ["structure"] = new { current = loadout?.CurrentInternalStructure, max = def?.InternalStructure },
                ["slots"] = new { total = def?.InventorySlots, used = installed.Sum(c => c.Def?.InventorySize ?? 0) },
                ["hardpoints"] = Hardpoints(def),
            };
        }

        private static bool HasRear(ChassisLocations loc) =>
            loc == ChassisLocations.CenterTorso || loc == ChassisLocations.LeftTorso || loc == ChassisLocations.RightTorso;

        private static Dictionary<string, int> Hardpoints(LocationDef? def)
        {
            var counts = new Dictionary<string, int>();
            if (def?.Hardpoints == null)
            {
                return counts;
            }
            foreach (var hp in def.Value.Hardpoints)
            {
                string key = hp.WeaponMountValue?.Name ?? "Unknown";
                if (hp.Omni)
                {
                    key = "Omni";
                }
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            return counts;
        }

        public static Dictionary<string, object> Component(MechComponentRef c)
        {
            var def = c.Def;
            var d = new Dictionary<string, object>
            {
                ["id"] = c.ComponentDefID,
                ["name"] = def?.Description?.UIName ?? def?.Description?.Name,
                ["type"] = c.ComponentDefType.ToString(),
                ["location"] = c.MountedLocation.ToString(),
                ["hardpoint_slot"] = c.HardpointSlot,
                ["damage"] = c.DamageLevel.ToString(),
                ["fixed"] = c.IsFixed,
                ["tonnage"] = def?.Tonnage,
                ["slots"] = def?.InventorySize,
            };
            if (def is WeaponDef w)
            {
                d["weapon"] = Weapon(w);
            }
            return d;
        }

        public static Dictionary<string, object> Weapon(WeaponDef w) => new Dictionary<string, object>
        {
            ["category"] = w.WeaponCategoryValue?.Name,
            ["weapon_type"] = w.Type.ToString(),
            ["damage"] = w.Damage,
            ["shots"] = w.ShotsWhenFired,
            ["heat"] = w.HeatGenerated,
            ["heat_damage"] = w.HeatDamage,
            ["stability_damage"] = w.Instability,
            ["accuracy_mod"] = w.AccuracyModifier,
            ["crit_mult"] = w.CriticalChanceMultiplier,
            ["range"] = new { min = w.MinRange, max = w.MaxRange, brackets = w.RangeSplit },
            ["indirect_fire"] = w.IndirectFireCapable,
            ["recoil"] = w.RefireModifier,
            ["starting_ammo"] = w.StartingAmmoCapacity,
        };

        private delegate void StatCalc(MechDef def, ref float current, ref float max);

        /// <summary>The same numbers the mechlab stat bars display.</summary>
        public static Dictionary<string, object> Stats(MechDef mech)
        {
            var stats = new Dictionary<string, object>();
            void Add(string name, StatCalc calc)
            {
                try
                {
                    float cur = 0f, max = 0f;
                    calc(mech, ref cur, ref max);
                    stats[name] = new { current = cur, max };
                }
                catch (Exception e)
                {
                    stats[name] = new { error = e.Message };
                }
            }
            Add("tonnage", MechStatisticsRules.CalculateTonnage);
            Add("cbill_value", MechStatisticsRules.CalculateCBillValue);
            Add("firepower", MechStatisticsRules.CalculateFirepowerStat);
            Add("heat_efficiency", MechStatisticsRules.CalculateHeatEfficiencyStat);
            Add("durability", MechStatisticsRules.CalculateDurabilityStat);
            Add("movement", MechStatisticsRules.CalculateMovementStat);
            Add("range", MechStatisticsRules.CalculateRangeStat);
            Add("melee", MechStatisticsRules.CalculateMeleeStat);
            // CalculateTonnage reports max = 100 (it drives a UI bar); the real cap is the chassis tonnage.
            try
            {
                float cur = 0f, ignored = 0f;
                MechStatisticsRules.CalculateTonnage(mech, ref cur, ref ignored);
                stats["tonnage"] = new { current = cur, max = mech.Chassis.Tonnage };
            }
            catch
            {
                // Keep whatever Add() recorded, including its error.
            }
            return stats;
        }

        private static float? SafeFloat(Func<float> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return null;
            }
        }
    }
}
