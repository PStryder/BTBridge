using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BattleTech;
using BattleTech.StringInterpolation;
using BattleTech.UI;
using BTBridge.Bridge;
using Harmony;
using isogame;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTBridge.Sim
{
    /// <summary>
    /// Story text for the agent: cinematic subtitles (StreamingAssets/Video/Subtitles/*.srt, no speaker
    /// names in the files) and the scripted campaign conversations (DataManager.SimGameConversations,
    /// isogame.Conversation from ShadowrunDTO, with speakers). Spoiler-gated: by default only material
    /// already seen (recorded when a video plays or a conversation starts, kept in story_seen.json in
    /// the mod folder, per install not per career). Plus cinematic status and skip.
    /// </summary>
    public static class Story
    {
        private static readonly object SeenLock = new object();
        private static HashSet<string> seen;

        private static string SeenPath => Main.ModDir == null ? null : Path.Combine(Main.ModDir, "story_seen.json");

        private static HashSet<string> Seen()
        {
            lock (SeenLock)
            {
                if (seen == null)
                {
                    seen = new HashSet<string>();
                    try
                    {
                        if (SeenPath != null && File.Exists(SeenPath))
                        {
                            seen.UnionWith(JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(SeenPath)) ?? new List<string>());
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn("story_seen.json unreadable: " + e.Message);
                    }
                }
                return seen;
            }
        }

        public static void MarkSeen(string key)
        {
            lock (SeenLock)
            {
                if (Seen().Add(key) && SeenPath != null)
                {
                    try
                    {
                        File.WriteAllText(SeenPath, JsonConvert.SerializeObject(seen.OrderBy(s => s).ToList(), Formatting.Indented));
                    }
                    catch (Exception e)
                    {
                        Log.Warn("story_seen.json not written: " + e.Message);
                    }
                }
            }
        }

        private static bool IsSeen(string key)
        {
            lock (SeenLock)
            {
                return Seen().Contains(key);
            }
        }

        private static string VideoKey(string video) => "video:" + StripExt(video);

        private static string StripExt(string video) =>
            Path.GetFileName(video ?? "").Split(new[] { '.' }, 2)[0];

        // ---- cinematics -------------------------------------------------------------------------

        private sealed class Cine
        {
            public string video;
            public int? milestone;
            public string milestone_id;
        }

        private static List<Cine> Cinematics()
        {
            var dir = Path.Combine(UnityEngine.Application.streamingAssetsPath, "data/milestones");
            var list = new List<Cine>();
            if (!Directory.Exists(dir))
            {
                return list;
            }
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var j = JObject.Parse(File.ReadAllText(file));
                    var video = j.SelectTokens("$.Results[*].Actions[?(@.Type == 'System_PlayVideo')].value")
                        .Select(t => (string)t).FirstOrDefault();
                    if (video == null)
                    {
                        continue;
                    }
                    var m = j.SelectTokens("$.Requirements[*].RequirementComparisons[?(@.obj == 'NextStoryMilestone')].val")
                        .Select(t => (int?)t).FirstOrDefault();
                    list.Add(new Cine { video = StripExt(video), milestone = m, milestone_id = (string)j.SelectToken("Description.Id") });
                }
                catch
                {
                    // Not a milestone we can read; skip.
                }
            }
            return list.OrderBy(c => c.milestone ?? int.MaxValue).ToList();
        }

        private static string SubtitlePath(string video) =>
            Path.Combine(UnityEngine.Application.streamingAssetsPath, "Video/Subtitles/" + StripExt(video) + "-English.srt");

        private sealed class SubLine
        {
            public string start;
            public string end;
            public string text;
        }

        private static List<SubLine> ParseSrt(string path)
        {
            var lines = new List<SubLine>();
            var blocks = Regex.Split(File.ReadAllText(path).Replace("\r\n", "\n").Trim('﻿', '\n'), "\n\\s*\n");
            foreach (var b in blocks)
            {
                var rows = b.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
                int arrow = rows.FindIndex(r => r.Contains("-->"));
                if (arrow < 0)
                {
                    continue;
                }
                var t = rows[arrow].Split(new[] { "-->" }, StringSplitOptions.None);
                lines.Add(new SubLine
                {
                    start = t[0].Trim(),
                    end = t[1].Trim(),
                    text = string.Join(" ", rows.Skip(arrow + 1)),
                });
            }
            return lines;
        }

        public static object CinematicList(bool spoilers)
        {
            var all = Cinematics();
            return new
            {
                note = "subtitle files carry no speaker names; cinematics without subtitles have none in the game files",
                cinematics = all
                    .Where(c => spoilers || IsSeen(VideoKey(c.video)))
                    .Select(c => new
                    {
                        c.video,
                        c.milestone,
                        seen = IsSeen(VideoKey(c.video)),
                        has_subtitles = File.Exists(SubtitlePath(c.video)),
                    }).ToList(),
                hidden_unseen = spoilers ? 0 : all.Count(c => !IsSeen(VideoKey(c.video))),
            };
        }

        public static object CinematicText(string video, bool spoilers)
        {
            if (string.IsNullOrEmpty(video))
            {
                throw new BridgeException(400, "video is required (e.g. 1B-betrayal)");
            }
            if (!spoilers && !IsSeen(VideoKey(video)))
            {
                throw new BridgeException(403, $"{StripExt(video)} has not played yet in this install; pass spoilers=true to read it anyway");
            }
            var path = SubtitlePath(video);
            if (!File.Exists(path))
            {
                throw new BridgeException(404, $"no English subtitles for {StripExt(video)}");
            }
            var lines = ParseSrt(path);
            return new
            {
                video = StripExt(video),
                lines,
                text = string.Join("\n", lines.Select(l => l.text)),
            };
        }

        // ---- the cinematic now on screen ----------------------------------------------------------

        private static SGVideoPlayer Player() => UnityEngine.Object.FindObjectOfType<SGVideoPlayer>();

        public static bool VideoPlaying(SimGameState sim) => sim != null && sim.VideoPlayerActive;

        public static object VideoStatus()
        {
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var p = Player();
            string path = p == null ? null : (Reflect.Get(p, "BinkMediaPlayer") as UnityEngine.Object) == null ? null
                : Reflect.Get(Reflect.Get(p, "BinkMediaPlayer"), "VideoPath") as string;
            return new
            {
                playing = VideoPlaying(sim),
                status = p?.Status.ToString(),
                video = path == null ? null : StripExt(path),
                answer = VideoPlaying(sim) ? "POST /sim/video/skip (or read GET /story/cinematic?video=<name> for its subtitles)" : null,
            };
        }

        public static object Skip()
        {
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            var p = Player();
            if (!VideoPlaying(sim) || p == null)
            {
                throw new BridgeException(409, "no cinematic is playing");
            }
            if (p.Status != SGVideoPlayer.VideoStatus.PLAYING)
            {
                throw new BridgeException(409, $"the cinematic is {p.Status}; retry once it is PLAYING");
            }
            // What the Escape key does: fade out, close, then the game's own completion runs milestones.
            p.StopVideo();
            Log.Info("cinematic skipped");
            return new { skipped = true };
        }

        // ---- scripted conversations --------------------------------------------------------------

        private static string ConvoKey(string id) => "convo:" + id;

        private static string Speaker(SimGameState sim, Conversation c, ConversationNode n)
        {
            try
            {
                CastDef cast = null;
                string raw = null;
                if (n.sourceInSceneRef != null)
                {
                    raw = n.sourceInSceneRef.id;
                    cast = sim.GetCastDef(raw, addPrefix: true);
                }
                else if (!string.IsNullOrEmpty(n.speaker_override_id))
                {
                    raw = n.speaker_override_id;
                    cast = sim.GetCastDefFromSpeakerID(raw);
                }
                else if (!string.IsNullOrEmpty(c.default_speaker_id))
                {
                    raw = c.default_speaker_id;
                    cast = sim.GetCastDefFromSpeakerID(raw);
                }
                if (cast == null)
                {
                    return raw;
                }
                var name = (cast.FirstName() + " " + cast.LastName()).Trim();
                var callsign = cast.Callsign();
                return string.IsNullOrEmpty(name) ? callsign : string.IsNullOrEmpty(callsign) || callsign == name ? name : $"{name} ({callsign})";
            }
            catch
            {
                return null;
            }
        }

        private static string Interp(SimGameState sim, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            try
            {
                return Interpolator.Interpolate(text, sim.Context, true);
            }
            catch
            {
                return text;
            }
        }

        private static IEnumerable<KeyValuePair<string, Conversation>> AllConversations(SimGameState sim)
        {
            var store = sim.DataManager.SimGameConversations;
            foreach (var kv in store)
            {
                yield return kv;
            }
        }

        public static object ConversationList(SimGameState sim, bool spoilers)
        {
            var all = AllConversations(sim).ToList();
            return new
            {
                conversations = all
                    .Where(kv => spoilers || IsSeen(ConvoKey(kv.Key)))
                    .OrderBy(kv => kv.Value.ui_name)
                    .Select(kv => new
                    {
                        id = kv.Key,
                        name = kv.Value.ui_name,
                        lines = kv.Value.nodes?.Count ?? 0,
                        seen = IsSeen(ConvoKey(kv.Key)),
                    }).ToList(),
                hidden_unseen = spoilers ? 0 : all.Count(kv => !IsSeen(ConvoKey(kv.Key))),
                loaded = all.Count,
            };
        }

        public static object ConversationScript(SimGameState sim, string id, bool spoilers)
        {
            var kv = AllConversations(sim).FirstOrDefault(x => x.Key == id || x.Value.ui_name == id);
            if (kv.Value == null)
            {
                throw new BridgeException(404, $"no loaded conversation '{id}'");
            }
            if (!spoilers && !IsSeen(ConvoKey(kv.Key)))
            {
                throw new BridgeException(403, $"'{kv.Value.ui_name}' has not been played in this install; pass spoilers=true to read it anyway");
            }
            var c = kv.Value;
            return new
            {
                id = kv.Key,
                name = c.ui_name,
                note = "a branching script: each node lists its responses and the node index each leads to; the roots are where it starts",
                roots = c.roots?.Select(r => r.nextNodeIndex).ToList(),
                nodes = (c.nodes ?? new List<ConversationNode>()).Select(n => new
                {
                    n.index,
                    speaker = Speaker(sim, c, n),
                    text = Interp(sim, n.text),
                    responses = n.branches?
                        .Select(b => new { text = Interp(sim, b.responseText), next = b.nextNodeIndex })
                        .ToList(),
                }).ToList(),
            };
        }
    }

    [HarmonyPatch(typeof(SGVideoPlayer), "PlayVideo")]
    public static class RecordVideoSeen
    {
        public static void Postfix(string video)
        {
            try
            {
                Story.MarkSeen("video:" + Path.GetFileName(video ?? "").Split(new[] { '.' }, 2)[0]);
            }
            catch (Exception e)
            {
                Log.Warn("video seen not recorded: " + e.Message);
            }
        }
    }

    // Two overloads; the ConversationEntry one delegates to this one, so this catches both.
    [HarmonyPatch(typeof(SimGameConversationManager), "StartConversation", new[]
    {
        typeof(Conversation), typeof(string), typeof(string), typeof(CastDef), typeof(bool),
        typeof(DropshipMenuType), typeof(string),
    })]
    public static class RecordConversationSeen
    {
        public static void Postfix(SimGameConversationManager __instance, Conversation convoDef)
        {
            try
            {
                var sim = UnityGameInstance.BattleTechGame?.Simulation;
                var key = sim?.DataManager.SimGameConversations
                    .FirstOrDefault(kv => ReferenceEquals(kv.Value, convoDef)).Key;
                if (key != null)
                {
                    Story.MarkSeen("convo:" + key);
                }
            }
            catch (Exception e)
            {
                Log.Warn("conversation seen not recorded: " + e.Message);
            }
        }
    }
}
