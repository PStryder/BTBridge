using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.Data;
using BTBridge.Bridge;

namespace BTBridge.State
{
    /// <summary>
    /// The DataManager loads defs lazily: at the main menu, weapon and chassis defs are not in memory
    /// until a screen asks for them. When a requested id exists in the game's resource index but is
    /// not loaded yet, request it (as SimGameState_Debug.SimDebug_RequestItemResource does) and ask the
    /// caller to retry, instead of reporting a real component as unknown.
    /// </summary>
    public static class DataLoader
    {
        public static readonly BattleTechResourceType[] ComponentTypes =
        {
            BattleTechResourceType.WeaponDef, BattleTechResourceType.AmmunitionBoxDef, BattleTechResourceType.HeatSinkDef,
            BattleTechResourceType.JumpJetDef, BattleTechResourceType.UpgradeDef,
        };

        private static readonly HashSet<string> InFlight = new HashSet<string>();

        /// <summary>
        /// For ids that aren't loaded: start loading the ones the game knows, then throw 409 (retry).
        /// Ids the game doesn't know at all are returned for the caller to report as unknown.
        /// </summary>
        public static List<string> RequestMissing(DataManager dm, IEnumerable<string> ids, params BattleTechResourceType[] types)
        {
            var unknown = new List<string>();
            var toLoad = new List<KeyValuePair<BattleTechResourceType, string>>();
            foreach (var id in ids.Distinct())
            {
                var type = types.Cast<BattleTechResourceType?>().FirstOrDefault(t => dm.ResourceLocator.EntryByID(id, t.Value, filterByOwnership: true) != null);
                if (type == null)
                {
                    unknown.Add(id);
                }
                else
                {
                    toLoad.Add(new KeyValuePair<BattleTechResourceType, string>(type.Value, id));
                }
            }
            if (toLoad.Count == 0)
            {
                return unknown;
            }
            var fresh = toLoad.Where(kv => InFlight.Add(kv.Key + ":" + kv.Value)).ToList();
            if (fresh.Count > 0)
            {
                var request = dm.CreateLoadRequest(r =>
                {
                    foreach (var kv in fresh)
                    {
                        InFlight.Remove(kv.Key + ":" + kv.Value);
                    }
                    Log.Info($"loaded {fresh.Count} game data entr(ies) on demand");
                });
                foreach (var kv in fresh)
                {
                    request.AddBlindLoadRequest(kv.Key, kv.Value, false);
                }
                request.ProcessRequests();
            }
            throw new BridgeException(409, $"loading {toLoad.Count} game data entr(ies) on demand; retry in a moment"
                + (unknown.Count > 0 ? $" (unknown: {string.Join(", ", unknown.ToArray())})" : ""));
        }
    }
}
