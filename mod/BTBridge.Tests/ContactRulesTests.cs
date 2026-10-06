using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class ContactRulesTests
    {
        private const int None = 0, Blob = 2, Blip0 = 4, Blip1Type = 5, Ghost = 6, Blip4 = 8, Los = 9;

        [Fact]
        public void A_sensor_blip_reveals_position_but_not_identity()
        {
            // The review's finding: blips carried the real name and current facing.
            foreach (var rank in new[] { Blob, Blip0, Blip1Type, Ghost, Blip4 })
            {
                var v = ContactRules.For(friendly: false, visibilityRank: rank);
                Assert.True(v.Position, $"rank {rank}");
                Assert.False(v.Identity, $"rank {rank}");
            }
        }

        [Fact]
        public void Only_type_level_sensor_returns_reveal_the_kind()
        {
            Assert.False(ContactRules.For(false, Blip0).Kind);
            Assert.True(ContactRules.For(false, Blip1Type).Kind);
            Assert.False(ContactRules.For(false, Ghost).Kind);
            Assert.True(ContactRules.For(false, Blip4).Kind);
        }

        [Fact]
        public void An_undetected_enemy_is_not_listed()
        {
            Assert.False(ContactRules.For(false, None).Listed);
        }

        [Fact]
        public void Full_sight_and_friendly_units_reveal_everything()
        {
            Assert.True(ContactRules.For(false, Los).Identity);
            Assert.True(ContactRules.For(true, None).Identity);
        }

        [Fact]
        public void Names_follow_the_same_rule()
        {
            Assert.Equal("Atlas", ContactRules.Name(false, Los, "Atlas", "mech"));
            Assert.Equal("Unknown mech contact", ContactRules.Name(false, Blip4, "Atlas", "mech"));
            Assert.Equal("Unknown contact", ContactRules.Name(false, Blip0, "Atlas", "mech"));
        }
    }
}
