using System;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Logic;
using Harmony;

namespace BTBridge.Sim
{
    /// <summary>
    /// Which campaign is loaded, and which load of it. The epoch moves on every save load
    /// (Rehydrate) and whenever the SimGameState instance changes (new career, back to the menu).
    /// Anything prepared against one load (refit plans) is invalid in the next. The cheat layer
    /// keeps its own epoch only while cheats are enabled; this one always runs.
    /// </summary>
    public static class CampaignLifecycle
    {
        public static int Epoch { get; private set; }

        public static event Action Changed;

        private static SimGameState lastSim;

        public static string CampaignId(SimGameState sim) => sim?.InstanceGUID;

        public static void Bump(string why)
        {
            Epoch++;
            Log.Info($"campaign lifecycle: epoch {Epoch} ({why})");
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Log.Warn("campaign lifecycle listener failed: " + e.Message);
            }
        }

        /// <summary>Per frame: notice the simulation object itself changing.</summary>
        public static void Tick()
        {
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            if (!ReferenceEquals(sim, lastSim))
            {
                lastSim = sim;
                Bump(sim == null ? "left the campaign" : "campaign instance changed");
            }
        }

        /// <summary>The shared write guard for mech-touching company writes.</summary>
        public static void RequireWritable(SimGameState sim)
        {
            var blockers = CampaignWrites.Blockers(Interrupts.Facts(sim));
            if (blockers.Count > 0)
            {
                throw new BridgeException(409, "can't change mechs now: " + string.Join("; ", blockers.ToArray()));
            }
        }
    }

    [HarmonyPatch(typeof(SimGameState), "Rehydrate")]
    public static class LifecycleOnLoad
    {
        public static void Postfix()
        {
            CampaignLifecycle.Bump("save loaded");
        }
    }
}
