using System;
using UnityEngine;

namespace BTBridge.Cheats
{
    /// <summary>
    /// OPERATOR CHEAT LAYER: the human side. Exists only when the cheat capability is on.
    /// - Arm/disarm hotkey (default Ctrl+Shift+F9): the only way to arm. There is no API for it.
    /// - Settings hotkey (Ctrl+Shift+F10): window length, operation budget, hotkey rebind.
    /// - While armed, a banner that cannot be missed.
    /// Drawn with IMGUI on its own GameObject; it never opens game popups or blocks input flow.
    /// </summary>
    public sealed class CheatOverlay : MonoBehaviour
    {
        private static CheatOverlay instance;
        private bool panelOpen;
        private bool rebinding;
        private Rect panelRect = new Rect(40, 120, 340, 230);
        private Chord armChord;
        private string armChordText;
        private readonly Chord settingsChord = Chord.Parse(CheatConfig.SettingsHotkey);
        private GUIStyle bannerStyle;
        private GUIStyle noticeStyle;

        public static void Create()
        {
            if (instance != null)
            {
                return;
            }
            var go = new GameObject("BTBridge.CheatOverlay");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<CheatOverlay>();
        }

        private Chord ArmChord()
        {
            string text = CheatConfig.ArmHotkey;
            if (armChord == null || armChordText != text)
            {
                armChord = Chord.Parse(text);
                armChordText = text;
            }
            return armChord;
        }

        private void Update()
        {
            try
            {
                CheatService.Tick();
                if (rebinding)
                {
                    return;
                }
                if (ArmChord().PressedThisFrame())
                {
                    CheatService.ToggleArm();
                }
                else if (settingsChord.PressedThisFrame())
                {
                    panelOpen = !panelOpen;
                }
            }
            catch (Exception e)
            {
                Log.Error("[CHEAT] overlay update failed", e);
            }
        }

        private void EnsureStyles()
        {
            if (bannerStyle != null)
            {
                return;
            }
            bannerStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = false };
            bannerStyle.normal.textColor = new Color(1f, 0.35f, 0.25f);
            noticeStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, alignment = TextAnchor.MiddleCenter, richText = false };
        }

        private void OnGUI()
        {
            EnsureStyles();
            var gate = CheatService.Gate;
            var left = gate.Remaining(DateTime.UtcNow);
            if (left.HasValue)
            {
                string ops = gate.OpsLeft.HasValue ? $" · {gate.OpsLeft} op(s) left" : "";
                GUI.Box(new Rect(Screen.width / 2f - 230, 6, 460, 30), $"BTBridge CHEATS ARMED · {left.Value:mm\\:ss} left{ops}", bannerStyle);
            }
            if (!string.IsNullOrEmpty(CheatService.Notice) && DateTime.Now < CheatService.NoticeUntil)
            {
                GUI.Box(new Rect(Screen.width / 2f - 230, 40, 460, 26), CheatService.Notice, noticeStyle);
            }
            if (panelOpen)
            {
                panelRect = GUI.Window(0x8771, panelRect, DrawPanel, "BTBridge cheat settings (operator only)");
            }
        }

        private void DrawPanel(int id)
        {
            GUILayout.Label($"Arm hotkey: {ArmChord()}");
            if (rebinding)
            {
                GUILayout.Label("Press the new key (with modifiers)... Esc cancels");
                var e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None &&
                    e.keyCode != KeyCode.LeftControl && e.keyCode != KeyCode.RightControl &&
                    e.keyCode != KeyCode.LeftShift && e.keyCode != KeyCode.RightShift &&
                    e.keyCode != KeyCode.LeftAlt && e.keyCode != KeyCode.RightAlt)
                {
                    if (e.keyCode != KeyCode.Escape)
                    {
                        var chord = new Chord { Ctrl = e.control, Shift = e.shift, Alt = e.alt, Key = e.keyCode };
                        CheatConfig.SetHotkey(chord.ToString());
                        Log.Info($"[CHEAT] operator rebound arm hotkey to {chord}");
                    }
                    rebinding = false;
                    e.Use();
                }
            }
            else if (GUILayout.Button("Rebind arm hotkey"))
            {
                rebinding = true;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Window: {CheatConfig.ArmMinutes} min", GUILayout.Width(160));
            if (GUILayout.Button("-5"))
            {
                CheatConfig.SetMinutes(CheatConfig.ArmMinutes - 5);
            }
            if (GUILayout.Button("-1"))
            {
                CheatConfig.SetMinutes(CheatConfig.ArmMinutes - 1);
            }
            if (GUILayout.Button("+1"))
            {
                CheatConfig.SetMinutes(CheatConfig.ArmMinutes + 1);
            }
            if (GUILayout.Button("+5"))
            {
                CheatConfig.SetMinutes(CheatConfig.ArmMinutes + 5);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Op budget: {(CheatConfig.ArmMaxOps == 0 ? "none" : CheatConfig.ArmMaxOps.ToString())}", GUILayout.Width(160));
            if (GUILayout.Button("-1"))
            {
                CheatConfig.SetMaxOps(CheatConfig.ArmMaxOps - 1);
            }
            if (GUILayout.Button("+1"))
            {
                CheatConfig.SetMaxOps(CheatConfig.ArmMaxOps + 1);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Changes apply to the next arming.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to mod.json"))
            {
                CheatConfig.ResetToFile();
            }
            if (GUILayout.Button("Close"))
            {
                panelOpen = false;
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }
}
