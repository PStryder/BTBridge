using BattleTech.UI;
using HBS;

namespace BTBridge.Ui
{
    /// <summary>
    /// The game's UIManager, only if the game has made it. LazySingletonBehavior.Instance CREATES the
    /// singleton when it doesn't exist yet: the overlay asked for it from its first frame, built a
    /// UIManager before the game's own, whose Awake failed, and the game stalled on a black screen.
    /// Every lookup goes through here (a test forbids .Instance elsewhere).
    /// </summary>
    public static class GameUi
    {
        public static UIManager Existing() =>
            LazySingletonBehavior<UIManager>.HasInstance ? LazySingletonBehavior<UIManager>.Instance : null;
    }
}
