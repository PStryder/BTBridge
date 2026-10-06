namespace BTBridge.Logic
{
    /// <summary>
    /// Whether the camera may pan to the unit whose turn just opened. The human at the screen must
    /// never be shown more than their side can see: with the agent commanding the enemy, following
    /// an unseen enemy unit would reveal it.
    /// </summary>
    public static class CameraRules
    {
        /// <param name="enabled">The operator's "camera follows the acting unit" toggle.</param>
        /// <param name="friendlyToScreen">The unit is on the local player's team or allied to it.</param>
        /// <param name="fullyVisibleToScreen">The local player's team has full line of sight to it.</param>
        public static bool ShouldFollow(bool enabled, bool friendlyToScreen, bool fullyVisibleToScreen) =>
            enabled && (friendlyToScreen || fullyVisibleToScreen);
    }
}
