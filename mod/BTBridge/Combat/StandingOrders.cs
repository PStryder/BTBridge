using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using Newtonsoft.Json.Linq;

namespace BTBridge.Combat
{
    /// <summary>
    /// A round plan: per unit, an optional move and an optional attack, plus activation sequence.
    /// When a planned unit's decision opens, its order is validated against the live situation
    /// and executed at once; if it no longer fits, the decision waits for the agent (or falls
    /// back as the order says). This is "review the board once, then click quickly".
    /// </summary>
    public static class StandingOrders
    {
        private sealed class Order
        {
            public string Unit;
            public int? Sequence;
            public JObject Move;
            public JObject Attack;
            public string OnInvalid = "wait";
            public bool MoveUsed;
            public bool AttackUsed;
        }

        private static readonly Dictionary<string, Order> Orders = new Dictionary<string, Order>();
        private static int ordersRound = -1;

        private static void DropIfStale(CombatGameState combat)
        {
            if (combat == null || combat.TurnDirector.CurrentRound != ordersRound)
            {
                Orders.Clear();
                ordersRound = combat?.TurnDirector.CurrentRound ?? -1;
            }
        }

        public static object Set(CombatGameState combat, JArray list, bool replace)
        {
            DropIfStale(combat);
            if (replace)
            {
                Orders.Clear();
            }
            var accepted = new List<object>();
            foreach (var token in list ?? new JArray())
            {
                var o = token as JObject ?? throw new BridgeException(400, "each standing order must be an object");
                string guid = o.Value<string>("unit") ?? throw new BridgeException(400, "standing order needs unit (guid)");
                var unit = combat.FindActorByGUID(guid) ?? throw new BridgeException(400, $"no unit '{guid}'");
                if (!CombatControl.IsAgentTeam(unit.team))
                {
                    throw new BridgeException(400, $"{unit.DisplayName} is not on an agent-controlled team");
                }
                var move = o["move"] as JObject;
                var attack = o["attack"] as JObject;
                if (move != null)
                {
                    move["action"] = "move";
                }
                if (attack != null)
                {
                    attack["action"] = "attack";
                }
                string onInvalid = o.Value<string>("on_invalid") ?? "wait";
                if (onInvalid != "wait" && onInvalid != "suggestion" && onInvalid != "brace")
                {
                    throw new BridgeException(400, "on_invalid must be wait | suggestion | brace");
                }
                Orders[guid] = new Order
                {
                    Unit = guid,
                    Sequence = o.Value<int?>("sequence"),
                    Move = move,
                    Attack = attack,
                    OnInvalid = onInvalid,
                };
                accepted.Add(new { unit = guid, name = unit.DisplayName, move = move != null, attack = attack != null, sequence = o.Value<int?>("sequence") });
            }
            Log.Info($"standing orders set for round {ordersRound}: {Orders.Count} unit(s)");
            return new { round = ordersRound, accepted, total = Orders.Count };
        }

        public static object View(CombatGameState combat)
        {
            DropIfStale(combat);
            return new
            {
                round = ordersRound,
                orders = Orders.Values.Select(o => new
                {
                    unit = o.Unit,
                    name = combat.FindActorByGUID(o.Unit)?.DisplayName,
                    sequence = o.Sequence,
                    move = o.Move,
                    attack = o.Attack,
                    on_invalid = o.OnInvalid,
                    move_done = o.MoveUsed,
                    attack_done = o.AttackUsed,
                }).ToList(),
            };
        }

        public static void Clear() => Orders.Clear();

        /// <summary>Called when a decision opens; sets d.Ready when the unit's plan still holds.</summary>
        public static void TryExecute(AITeam team, AbstractActor unit, Decision d)
        {
            DropIfStale(unit.Combat);
            if (!Orders.TryGetValue(unit.GUID, out var o))
            {
                return;
            }
            JObject step = null;
            bool isMove = false;
            if (d.Stage == "move" && o.Move != null && !o.MoveUsed)
            {
                step = o.Move;
                isMove = true;
            }
            else if (o.Attack != null && !o.AttackUsed && !unit.HasFiredThisRound)
            {
                step = o.Attack;
            }
            if (step == null)
            {
                // Plan exhausted for this activation: end it the way a human clicks "Done".
                step = new JObject { ["action"] = "brace" };
            }
            try
            {
                d.Ready = DecisionBroker.Build(team, unit, d, step);
                d.Chosen = "standing order: " + step.ToString(Newtonsoft.Json.Formatting.None);
                if (isMove)
                {
                    o.MoveUsed = true;
                }
                else if (step.Value<string>("action") == "attack")
                {
                    o.AttackUsed = true;
                }
            }
            catch (Exception e)
            {
                string reason = e is BridgeException ? e.Message : e.GetType().Name + ": " + e.Message;
                d.StandingOrderError = $"{step.Value<string>("action")} failed: {reason}";
                Log.Info($"standing order for {unit.DisplayName} not executable ({reason}); on_invalid={o.OnInvalid}");
                switch (o.OnInvalid)
                {
                    case "suggestion":
                        d.Ready = d.Suggestion;
                        d.Chosen = "standing order invalid -> stock AI suggestion";
                        break;
                    case "brace":
                        d.Ready = new ReserveActorInvocation(unit, ReserveActorAction.DONE, unit.Combat.TurnDirector.CurrentRound);
                        d.Chosen = "standing order invalid -> brace";
                        break;
                }
                // Either way the plan step is spent: don't retry a failed step on the next stage.
                if (isMove)
                {
                    o.MoveUsed = true;
                }
                else
                {
                    o.AttackUsed = true;
                }
            }
        }

        /// <summary>The planned next unit among those that may activate now, or null.</summary>
        public static AbstractActor NextInSequence(CombatGameState combat, List<AbstractActor> available)
        {
            DropIfStale(combat);
            return available
                .Where(a => Orders.TryGetValue(a.GUID, out var o) && o.Sequence.HasValue)
                .OrderBy(a => Orders[a.GUID].Sequence.Value)
                .FirstOrDefault();
        }
    }
}
