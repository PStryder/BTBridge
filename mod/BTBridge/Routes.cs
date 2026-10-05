using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Combat;
using BTBridge.Patches;
using BTBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTBridge
{
    public static class Routes
    {
        public static List<Route> Build() => new List<Route>
        {
            new Route { Method = "GET", Path = "/health", Handler = Health },

            // campaign (read)
            new Route { Method = "GET", Path = "/sim/company", Handler = r => SimSerializer.Company(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/mechbay", Handler = r => SimSerializer.MechBay(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/storage", Handler = r => SimSerializer.Storage(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/mech", Handler = SimMech },
            new Route { Method = "GET", Path = "/mechlab/current", Handler = MechLabCurrent },

            // campaign (write: preview is side-effect free, apply commits)
            new Route { Method = "POST", Path = "/sim/refit/preview", Handler = RefitPreview },
            new Route { Method = "POST", Path = "/sim/refit/apply", Handler = r => RefitPlanner.Apply(RequireSim(), Body(r).Value<string>("plan_id")) },

            // any mode
            new Route { Method = "POST", Path = "/mech/validate", Handler = Validate },

            // skirmish
            new Route { Method = "GET", Path = "/skirmish/custom", Handler = r => Skirmish.List() },
            new Route { Method = "GET", Path = "/skirmish/pilots", Handler = r => Skirmish.Pilots() },
            new Route { Method = "POST", Path = "/skirmish/mechs", Handler = SkirmishSaveMech },
            new Route { Method = "DELETE", Path = "/skirmish/mechs", Handler = r => Skirmish.DeleteMech(r.QueryOr("id", null)) },
            new Route { Method = "POST", Path = "/skirmish/lances", Handler = SkirmishSaveLance },
            new Route { Method = "DELETE", Path = "/skirmish/lances", Handler = r => Skirmish.DeleteLance(r.QueryOr("id", null)) },

            // combat
            new Route { Method = "GET", Path = "/combat/control", Handler = r => ControlView() },
            new Route { Method = "POST", Path = "/combat/control", Handler = SetControl },
            new Route { Method = "GET", Path = "/combat/state", Handler = r => CombatSerializer.State(RequireCombat(), RequireCombat().LocalPlayerTeam) },
            new Route { Method = "GET", Path = "/combat/decision", Handler = DecisionView },
            new Route { Method = "POST", Path = "/combat/decision", Handler = r =>
                {
                    var body = Body(r);
                    return DecisionBroker.Answer(RequireCombat(), body.Value<string>("id"), body.Value<string>("unit"), body["order"] as JObject);
                } },
            new Route { Method = "GET", Path = "/combat/history", Handler = r => DecisionBroker.RecentHistory() },
            new Route { Method = "GET", Path = "/combat/reachable", Handler = r => DecisionBroker.ReachableView(
                RequireCombat(), r.QueryOr("move", "walk"),
                float.TryParse(r.QueryOr("x", ""), out var x) ? x : (float?)null,
                float.TryParse(r.QueryOr("z", ""), out var z) ? z : (float?)null,
                int.TryParse(r.QueryOr("limit", ""), out var n) ? n : 20) },
        };

        private static CombatGameState RequireCombat() =>
            Game?.Combat ?? throw new BridgeException(409, "no combat in progress");

        private static object ControlView() => new
        {
            next_mission = CombatControl.Requested.ToString(),
            current_mission = Game?.Combat != null ? CombatControl.ActiveMode.ToString() : null,
            decision_timeout_seconds = CombatControl.DecisionTimeoutSeconds,
            modes = new[] { "Human", "Agent", "BuiltinAI" },
            note = "the mode is applied when a mission builds its teams, so changes take effect from the next mission",
        };

        private static object SetControl(BridgeRequest r)
        {
            var body = Body(r);
            string mode = body.Value<string>("player");
            if (mode != null)
            {
                try
                {
                    CombatControl.Requested = (PlayerControl)System.Enum.Parse(typeof(PlayerControl), mode, ignoreCase: true);
                }
                catch (System.ArgumentException)
                {
                    throw new BridgeException(400, $"unknown mode '{mode}' (Human | Agent | BuiltinAI)");
                }
            }
            float? timeout = body.Value<float?>("decision_timeout_seconds");
            if (timeout.HasValue)
            {
                CombatControl.DecisionTimeoutSeconds = System.Math.Max(0f, timeout.Value);
            }
            Log.Info($"combat control: next mission {CombatControl.Requested}, timeout {CombatControl.DecisionTimeoutSeconds}s");
            return ControlView();
        }

        private static object DecisionView(BridgeRequest r)
        {
            var combat = RequireCombat();
            var d = DecisionBroker.Current;
            if (d == null)
            {
                return new
                {
                    open = false,
                    agent_controls_player = CombatControl.IsAgentTeam(combat.LocalPlayerTeam),
                    active_team = (combat.TurnDirector.ActiveTurnActor as Team)?.DisplayName,
                    round = combat.TurnDirector.CurrentRound,
                    phase = combat.TurnDirector.CurrentPhase,
                };
            }
            return DecisionBroker.View(d, combat);
        }

        private static GameInstance Game => UnityGameInstance.BattleTechGame;

        private static SimGameState RequireSim() =>
            Game?.Simulation ?? throw new BridgeException(409, "no campaign (sim game) is loaded");

        private static JObject Body(BridgeRequest r)
        {
            if (string.IsNullOrEmpty(r.Body))
            {
                throw new BridgeException(400, "a JSON body is required");
            }
            try
            {
                return JObject.Parse(r.Body);
            }
            catch (JsonException e)
            {
                throw new BridgeException(400, "invalid JSON body: " + e.Message);
            }
        }

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

        private static object SimMech(BridgeRequest r)
        {
            var sim = RequireSim();
            string key = r.QueryOr("bay", null) ?? r.QueryOr("guid", null)
                ?? throw new BridgeException(400, "pass ?bay=N or ?guid=...");
            var hit = sim.ActiveMechs.FirstOrDefault(kv => kv.Value != null && (kv.Key.ToString() == key || kv.Value.GUID == key));
            if (hit.Value == null)
            {
                throw new BridgeException(404, $"no active mech '{key}'");
            }
            var d = MechSerializer.Mech(hit.Value);
            d["bay"] = hit.Key;
            return new { mech = d, spec = MechSerializer.Spec(hit.Value) };
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
                current_spec = MechSerializer.Spec(snapshot),
                validation_errors = errors,
            };
        }

        private static object Validate(BridgeRequest r)
        {
            var dm = Game?.DataManager ?? throw new BridgeException(409, "game data not loaded");
            var spec = BuildSpec.Parse(Body(r)["mechdef"], dm);
            var mech = MechBuilder.BuildNew(spec, dm, "mechdef_BTBRIDGE_validate", "Proposed build");
            var errors = MechBuilder.Validate(mech, dm);
            return new
            {
                can_field = !MechBuilder.IsBlocked(errors),
                validation_errors = errors,
                blocking_types = MechBuilder.Blocking.Select(b => b.ToString()).ToList(),
                mech = MechSerializer.Mech(mech),
            };
        }

        private static object RefitPreview(BridgeRequest r)
        {
            var sim = RequireSim();
            var body = Body(r);
            var spec = BuildSpec.Parse(body["mechdef"], sim.DataManager);
            return RefitPlanner.Preview(sim, body.Value<string>("mech"), spec);
        }

        private static object SkirmishSaveMech(BridgeRequest r)
        {
            var dm = Game?.DataManager ?? throw new BridgeException(409, "game data not loaded");
            var body = Body(r);
            var spec = BuildSpec.Parse(body["mechdef"], dm);
            return Skirmish.SaveMech(spec, body.Value<string>("name"), body.Value<string>("replace_id"));
        }

        private static object SkirmishSaveLance(BridgeRequest r)
        {
            var body = Body(r);
            var units = (body["units"] as JArray ?? new JArray())
                .Select(u => new Skirmish.LanceUnitSpec { MechId = u.Value<string>("mech_id"), PilotId = u.Value<string>("pilot_id") })
                .ToList();
            return Skirmish.SaveLance(body.Value<string>("name"), units, body.Value<string>("replace_id"));
        }
    }
}
