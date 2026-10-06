using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class TravelRouteTests
    {
        [Fact]
        public void The_requested_route_matches()
        {
            Assert.True(TravelRoute.Matches("herotitus", "kimi", "herotitus", "kimi"));
        }

        [Fact]
        public void A_previous_previews_route_is_rejected()
        {
            // The review's reproduction: previewed A, asked for B, ticked before B's route finished.
            Assert.False(TravelRoute.Matches("herotitus", "systemA", "herotitus", "systemB"));
        }

        [Fact]
        public void A_route_from_another_origin_is_rejected()
        {
            Assert.False(TravelRoute.Matches("lyreton", "kimi", "herotitus", "kimi"));
        }

        [Fact]
        public void No_target_never_matches()
        {
            Assert.False(TravelRoute.Matches(null, null, null, null));
        }
    }
}
