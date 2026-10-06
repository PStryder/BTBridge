using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Combat;
using BTBridge.Logic;
using Newtonsoft.Json;
using UnityEngine;

namespace BTBridge.Ui
{
    /// <summary>
    /// In-game chat overlay: agent text on screen, drawn with IMGUI on its own GameObject.
    /// - Plain text only (richText off), capped and rate-limited (Logic/OverlayRules).
    /// - Four channels; the operator toggles each live (Ctrl+Shift+O); only commentary is on by default.
    /// - Rolling feed on the right edge, never wider than a third of the screen; history on Ctrl+Shift+H.
    /// - The mod labels the speaker, not the model; enemy decisions are held until carried out.
    /// Never opens game popups or interrupts, and nothing waits on it.
    /// </summary>
    public sealed class ChatOverlay : MonoBehaviour
    {
        private const string PrefPrefix = "BTBridge.Overlay.";
        private static ChatOverlay instance;
        private static readonly object Sync = new object();
        private static readonly FeedState Feed = new FeedState();
        private static readonly RateLimit AgentRate = new RateLimit(1, 5, DateTime.UtcNow);
        private static string logPath;

        private readonly Chord settingsChord = Chord.Parse("Ctrl+Shift+O");
        private readonly Chord historyChord = Chord.Parse("Ctrl+Shift+H");
        private readonly Chord inputChord = Chord.Parse("Ctrl+Shift+T");

        // -- operator input (human -> agent) ----------------------------------------------------------
        private static readonly Inbox OperatorInbox = new Inbox();
        private const string InputControl = "BTBridge.OperatorInput";
        private const float InputHeight = 30f;
        private string inputText = "";
        private bool focusPending;
        private bool? savedDynamic;
        private bool? savedStatic;
        private GUIStyle inputStyle;
        private GUIStyle inputLabelStyle;

        /// <summary>True while the operator is typing; game input is suspended (see InputPatches).</summary>
        public static bool Typing { get; private set; }
        private bool settingsOpen;
        private bool historyOpen;
        private Vector2 historyScroll;
        private Rect settingsRect = new Rect(60, 160, 300, 210);
        private GUIStyle textStyle;
        private readonly Dictionary<Channel, Color> colors = new Dictionary<Channel, Color>
        {
            [Channel.Commentary] = new Color(0.92f, 0.92f, 0.92f),
            [Channel.Decision] = new Color(0.45f, 0.85f, 1f),
            [Channel.Warning] = new Color(1f, 0.75f, 0.3f),
            [Channel.System] = new Color(0.65f, 0.65f, 0.65f),
            [Channel.Operator] = new Color(0.6f, 1f, 0.6f),
        };

        public static void Create(string modDir)
        {
            if (instance != null)
            {
                return;
            }
            logPath = Path.Combine(modDir ?? ".", "overlay_log.jsonl");
            LoadPrefs();
            var go = new GameObject("BTBridge.ChatOverlay");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ChatOverlay>();
        }

        // -- posting ------------------------------------------------------------------------------

        /// <summary>From the agent (bridge route). Rate-limited; speaker and hold decided here.</summary>
        public static object Say(string type, string text)
        {
            Channel channel;
            string clean;
            try
            {
                channel = MessageText.ParseChannel(type);
                clean = MessageText.Clean(text);
            }
            catch (RuleException e)
            {
                throw new BridgeException(400, e.Message);
            }
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                if (!AgentRate.TryTake(now))
                {
                    throw new BridgeException(429, "overlay rate limit (1 message/second, bursts of 5)");
                }
                string speaker = Speaker(out string enemyDecision);
                var m = Feed.Post(channel, speaker, clean, now, enemyDecision);
                Append(m);
                return new
                {
                    posted = true,
                    id = m.Id,
                    speaker,
                    channel_visible = Feed.Enabled[channel],
                    held_until_action = m.Held,
                    text = clean,
                };
            }
        }

