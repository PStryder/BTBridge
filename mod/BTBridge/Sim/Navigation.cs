using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;

namespace BTBridge.Sim
{
    /// <summary>
    /// Star map reads and travel. Routing is an A* the starmap steps per frame, so travel is a small
    /// job: select the system, wait for the route, then (if confirmed) commit it exactly as the
    /// navigation screen's travel button does: SetActivePath + switch to the SHIP room.
    /// </summary>
    public static class Navigation
    {
        private sealed class Job
        {
            public string SystemId;
            public bool Confirm;
            public string State;
            public int? Days;
            public int? Cost;
        }

        private static Job job;

        public static object JobView() => job == null ? null : new { system = job.SystemId, state = job.State, travel_days = job.Days, cost = job.Cost };

        public static object Map(SimGameState sim, int jumps)
        {
            var start = sim.Starmap?.GetSystemByID(sim.CurSystem.ID) ?? throw new BridgeException(409, "starmap not ready");
            jumps = System.Math.Max(1, System.Math.Min(jumps, 6));
            var seen = new Dictionary<string, int> { [start.System.ID] = 0 };
            var frontier = new Queue<StarSystemNode>();
            frontier.Enqueue(start);
            while (frontier.Count > 0)
            {
                var node = frontier.Dequeue();
                int d = seen[node.System.ID];
                if (d >= jumps)
                {
                    continue;
                }
                foreach (var next in node.AdjacentSystems)
                {
                    if (!seen.ContainsKey(next.System.ID))
                    {
                        seen[next.System.ID] = d + 1;
                        frontier.Enqueue(next);
                    }
                }
            }
            return new
            {
                current = SystemView(sim, sim.CurSystem, 0),
                travel_state = sim.TravelManager?.TravelState.ToString(),
                active_travel_contract = sim.ActiveTravelContract?.Name,
                systems = seen.Where(kv => kv.Value > 0).OrderBy(kv => kv.Value)
                    .Select(kv => SystemView(sim, sim.Starmap.GetSystemByID(kv.Key).System, kv.Value)).ToList(),
                note = "jumps = hops on the starmap; preview a route for exact days and cost",
            };
        }

        private static object SystemView(SimGameState sim, StarSystem s, int jumps)
        {
            var flashpoint = sim.AvailableFlashpoints?.FirstOrDefault(f => f.CurSystem == s);
            return new
            {
                id = s.ID,
                name = s.Name,
                jumps,
                owner = s.OwnerValue?.FriendlyName,
                can_travel = jumps == 0 || sim.Starmap.CanTravelToNode(s.ID),
                shops = new { system = s.CanUseSystemStore(), faction = s.CanUseFactionStore(), black_market = s.CanUseBlackMarketStore() },
                contracts_known = s.InitialContractsFetched ? (int?)s.SystemContracts.Count : null,
                flashpoint = flashpoint?.Def?.Description?.Name,
                tags = s.Tags?.Where(t => t.StartsWith("planet_")).Take(12).ToList(),
            };
        }

        public static object Travel(SimGameState sim, string systemId, bool confirm)
        {
            if (string.IsNullOrEmpty(systemId))
            {
                throw new BridgeException(400, "system (id) is required");
            }
            if (sim.InterruptQueue.IsOpen || sim.InterruptQueue.HasQueue)
            {
                throw new BridgeException(409, "an interrupt is waiting; resolve it first");
            }
            if (systemId == sim.CurSystem.ID)
            {
                throw new BridgeException(400, "already in that system");
            }
            if (sim.HasTravelContract)
            {
                throw new BridgeException(409, "a travel contract is active; travelling elsewhere would break it (not supported)");
            }
            if (sim.TravelManager != null && sim.TravelManager.TravelState != SimGameTravelStatus.IN_SYSTEM)
            {
                throw new BridgeException(409, $"already travelling ({sim.TravelManager.TravelState})");
            }
            if (sim.Starmap.GetSystemByID(systemId) == null)
            {
                throw new BridgeException(404, $"no system '{systemId}'");
            }
            if (!sim.Starmap.CanTravelToNode(systemId))
            {
                throw new BridgeException(400, "travel requirements for that system are not met");
            }
            sim.Starmap.SetSelectedSystem(systemId);
            job = new Job { SystemId = systemId, Confirm = confirm, State = "routing" };
            Log.Info($"travel {(confirm ? "ordered" : "previewed")} to {systemId}");
            return new { routing = true, confirm, next = "poll GET /sim/travel" };
        }

        /// <summary>Frame tick: once the route is ready, report it and (if confirmed) commit it.</summary>
        public static void Tick()
        {
            if (job == null || job.State != "routing")
            {
                return;
            }
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var map = sim?.Starmap;
            if (map?.CurSelected == null || map.CurSelected.System.ID != job.SystemId || map.PotentialPath == null || map.PotentialPath.Count == 0)
            {
                return;
            }
            job.Days = map.ProjectedTravelTime;
            job.Cost = map.ProjectedTravelCost;
            if (!job.Confirm)
            {
                job.State = "previewed";
                return;
            }
            if (sim.Funds < map.ProjectedTravelCost)
            {
                job.State = $"refused: travel costs {map.ProjectedTravelCost:N0}, funds {sim.Funds:N0}";
                return;
            }
            map.SetActivePath();
            sim.SetSimRoomState(DropshipLocation.SHIP);
            job.State = "travelling: run time to progress";
            Log.Info($"travel committed to {job.SystemId}: {job.Days} day(s), {job.Cost} C-bills");
        }

        public static object View(SimGameState sim) => new
        {
            job = JobView(),
            travel_state = sim.TravelManager?.TravelState.ToString(),
            current_system = sim.CurSystem?.Name,
        };
    }
}
