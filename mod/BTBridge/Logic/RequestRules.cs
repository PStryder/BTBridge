namespace BTBridge.Logic
{
    /// <summary>
    /// Who may call the bridge. It listens on loopback only, but a web page open in the player's
    /// browser can still send it requests (a simple POST needs no CORS preflight), and DNS
    /// rebinding can point a hostile name at 127.0.0.1. Browsers mark their requests with Origin
    /// or Sec-Fetch-Site; the MCP server's HTTP client sends neither. (Review follow-up.)
    /// </summary>
    public static class RequestRules
    {
        /// <returns>null when allowed, otherwise why not.</returns>
        public static string Problem(string origin, string secFetchSite, string host, int port)
        {
            if (!string.IsNullOrEmpty(origin) || !string.IsNullOrEmpty(secFetchSite))
            {
                return "requests from web browsers are refused";
            }
            if (host != "127.0.0.1:" + port && host != "localhost:" + port)
            {
                return $"unexpected Host '{host}'";
            }
            return null;
        }
    }
}
