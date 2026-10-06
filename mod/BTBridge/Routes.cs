using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Combat;
using BTBridge.Patches;
using BTBridge.Sim;
using BTBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTBridge
{
    public static class Routes
    {
        /// <summary>
        /// The route table. Cheat routes are added only when the cheat capability was enabled at
        /// startup; otherwise they do not exist (404 like any unknown path).
        /// </summary>
        public static List<Route> Build(bool cheats)
        {
            var routes = Core();
            if (cheats)
            {
                routes.AddRange(CheatRoutes());
            }
            return routes;
        }

        /// <summary>OPERATOR CHEAT LAYER routes. There is deliberately no route to arm or reconfigure.</summary>
        public static List<Route> CheatRoutes() => new List<Route>
        {
            new Route { Method = "GET", Path = "/cheat/status", Handler = r => Cheats.CheatService.Status() },
            new Route { Method = "POST", Path = "/cheat/preview", Handler = r =>
                {
                    var b = Body(r);
                    return Cheats.CheatService.Preview(b.Value<string>("op"), b["args"] as JObject, b.Value<string>("operator_request"));
                } },
            new Route { Method = "POST", Path = "/cheat/execute", Handler = r => Cheats.CheatService.Execute(Body(r).Value<string>("plan_id")) },
            new Route { Method = "POST", Path = "/cheat/disarm", Handler = r => Cheats.CheatService.Disarm() },
            new Route { Method = "GET", Path = "/cheat/audit", Handler = r => Cheats.CheatService.AuditTail(int.TryParse(r.QueryOr("limit", ""), out var n) ? n : 20) },
        };

        private static List<Route> Core() => new List<Route>
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

            // in-game chat overlay (output only; channel visibility is the operator's, in-game)
            new Route { Method = "POST", Path = "/overlay/say", Handler = r =>
                {
                    var b = Body(r);
                    return Ui.ChatOverlay.Say(b.Value<string>("type"), b.Value<string>("text"));
                } },
            new Route { Method = "GET", Path = "/overlay/history", Handler = r => Ui.ChatOverlay.History() },
            // operator -> agent messages: read-only here (only the in-game box writes them) plus acknowledge
            new Route { Method = "GET", Path = "/overlay/inbox", Handler = r => Ui.ChatOverlay.InboxView() },
            new Route { Method = "POST", Path = "/overlay/inbox/ack", Handler = r => Ui.ChatOverlay.Ack(Body(r).Value<int?>("up_to_id") ?? throw new BridgeException(400, "up_to_id is required")) },

            // campaign: status, interrupts, time
            new Route { Method = "GET", Path = "/sim/status", Handler = r => Interrupts.Status(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/interrupt", Handler = r => Interrupts.Describe(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/interrupt", Handler = r => Interrupts.Resolve(RequireSim(), Body(r)) },
            new Route { Method = "POST", Path = "/sim/time", Handler = r =>
                {
                    var b = Body(r);
                    if (b.Value<bool?>("stop") == true)
                    {
                        return TimeControl.Stop(RequireSim());
                    }
                    return TimeControl.Start(RequireSim(), b.Value<int?>("days"), b.Value<bool?>("until_event") ?? false, b.Value<float?>("day_seconds"));
                } },

            // campaign: contracts and missions
            new Route { Method = "GET", Path = "/sim/contracts", Handler = r => Contracts.List(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/contracts/accept", Handler = r =>
                {
                    var b = Body(r);
                    return Contracts.Accept(RequireSim(), b.Value<int?>("index") ?? -1, b.Value<string>("name"), b.Value<float?>("pay"), b.Value<float?>("salvage"));
                } },
            new Route { Method = "POST", Path = "/sim/contracts/launch", Handler = r =>
                {
                    var units = (Body(r)["units"] as JArray ?? new JArray())
                        .Select(u => new Contracts.UnitChoice { Bay = u.Value<int?>("bay"), Pilot = u.Value<string>("pilot") })
                        .ToList();
                    return Contracts.Launch(RequireSim(), units);
                } },
            new Route { Method = "GET", Path = "/combat/mission", Handler = r => Contracts.MissionEnd(RequireCombat()) },
            new Route { Method = "POST", Path = "/combat/begin", Handler = r => Contracts.BeginMission() },
            new Route { Method = "GET", Path = "/combat/dialog", Handler = r => MissionDialog.View() },
            new Route { Method = "POST", Path = "/combat/dialog/continue", Handler = r => MissionDialog.Continue() },
            new Route { Method = "GET", Path = "/dialog/transcript", Handler = r => MissionDialog.TranscriptView(
                int.TryParse(r.QueryOr("limit", ""), out var lim) ? lim : 50,
                r.QueryOr("repeats", "false") == "true",
                int.TryParse(r.QueryOr("since", ""), out var since) ? since : 0) },
            new Route { Method = "POST", Path = "/combat/withdraw", Handler = r => Contracts.Withdraw(RequireCombat()) },
            new Route { Method = "POST", Path = "/combat/exit", Handler = r => Contracts.ExitMission() },
            new Route { Method = "GET", Path = "/sim/aar", Handler = r => Contracts.Aar(Game?.Simulation) },
            new Route { Method = "POST", Path = "/sim/aar", Handler = r =>
                {
                    var b = Body(r);
                    return Contracts.AarContinue(Game?.Simulation, b["salvage"] as JArray);
                } },

            // campaign: navigation
            new Route { Method = "GET", Path = "/sim/starmap", Handler = r => Navigation.Map(RequireSim(), int.TryParse(r.QueryOr("jumps", ""), out var j) ? j : 2) },
            new Route { Method = "GET", Path = "/sim/travel", Handler = r => Navigation.View(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/travel", Handler = r =>
                {
                    var b = Body(r);
                    return Navigation.Travel(RequireSim(), b.Value<string>("system"), b.Value<bool?>("confirm") ?? false);
                } },

            // campaign: company
            new Route { Method = "GET", Path = "/sim/pilots", Handler = r => Company.Pilots(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/pilots/train", Handler = r =>
                {
                    var b = Body(r);
                    return Company.Train(RequireSim(), b.Value<string>("pilot"), b.Value<string>("skill"), b.Value<int?>("to") ?? 0, b.Value<bool?>("confirm") ?? false);
                } },
            new Route { Method = "GET", Path = "/sim/hiring", Handler = r => Company.HiringHall(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/pilots/hire", Handler = r => Company.Hire(RequireSim(), Body(r).Value<string>("id")) },
            new Route { Method = "POST", Path = "/sim/pilots/dismiss", Handler = r => Company.Dismiss(RequireSim(), Body(r).Value<string>("pilot")) },
            new Route { Method = "GET", Path = "/sim/store", Handler = r => Company.Store(RequireSim(), r.QueryOr("shop", "system")) },
            new Route { Method = "GET", Path = "/sim/store/sellable", Handler = r => Company.Sellables(RequireSim(), r.QueryOr("shop", "system")) },
            new Route { Method = "POST", Path = "/sim/store/buy", Handler = r =>
                {
                    var b = Body(r);
                    return Company.Buy(RequireSim(), b.Value<string>("shop"), b.Value<string>("id"), b.Value<int?>("count") ?? 1);
                } },
            new Route { Method = "POST", Path = "/sim/store/sell", Handler = r =>
                {
                    var b = Body(r);
                    return Company.Sell(RequireSim(), b.Value<string>("shop"), b.Value<string>("id"), b.Value<string>("type"), b.Value<int?>("count") ?? 1);
                } },
            new Route { Method = "GET", Path = "/sim/argo", Handler = r => Company.Argo(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/argo/upgrade", Handler = r => Company.BuyUpgrade(RequireSim(), Body(r).Value<string>("id")) },
            new Route { Method = "GET", Path = "/sim/finances", Handler = r => Company.Finances(RequireSim()) },
            new Route { Method = "GET", Path = "/sim/flashpoints", Handler = r => Company.Flashpoints(RequireSim()) },
            new Route { Method = "POST", Path = "/sim/flashpoints/accept", Handler = r => Company.AcceptFlashpoint(RequireSim(), Body(r).Value<string>("id")) },

            // combat
            new Route { Method = "GET", Path = "/combat/control", Handler = r => ControlView() },
            new Route { Method = "POST", Path = "/combat/control", Handler = SetControl },
            new Route { Method = "GET", Path = "/combat/state", Handler = r => CombatSerializer.State(RequireCombat(), Viewer(r)) },
            new Route { Method = "GET", Path = "/combat/briefing", Handler = r => Briefing.Build(RequireCombat(), Viewer(r), Side(r)) },
            new Route { Method = "GET", Path = "/combat/orders", Handler = r => StandingOrders.View(RequireCombat()) },
            new Route { Method = "POST", Path = "/combat/orders", Handler = r =>
                {
                    var body = Body(r);
                    return StandingOrders.Set(RequireCombat(), body["orders"] as JArray, body.Value<bool?>("replace") ?? true);
                } },
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

        private static string Side(BridgeRequest r)
        {
            string side = r.QueryOr("side", null);
            if (side == null)
            {
                // Default to the side the agent commands (the open decision's, else enemy if only it is agent-driven).
                side = DecisionBroker.Current?.Side
                    ?? (CombatControl.ActiveEnemy == EnemyControl.Agent && CombatControl.ActivePlayer != PlayerControl.Agent ? "enemy" : "player");
            }
            if (side != "player" && side != "enemy")
            {
                throw new BridgeException(400, "side must be player or enemy");
            }
            return side;
        }

        private static Team Viewer(BridgeRequest r) =>
            CombatControl.ViewerFor(RequireCombat(), Side(r)) ?? throw new BridgeException(409, "no team for that side");

        private static object ControlView() => new
        {
            next_mission = new { player = CombatControl.RequestedPlayer.ToString(), enemy = CombatControl.RequestedEnemy.ToString() },
            current_mission = Game?.Combat != null
                ? new { player = CombatControl.ActivePlayer.ToString(), enemy = CombatControl.ActiveEnemy.ToString() }
                : null,
            decision_timeout_seconds = CombatControl.DecisionTimeoutSeconds,
            player_modes = new[] { "Human", "Agent", "BuiltinAI" },
            enemy_modes = new[] { "StockAI", "Agent" },
            note = "modes are applied when a mission builds its teams, so changes take effect from the next mission",
        };

        private static T ParseEnum<T>(string value, string what)
        {
            try
            {
                return (T)System.Enum.Parse(typeof(T), value, ignoreCase: true);
            }
            catch (System.ArgumentException)
            {
                throw new BridgeException(400, $"unknown {what} mode '{value}' ({string.Join(" | ", System.Enum.GetNames(typeof(T)))})");
            }
        }

        private static object SetControl(BridgeRequest r)
        {
            var body = Body(r);
            if (body.Value<string>("player") is string player)
            {
                CombatControl.RequestedPlayer = ParseEnum<PlayerControl>(player, "player");
            }
            if (body.Value<string>("enemy") is string enemy)
            {
                CombatControl.RequestedEnemy = ParseEnum<EnemyControl>(enemy, "enemy");
            }
            float? timeout = body.Value<float?>("decision_timeout_seconds");
            if (timeout.HasValue)
            {
                CombatControl.DecisionTimeoutSeconds = System.Math.Max(0f, timeout.Value);
            }
            Log.Info($"combat control: next mission player {CombatControl.RequestedPlayer}, enemy {CombatControl.RequestedEnemy}, timeout {CombatControl.DecisionTimeoutSeconds}s");
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
                    agent_commands = new { player = CombatControl.ActivePlayer == PlayerControl.Agent, enemy = CombatControl.ActiveEnemy == EnemyControl.Agent },
                    operator_messages = Ui.ChatOverlay.UnreadForAgent(),
                    dialog_waiting = MissionDialog.Waiting,
                    briefing_waiting = UnityEngine.Object.FindObjectOfType<BattleTech.UI.Briefing>()?.Visible ?? false,
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
