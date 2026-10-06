using System;
using System.Collections.Generic;
using BattleTech;
using BattleTech.UI;
using BTBridge.Sim;
using Harmony;

namespace BTBridge.Patches
{
    /// <summary>Remembers the event on screen; its tracker and entry are private to the panel.</summary>
    [HarmonyPatch(typeof(SGEventPanel), "SetEvent")]
    public static class CaptureEvent
    {
        public static void Postfix(SGEventPanel __instance, SimGameEventDef evt, SimGameInterruptManager.EventPopupEntry entry)
        {
            Interrupts.EventPanel = __instance;
            Interrupts.EventDef = evt;
            Interrupts.EventEntryUid = entry != null ? entry.uid : -1;
            Interrupts.LastEventResult = null;
        }
    }

    /// <summary>The result set an event option rolled, so the outcome can be reported as text.</summary>
    [HarmonyPatch(typeof(SimGameState), "OnEventOptionSelected")]
    public static class CaptureEventResult
    {
        public static void Postfix(SimGameEventResultSet __result) => Interrupts.LastEventResult = __result;
    }

    [HarmonyPatch(typeof(SimGameState), "OnDayPassed")]
    public static class CountDays
    {
        public static void Postfix(SimGameState __instance)
        {
            try
            {
                TimeControl.OnDayPassed(__instance);
            }
            catch (Exception e)
            {
                Log.Error("day counter failed", e);
            }
        }
    }

    /// <summary>
    /// The launch path reads the lance from the configurator twice (OnLanceConfiguratorAccept and
    /// FillContractLance); while an agent launch is pending, both get the agent's lance.
    /// </summary>
    [HarmonyPatch(typeof(LanceConfiguratorPanel), "CreateLanceConfiguration")]
    public static class SubstituteLance
    {
        public static bool Prefix(ref LanceConfiguration __result)
        {
            if (Contracts.PendingLance == null)
            {
                return true;
            }
            __result = Contracts.PendingLance;
            return false;
        }
    }
}
