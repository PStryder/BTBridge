using System;

namespace BTBridge.Logic
{
    /// <summary>
    /// Per-location armor limits. The game's own Full validation checks total tonnage but not
    /// per-location maximums, and LocationLoadoutDef doesn't clamp, so moving 100 points from the
    /// legs onto the head passed validation and could be saved as a custom mech (review finding).
    /// Comparisons alone also let NaN through (every comparison with NaN is false).
    /// </summary>
    public static class ArmorRules
    {
        /// <returns>null when the location is fine, otherwise what's wrong.</returns>
        public static string Problem(string location, float front, float rear, float maxFront, float maxRear, bool hasRear)
        {
            if (float.IsNaN(front) || float.IsInfinity(front))
            {
                return $"{location}: front armor must be a number";
            }
            if (front < 0f || front > maxFront)
            {
                return $"{location}: front armor {front} is outside 0..{maxFront}";
            }
            if (hasRear)
            {
                if (float.IsNaN(rear) || float.IsInfinity(rear))
                {
                    return $"{location}: rear armor must be a number";
                }
                if (rear < 0f || rear > maxRear)
                {
                    return $"{location}: rear armor {rear} is outside 0..{maxRear}";
                }
            }
            else if (!(rear == -1f || rear == 0f))
            {
                return $"{location}: has no rear armor (got {rear})";
            }
            return null;
        }
    }
}
