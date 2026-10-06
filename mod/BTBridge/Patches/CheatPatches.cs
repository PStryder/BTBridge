using System;
using BattleTech;
using BattleTech.Save.SaveGameStructure;
using BTBridge.Cheats;
using Harmony;

namespace BTBridge.Patches
{
    // OPERATOR CHEAT LAYER lifecycle hooks: load epochs and save tracking. Inert unless the
    // cheat capability is on.

    [HarmonyPatch(typeof(SimGameState), "Rehydrate")]
    public static class CheatOnLoad
    {
        public static void Postfix()
        {
            if (CheatConfig.Capability)
            {
                CheatService.OnRehydrate();
            }
        }
    }

    [HarmonyPatch(typeof(SimGameState), "TriggerSaveNow")]
    public static class CheatOnSaveRequested
    {
        public static void Prefix(SaveReason reason)
        {
            if (CheatConfig.Capability)
            {
                CheatService.OnSaveRequested(reason);
            }
        }
    }

    /// <summary>Dehydrate writes the campaign into a save, whatever path asked for it.</summary>
    [HarmonyPatch(typeof(SimGameState), "Dehydrate")]
    public static class CheatOnSaved
    {
        public static void Postfix()
        {
            if (!CheatConfig.Capability)
            {
                return;
            }
            try
            {
                CheatService.OnSaved();
            }
            catch (Exception e)
            {
                Log.Error("[CHEAT] save tracking failed", e);
            }
        }
    }
}
