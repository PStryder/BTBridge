using BattleTech.UI;
using Harmony;

namespace BTBridge.Patches
{
    /// <summary>Remembers the open mechlab so the bridge can snapshot the build in progress.</summary>
    public static class MechLabTracker
    {
        public static MechLabPanel Current { get; private set; }

        // SetData has a skirmish and a sim-game overload; both end in LoadMech.
        [HarmonyPatch(typeof(MechLabPanel), "LoadMech")]
        public static class OnLoad
        {
            public static void Postfix(MechLabPanel __instance) => Current = __instance;
        }

        [HarmonyPatch(typeof(MechLabPanel), "ExitMechLab")]
        public static class OnExit
        {
            public static void Prefix(MechLabPanel __instance)
            {
                if (Current == __instance)
                {
                    Current = null;
                }
            }
        }

        /// <summary>The tracked panel, only if it is still live and initialized.</summary>
        public static MechLabPanel Live =>
            Current != null && Current && Current.Initialized && Current.gameObject.activeInHierarchy ? Current : null;
    }
}
