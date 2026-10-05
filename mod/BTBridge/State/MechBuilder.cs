using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.Data;
using BTBridge.Bridge;
using Newtonsoft.Json.Linq;

namespace BTBridge.State
{
    /// <summary>One requested component placement from a build spec.</summary>
    public sealed class SpecItem
    {
        public string Id;
        public ChassisLocations Location;
        public MechComponentDef Def;
    }

    /// <summary>
    /// A build in the same JSON shape the offline checker uses (the game's mechdef format):
    /// {"ChassisID": "...", "Locations": [{"Location", "AssignedArmor", "AssignedRearArmor"}],
    ///  "inventory": [{"ComponentDefID", "MountedLocation"}]}.
    /// Fixed equipment is implied by the chassis and must not be listed.
    /// </summary>
    public sealed class BuildSpec
    {
        public string ChassisId;
        public readonly Dictionary<ChassisLocations, float> Front = new Dictionary<ChassisLocations, float>();
        public readonly Dictionary<ChassisLocations, float> Rear = new Dictionary<ChassisLocations, float>();
        public readonly List<SpecItem> Items = new List<SpecItem>();

        public static BuildSpec Parse(JToken token, DataManager dm)
        {
            if (!(token is JObject obj))
            {
                throw new BridgeException(400, "mechdef must be a JSON object");
            }
            var spec = new BuildSpec { ChassisId = obj.Value<string>("ChassisID") };
            if (string.IsNullOrEmpty(spec.ChassisId))
            {
                throw new BridgeException(400, "mechdef.ChassisID is required");
            }
            foreach (var loc in obj["Locations"] as JArray ?? new JArray())
            {
                var where = ParseLocation(loc.Value<string>("Location"));
                if (loc["AssignedArmor"] != null)
                {
                    spec.Front[where] = loc.Value<float>("AssignedArmor");
                }
                if (loc["AssignedRearArmor"] != null)
                {
                    spec.Rear[where] = loc.Value<float>("AssignedRearArmor");
                }
            }
            var unknown = new List<string>();
            foreach (var item in obj["inventory"] as JArray ?? new JArray())
            {
                string id = item.Value<string>("ComponentDefID");
                var def = ResolveComponent(dm, id);
                if (def == null)
                {
                    unknown.Add(id);
                    continue;
                }
                spec.Items.Add(new SpecItem { Id = id, Location = ParseLocation(item.Value<string>("MountedLocation")), Def = def });
            }
            if (unknown.Count > 0)
            {
                throw new BridgeException(400, "unknown or unloaded components: " + string.Join(", ", unknown.ToArray()));
            }
            return spec;
        }

        public static ChassisLocations ParseLocation(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new BridgeException(400, "missing location");
            }
            ChassisLocations loc;
            try
            {
                loc = (ChassisLocations)Enum.Parse(typeof(ChassisLocations), name, ignoreCase: true);
            }
            catch (ArgumentException)
            {
                throw new BridgeException(400, $"unknown location '{name}'");
            }
            if (Array.IndexOf(MechSerializer.Locations, loc) < 0)
            {
                throw new BridgeException(400, $"'{name}' is not a single mech location");
            }
            return loc;
        }

        public static MechComponentDef ResolveComponent(DataManager dm, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            if (dm.WeaponDefs.TryGet(id, out var w)) return w;
            if (dm.AmmoBoxDefs.TryGet(id, out var a)) return a;
            if (dm.HeatSinkDefs.TryGet(id, out var h)) return h;
            if (dm.JumpJetDefs.TryGet(id, out var j)) return j;
            if (dm.UpgradeDefs.TryGet(id, out var u)) return u;
            return null;
        }

        /// <summary>Armor for a location: the spec's value if given, else the fallback.</summary>
        public float FrontFor(ChassisLocations loc, float fallback) => Front.TryGetValue(loc, out var v) ? v : fallback;