        /// <summary>From the mod itself (system channel); not rate-limited.</summary>
        public static void System(string text)
        {
            try
            {
                lock (Sync)
                {
                    Append(Feed.Post(Channel.System, "BTBridge", MessageText.Clean(text), DateTime.UtcNow, null));
                }
            }
            catch (Exception e)
            {
                Log.Warn("overlay system message dropped: " + e.Message);
            }
        }

        /// <summary>A combat decision closed: reveal any enemy plans held for it.</summary>
        public static void OnDecisionClosed(string decisionId)
        {
            lock (Sync)
            {
                Feed.Release(decisionId, DateTime.UtcNow);
            }
        }

        /// <summary>Who is talking, decided by the mod from the open decision / game state.</summary>
        private static string Speaker(out string enemyDecision)
        {
            enemyDecision = null;
            var d = DecisionBroker.Current;
            if (d != null)
            {
                if (d.Side == "enemy")
                {
                    enemyDecision = d.Id;
                    return "OPFOR";
                }
                return d.Side == "player" ? "YOUR LANCE" : "AGENT";
            }
            var game = UnityGameInstance.BattleTechGame;
            if (game?.Combat != null)
            {
                bool player = CombatControl.ActivePlayer == PlayerControl.Agent;
                bool enemy = CombatControl.ActiveEnemy == EnemyControl.Agent;
                if (enemy && !player)
                {
                    return "OPFOR";
                }
                if (player && !enemy)
                {
                    return "YOUR LANCE";
                }
                return "AGENT";
            }
            return game?.Simulation != null ? "CAMPAIGN" : "AGENT";
        }

        private static void Append(OverlayMessage m)
        {
            Log.Info($"[OVERLAY] {m.Channel} {m.Speaker}: {m.Text.Replace('\n', ' ')}");
            if (logPath == null)
            {
                return;
            }
            try
            {
                File.AppendAllText(logPath, JsonConvert.SerializeObject(new
                {
                    utc = m.Posted.ToString("o"),
                    id = m.Id,
                    channel = m.Channel.ToString(),
                    speaker = m.Speaker,
                    text = m.Text,
                    held_for = m.HeldFor,
                }) + Environment.NewLine);
            }
            catch
            {
                // The overlay log is best-effort.
            }
        }

