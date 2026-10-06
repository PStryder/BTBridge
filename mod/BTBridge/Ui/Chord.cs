using System;
using System.Linq;
using UnityEngine;

namespace BTBridge.Ui
{
    /// <summary>A key chord like "Ctrl+Shift+F9", read with UnityEngine.Input.</summary>
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