        public float RearFor(ChassisLocations loc, float fallback) =>
            MechBuilder.HasRear(loc) && Rear.TryGetValue(loc, out var v) ? v : fallback;
    }

    public static class MechBuilder
    {
        /// <summary>Validation types the mechlab refuses to save with (see MechLabPanel.ValidateLoadout).</summary>
        public static readonly HashSet<MechValidationType> Blocking = new HashSet<MechValidationType>
        {
            MechValidationType.ValidManifest,
            MechValidationType.Overweight,
            MechValidationType.WeaponsMissing,
            MechValidationType.InvalidInventorySlots,
            MechValidationType.InvalidHardpoints,
            MechValidationType.InvalidJumpjets,
            MechValidationType.StructureDestroyed,
        };

        public static bool HasRear(ChassisLocations loc) =>
            loc == ChassisLocations.CenterTorso || loc == ChassisLocations.LeftTorso || loc == ChassisLocations.RightTorso;

        /// <summary>A brand-new mech (skirmish designs, or validating a design in isolation).</summary>
        public static MechDef BuildNew(BuildSpec spec, DataManager dm, string id, string name, int cost = 0)
        {
            if (!dm.ChassisDefs.TryGet(spec.ChassisId, out var chassis))
            {
                throw new BridgeException(400, $"unknown or unloaded chassis '{spec.ChassisId}'");
            }
            var locations = MechSerializer.Locations.Select(loc =>
            {
                var cl = chassis.GetLocationDef(loc);
                float front = spec.FrontFor(loc, 0f);
                float rear = spec.RearFor(loc, HasRear(loc) ? 0f : -1f);
                return new LocationLoadoutDef(loc, front, rear, cl.InternalStructure, front, rear);
            }).ToArray();
            var slots = new HardpointCounter();
            var inventory = spec.Items
                .Select(i => NewRef(i.Def, i.Location, slots.Take(i.Def, i.Location), dm, null))
                .ToArray();
            var description = new DescriptionDef(id, name, "", "", cost, 0f, false, "", "", name);
            // Same constructor MechLabPanel.CreateMechDef uses; it inserts the chassis's fixed equipment.
            return new MechDef(description, spec.ChassisId, inventory, new HBS.Collections.TagSet(), locations, dm);
        }

        /// <summary>
        /// Weapons get hardpoint slots numbered per location in placement order, the way
        /// MechLabLocationWidget does (slot = weapons already in that location).
        /// </summary>
        public sealed class HardpointCounter
        {
            private readonly Dictionary<ChassisLocations, int> used = new Dictionary<ChassisLocations, int>();

            public void Count(MechComponentRef existing)
            {
                if (existing.ComponentDefType == ComponentType.Weapon)
                {
                    used[existing.MountedLocation] = Next(existing.MountedLocation) + 1;
                }
            }

            public int Take(MechComponentDef def, ChassisLocations loc)
            {
                if (def.ComponentType != ComponentType.Weapon)
                {
                    return -1;
                }
                int slot = Next(loc);
                used[loc] = slot + 1;
                return slot;
            }

            private int Next(ChassisLocations loc) => used.TryGetValue(loc, out var n) ? n : 0;
        }

        public static MechComponentRef NewRef(MechComponentDef def, ChassisLocations loc, int hardpointSlot, DataManager dm, string simGameUid)
        {
            var r = new MechComponentRef(def.Description.Id, simGameUid, def.ComponentType, loc, hardpointSlot, ComponentDamageLevel.Functional);
            r.DataManager = dm;
            r.SetComponentDef(def);
            return r;
        }

        /// <summary>Copy a ref keeping its SimGameUID. The game's copy constructor drops Def, so restore it.</summary>
        public static MechComponentRef Copy(MechComponentRef c)
        {
            var r = new MechComponentRef(c);
            if (c.Def != null)
            {
                r.SetComponentDef(c.Def);
            }
            else
            {
                r.RefreshComponentDef();
            }
            return r;
        }

        public static Dictionary<string, List<string>> Validate(MechDef mech, DataManager dm, MechValidationLevel level)
        {
            return MechValidationRules.ValidateMechDef(level, dm, mech, null)
                .Where(kv => kv.Value != null && kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(t => t.ToString()).ToList());
        }

        public static bool IsBlocked(Dictionary<string, List<string>> errors) =>
            errors.Keys.Any(k => Blocking.Any(b => b.ToString() == k));

        public static float CBillValue(MechDef mech)
        {
            float cur = 0f, max = 0f;
            MechStatisticsRules.CalculateCBillValue(mech, ref cur, ref max);
            return cur;
        }
    }
}
