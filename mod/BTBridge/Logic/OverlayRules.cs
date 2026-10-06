using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BTBridge.Logic
{
    // In-game chat overlay: pure rules (no Unity), so BTBridge.Tests can exercise them.

    public enum Channel
    {
        Commentary,
        Decision,
        Warning,
        System,
        /// <summary>The operator's own typed messages (echo); not toggleable, always shown.</summary>
        Operator,
    }

    public sealed class OverlayMessage
    {
        public int Id;
        public Channel Channel;
        public string Speaker;
        public string Text;
        public DateTime Posted;
        /// <summary>Held until this decision closes (enemy plans revealed after the action).</summary>
        public string HeldFor;
        public DateTime? Released;

        public bool Held => HeldFor != null;
        /// <summary>When the message became visible (posting time, or release time if it was held).</summary>
        public DateTime ShownAt => Released ?? Posted;
    }

    public static class MessageText
    {
        public const int MaxChars = 280;
        public const int MaxLines = 3;

        /// <summary>
        /// Plain text only: control characters removed (newlines kept, at most MaxLines),
        /// trimmed, capped at MaxChars with an ellipsis. Markup is not interpreted downstream
        /// (richText is off), so nothing is escaped here beyond control characters.
        /// </summary>
        public static string Clean(string text)
        {
            if (text == null)
            {
                throw new RuleException("text is required");
            }
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                if (c == '\n' || c == '\t' || !char.IsControl(c))
                {
                    sb.Append(c == '\t' ? ' ' : c);
                }
            }
            var lines = sb.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(MaxLines);
            string clean = string.Join("\n", lines.ToArray());
            if (clean.Length == 0)
            {
                throw new RuleException("text is empty");
            }
            if (clean.Length > MaxChars)
            {
                clean = clean.Substring(0, MaxChars - 1).TrimEnd() + "…";
            }
            return clean;
        }

        /// <summary>Operator input: one line, control characters removed, capped at Inbox.MaxChars.</summary>
        public static string CleanForInbox(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
            {
                if (!char.IsControl(c))
                {
                    sb.Append(c);
                }
                else if (c == '\t' || c == '\n' || c == '\r')
                {
                    sb.Append(' ');
                }
            }
            string clean = sb.ToString().Trim();
            if (clean.Length == 0)
            {
                throw new RuleException("message is empty");
            }
            return clean.Length > Inbox.MaxChars ? clean.Substring(0, Inbox.MaxChars) : clean;
        }

        public static Channel ParseChannel(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "commentary": return Channel.Commentary;
                case "decision": return Channel.Decision;
                case "warning": return Channel.Warning;
                case "system": return Channel.System;
                default: throw new RuleException("type must be commentary | decision | warning | system");
            }
        }
    }

    public sealed class InboxMessage
    {
        public int Id;
        public string Text;
        public DateTime Sent;
        public bool Acked;
    }

    /// <summary>
    /// Operator -> agent messages typed in the in-game box. Only the box writes here (there is no
    /// route to inject), so every entry is human-authored. Reads peek; the agent acknowledges
    /// explicitly, so polling loops that discard intermediate results never lose a message.
    /// </summary>
    public sealed class Inbox
    {
        public const int MaxChars = 500;
        public const int Capacity = 50;
        private readonly List<InboxMessage> messages = new List<InboxMessage>();
        private int next;

        public InboxMessage Add(string text, DateTime now)
        {
            string clean = MessageText.CleanForInbox(text);
            var m = new InboxMessage { Id = ++next, Text = clean, Sent = now };
            messages.Add(m);
            if (messages.Count > Capacity)
            {
                messages.RemoveAt(0);
            }
            return m;
        }

        public List<InboxMessage> Unread() => messages.Where(m => !m.Acked).ToList();

        /// <summary>Acknowledge everything up to and including id; returns how many were newly acked.</summary>
        public int AckUpTo(int id)
        {
            int n = 0;
            foreach (var m in messages.Where(m => !m.Acked && m.Id <= id))
            {
                m.Acked = true;
                n++;
            }
            return n;
        }

        public IReadOnlyList<InboxMessage> All => messages;
    }

    /// <summary>Token bucket: a short burst, then a steady rate.</summary>
    public sealed class RateLimit
    {
        private readonly double perSecond;
        private readonly double burst;
        private double tokens;
        private DateTime last;

        public RateLimit(double perSecond, int burst, DateTime start)
        {
            this.perSecond = perSecond;
            this.burst = burst;
            tokens = burst;
            last = start;
        }

        public bool TryTake(DateTime now)
        {
            tokens = Math.Min(burst, tokens + Math.Max(0, (now - last).TotalSeconds) * perSecond);
            last = now;
            if (tokens < 1)
            {
                return false;
            }
            tokens -= 1;
            return true;
        }
    }

    public sealed class FeedState
    {
        public const int HistorySize = 20;
        public const int MaxVisible = 5;
        public static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(12);

        private readonly List<OverlayMessage> history = new List<OverlayMessage>();
        private int next;

        /// <summary>Channel visibility; only commentary is on by default (operator decision).</summary>
        public readonly Dictionary<Channel, bool> Enabled = new Dictionary<Channel, bool>
        {
            [Channel.Commentary] = true,
            [Channel.Decision] = false,
            [Channel.Warning] = false,
            [Channel.System] = false,
        };

        /// <summary>Hold enemy decision messages until that decision has been carried out.</summary>
        public bool RevealEnemyDecisionsAfterAction = true;

        public IReadOnlyList<OverlayMessage> History => history;

        public OverlayMessage Post(Channel channel, string speaker, string cleanText, DateTime now, string enemyDecisionId)
        {
            var m = new OverlayMessage
            {
                Id = ++next,
                Channel = channel,
                Speaker = speaker,
                Text = cleanText,
                Posted = now,
                HeldFor = channel == Channel.Decision && RevealEnemyDecisionsAfterAction ? enemyDecisionId : null,
            };
            history.Add(m);
            if (history.Count > HistorySize)
            {
                history.RemoveAt(0);
            }
            return m;
        }

        /// <summary>A decision closed: its held messages become visible now.</summary>
        public int Release(string decisionId, DateTime now)
        {
            int n = 0;
            foreach (var m in history.Where(m => m.HeldFor != null && m.HeldFor == decisionId))
            {
                m.HeldFor = null;
                m.Released = now;
                n++;
            }
            return n;
        }

        private bool Shown(OverlayMessage m) => !m.Held && (!Enabled.TryGetValue(m.Channel, out bool on) || on);

        /// <summary>The rolling feed: recent, enabled, not held; newest last.</summary>
        public List<OverlayMessage> Visible(DateTime now) =>
            history.Where(m => Shown(m) && now - m.ShownAt < VisibleFor)
                .OrderBy(m => m.ShownAt).ThenBy(m => m.Id)
                .Reverse().Take(MaxVisible).Reverse().ToList();

        /// <summary>The history panel: everything enabled and not held, oldest first.</summary>
        public List<OverlayMessage> HistoryView() => history.Where(Shown).ToList();
    }
}