        public static object History()
        {
            lock (Sync)
            {
                return new
                {
                    channels = Feed.Enabled.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value),
                    reveal_enemy_decisions_after_action = Feed.RevealEnemyDecisionsAfterAction,
                    messages = Feed.History.Select(m => new
                    {
                        id = m.Id,
                        channel = m.Channel.ToString().ToLowerInvariant(),
                        speaker = m.Speaker,
                        text = m.Text,
                        utc = m.Posted.ToString("o"),
                        held = m.Held,
                    }).ToList(),
                    note = "channel visibility is the operator's choice (in-game Ctrl+Shift+O); it is not settable here",
                };
            }
        }

        // -- operator preferences (in-game only) ------------------------------------------------------

        private static void LoadPrefs()
        {
            foreach (var ch in Feed.Enabled.Keys.ToList())
            {
                string key = PrefPrefix + ch;
                if (PlayerPrefs.HasKey(key))
                {
                    Feed.Enabled[ch] = PlayerPrefs.GetInt(key) == 1;
                }
            }
            if (PlayerPrefs.HasKey(PrefPrefix + "RevealAfterAction"))
            {
                Feed.RevealEnemyDecisionsAfterAction = PlayerPrefs.GetInt(PrefPrefix + "RevealAfterAction") == 1;
            }
        }

        private static void SavePrefs()
        {
            foreach (var kv in Feed.Enabled)
            {
                PlayerPrefs.SetInt(PrefPrefix + kv.Key, kv.Value ? 1 : 0);
            }
            PlayerPrefs.SetInt(PrefPrefix + "RevealAfterAction", Feed.RevealEnemyDecisionsAfterAction ? 1 : 0);
            PlayerPrefs.Save();
        }

        // -- drawing ------------------------------------------------------------------------------

        private void Update()
        {
            try
            {
                if (Typing)
                {
                    return;
                }
                if (inputChord.PressedThisFrame())
                {
                    OpenInput();
                }
                else if (settingsChord.PressedThisFrame())
                {
                    settingsOpen = !settingsOpen;
                }
                else if (historyChord.PressedThisFrame())
                {
                    historyOpen = !historyOpen;
                }
            }
            catch (Exception e)
            {
                Log.Error("overlay update failed", e);
            }
        }

        /// <summary>
        /// Open the bottom input bar. All bound game actions (BTInput's InControl action sets) are
        /// disabled while it is open, and the DebugConsole visibility gate makes the combat key
        /// handler stand down, exactly as it does for HBS's own console.
        /// </summary>
        private void OpenInput()
        {
            Typing = true;
            inputText = "";
            focusPending = true;
            try
            {
                var input = BTInput.Instance;
                savedDynamic = input.DynamicActions?.Enabled;
                savedStatic = input.StaticActions?.Enabled;
                if (input.DynamicActions != null)
                {
                    input.DynamicActions.Enabled = false;
                }
                if (input.StaticActions != null)
                {
                    input.StaticActions.Enabled = false;
                }
            }
            catch (Exception e)
            {
                Log.Warn("could not suspend game input: " + e.Message);
            }
        }

        private void CloseInput()
        {
            if (!Typing)
            {
                return;
            }
            Typing = false;
            focusPending = false;
            try
            {
                var input = BTInput.Instance;
                if (input.DynamicActions != null && savedDynamic.HasValue)
                {
                    input.DynamicActions.Enabled = savedDynamic.Value;
                }
                if (input.StaticActions != null && savedStatic.HasValue)
                {
                    input.StaticActions.Enabled = savedStatic.Value;
                }
            }
            catch (Exception e)
            {
                Log.Warn("could not restore game input: " + e.Message);
            }
            savedDynamic = savedStatic = null;
        }

        private void OnDisable() => CloseInput();

        private void SendInput()
        {
            string text = inputText;
            CloseInput();
            try
            {
                lock (Sync)
                {
                    var m = OperatorInbox.Add(text, DateTime.UtcNow);
                    var echo = Feed.Post(Channel.Operator, "YOU", MessageText.Clean(m.Text), DateTime.UtcNow, null);
                    Append(echo);
                }
            }
            catch (RuleException)
            {
                // Empty input: nothing to send.
            }
        }

        private void DrawInputBar()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    SendInput();
                    return;
                }
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CloseInput();
                    return;
                }
            }
            float y = Screen.height - InputHeight;
            GUI.Box(new Rect(0, y, Screen.width, InputHeight), GUIContent.none);
            GUI.Label(new Rect(8, y + 5, 150, InputHeight - 8), "To agent (Enter / Esc):", inputLabelStyle);
            GUI.SetNextControlName(InputControl);
            inputText = GUI.TextField(new Rect(164, y + 4, Screen.width - 172, InputHeight - 8), inputText, Inbox.MaxChars, inputStyle);
            if (focusPending)
            {
                GUI.FocusControl(InputControl);
                focusPending = false;
            }
        }

        // -- inbox for the agent -----------------------------------------------------------------------

        /// <summary>Unread operator messages (peek; acknowledged explicitly by the agent).</summary>
        public static List<object> UnreadForAgent()
        {
            lock (Sync)
            {
                return OperatorInbox.Unread().Select(m => (object)new { id = m.Id, text = m.Text, utc = m.Sent.ToString("o") }).ToList();
            }
        }

        public static object InboxView()
        {
            lock (Sync)
            {
                return new
                {
                    unread = OperatorInbox.Unread().Select(m => new { id = m.Id, text = m.Text, utc = m.Sent.ToString("o") }).ToList(),
                    recent = OperatorInbox.All.Reverse().Take(10).Select(m => new { id = m.Id, text = m.Text, utc = m.Sent.ToString("o"), acked = m.Acked }).ToList(),
                    note = "messages typed by the operator in-game (Ctrl+Shift+T); acknowledge with up_to_id once handled",
                };
            }
        }

        public static object Ack(int upToId)
        {
            lock (Sync)
            {
                return new { acknowledged = OperatorInbox.AckUpTo(upToId), unread = OperatorInbox.Unread().Count };
            }
        }

        private void EnsureStyles()
        {
            if (textStyle != null)
            {
                return;
            }
            inputStyle = new GUIStyle(GUI.skin.textField) { richText = false, fontSize = 14, alignment = TextAnchor.MiddleLeft };
            inputLabelStyle = new GUIStyle(GUI.skin.label) { richText = false, fontSize = 12, alignment = TextAnchor.MiddleLeft };
            textStyle = new GUIStyle(GUI.skin.box)
            {
                richText = false,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                padding = new RectOffset(8, 8, 5, 5),
            };
        }

        private static float PanelWidth => Mathf.Floor(Screen.width / 3f);

        private string Label(OverlayMessage m) => $"[{m.Speaker}] {m.Text}";

        private void OnGUI()
        {
            EnsureStyles();
            List<OverlayMessage> shown;
            lock (Sync)
            {
                shown = Feed.Visible(DateTime.UtcNow);
            }
            float width = PanelWidth;
            float x = Screen.width - width - 12;
            float y = Screen.height * 0.38f;
            foreach (var m in shown)
            {
                var content = new GUIContent(Label(m));
                float h = textStyle.CalcHeight(content, width);
                var old = GUI.contentColor;
                GUI.contentColor = colors[m.Channel];
                GUI.Box(new Rect(x, y, width, h), content, textStyle);
                GUI.contentColor = old;
                y += h + 4;
            }
            if (historyOpen)
            {
                DrawHistory(width);
            }
            if (settingsOpen)
            {
                settingsRect = GUI.Window(0x8772, settingsRect, DrawSettings, "BTBridge chat channels");
            }
            if (Typing)
            {
                DrawInputBar();
            }
        }

        private void DrawHistory(float width)
        {
            List<OverlayMessage> items;
            lock (Sync)
            {
                items = Feed.HistoryView();
            }
            float height = Screen.height * 0.5f;
            var area = new Rect(Screen.width - width - 12, Screen.height * 0.12f, width, height);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"History ({items.Count}) · Ctrl+Shift+H to close");
            historyScroll = GUILayout.BeginScrollView(historyScroll);
            foreach (var m in items)
            {
                var old = GUI.contentColor;
                GUI.contentColor = colors[m.Channel];
                GUILayout.Label($"{m.ShownAt.ToLocalTime():HH:mm} {Label(m)}", textStyle, GUILayout.Width(width - 30));
                GUI.contentColor = old;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawSettings(int id)
        {
            bool changed = false;
            lock (Sync)
            {
                foreach (var ch in Feed.Enabled.Keys.ToList())
                {
                    bool on = GUILayout.Toggle(Feed.Enabled[ch], " " + ch);
                    if (on != Feed.Enabled[ch])
                    {
                        Feed.Enabled[ch] = on;
                        changed = true;
                    }
                }
                GUILayout.Space(6);
                bool reveal = GUILayout.Toggle(Feed.RevealEnemyDecisionsAfterAction, " Reveal enemy decisions only after they act");
                if (reveal != Feed.RevealEnemyDecisionsAfterAction)
                {
                    Feed.RevealEnemyDecisionsAfterAction = reveal;
                    changed = true;
                }
            }
            if (changed)
            {
                SavePrefs();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Close (Ctrl+Shift+O)"))
            {
                settingsOpen = false;
            }
            GUI.DragWindow();
        }
    }
}
