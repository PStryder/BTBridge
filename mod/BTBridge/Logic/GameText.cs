using System.Text.RegularExpressions;

namespace BTBridge.Logic
{
    /// <summary>
    /// Plain text from the game's display strings. Interpolated event text keeps the UI's inline
    /// link markup ([[SCN_MW,Crazy Quilt]], [[TDSF[pilot_honest], Honest]]) and TextMeshPro style
    /// tags; an agent wants the visible words.
    /// </summary>
    public static class GameText
    {
        // [[KIND,Shown]] where KIND may itself contain one bracketed part: TDSF[pilot_honest].
        private static readonly Regex Link = new Regex(@"\[\[(?:[^\[\],]|\[[^\[\]]*\])*,\s*([^\[\]]*?)\s*\]\]", RegexOptions.Compiled);
        private static readonly Regex Style = new Regex(@"</?(?:i|b|u|s|color|size|sup|sub|mark|smallcaps|uppercase|lowercase)(?:=[^>]*)?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            s = Link.Replace(s, "$1");
            s = Style.Replace(s, "");
            return s.Replace("\r\n", "\n").Trim();
        }
    }
}
