using System;
using System.Linq;
using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class MessageTextTests
    {
        [Fact]
        public void KeepsPlainTextAndMarkupLiterally()
        {
            Assert.Equal("<b>Focus</b> the Atlas", MessageText.Clean("<b>Focus</b> the Atlas"));
        }

        [Fact]
        public void StripsControlCharacters()
        {
            Assert.Equal("ab c", MessageText.Clean("a\u0007b\tc\u001b"));
        }

        [Fact]
        public void CapsLines()
        {
            Assert.Equal("1\n2\n3", MessageText.Clean("1\n2\r\n3\n4\n5"));
        }

        [Fact]
        public void CapsLengthWithEllipsis()
        {
            var clean = MessageText.Clean(new string('x', 1400));
            Assert.Equal(MessageText.MaxChars, clean.Length);
            Assert.EndsWith("…", clean);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   \n\t ")]
        public void RejectsEmpty(string text)
        {
            Assert.Throws<RuleException>(() => MessageText.Clean(text));
        }

        [Fact]
        public void ParsesOnlyTheFourChannels()
        {
            Assert.Equal(Channel.Warning, MessageText.ParseChannel("WARNING"));
            Assert.Throws<RuleException>(() => MessageText.ParseChannel("shout"));
        }
    }

    public class RateLimitTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0);

        [Fact]
        public void BurstThenSteady()
        {
            var r = new RateLimit(1, 5, T0);
            for (int i = 0; i < 5; i++)
            {
                Assert.True(r.TryTake(T0));
            }
            Assert.False(r.TryTake(T0));
            Assert.True(r.TryTake(T0.AddSeconds(1)));
            Assert.False(r.TryTake(T0.AddSeconds(1.5)));
        }
    }

    public class FeedStateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0);

        [Fact]
        public void OnlyCommentaryIsOnByDefault()
        {
            var f = new FeedState();
            Assert.True(f.Enabled[Channel.Commentary]);
            Assert.False(f.Enabled[Channel.Decision]);
            Assert.False(f.Enabled[Channel.Warning]);
            Assert.False(f.Enabled[Channel.System]);
        }

        [Fact]
        public void DisabledChannelsAreKeptButNotShownUntilEnabled()
        {
            var f = new FeedState();
            f.Post(Channel.System, "BTBridge", "armed", T0, null);
            Assert.Empty(f.Visible(T0));
            Assert.Empty(f.HistoryView());
            f.Enabled[Channel.System] = true;
            Assert.Single(f.Visible(T0));
            Assert.Single(f.HistoryView());
        }

        [Fact]
        public void FeedRollsAndFades()
        {
            var f = new FeedState();
            for (int i = 0; i < 8; i++)
            {
                f.Post(Channel.Commentary, "AGENT", "m" + i, T0.AddSeconds(i), null);
            }
            var shown = f.Visible(T0.AddSeconds(8));
            Assert.Equal(new[] { "m3", "m4", "m5", "m6", "m7" }, shown.Select(m => m.Text));
            Assert.Empty(f.Visible(T0.AddSeconds(30)));
        }

        [Fact]
        public void HistoryKeepsTheLastTwenty()
        {
            var f = new FeedState();
            for (int i = 0; i < 25; i++)
            {
                f.Post(Channel.Commentary, "AGENT", "m" + i, T0, null);
            }
            Assert.Equal(FeedState.HistorySize, f.HistoryView().Count);
            Assert.Equal("m5", f.HistoryView().First().Text);
        }

        [Fact]
        public void EnemyDecisionsAreHeldUntilTheDecisionCloses()
        {
            var f = new FeedState();
            f.Enabled[Channel.Decision] = true;
            f.Post(Channel.Decision, "OPFOR", "flank the Centurion", T0, "d7");
            Assert.Empty(f.Visible(T0));
            Assert.Empty(f.HistoryView());
            Assert.Equal(1, f.Release("d7", T0.AddSeconds(20)));
            var shown = f.Visible(T0.AddSeconds(21));
            Assert.Single(shown);
            Assert.Equal("flank the Centurion", shown[0].Text);
        }

        [Fact]
        public void HoldingCanBeTurnedOff()
        {
            var f = new FeedState { RevealEnemyDecisionsAfterAction = false };
            f.Enabled[Channel.Decision] = true;
            f.Post(Channel.Decision, "OPFOR", "flank", T0, "d7");
            Assert.Single(f.Visible(T0));
        }

        [Fact]
        public void OnlyDecisionsAreHeld()
        {
            var f = new FeedState();
            f.Post(Channel.Commentary, "OPFOR", "you'll regret that", T0, "d7");
            Assert.Single(f.Visible(T0));
        }
    }
}
