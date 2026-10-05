using System;
using BattleTech;
using BTBridge.Bridge;
using Harmony;

namespace BTBridge.Patches
{
    // UnityGameInstance.Update runs every frame on the main thread for the whole
    // session (menus, sim game, combat), which makes it the bridge's clock.
    [HarmonyPatch(typeof(UnityGameInstance), "Update")]
    public static class FramePump
    {
        public static void Postfix()
        {
            try
            {
                MainThread.Pump();
                Combat.Briefing.Tick();
            }
            catch (Exception e)
            {
                Log.Error("pump failed", e);
            }
        }
    }
}
