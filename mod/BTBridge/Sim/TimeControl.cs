using BattleTech;
using BTBridge.Bridge;
using BTBridge.Logic;

namespace BTBridge.Sim
{
    /// <summary>
    /// Runs the campaign clock the way the play button does (SetTimeMoving) and stops it after N
    /// days via a postfix on OnDayPassed. Days are always passed by SimGameState.Update, never by
    /// calling OnDayPassed directly (OnDayPassed(n) skips travel, events and milestones).
    /// Any interrupt stops time by itself; the job stays active and can be resumed.
    /// </summary>
    public static class TimeControl
    {
        private static readonly DayCounter Counter = new DayCounter();
        private static bool untilEvent;

        public static object View() => Counter.Active || untilEvent
            ? new { target_days = untilEvent ? (int?)null : Counter.Target, days_passed = Counter.Passed, until_event = untilEvent }
            : null;

        public static object Start(SimGameState sim, int? days, bool toEvent, float? daySeconds)
        {
            if (sim.InterruptQueue.IsOpen || sim.InterruptQueue.HasQueue)
            {
                throw new BridgeException(409, "an interrupt is waiting; resolve it first (GET /sim/interrupt)");
            }
            if (sim.TravelManager != null && sim.TravelManager.InTransition)
            {
                throw new BridgeException(409, "a travel transition is animating; retry shortly");
            }
            if (UnityGameInstance.BattleTechGame?.Combat != null)
            {
                throw new BridgeException(409, "in combat");
            }
            if (days.HasValue)
            {
                try
                {
                    Counter.Start(days.Value);
                }
                catch (RuleException e)
                {
                    throw new BridgeException(400, e.Message);
                }
                untilEvent = false;
            }
            else if (toEvent)
            {
                Counter.Stop();
                untilEvent = true;
            }
            else if (!Counter.Active && !untilEvent)
            {
                throw new BridgeException(400, "pass days (1-365) or until_event: true");
            }
            if (daySeconds.HasValue)
            {
                sim.Constants.Time.DayElapseTimeNormal = System.Math.Max(0.1f, daySeconds.Value);
            }
            // Time only runs in the Argo's main view, and only when the timer gate is open.
            if (sim.CurRoomState != DropshipLocation.SHIP)
            {
                sim.SetSimRoomState(DropshipLocation.SHIP);
            }
            sim.ResumeTimer();
            sim.SetTimeMoving(true);
            Log.Info($"time running: {(untilEvent ? "until next event" : Counter.Target + " day(s)")}");
            return new { running = true, job = View() };
        }

        public static object Stop(SimGameState sim)
        {
            Counter.Stop();
            untilEvent = false;
            sim.SetTimeMoving(false);
            return new { running = false };
        }

        /// <summary>Called after each day passes (patch on SimGameState.OnDayPassed).</summary>
        public static void OnDayPassed(SimGameState sim)
        {
            if (Counter.OnDay())
            {
                sim.SetTimeMoving(false);
                Log.Info($"time job done: {Counter.Passed} day(s) passed");
            }
        }

        /// <summary>An interrupt appeared: an until-event job is finished.</summary>
        public static void OnInterrupt()
        {
            if (untilEvent)
            {
                untilEvent = false;
                Log.Info("time job done: interrupt reached");
            }
        }
    }
}
