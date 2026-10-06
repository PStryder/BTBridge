using System;
using System.Linq;
using UnityEngine;

namespace BTBridge.Cheats
{
    /// <summary>
    /// OPERATOR CHEAT LAYER configuration.
    /// Capability (restart required): the launch option --btbridge-allow-cheats, or mod.json
    /// Settings.AllowCheats. Arming parameters: mod.json defaults, overridable only from the
    /// in-game panel (PlayerPrefs). Nothing here is settable over the bridge API.
    /// </summary>
    public static class CheatConfig
    {
        public const string CommandLineFlag = "--btbridge-allow-cheats";
        private const string PrefMinutes = "BTBridge.Cheat.Minutes";
        private const string PrefMaxOps = "BTBridge.Cheat.MaxOps";
        private const string PrefHotkey = "BTBridge.Cheat.Hotkey";

        public static bool Capability { get; private set; }
        public static string CapabilitySource { get; private set; }

        // mod.json defaults
        private static int fileMinutes = 15;
        private static int fileMaxOps;
        private static string fileHotkey = "Ctrl+Shift+F9";

        public const string SettingsHotkey = "Ctrl+Shift+F10";

        public static void Init(bool allowFromSettings, int minutes, int maxOps, string hotkey)
        {
            bool fromArgs = Environment.GetCommandLineArgs().Any(a => string.Equals(a, CommandLineFlag, StringComparison.OrdinalIgnoreCase));
            Capability = fromArgs || allowFromSettings;
            CapabilitySource = fromArgs ? "command line" : allowFromSettings ? "mod.json" : null;
            fileMinutes = minutes > 0 ? minutes : 15;
            fileMaxOps = Math.Max(0, maxOps);
            fileHotkey = string.IsNullOrEmpty(hotkey) ? "Ctrl+Shift+F9" : hotkey;
        }

        public static int ArmMinutes => Clamp(PlayerPrefs.HasKey(PrefMinutes) ? PlayerPrefs.GetInt(PrefMinutes) : fileMinutes, 1, 240);

        public static int ArmMaxOps => Math.Max(0, PlayerPrefs.HasKey(PrefMaxOps) ? PlayerPrefs.GetInt(PrefMaxOps) : fileMaxOps);

        public static string ArmHotkey => PlayerPrefs.HasKey(PrefHotkey) ? PlayerPrefs.GetString(PrefHotkey) : fileHotkey;

        public static bool PanelOverrides => PlayerPrefs.HasKey(PrefMinutes) || PlayerPrefs.HasKey(PrefMaxOps) || PlayerPrefs.HasKey(PrefHotkey);

        // Only the in-game panel calls these.
        internal static void SetMinutes(int v)
        {
            PlayerPrefs.SetInt(PrefMinutes, Clamp(v, 1, 240));
            PlayerPrefs.Save();
        }

        internal static void SetMaxOps(int v)
        {
            PlayerPrefs.SetInt(PrefMaxOps, Math.Max(0, Math.Min(v, 999)));
            PlayerPrefs.Save();
        }

        internal static void SetHotkey(string v)
        {
            PlayerPrefs.SetString(PrefHotkey, v);
            PlayerPrefs.Save();
        }

        internal static void ResetToFile()
        {
            PlayerPrefs.DeleteKey(PrefMinutes);
            PlayerPrefs.DeleteKey(PrefMaxOps);
            PlayerPrefs.DeleteKey(PrefHotkey);
            PlayerPrefs.Save();
        }

        private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    }

    /// <summary>A key chord like "Ctrl+Shift+F9".</summary>
    public sealed class Chord
    {
        public bool Ctrl, Shift, Alt;
        public KeyCode Key;

        public static Chord Parse(string text)
        {
            var chord = new Chord();
            foreach (var part in (text ?? "").Split('+').Select(p => p.Trim()))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        chord.Ctrl = true;
                        break;
                    case "shift":
                        chord.Shift = true;
                        break;
                    case "alt":
                        chord.Alt = true;
                        break;
                    default:
                        try
                        {
                            chord.Key = (KeyCode)Enum.Parse(typeof(KeyCode), part, ignoreCase: true);
                        }
                        catch (ArgumentException)
                        {
                            chord.Key = KeyCode.None;
                        }
                        break;
                }
            }
            return chord;
        }

        public bool PressedThisFrame() =>
            Key != KeyCode.None && Input.GetKeyDown(Key)
            && Ctrl == (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            && Shift == (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            && Alt == (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        public override string ToString() =>
            (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;
    }
}
