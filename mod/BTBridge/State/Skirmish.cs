using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.Data;
using BattleTech.Save;
using BTBridge.Bridge;
using HBS.Collections;

namespace BTBridge.State
{
    /// <summary>
    /// Skirmish custom mechs and lances, saved the way SkirmishMechBayPanel saves them
    /// (SaveMech / SetCustomMechTags / LanceConfiguratorPanel.CreateLanceDef).
    /// Nothing here touches a campaign.
    /// </summary>
    public static class Skirmish
    {
        private static SkirmishUnitsAndLances Custom =>
            ActiveOrDefaultSettings.CloudSettings?.CustomUnitsAndLances
            ?? throw new BridgeException(409, "user settings not loaded yet");

        private static DataManager Dm =>
            UnityGameInstance.BattleTechGame?.DataManager ?? throw new BridgeException(409, "game data not loaded");

        public static object List()
        {
            var custom = Custom;
            return new
            {
                mechs = custom.GetValidMechs().Select(MechSerializer.Mech).ToList(),
                lances = custom.GetValidLances().Select(Lance).ToList(),
            };
        }

        public static object SaveMech(BuildSpec spec, string name, string replaceId)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new BridgeException(400, "name is required");
            }
            var custom = Custom;
            string id = replaceId;
            if (!string.IsNullOrEmpty(replaceId))
            {
                var existing = custom.GetMechDef(replaceId);
                if (existing == null || !existing.MechTags.Contains("unit_custom"))
                {
                    throw new BridgeException(404, $"no custom skirmish mech '{replaceId}' to replace");
                }
            }
            else
            {
                id = $"mechdef_CUSTOM_{Guid.NewGuid()}";
            }

            var mech = MechBuilder.BuildNew(spec, Dm, id, name);
            var errors = MechBuilder.Validate(mech, Dm, MechValidationLevel.MechLab);
            if (MechBuilder.IsBlocked(errors))
            {
                return new { saved = false, id = (string)null, validation_errors = errors, mech = MechSerializer.Mech(mech) };
            }
            // CreateMechDef stores the mechlab's C-bill value as the description cost; lance brackets use it.
            mech = MechBuilder.BuildNew(spec, Dm, id, name, (int)MechBuilder.CBillValue(mech));
            mech.MechTags.Clear();
            mech.MechTags.AddRange(new[] { "unit_custom", "unit_release" });
            custom.AddOrUpdateMechDef(mech);
            ActiveOrDefaultSettings.SaveUserSettings();
            Log.Info($"saved skirmish mech {id} '{name}'");
            return new { saved = true, id, validation_errors = errors, mech = MechSerializer.Mech(mech) };
        }

        public static object DeleteMech(string id)
        {
            var custom = Custom;
            var existing = custom.GetMechDef(id);
            if (existing == null || !existing.MechTags.Contains("unit_custom"))
            {
                throw new BridgeException(404, $"no custom skirmish mech '{id}'");
            }
            bool removed = custom.RemoveMechDef(id);
            ActiveOrDefaultSettings.SaveUserSettings();
            return new { deleted = removed, id };
        }

        public static object Pilots()
        {
            var pilots = Dm.PilotDefs
                .Select(kv => kv.Value)
                .Where(p => p != null && MechValidationRules.PilotIsValidForSkirmish(p))
                .Select(p => new
                {
                    id = p.Description.Id,
                    name = p.Description.Name,
                    callsign = p.Description.Callsign,
                    gunnery = p.BaseGunnery,
                    piloting = p.BasePiloting,
                    guts = p.BaseGuts,
                    tactics = p.BaseTactics,
                })
                .OrderBy(p => p.callsign)
                .ToList();
            return new
            {
                pilots,
                note = pilots.Count == 0 ? "no skirmish pilots loaded yet; open the skirmish mechbay once so the game loads them" : null,
            };
        }

        public sealed class LanceUnitSpec
        {
            public string MechId;
            public string PilotId;
        }

        public static object SaveLance(string name, List<LanceUnitSpec> units, string replaceId)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new BridgeException(400, "name is required");
            }
            if (units == null || units.Count < 1 || units.Count > 4)
            {
                throw new BridgeException(400, "a lance needs 1 to 4 units");
            }
            var custom = Custom;
            var dm = Dm;
            var lanceUnits = new List<LanceDef.Unit>();
            int value = 0;
            foreach (var u in units)
            {
                var mech = custom.GetMechDef(u.MechId) ?? (dm.MechDefs.TryGet(u.MechId ?? "", out var stock) ? stock : null);
                if (mech == null)
                {
                    throw new BridgeException(400, $"unknown mech '{u.MechId}' (stock mechdef id or custom skirmish mech id)");
                }
                if (!MechValidationRules.MechIsValidForSkirmish(mech, includeCustomMechs: true))
                {
                    throw new BridgeException(400, $"'{u.MechId}' is not allowed in skirmish");
                }
                if (!dm.PilotDefs.TryGet(u.PilotId ?? "", out var pilot) || !MechValidationRules.PilotIsValidForSkirmish(pilot))
                {
                    throw new BridgeException(400, $"unknown or non-skirmish pilot '{u.PilotId}' (see /skirmish/pilots)");
                }
                value += mech.Description.Cost;
                lanceUnits.Add(new LanceDef.Unit { unitType = UnitType.Mech, unitId = mech.Description.Id, pilotId = pilot.Description.Id });
            }
            string id = string.IsNullOrEmpty(replaceId) ? $"lancedef_CUSTOM_{Guid.NewGuid()}" : replaceId;
            if (!string.IsNullOrEmpty(replaceId) && !custom.ContainsLanceDef(replaceId))
            {
                throw new BridgeException(404, $"no custom lance '{replaceId}' to replace");
            }
            var description = new DescriptionDef(id, name, "", "", value, 0f, false, "", "", "");
            var tags = new TagSet("lance_type_custom", "lance_release", "lance_bracket_skirmish", MechValidationRules.GetLanceBracketTag(value));
            var lance = new LanceDef(description, 0, tags, lanceUnits.ToArray());
            custom.RemoveLanceDef(id);
            custom.AddOrUpdateLanceDef(lance);
            ActiveOrDefaultSettings.SaveUserSettings();
            Log.Info($"saved skirmish lance {id} '{name}'");
            return new { saved = true, lance = Lance(lance) };
        }

        public static object DeleteLance(string id)
        {
            var custom = Custom;
            if (!custom.ContainsLanceDef(id))
            {
                throw new BridgeException(404, $"no custom lance '{id}'");
            }
            bool removed = custom.RemoveLanceDef(id);
            ActiveOrDefaultSettings.SaveUserSettings();
            return new { deleted = removed, id };
        }

        private static object Lance(LanceDef l) => new
        {
            id = l.Description.Id,
            name = l.Description.Name,
            value = l.Description.Cost,
            tags = l.LanceTags?.ToArray(),
            units = l.LanceUnits.Select(u => new { mech_id = u.unitId, pilot_id = u.pilotId }).ToList(),
        };
    }
}
