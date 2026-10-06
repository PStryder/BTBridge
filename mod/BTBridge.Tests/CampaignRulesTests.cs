using System.Collections.Generic;
using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class NegotiationTests
    {
        [Fact]
        public void ReputationIsTheRemainderForReputationEmployers()
        {
            var t = Negotiation.Resolve(0.5f, 0.3f, employerGainsReputation: true);
            Assert.Equal(0.5f, t.Pay);
            Assert.Equal(0.3f, t.Salvage);
            Assert.Equal(0.2f, t.Reputation, 3);
        }

        [Fact]
        public void PayPlusSalvageOverOneIsRejected()
        {
            Assert.Throws<RuleException>(() => Negotiation.Resolve(0.7f, 0.5f, employerGainsReputation: true));
        }

        [Fact]
        public void RoundingSlackIsAllowed()
        {
            var t = Negotiation.Resolve(0.505f, 0.5f, employerGainsReputation: true);
            Assert.Equal(0f, t.Reputation);
        }

        [Fact]
        public void NoReputationEmployerImpliesSalvage()
        {
            var t = Negotiation.Resolve(0.75f, null, employerGainsReputation: false);
            Assert.Equal(0.25f, t.Salvage, 3);
            Assert.Equal(0f, t.Reputation);
        }

        [Fact]
        public void NoReputationEmployerRejectsInconsistentSalvage()
        {
            Assert.Throws<RuleException>(() => Negotiation.Resolve(0.5f, 0.2f, employerGainsReputation: false));
        }

        [Theory]
        [InlineData(-0.1f)]
        [InlineData(1.1f)]
        public void OutOfRangePayIsRejected(float pay)
        {
            Assert.Throws<RuleException>(() => Negotiation.Resolve(pay, 0f, true));
        }
    }

    public class SalvageTests
    {
        private static List<SalvageOption> Potential() => new List<SalvageOption>
        {
            new SalvageOption { Id = "Weapon_PPC", Damaged = false, Count = 1 },
            new SalvageOption { Id = "Weapon_PPC", Damaged = true, Count = 1 },
            new SalvageOption { Id = "Ammo_LRM", Damaged = false, Count = 3 },
        };

        [Fact]
        public void MatchesPicksToStacks()
        {
            var picks = new List<SalvagePick>
            {
                new SalvagePick { Id = "Ammo_LRM" },
                new SalvagePick { Id = "Weapon_PPC", Damaged = true },
            };
            Assert.Equal(new List<int> { 2, 1 }, Salvage.Match(Potential(), picks, 3));
        }

        [Fact]
        public void EnforcesThePickCap()
        {
            var picks = new List<SalvagePick> { new SalvagePick { Id = "Ammo_LRM" }, new SalvagePick { Id = "Ammo_LRM" } };
            Assert.Throws<RuleException>(() => Salvage.Match(Potential(), picks, 1));
        }

        [Fact]
        public void CannotTakeMoreThanAStackHolds()
        {
            var picks = new List<SalvagePick> { new SalvagePick { Id = "Weapon_PPC" }, new SalvagePick { Id = "Weapon_PPC" } };
            Assert.Throws<RuleException>(() => Salvage.Match(Potential(), picks, 5));
        }

        [Fact]
        public void DamagedAndIntactAreDistinct()
        {
            var picks = new List<SalvagePick> { new SalvagePick { Id = "Ammo_LRM", Damaged = true } };
            Assert.Throws<RuleException>(() => Salvage.Match(Potential(), picks, 5));
        }

        [Fact]
        public void StacksAreConsumedPerPick()
        {
            var picks = new List<SalvagePick>
            {
                new SalvagePick { Id = "Ammo_LRM" }, new SalvagePick { Id = "Ammo_LRM" }, new SalvagePick { Id = "Ammo_LRM" },
            };
            Assert.Equal(new List<int> { 2, 2, 2 }, Salvage.Match(Potential(), picks, 3));
            picks.Add(new SalvagePick { Id = "Ammo_LRM" });
            Assert.Throws<RuleException>(() => Salvage.Match(Potential(), picks, 4));
        }
    }

    public class SkillsTests
    {
        private static int Cost(int pip) => (pip + 1) * 100;

        [Fact]
        public void BuysEachPipInOrder()
        {
            var pips = Skills.PipsToBuy(4, 6, Cost, 10000, out int total);
            Assert.Equal(new List<int> { 4, 5 }, pips);
            Assert.Equal(500 + 600, total);
        }

        [Fact]
        public void RejectsInsufficientXp()
        {
            Assert.Throws<RuleException>(() => Skills.PipsToBuy(4, 6, Cost, 1000, out _));
        }

        [Fact]
        public void RejectsBeyondMax()
        {
            Assert.Throws<RuleException>(() => Skills.PipsToBuy(9, 11, Cost, 99999, out _));
        }

        [Fact]
        public void RejectsNonIncrease()
        {
            Assert.Throws<RuleException>(() => Skills.PipsToBuy(5, 5, Cost, 99999, out _));
        }

        [Theory]
        [InlineData("gunnery", "Gunnery")]
        [InlineData("TACTICS", "Tactics")]
        public void NormalizesSkillNames(string input, string expected)
        {
            Assert.Equal(expected, Skills.Normalize(input));
        }

        [Fact]
        public void RejectsUnknownSkill()
        {
            Assert.Throws<RuleException>(() => Skills.Normalize("Luck"));
        }
    }

    public class DayCounterTests
    {
        [Fact]
        public void ReachesTargetExactlyOnce()
        {
            var c = new DayCounter();
            c.Start(3);
            Assert.False(c.OnDay());
            Assert.False(c.OnDay());
            Assert.True(c.OnDay());
            Assert.False(c.Active);
            Assert.False(c.OnDay());
            Assert.Equal(3, c.Passed);
        }

        [Fact]
        public void StopCancels()
        {
            var c = new DayCounter();
            c.Start(5);
            c.OnDay();
            c.Stop();
            Assert.False(c.Active);
            Assert.False(c.OnDay());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(366)]
        public void RejectsBadRanges(int days)
        {
            Assert.Throws<RuleException>(() => new DayCounter().Start(days));
        }
    }

    public class IdleTests
    {
        private static SimFacts Idle() => new SimFacts { UxAttached = true, ShipSet = true };

        [Fact]
        public void QuietShipIsIdle()
        {
            Assert.Empty(Logic.Idle.Blockers(Idle()));
        }

        [Fact]
        public void EachBlockerIsReported()
        {
            var checks = new List<System.Action<SimFacts>>
            {
                f => f.Saving = true, f => f.InCombat = true, f => f.ContractCompleting = true,
                f => f.MilestoneContractPending = true, f => f.InterruptOpen = true, f => f.InterruptQueued = true,
                f => f.ConversationOn = true, f => f.VideoPlaying = true, f => f.InTransition = true,
                f => f.TimeMoving = true, f => f.MechLabOpen = true, f => f.LanceConfigOpen = true,
                f => f.VisiblePopups = 1, f => f.UxAttached = false,
            };
            foreach (var set in checks)
            {
                var facts = Idle();
                set(facts);
                Assert.Single(Logic.Idle.Blockers(facts));
            }
        }
    }
}
