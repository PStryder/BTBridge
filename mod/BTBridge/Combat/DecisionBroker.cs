using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BattleTech;
using BTBridge.Bridge;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BTBridge.Combat
{
    /// <summary>
    /// One pending choice for one unit. The stock AI has already thought about it: its chosen
    /// invocation is held as the suggestion and its influence-map ranking supplies candidates.
    /// </summary>
    public sealed class Decision
    {
        public string Id;
        public string UnitGuid;
        public int Round;
        public int Phase;
        public string Stage;
        public InvocationMessage Suggestion;
        public object SuggestionInfo;
        public List<Candidate> Candidates = new List<Candidate>();
        public float OpenedRealtime;
        public InvocationMessage Ready;
        public string Chosen;
        public string Side;
        /// <summary>Why this unit's standing order could not be executed, if it had one.</summary>
        public string StandingOrderError;
    }

    public sealed class Candidate
    {
        public int Index;
        public Vector3 Position;
        public float Angle;
        public MoveType MoveType;
        public float Score;
        public Dictionary<string, float> Factors;
    }

    /// <summary>
    /// Mediates between AITeam's think loop (main thread, every frame) and the agent (HTTP).
    /// Orders are turned into invocations through AITeam.makeInvocationFromOrders, i.e. the
    /// stock AI's own order-to-invocation code, after validation it does not do itself.
    /// </summary>
    public static class DecisionBroker
    {
        private static readonly FieldInfo CurrentUnitField = typeof(AITeam).GetField("currentUnit", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PlanningStartField = typeof(AITeam).GetField("planningStartTime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TreeIdField = typeof(BehaviorTree).GetField("behaviorTreeIDEnum", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo MakeInvocation = typeof(AITeam).GetMethod("makeInvocationFromOrders", BindingFlags.Instance | BindingFlags.NonPublic);

        private const int MaxCandidates = 8;

        public static Decision Current { get; private set; }

        /// <summary>The order the stock AI turned into its last invocation (recorded by a patch).</summary>
        public static OrderInfo LastAiOrder;

        private static int nextId;
        private static readonly List<object> History = new List<object>();

        public static AbstractActor CurrentUnit(AITeam team) => CurrentUnitField.GetValue(team) as AbstractActor;

        /// <summary>The stock think budget (Float_MaxThinkSeconds) runs from activation start; restart it.</summary>
        public static void RestartThinkClock(AITeam team) => PlanningStartField.SetValue(team, team.Combat.BattleTechGame.Time);

        /// <summary>Player units spawn with DoNothingTree; the stock AI (our suggestion source) needs the real one.</summary>
        public static void EnsureCoreTree(AITeam team)
        {
            foreach (var unit in team.units)
            {
                if (unit.BehaviorTree == null || (BehaviorTreeIDEnum)TreeIdField.GetValue(unit.BehaviorTree) != BehaviorTreeIDEnum.CoreAITree)
                {
                    team.SetBehaviorTree(BehaviorTreeIDEnum.CoreAITree);
                    Log.Info("agent team: assigned CoreAITree to player units");
                    return;
                }
            }
        }

        public static string StageFor(AbstractActor unit) => unit.HasMovedThisRound ? "attack" : "move";

        public static bool Matches(Decision d, AbstractActor unit) =>
            d != null && d.UnitGuid == unit.GUID && d.Round == unit.Combat.TurnDirector.CurrentRound && d.Stage == StageFor(unit);

        public static void Open(AITeam team, AbstractActor unit, InvocationMessage suggestion)
        {
            var d = new Decision
            {
                Id = "d" + (++nextId),
                UnitGuid = unit.GUID,
                Round = unit.Combat.TurnDirector.CurrentRound,
                Phase = unit.Combat.TurnDirector.CurrentPhase,
                Stage = StageFor(unit),
                Suggestion = suggestion,
                SuggestionInfo = Describe(unit.Combat, suggestion, LastAiOrder),
                OpenedRealtime = Time.realtimeSinceStartup,
            };
            d.Side = CombatControl.SideOf(team);
            if (d.Stage == "move")
            {
                d.Candidates = ReadCandidates(unit);
            }
            Current = d;
            Log.Info($"decision {d.Id} opened: {d.Side} {unit.DisplayName} round {d.Round} phase {d.Phase} stage {d.Stage}");
            StandingOrders.TryExecute(team, unit, d);
        }

        public static void Close(Decision d, string how)
        {
            History.Add(new { id = d.Id, unit = d.UnitGuid, round = d.Round, stage = d.Stage, chosen = how, waited_seconds = Math.Round(Time.realtimeSinceStartup - d.OpenedRealtime, 1) });
            if (History.Count > 50)
            {
                History.RemoveAt(0);
            }
            if (Current == d)
            {
                Current = null;
            }
            Log.Info($"decision {d.Id} closed: {how}");
            Ui.ChatOverlay.OnDecisionClosed(d.Id);
        }

        public static void Reset()
        {
            Current = null;
        }

        public static object RecentHistory() => History.ToList();

        // -- describing what the stock AI wants --------------------------------------

        private static object Describe(CombatGameState combat, InvocationMessage inv, OrderInfo order)
        {
            if (inv is ReserveActorInvocation)
            {
                return new { action = "brace" };
            }
            switch (order)
            {
                case MovementOrderInfo mo:
                    return new
                    {
                        action = "move",
                        move = mo.IsJumping ? "jump" : mo.IsSprinting ? "sprint" : mo.IsReverse ? "backward" : "walk",
                        position = CombatSerializer.Position(combat, mo.Destination),
                        facing = CombatSerializer.Round(Quaternion.LookRotation(mo.LookAt - mo.Destination).eulerAngles.y),
                    };
                case AttackOrderInfo ao:
                    return new
                    {
                        action = ao.IsMelee ? "melee" : ao.IsDeathFromAbove ? "dfa" : "attack",
                        target = (ao.TargetUnit as AbstractActor)?.GUID,
                        target_name = (ao.TargetUnit as AbstractActor)?.DisplayName,
                        weapons = ao.Weapons?.Select(w => w.uid).ToList(),
                    };
                case MultiTargetAttackOrderInfo mt:
                    return new
                    {
                        action = "multi_attack",
                        targets = mt.SubTargetOrders.Select(s => new { target = (s.TargetUnit as AbstractActor)?.GUID, weapons = s.Weapons.Select(w => w.uid).ToList() }).ToList(),
                    };
                default:
                    return new { action = order?.OrderType.ToString() ?? inv?.GetType().Name ?? "unknown" };
            }
        }

        /// <summary>
        /// The influence map's ranking from this activation's evaluation, re-checked against the
        /// unit's current path grids (entries are reused across evaluations and may be stale).
        /// </summary>
        private static List<Candidate> ReadCandidates(AbstractActor unit)
        {
            var result = new List<Candidate>();
            var ev = unit.BehaviorTree?.influenceMapEvaluator;
            if (ev == null)
            {
                return result;
            }
            int count = Math.Min(ev.firstFreeWorkspaceEvaluationEntryIndex, ev.WorkspaceEvaluationEntries.Count);
            var ranked = ev.WorkspaceEvaluationEntries.Take(count)
                .Where(e => e.Target == null)
                .OrderByDescending(e => e.GetHighestAccumulator())
                .ToList();
            foreach (var e in ranked)
            {
                var moveType = e.GetBestMoveType();
                if (!IsReachable(unit, e.Position, moveType))
                {
                    continue;
                }
                result.Add(new Candidate
                {
                    Index = result.Count,
                    Position = e.Position,
                    Angle = e.Angle,
                    MoveType = moveType,
                    Score = e.GetHighestAccumulator(),
                    Factors = (e.ValuesByFactorName ?? new Dictionary<string, EvaluationDebugLogRecord>())
                        .Select(kv => new KeyValuePair<string, float>(kv.Key, kv.Value.RegularValue * kv.Value.RegularWeight))
                        .Where(kv => Math.Abs(kv.Value) > 1e-4f)
                        .OrderByDescending(kv => Math.Abs(kv.Value))
                        .Take(4)
                        .ToDictionary(kv => kv.Key, kv => (float)Math.Round(kv.Value, 3)),
                });
                if (result.Count >= MaxCandidates)
                {
                    break;
                }
            }
            return result;
        }

        private static bool IsReachable(AbstractActor unit, Vector3 pos, MoveType type)
        {
            try
            {
                if (type == MoveType.Jumping)
                {
                    return unit is Mech m && m.JumpPathing.IsValidLandingSpot(pos, unit.Combat.AllActors);
                }
                return unit.Pathing.getGrid(type).GetValidPathNodeAt(pos, Budget(unit, type)) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>How far a requested point may be from the node actually used.</summary>
        public const float SnapTolerance = 25f;

        /// <summary>
        /// Path grids are sparse lattices: an exact-cell lookup (GetValidPathNodeAt) misses almost
        /// any arbitrary point. Like the movement UI, snap to the nearest reachable node instead.
        /// </summary>
        private static PathNode Snap(AbstractActor unit, MoveType type, Vector3 wanted)
        {
            if (!unit.Pathing.ArePathGridsComplete)
            {
                throw new BridgeException(409, "the unit's path grids are still being computed; retry in a moment");
            }
            var exact = unit.Pathing.getGrid(type).GetValidPathNodeAt(wanted, Budget(unit, type));
            if (exact != null)
            {
                return exact;
            }
            var nearest = Reachable(unit, type).OrderBy(n => FlatDistance(n.Position, wanted)).FirstOrDefault()
                ?? throw new BridgeException(400, $"no position is reachable by {MoveName(type)} this turn");
            float off = FlatDistance(nearest.Position, wanted);
            if (off > SnapTolerance)
            {
                var p = nearest.Position;
                throw new BridgeException(400, $"not reachable by {MoveName(type)}; the nearest reachable point is x={CombatSerializer.Round(p.x)} z={CombatSerializer.Round(p.z)} ({CombatSerializer.Round(off)} m away). Use /combat/reachable to explore.");
            }
            return nearest;
        }

        public static IEnumerable<PathNode> Reachable(AbstractActor unit, MoveType type)
        {
            float budget = Budget(unit, type);
            return unit.Pathing.getGrid(type).GetSampledPathNodes()
                .Where(n => n != null && n.IsValidDestination && n.CostToThisNode > -0.01f && n.CostToThisNode < budget);
        }

        private static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        /// <summary>Reachable destinations for the open decision's unit, nearest first to a point of interest.</summary>
        public static object ReachableView(CombatGameState combat, string move, float? nearX, float? nearZ, int limit)
        {
            var d = Current ?? throw new BridgeException(409, "no decision is open");
            var unit = combat.FindActorByGUID(d.UnitGuid) ?? throw new BridgeException(409, "unit no longer exists");
            var type = ParseMove(move ?? "walk");
            IEnumerable<Vector3> points;
            if (type == MoveType.Jumping)
            {
                var mech = unit as Mech ?? throw new BridgeException(400, "only mechs can jump");
                points = mech.JumpPathing.GetSampledPathNodes().Where(n => n != null).Select(n => n.Position);
            }
            else
            {
                if (!unit.Pathing.ArePathGridsComplete)
                {
                    throw new BridgeException(409, "the unit's path grids are still being computed; retry in a moment");
                }
                points = Reachable(unit, type).Select(n => n.Position);
            }
            var focus = new Vector3(nearX ?? unit.CurrentPosition.x, 0f, nearZ ?? unit.CurrentPosition.z);
            var list = points.OrderBy(p => FlatDistance(p, focus)).Take(Math.Max(1, Math.Min(limit, 100))).ToList();
            var enemies = combat.GetAllEnemiesOf(unit).Where(e => !e.IsDead && unit.VisibilityToTargetUnit(e) != VisibilityLevel.None).ToList();
            return new
            {
                unit = unit.DisplayName,
                move = MoveName(type),
                total = points.Count(),
                points = list.Select(p => new
                {
                    position = CombatSerializer.Position(combat, p),
                    distance_from_focus = CombatSerializer.Round(FlatDistance(p, focus)),
                    nearest_enemy = enemies.Count == 0 ? null : enemies
                        .Select(e => new { guid = e.GUID, name = e.DisplayName, distance = CombatSerializer.Round(FlatDistance(p, e.CurrentPosition)) })
                        .OrderBy(e => e.distance).First(),
                }).ToList(),
            };
        }

        private static float Budget(AbstractActor unit, MoveType type)
        {
            switch (type)
            {
                case MoveType.Sprinting: return unit.MaxSprintDistance;
                case MoveType.Backward: return unit.MaxBackwardDistance;
                default: return unit.MaxWalkDistance;
            }
        }

        public static object View(Decision d, CombatGameState combat)
        {
            var unit = combat.FindActorByGUID(d.UnitGuid);
            return new
            {
                open = true,
                id = d.Id,
                side = d.Side,
                standing_order_error = d.StandingOrderError,
                round = d.Round,
                phase = d.Phase,
                stage = d.Stage,
                waiting_seconds = Math.Round(Time.realtimeSinceStartup - d.OpenedRealtime, 1),
                timeout_seconds = CombatControl.DecisionTimeoutSeconds,
                unit = unit == null ? null : CombatSerializer.Actor(combat, unit, "own", VisibilityLevel.LOSFull),
                suggestion = d.SuggestionInfo,
                candidates = d.Candidates.Select(c => new
                {
                    index = c.Index,
                    move = MoveName(c.MoveType),
                    position = CombatSerializer.Position(combat, c.Position),
                    facing = CombatSerializer.Round(c.Angle),
                    score = (float)Math.Round(c.Score, 3),
                    top_factors = c.Factors,
                }).ToList(),
                targets = unit == null ? null : CombatSerializer.Targets(combat, unit),
                allowed = d.Stage == "move"
                    ? new[] { "accept", "move", "attack", "brace" }
                    : new[] { "accept", "attack", "brace" },
            };
        }

        private static string MoveName(MoveType t)
        {
            switch (t)
            {
                case MoveType.Sprinting: return "sprint";
                case MoveType.Backward: return "backward";
                case MoveType.Jumping: return "jump";
                default: return "walk";
            }
        }

        // -- turning the agent's answer into an invocation -----------------------------

        /// <summary>Validate the agent's order and prepare the invocation. Throws BridgeException(400) if invalid.</summary>
        public static object Answer(CombatGameState combat, string id, string unitGuid, JObject order)
        {
            var d = Current;
            if (d == null || d.Id != id)
            {
                throw new BridgeException(409, d == null ? "no decision is open" : $"decision {id} is not current (current is {d.Id})");
            }
            // Weapon uids repeat across units ("0", "3"...), so an order meant for one unit can be
            // valid for another. The caller must say which unit it is ordering.
            if (string.IsNullOrEmpty(unitGuid))
            {
                throw new BridgeException(400, "unit (the guid of the unit you are ordering) is required");
            }
            if (unitGuid != d.UnitGuid)
            {
                var actual = combat.FindActorByGUID(d.UnitGuid);
                throw new BridgeException(409, $"decision {id} is for {actual?.DisplayName} ({d.UnitGuid}), not {unitGuid}; re-read the decision");
            }
            if (d.Ready != null)
            {
                throw new BridgeException(409, $"decision {id} was already answered");
            }
            var unit = combat.FindActorByGUID(d.UnitGuid) ?? throw new BridgeException(409, "unit no longer exists");
            var team = unit.team as AITeam ?? throw new BridgeException(409, "unit is not on an AI-driven team");
            d.Ready = Build(team, unit, d, order);
            d.Chosen = order.ToString(Newtonsoft.Json.Formatting.None);
            return new { accepted = true, id, action = order.Value<string>("action") };
        }

        /// <summary>Validate one order for the unit of decision d and build its invocation.</summary>
        public static InvocationMessage Build(AITeam team, AbstractActor unit, Decision d, JObject order)
        {
            var combat = unit.Combat;
            string action = order?.Value<string>("action") ?? throw new BridgeException(400, "order.action is required");
            InvocationMessage inv;
            switch (action)
            {
                case "accept":
                    inv = d.Suggestion;
                    break;
                case "brace":
                    inv = new ReserveActorInvocation(unit, ReserveActorAction.DONE, combat.TurnDirector.CurrentRound);
                    break;
                case "move":
                    inv = BuildMove(team, unit, d, order);
                    break;
                case "attack":
                    inv = BuildAttack(team, unit, order);
                    break;
                default:
                    throw new BridgeException(400, $"unknown action '{action}' (accept | move | attack | brace)");
            }
            return inv ?? throw new BridgeException(500, "the game produced no invocation for that order");
        }

        private static InvocationMessage BuildMove(AITeam team, AbstractActor unit, Decision d, JObject order)
        {
            if (unit.HasMovedThisRound)
            {
                throw new BridgeException(400, "this unit has already moved this round");
            }
            Vector3 dest;
            MoveType moveType;
            float? facing = order.Value<float?>("facing");
            int? index = order.Value<int?>("candidate");
            if (index.HasValue)
            {
                var c = d.Candidates.FirstOrDefault(x => x.Index == index.Value)
                    ?? throw new BridgeException(400, $"no candidate {index.Value}");
                dest = c.Position;
                moveType = c.MoveType;
                facing = facing ?? c.Angle;
            }
            else
            {
                var pos = order["position"] as JObject ?? throw new BridgeException(400, "move needs candidate or position {x, z}");
                dest = new Vector3(pos.Value<float>("x"), 0f, pos.Value<float>("z"));
                dest.y = unit.Combat.MapMetaData.GetLerpedHeightAt(dest);
                moveType = ParseMove(order.Value<string>("move") ?? "walk");
            }
            if (order.Value<string>("move") != null && index.HasValue)
            {
                moveType = ParseMove(order.Value<string>("move"));
            }

            if (moveType == MoveType.Jumping)
            {
                var mech = unit as Mech ?? throw new BridgeException(400, "only mechs can jump");
                if (mech.WorkingJumpjets < 1)
                {
                    throw new BridgeException(400, "no working jump jets");
                }
                dest = unit.Combat.HexGrid.GetClosestPointOnGrid(dest);
                dest.y = unit.Combat.MapMetaData.GetLerpedHeightAt(dest);
                if (!mech.JumpPathing.IsValidLandingSpot(dest, unit.Combat.AllActors))
                {
                    throw new BridgeException(400, "not a valid jump landing spot (range, height, terrain or occupied)");
                }
            }
            else
            {
                if (moveType == MoveType.Sprinting && !unit.CanSprint)
                {
                    throw new BridgeException(400, "this unit cannot sprint now");
                }
                var node = Snap(unit, moveType, dest);
                dest = node.Position;
            }

            Vector3 lookAt = LookAt(unit, dest, facing, order.Value<string>("face_unit"));
            unit.Pathing.ClearMeleeTarget();
            var info = new MovementOrderInfo(dest, lookAt)
            {
                IsSprinting = moveType == MoveType.Sprinting,
                IsJumping = moveType == MoveType.Jumping,
                IsReverse = moveType == MoveType.Backward,
            };
            var inv = (InvocationMessage)MakeInvocation.Invoke(team, new object[] { unit, info });
            if (moveType != MoveType.Jumping && Vector3.Distance(unit.Pathing.ResultDestination, dest) > 1f)
            {
                throw new BridgeException(400, "the game could not path to that position");
            }
            return inv;
        }

        private static Vector3 LookAt(AbstractActor unit, Vector3 dest, float? facing, string faceUnit)
        {
            if (!string.IsNullOrEmpty(faceUnit))
            {
                var target = unit.Combat.FindActorByGUID(faceUnit) ?? throw new BridgeException(400, $"no unit '{faceUnit}' to face");
                return target.CurrentPosition;
            }
            if (facing.HasValue)
            {
                return dest + Quaternion.Euler(0f, facing.Value, 0f) * Vector3.forward * 100f;
            }
            var nearest = unit.Combat.GetAllEnemiesOf(unit).Where(e => !e.IsDead)
                .OrderBy(e => Vector3.Distance(dest, e.CurrentPosition)).FirstOrDefault();
            return nearest != null ? nearest.CurrentPosition : dest + unit.CurrentRotation * Vector3.forward * 100f;
        }

        private static MoveType ParseMove(string name)
        {
            switch (name)
            {
                case "walk": return MoveType.Walking;
                case "sprint": return MoveType.Sprinting;
                case "backward": return MoveType.Backward;
                case "jump": return MoveType.Jumping;
                default: throw new BridgeException(400, $"unknown move '{name}' (walk | sprint | backward | jump)");
            }
        }

        private static InvocationMessage BuildAttack(AITeam team, AbstractActor unit, JObject order)
        {
            if (unit.HasFiredThisRound)
            {
                throw new BridgeException(400, "this unit has already fired this round");
            }
            if (!unit.Combat.TurnDirector.IsInterleaved)
            {
                throw new BridgeException(400, "no attacks outside of combat (not interleaved)");
            }
            string guid = order.Value<string>("target") ?? throw new BridgeException(400, "attack needs target (unit guid)");
            var target = unit.Combat.FindActorByGUID(guid);
            if (target == null || target.IsDead || !unit.team.IsEnemy(target.team))
            {
                throw new BridgeException(400, $"'{guid}' is not a living enemy");
            }
            var wanted = (order["weapons"] as JArray)?.Select(t => t.ToString()).ToList();
            var weapons = unit.Weapons
                .Where(w => wanted == null || wanted.Contains(w.uid))
                .Where(w => w.WillFireAtTarget(target))
                .ToList();
            if (wanted != null)
            {
                var cannot = wanted.Where(u => !weapons.Any(w => w.uid == u)).ToList();
                if (cannot.Count > 0)
                {
                    throw new BridgeException(400, "these weapons cannot fire at that target now: " + string.Join(", ", cannot.ToArray()));
                }
            }
            if (weapons.Count == 0)
            {
                throw new BridgeException(400, "no weapon can fire at that target from here");
            }
            var info = new AttackOrderInfo(target);
            foreach (var w in weapons)
            {
                info.AddWeapon(w);
            }
            return (InvocationMessage)MakeInvocation.Invoke(team, new object[] { unit, info });
        }
    }
}
