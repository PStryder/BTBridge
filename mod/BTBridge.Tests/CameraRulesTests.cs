using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class CameraRulesTests
    {
        [Fact]
        public void Follows_own_units_even_out_of_sight()
        {
            Assert.True(CameraRules.ShouldFollow(enabled: true, friendlyToScreen: true, fullyVisibleToScreen: false));
        }

        [Fact]
        public void Never_reveals_an_unseen_enemy()
        {
            // The agent commanding the OpFor must not pan the human's camera onto hidden units.
            Assert.False(CameraRules.ShouldFollow(enabled: true, friendlyToScreen: false, fullyVisibleToScreen: false));
        }

        [Fact]
        public void Follows_a_visible_enemy()
        {
            Assert.True(CameraRules.ShouldFollow(enabled: true, friendlyToScreen: false, fullyVisibleToScreen: true));
        }

        [Fact]
        public void Toggle_off_stops_everything()
        {
            Assert.False(CameraRules.ShouldFollow(enabled: false, friendlyToScreen: true, fullyVisibleToScreen: true));
        }
    }
}
