using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class RequestRulesTests
    {
        [Fact]
        public void The_mcp_client_is_allowed()
        {
            Assert.Null(RequestRules.Problem(null, null, "127.0.0.1:8787", 8787));
            Assert.Null(RequestRules.Problem(null, null, "localhost:8787", 8787));
        }

        [Fact]
        public void A_browser_page_is_refused()
        {
            Assert.NotNull(RequestRules.Problem("https://evil.example", null, "127.0.0.1:8787", 8787));
            Assert.NotNull(RequestRules.Problem("null", null, "127.0.0.1:8787", 8787));
            Assert.NotNull(RequestRules.Problem(null, "cross-site", "127.0.0.1:8787", 8787));
        }

        [Fact]
        public void A_rebound_hostname_is_refused()
        {
            Assert.NotNull(RequestRules.Problem(null, null, "evil.example:8787", 8787));
            Assert.NotNull(RequestRules.Problem(null, null, "127.0.0.1:9999", 8787));
            Assert.NotNull(RequestRules.Problem(null, null, null, 8787));
        }
    }
}
