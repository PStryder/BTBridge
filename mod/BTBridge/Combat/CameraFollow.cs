using System;
using BattleTech;
using BTBridge.Logic;

namespace BTBridge.Combat
{
    /// <summary>
    /// Pans to the unit whose decision just opened, as selecting it would (CombatSelectionHandler
    /// uses the same SetMovingToGroundPos). The game has no follow setting of its own; it only
    /// shows camera for visible enemy movement. Toggle: overlay settings panel (Ctrl+Shift+O).
    /// </summary>
    public static class CameraFollow
    {
        public static void OnTurnOpened(AbstractActor unit)
        {
            try
            {
                var combat = unit?.Combat;
                var local = combat?.LocalPlayerTeam;
                if (local == null || CameraControl.Instance == null)
                {
                    return;
                }
                bool friendly = unit.team == local || local.IsFriendly(unit.team);
                bool visible = local.VisibilityToTarget(unit) == VisibilityLevel.LOSFull;
                if (CameraRules.ShouldFollow(Ui.ChatOverlay.FollowActingUnit, friendly, visible))
                {
                    CameraControl.Instance.SetMovingToGroundPos(unit.CurrentPosition);
                }
            }
            catch (Exception e)
            {
                Log.Warn("camera follow failed: " + e.Message);
            }
        }
    }
}
