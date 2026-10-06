using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class ArmorRulesTests
    {
        [Fact]
        public void Legal_armor_passes()
        {
            Assert.Null(ArmorRules.Problem("CenterTorso", 40, 20, 140, 70, hasRear: true));
            Assert.Null(ArmorRules.Problem("Head", 45, -1, 45, 0, hasRear: false));
        }

        [Fact]
        public void Head_armor_above_its_maximum_is_refused()
        {
            // The review's trigger: 100 points moved from the legs to the head, same total tonnage.
            Assert.NotNull(ArmorRules.Problem("Head", 145, -1, 45, 0, hasRear: false));
        }

        [Fact]
        public void Rear_armor_above_its_maximum_is_refused()
        {
            Assert.NotNull(ArmorRules.Problem("LeftTorso", 30, 999, 120, 60, hasRear: true));
        }

        [Fact]
        public void Negative_armor_is_refused()
        {
            Assert.NotNull(ArmorRules.Problem("LeftArm", -10, -1, 80, 0, hasRear: false));
            Assert.NotNull(ArmorRules.Problem("LeftTorso", 30, -5, 120, 60, hasRear: true));
        }

        [Fact]
        public void Nonfinite_armor_is_refused()
        {
            Assert.NotNull(ArmorRules.Problem("Head", float.NaN, -1, 45, 0, hasRear: false));
            Assert.NotNull(ArmorRules.Problem("Head", float.PositiveInfinity, -1, 45, 0, hasRear: false));
            Assert.NotNull(ArmorRules.Problem("CenterTorso", 40, float.NaN, 140, 70, hasRear: true));
        }

        [Fact]
        public void Rear_armor_on_a_limb_is_refused()
        {
            Assert.NotNull(ArmorRules.Problem("RightArm", 30, 15, 80, 0, hasRear: false));
        }
    }
}
