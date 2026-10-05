using System;
using BattleTech;
using BattleTech.UI;
using BTBridge.Combat;
using Harmony;
using UnityEngine;

namespace BTBridge.Patches
{
    /// <summary>Builds the player's team as an AITeam when the agent or the stock AI should drive it.</summary>
    [HarmonyPatch(typeof(EncounterLayerData), "CreatePlayerOneTeam")]
    public static class CreatePlayerTeam
    {
        public static bool Prefix(EncounterLayerData __instance, ref Team __result)
        {
            var mode = CombatControl.Requested;
            CombatControl.OnPlayerTeamCreated(mode);
            DecisionBroker.Reset();
            if (mode == PlayerControl.Human)
            {
                return true;
            }
            // Same construction as the game's debug path (DebugBridge.PlayerOneIsAIControlled),
            // without turning on TestToolsEnabled and everything else it affects.
            Color color = UnitSpawnPointGameLogic.GetPlayerHostilityColor(CombatControl.Player1Guid);
            var team = new AITeam("Player 1", color, CombatControl.Player1Guid, true, __instance.Combat, substitutingforHuman: true, isMultiplayer: false);
            team.FactionValue = FactionEnumeration.GetPlayer1sMercUnitFactionValue();
            __result = team;
            Log.Info($"player team created as AITeam (mode {mode})");
            return false;
        }
    }

    [HarmonyPatch(typeof(AITeam), "TurnActorProcessActivation")]
    public static class EnsureTreeOnActivation
    {
        public static void Prefix(AITeam __instance)
        {
            if (CombatControl.ActiveMode != PlayerControl.Human && __instance.GUID == CombatControl.Player1Guid)
            {
                DecisionBroker.EnsureCoreTree(__instance);
            }
        }
    }

    /// <summary>Records which order the stock AI turned into an invocation, to describe its suggestion.</summary>
    [HarmonyPatch(typeof(AITeam), "makeInvocationFromOrders")]
    public static class RecordAiOrder
    {
        public static void Prefix(OrderInfo order) => DecisionBroker.LastAiOrder = order;
    }

    /// <summary>
    /// The decision point. For the agent's team: let the stock AI think until it has an
    /// invocation, hold it as a suggestion, and return null ("still thinking") until the agent
    /// answers. AITeam.think() polls this every frame, so waiting is the designed path.
    /// </summary>
    [HarmonyPatch(typeof(AITeam), "getInvocationForCurrentUnit")]
    public static class AgentDecisionPoint
    {
        private static bool producedByBroker;
        private static string stageKey;

        public static bool Prefix(AITeam __instance, ref InvocationMessage __result)
        {
            producedByBroker = false;
            if (!CombatControl.IsAgentTeam(__instance))
            {
                return true;
            }
            var unit = DecisionBroker.CurrentUnit(__instance);
            if (unit == null)
            {
                return true;
            }
            try
            {
                var d = DecisionBroker.Current;
                if (d != null && !DecisionBroker.Matches(d, unit))
                {
                    DecisionBroker.Close(d, "abandoned (unit or stage changed)");
                    d = null;
                }
                if (d != null)
                {
                    producedByBroker = true;
                    float waited = Time.realtimeSinceStartup - d.OpenedRealtime;
                    if (d.Ready != null)
                    {
                        __result = d.Ready;
                        DecisionBroker.Close(d, d.Chosen ?? "accept");
                    }
                    else if (CombatControl.DecisionTimeoutSeconds > 0 && waited > CombatControl.DecisionTimeoutSeconds)
                    {
                        __result = d.Suggestion;
                        DecisionBroker.Close(d, "timeout: took the stock AI's suggestion");
                    }
                    else
                    {
                        __result = null;
                    }
                    // The stock think budget runs from activation start and would brace the unit
                    // after Float_MaxThinkSeconds; keep it fresh while the agent deliberates.
                    DecisionBroker.RestartThinkClock(__instance);
                    return false;
                }
                string key = $"{unit.GUID}|{unit.Combat.TurnDirector.CurrentRound}|{DecisionBroker.StageFor(unit)}";
                if (key != stageKey)
                {
                    stageKey = key;
                    DecisionBroker.RestartThinkClock(__instance);
                }
            }
            catch (Exception e)
            {
                Log.Error("agent decision prefix failed; letting the stock AI act", e);
                producedByBroker = false;
            }
            return true;
        }

        public static void Postfix(AITeam __instance, ref InvocationMessage __result)
        {
            if (producedByBroker || __result == null || !CombatControl.IsAgentTeam(__instance))
            {
                return;
            }
            // Team-wide reserve (defer the whole lance to a later phase) stays the stock AI's call.
            if (__result is ReserveActorInvocation r && r.targetGUID == __instance.GUID)
            {
                return;
            }
            var unit = DecisionBroker.CurrentUnit(__instance);
            if (unit == null)
            {
                return;
            }
            try
            {
                DecisionBroker.Open(__instance, unit, __result);
                __result = null;
            }
            catch (Exception e)
            {
                Log.Error("could not open an agent decision; letting the stock AI's order through", e);
            }
        }
    }

    /// <summary>While the agent drives the player's lance, the HUD must not let a human order those units too.</summary>
    [HarmonyPatch(typeof(CombatSelectionHandler), "TrySelectActor")]
    public static class BlockManualSelect
    {
        public static bool Prefix(AbstractActor actor, ref bool __result)
        {
            if (actor != null && CombatControl.ActiveMode != PlayerControl.Human && actor.team != null && actor.team.GUID == CombatControl.Player1Guid)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CombatSelectionHandler), "AutoSelectActor")]
    public static class BlockAutoSelect
    {
        public static bool Prefix() => CombatControl.ActiveMode == PlayerControl.Human;
    }
}
