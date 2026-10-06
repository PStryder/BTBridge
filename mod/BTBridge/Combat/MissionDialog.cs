using System;
using System.Linq;
using BattleTech;
using BattleTech.StringInterpolation;
using BattleTech.UI;
using BTBridge.Bridge;
using Harmony;

namespace BTBridge.Combat
{
    /// <summary>
    /// In-mission story dialogue (contract intros, objective chatter). It runs through
    /// InterruptDialogSequence on the shared SGDialogWidget and blocks the mission until the
    /// Continue button is pressed. Found in the first career test.
    /// </summary>
    public static class MissionDialog
    {
        public static SGDialogWidget Widget;
        public static string Speaker;
        public static string Text;
        public static bool EndOfConversation;

        // Transcript of every line shown through the dialog widget (mission dialogue and campaign
        // conversations), so lines that auto-advance or are clicked through quickly aren't lost.
        private const int TranscriptSize = 50;
        private static readonly object TranscriptLock = new object();
        private static readonly System.Collections.Generic.List<Line> Transcript = new System.Collections.Generic.List<Line>();
        private static int lineId;
        // How often each (speaker, text) has been shown this session; radio barks repeat a lot.
        private static readonly System.Collections.Generic.Dictionary<string, int> Seen =
            new System.Collections.Generic.Dictionary<string, int>();

        private sealed class Line
        {
            public int id;
            public string utc;
            public string where;
            public string channel;
            public string speaker;
            public string text;
            public bool? end_of_conversation;
            public int times_seen;
        }

        /// <param name="channel">"dialog" (blocking, Continue button) or "radio" (voiced side-panel chatter).</param>
        public static void Record(string speaker, string text, bool? endOfConvo, string channel = "dialog")
        {
            var now = DateTime.UtcNow;
            var where = UnityGameInstance.BattleTechGame?.Combat != null ? "mission" : "campaign";
            Line entry;
            lock (TranscriptLock)
            {
                string key = speaker + "\u0001" + text;
                Seen.TryGetValue(key, out int n);
                Seen[key] = ++n;
                entry = new Line
                {
                    id = ++lineId, utc = now.ToString("o"), where = where, channel = channel, speaker = speaker, text = text,
                    end_of_conversation = endOfConvo, times_seen = n,
                };
                Transcript.Add(entry);
                if (Transcript.Count > TranscriptSize)
                {
                    Transcript.RemoveAt(0);
                }
            }
            try
            {
                if (Main.ModDir != null)
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(Main.ModDir, "dialog_log.jsonl"),
                        Newtonsoft.Json.JsonConvert.SerializeObject(entry) + Environment.NewLine);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        /// <param name="repeats">false drops lines already shown earlier this session (stock barks
        /// like "Target destroyed"); each kept line still carries times_seen.</param>
        public static object TranscriptView(int limit, bool repeats = false, int sinceId = 0)
        {
            lock (TranscriptLock)
            {
                var lines = Transcript.Where(l => l.id > sinceId && (repeats || l.times_seen == 1)).ToList();
                int take = Math.Max(1, Math.Min(limit, TranscriptSize));
                return lines.Skip(Math.Max(0, lines.Count - take)).ToList();
            }
        }

        public static bool Waiting =>
            UnityGameInstance.BattleTechGame?.Combat != null && Widget != null && Widget.Visible && Widget.gameObject.activeInHierarchy;

        public static object View() => new
        {
            open = Waiting,
            speaker = Waiting ? Speaker : null,
            text = Waiting ? Text : null,
            last_line = Waiting ? (bool?)EndOfConversation : null,
            answer = Waiting ? "POST /combat/dialog/continue" : null,
            recent = TranscriptView(10, repeats: true),
        };

        public static object Continue()
        {
            if (!Waiting)
            {
                throw new BridgeException(409, "no mission dialogue is showing");
            }
            // Exactly what the Continue button sends; handles end-of-conversation closing too.
            Widget.ReceiveButtonPress("ContinueDialog");
            return new { continued = true, was_last_line = EndOfConversation };
        }
    }

    [HarmonyPatch(typeof(SGDialogWidget), "Show")]
    public static class CaptureDialogLine
    {
        public static void Postfix(SGDialogWidget __instance, string text, GameContext gameContext, CastDef whoIsTalking, bool endOfConvo)
        {
            try
            {
                MissionDialog.Widget = __instance;
                MissionDialog.EndOfConversation = endOfConvo;
                MissionDialog.Speaker = whoIsTalking == null ? null
                    : !string.IsNullOrEmpty(whoIsTalking.callsign) ? whoIsTalking.callsign
                    : (whoIsTalking.firstName + " " + whoIsTalking.lastName).Trim();
                string line = text;
                try
                {
                    line = gameContext != null ? Interpolator.Interpolate(text, gameContext, true) : text;
                }
                catch
                {
                    // Keep the raw template if interpolation fails.
                }
                line = BTBridge.Logic.GameText.Plain(line);
                MissionDialog.Text = line;
                MissionDialog.Record(MissionDialog.Speaker, line, endOfConvo);
            }
            catch (Exception e)
            {
                Log.Warn("dialog capture failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Voiced, non-blocking mission chatter (Darius on the radio, pilot barks) shown in the combat
    /// HUD's side and front dialog stacks. Every such line ends in CombatHUDDialogItem.Show.
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDDialogItem), "Show")]
    public static class CaptureRadioLine
    {
        public static void Postfix(string dialogText, string speakerName)
        {
            try
            {
                if (string.IsNullOrEmpty(dialogText))
                {
                    return;
                }
                string line = dialogText;
                var ctx = UnityGameInstance.BattleTechGame?.Combat?.ActiveContract?.GameContext;
                try
                {
                    if (ctx != null)
                    {
                        line = Interpolator.Interpolate(dialogText, ctx, true);
                    }
                }
                catch
                {
                    // Keep the raw template if interpolation fails.
                }
                MissionDialog.Record(speakerName, BTBridge.Logic.GameText.Plain(line), null, "radio");
            }
            catch (Exception e)
            {
                Log.Warn("radio capture failed: " + e.Message);
            }
        }
    }
}
