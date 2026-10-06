using System;
using System.Collections.Generic;
using System.Linq;
using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class CheatGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void NoCapabilityMeansNoArming()
        {
            var g = new CheatGate(false);
            Assert.Throws<RuleException>(() => g.Arm(T0, 15, 0));
            Assert.False(g.IsArmed(T0));
            Assert.Throws<RuleException>(() => g.RequireExecutable(T0, ironman: false));
        }

        [Fact]
        public void ArmingOpensAWindowThatExpires()
        {
            var g = new CheatGate(true);
            Assert.Throws<RuleException>(() => g.RequireExecutable(T0, false));
            g.Arm(T0, 15, 0);
            g.RequireExecutable(T0.AddMinutes(14), false);
            Assert.Throws<RuleException>(() => g.RequireExecutable(T0.AddMinutes(15), false));
            Assert.False(g.IsArmed(T0.AddMinutes(15)));
        }

        [Fact]
        public void IronmanIsRefusedEvenWhenArmed()
        {
            var g = new CheatGate(true);
            g.Arm(T0, 15, 0);
            var e = Assert.Throws<RuleException>(() => g.RequireExecutable(T0, ironman: true));
            Assert.Contains("Ironman", e.Message);
        }

        [Fact]
        public void BudgetDisarmsWhenSpent()
        {
            var g = new CheatGate(true);
            g.Arm(T0, 15, 2);
            g.Consume();
            Assert.True(g.IsArmed(T0));
            g.Consume();
            Assert.False(g.IsArmed(T0));
        }

        [Fact]
        public void DisarmAlwaysCloses()
        {
            var g = new CheatGate(true);
            g.Arm(T0, 15, 0);
            g.Disarm();
            Assert.False(g.IsArmed(T0));
            Assert.Null(g.ArmSession);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(241)]
        public void WindowLengthIsBounded(int minutes)
        {
            Assert.Throws<RuleException>(() => new CheatGate(true).Arm(T0, minutes, 0));
        }
    }

    public class PlanLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        private const string Request = "give the company 250 million c-bills";

        private static CheatPlan Plan(PlanLedger l) => l.Create("add_funds", "campaign-A", 1, "funds=100", T0, Request, null);

        [Fact]
        public void RequiresAnOperatorRequest()
        {
            var l = new PlanLedger();
            Assert.Throws<RuleException>(() => l.Create("add_funds", "c", 1, "f", T0, "", null));
            Assert.Throws<RuleException>(() => l.Create("add_funds", "c", 1, "f", T0, "do it", null));
        }

        [Fact]
        public void ClaimsWhenEverythingMatches()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            Assert.Same(p, l.Claim(p.Id, "campaign-A", 1, "funds=100", T0.AddSeconds(30)));
        }

        [Fact]
        public void ReplayReturnsTheExecutedPlan()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            l.MarkExecuted(p, "result-1");
            // Even with changed state, a replay is answered from the ledger and never re-run.
            var again = l.Claim(p.Id, "campaign-B", 9, "funds=999", T0.AddMinutes(10));
            Assert.True(again.Executed);
            Assert.Equal("result-1", again.Result);
        }

        [Fact]
        public void ExpiredPlansAreRejected()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-A", 1, "funds=100", T0 + PlanLedger.Lifetime + TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void ADifferentCampaignIsRejected()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            var e = Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-B", 1, "funds=100", T0));
            Assert.Contains("campaign or load changed", e.Message);
        }

        [Fact]
        public void AReloadOfTheSameCampaignIsRejected()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            var e = Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-A", 2, "funds=100", T0));
            Assert.Contains("campaign or load changed", e.Message);
        }

        [Fact]
        public void ChangedBeforeStateIsRejected()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            var e = Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-A", 1, "funds=101", T0));
            Assert.Contains("state changed", e.Message);
        }

        [Fact]
        public void FingerprintIsNotReadForAnotherCampaign()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            bool read = false;
            Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-B", 1, _ => { read = true; return "funds=100"; }, T0));
            Assert.False(read);
        }

        [Fact]
        public void RejectedPlansCannotBeRetried()
        {
            var l = new PlanLedger();
            var p = Plan(l);
            Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-A", 1, "funds=101", T0));
            Assert.Throws<RuleException>(() => l.Claim(p.Id, "campaign-A", 1, "funds=100", T0));
        }

        [Fact]
        public void UnknownPlansAreRejected()
        {
            Assert.Throws<RuleException>(() => new PlanLedger().Claim("cheat-99", "c", 1, "f", T0));
        }
    }

    public class SaveTrackerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void CleanThenDirty()
        {
            var s = new SaveTracker();
            Assert.Equal("clean", s.OnCheatExecuted("cheat-1"));
            Assert.Equal("dirty", s.OnCheatExecuted("cheat-2"));
        }

        [Fact]
        public void ASaveReportsExactlyWhatItPersisted()
        {
            var s = new SaveTracker();
            s.OnCheatExecuted("cheat-1");
            s.OnCheatExecuted("cheat-2");
            s.PendingReason = "SIM_GAME_ARRIVED_AT_PLANET";
            var r = s.OnSaved(T0);
            Assert.Equal(new List<string> { "cheat-1", "cheat-2" }, r.PlanIds);
            Assert.Equal("SIM_GAME_ARRIVED_AT_PLANET", r.Reason);
            Assert.Empty(s.Unsaved);
            Assert.Equal("clean", s.OnCheatExecuted("cheat-3"));
        }

        [Fact]
        public void ASaveWithNothingUnsavedRecordsNothing()
        {
            var s = new SaveTracker();
            Assert.Null(s.OnSaved(T0));
            Assert.Equal(T0, s.LastSave);
            Assert.Equal("manual or unrecorded", s.LastSaveReason);
        }

        [Fact]
        public void LoadingDropsUnsavedCheats()
        {
            var s = new SaveTracker();
            s.OnCheatExecuted("cheat-1");
            s.OnLoad();
            Assert.Null(s.OnSaved(T0));
        }
    }

    public class CheatGuardTests
    {
        [Fact]
        public void FundsAddAndRemove()
        {
            Assert.Equal(250_001_000, CheatGuards.FundsAfter(1000, 250_000_000, -1_000_000));
            Assert.Equal(-500_000, CheatGuards.FundsAfter(500_000, -1_000_000, -1_000_000));
        }

        [Fact]
        public void FundsRefuseOverflowZeroAndDebt()
        {
            Assert.Throws<RuleException>(() => CheatGuards.FundsAfter(int.MaxValue - 5, 10, -1_000_000));
            Assert.Throws<RuleException>(() => CheatGuards.FundsAfter(100, 0, -1_000_000));
            Assert.Throws<RuleException>(() => CheatGuards.FundsAfter(0, -1_000_001, -1_000_000));
        }

        [Fact]
        public void ComponentCounts()
        {
            CheatGuards.ComponentCount(50, false, 0);
            Assert.Throws<RuleException>(() => CheatGuards.ComponentCount(51, false, 0));
            Assert.Throws<RuleException>(() => CheatGuards.ComponentCount(0, false, 0));
            Assert.Throws<RuleException>(() => CheatGuards.ComponentCount(3, true, 2));
            CheatGuards.ComponentCount(2, true, 2);
        }

        [Fact]
        public void BayChoice()
        {
            var occupied = new List<int> { 0, 1 };
            Assert.Equal(2, CheatGuards.Bay(null, 6, occupied, 2));
            Assert.Equal(4, CheatGuards.Bay(4, 6, occupied, 2));
            Assert.Throws<RuleException>(() => CheatGuards.Bay(1, 6, occupied, 2));
            Assert.Throws<RuleException>(() => CheatGuards.Bay(6, 6, occupied, 2));
            Assert.Throws<RuleException>(() => CheatGuards.Bay(null, 6, occupied, -1));
        }
    }

    public class CheatRouteRegistrationTests
    {
        private static readonly string[] CheatPaths = { "/cheat/status", "/cheat/preview", "/cheat/execute", "/cheat/disarm", "/cheat/audit" };

        [Fact]
        public void CheatRoutesExistOnlyWithCapability()
        {
            var off = Routes.Build(false).Select(r => r.Path).ToList();
            Assert.DoesNotContain(off, p => p.StartsWith("/cheat"));
            var on = Routes.Build(true).Select(r => r.Path).ToList();
            Assert.Equal(CheatPaths.OrderBy(p => p), on.Where(p => p.StartsWith("/cheat")).OrderBy(p => p));
        }

        [Fact]
        public void ThereIsNoRouteToArmOrReconfigure()
        {
            var forbidden = new[] { "arm", "enable", "unlock", "config", "settings", "hotkey", "window", "budget" };
            var segments = Routes.Build(true).SelectMany(r => r.Path.ToLowerInvariant().Split('/')).ToList();
            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, segments);
            }
        }
    }
}
