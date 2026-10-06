namespace BTBridge.Logic
{
    /// <summary>What a side may know about one unit.</summary>
    public sealed class ContactView
    {
        public bool Listed;    // appears at all
        public bool Position;  // where it is
        public bool Kind;      // mech / vehicle / turret
        public bool Identity;  // name, team, facing, status, loadout
    }

    /// <summary>
    /// Fog of war for everything the bridge shows an agent. Ranks are BattleTech's VisibilityLevel
    /// values (None 0, blobs 1-3, Blip0Minimum 4, Blip1Type 5, BlipGhost 6, Blip4Maximum 8,
    /// LOSFull 9). A sensor contact used to carry its real name and facing (review finding), and
    /// target lists named blips too.
    /// </summary>
    public static class ContactRules
    {
        public const int LosFull = 9;
        public const int Blip1Type = 5;
        public const int BlipGhost = 6;

        public static ContactView For(bool friendly, int visibilityRank)
        {
            if (friendly || visibilityRank >= LosFull)
            {
                return new ContactView { Listed = true, Position = true, Kind = true, Identity = true };
            }
            if (visibilityRank <= 0)
            {
                return new ContactView();
            }
            // A ghost is an ECM decoy-quality return: where, not what.
            bool kind = visibilityRank >= Blip1Type && visibilityRank != BlipGhost;
            return new ContactView { Listed = true, Position = true, Kind = kind, Identity = false };
        }

        /// <summary>The name a side may use for a unit.</summary>
        public static string Name(bool friendly, int visibilityRank, string realName, string kind)
        {
            var v = For(friendly, visibilityRank);
            if (v.Identity)
            {
                return realName;
            }
            return v.Kind && !string.IsNullOrEmpty(kind) ? $"Unknown {kind} contact" : "Unknown contact";
        }
    }
}
