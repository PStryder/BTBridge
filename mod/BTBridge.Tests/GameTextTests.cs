using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class GameTextTests
    {
        [Theory]
        // Strings captured from the first career test (Minor Infraction event).
        [InlineData("[[SCN_MW,Crazy Quilt]] starts toward you", "Crazy Quilt starts toward you")]
        [InlineData("but [[TGT_MW,Snowbank]] is slacking", "but Snowbank is slacking")]
        [InlineData("• [[RES_MW,Snowbank]] has lost the following tags: [[TDSF[pilot_rebellious], Rebellious]]\r\n",
            "• Snowbank has lost the following tags: Rebellious")]
        [InlineData("we're supposed to <i>share</i> garbage detail", "we're supposed to share garbage detail")]
        [InlineData("<color=#F79B26FF>Warning</color> text", "Warning text")]
        public void Strips_link_markup_and_style_tags(string raw, string plain)
        {
            Assert.Equal(plain, GameText.Plain(raw));
        }

        [Theory]
        [InlineData("A plain line, with a comma.")]
        [InlineData("Range [5, 10] stays")]
        [InlineData("1 < 2 and <3 hearts")]
        public void Leaves_ordinary_text_alone(string s)
        {
            Assert.Equal(s, GameText.Plain(s));
        }
    }
}
