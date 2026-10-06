using System;
using BattleTech;

namespace BTBridge.Sim
{
    /// <summary>Per-frame progress for the campaign jobs that wait on asynchronous game steps.</summary>
    public static class SimTicks
    {
        public static void Tick()
        {
            var game = UnityGameInstance.BattleTechGame;
            if (game == null)
            {
                return;
            }
            try
            {
                if (game.Combat != null)
                {
                    Contracts.OnCombatStarted();
                    return;
                }
                var sim = game.Simulation;
                if (sim == null)
                {
                    return;
                }
                Contracts.Tick();
                Navigation.Tick();
                if (sim.InterruptQueue != null && sim.InterruptQueue.IsOpen)
                {
                    TimeControl.OnInterrupt();
                }
            }
            catch (Exception e)
            {
                Log.Error("sim tick failed", e);
            }
        }
    }
}
