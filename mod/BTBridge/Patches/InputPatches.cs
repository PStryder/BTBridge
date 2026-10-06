using BTBridge.Ui;
using Harmony;
using HBS.DebugConsole;

namespace BTBridge.Patches
{
    /// <summary>
    /// The combat key handler (CombatSelectionHandler.Update) only runs while HBS's debug console is
    /// hidden. While the operator is typing in our input bar, report the console as visible so the
    /// same gate stands down; bound game actions are separately disabled (ChatOverlay.OpenInput).
    /// </summary>
    [HarmonyPatch(typeof(DebugConsole), "get_IsHidden")]
    public static class ConsoleGateWhileTyping
    {
        public static void Postfix(ref bool __result)
        {
            if (ChatOverlay.Typing)
            {
                __result = false;
            }
        }
    }
}
