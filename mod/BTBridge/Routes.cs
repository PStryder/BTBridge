using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.Save;
using BTBridge.Bridge;
using BTBridge.Patches;
using BTBridge.State;

namespace BTBridge
{
    public static class Routes
    {
        public static List<Route> Build() => new List<Route>
        {
            new Route { Method = "GET", Path = "/health", Handler = Health },
            new Route { Method = "GET", Path = "/sim/company", Handler = r => SimSerializer.Company(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/mechbay", Handler = r => SimSerializer.MechBay(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/storage", Handler = r => SimSerializer.Storage(RequireSim()) },
            new Route { Method = "GET", Path = "/mechlab/current", Handler = MechLabCurrent },
            new Route { Method = "GET", Path = "/skirmish/custom", Handler = SkirmishCustom },
        };

        private static GameInstance Game => UnityGameInstance.BattleTechGame;

        private static SimGameState RequireSim() =>
            Game?.Simulation ?? throw new BridgeException(409, "no campaign (sim game) is loaded");

        private static object Health(BridgeRequest r)
        {
            var game = Game;
            return new
            {
                bridge_version = Main.Version,
                game_version = VersionInfo.GetReleaseVersion(),
                sim_loaded = game?.Simulation != null,
                combat_active = game?.Combat != null,
                mechlab_open = MechLabTracker.Live != null,
            };
        }

        private static object MechLabCurrent(BridgeRequest r)
        {
            var lab = MechLabTracker.Live;
            if (lab == null)
            {
                return new { open = false };
            }
            // CreateMechDef snapshots the panel's widgets without touching game state,
            // so this reflects unsaved, in-progress edits.
            var snapshot = lab.CreateMechDef();
            // The static validator, not lab.ValidateLoadout, which also repaints the UI.
            var errors = MechValidationRules
                .ValidateMechDef(MechValidationLevel.MechLab, lab.dataManager, snapshot, lab.baseWorkOrder)
                .Where(kv => kv.Value != null && kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(t => t.ToString()).ToList());
            return new
            {
                open = true,
                mode = lab.IsSimGame ? "campaign" : "skirmish",
                modified = lab.Modified,
                original = MechSerializer.Mech(lab.originalMechDef),
                current = MechSerializer.Mech(snapshot),
                validation_errors = errors,
            };
        }

        private static object SkirmishCustom(BridgeRequest r)
        {
            var custom = ActiveOrDefaultSettings.CloudSettings?.CustomUnitsAndLances;
            if (custom == null)
            {
                throw new BridgeException(409, "user settings not loaded yet");
            }
            return new
            {
                mechs = custom.GetValidMechs().Select(MechSerializer.Mech).ToList(),
            };
        }
    }
}
