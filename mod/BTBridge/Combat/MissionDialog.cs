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
        private static readonly System.Collections.Generic.List<object> Transcript = new System.Collections.Generic.List<object>();
        private static int lineId;

        public static void Record(string speaker, string text, bool endOfConvo)
        {
            var now = DateTime.UtcNow;
            var where = UnityGameInstance.BattleTechGame?.Combat != null ? "mission" : "campaign";
            var entry = new { id = ++lineId, utc = now.ToString("o"), where, speaker, text, end_of_conversation = endOfConvo };
            lock (TranscriptLock)
            {
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

        public static object TranscriptView(int limit)
        {
            lock (TranscriptLock)
            {
                return Transcript.Skip(Math.Max(0, Transcript.Count - Math.Max(1, Math.Min(limit, TranscriptSize)))).ToList();
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
            recent = TranscriptView(10),
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
                MissionDialog.Text = line;
                MissionDialog.Record(MissionDialog.Speaker, line, endOfConvo);
            }
            catch (Exception e)
            {
                Log.Warn("dialog capture failed: " + e.Message);
            }
        }
    }
}
