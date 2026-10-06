using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class CampaignWriteTests
    {
        private static SimFacts Ready() => new SimFacts { UxAttached = true, ShipSet = true };

        [Fact]
        public void A_quiet_campaign_allows_mech_writes()
        {
            Assert.Empty(CampaignWrites.Blockers(Ready()));
        }

        [Fact]
        public void Combat_blocks_mech_writes()
        {
            // The review's trigger: a removal-only refit on a deployed mech during a mission.
            var f = Ready();
            f.InCombat = true;
            Assert.Contains(CampaignWrites.Blockers(f), b => b.Contains("mission is in progress"));
        }

        [Fact]
        public void An_unresolved_contract_blocks_mech_writes()
        {
            var f = Ready();
            f.ContractCompleting = true;
            Assert.NotEmpty(CampaignWrites.Blockers(f));
        }

        [Fact]
        public void Saving_loading_and_open_editors_block_mech_writes()
        {
            var saving = Ready(); saving.Saving = true;
            var loading = new SimFacts { UxAttached = false, ShipSet = true };
            var lab = Ready(); lab.MechLabOpen = true;
            var lance = Ready(); lance.LanceConfigOpen = true;
            Assert.NotEmpty(CampaignWrites.Blockers(saving));
            Assert.NotEmpty(CampaignWrites.Blockers(loading));
            Assert.NotEmpty(CampaignWrites.Blockers(lab));
            Assert.NotEmpty(CampaignWrites.Blockers(lance));
        }
    }

    public class PlanBindingTests
    {
        [Fact]
        public void A_plan_applies_in_the_load_that_made_it()
        {
            Assert.True(PlanBinding.Valid("career-1", 3, "career-1", 3));
        }

        [Fact]
        public void A_plan_dies_when_the_same_campaign_is_reloaded()
        {
            // The review's trigger: preview, reload the pre-preview save, apply the old plan.
            Assert.False(PlanBinding.Valid("career-1", 3, "career-1", 4));
        }

        [Fact]
        public void A_plan_never_applies_to_another_campaign()
        {
            Assert.False(PlanBinding.Valid("career-1", 3, "career-2", 3));
        }

        [Fact]
        public void A_plan_without_a_campaign_is_invalid()
        {
            Assert.False(PlanBinding.Valid(null, 0, null, 0));
        }
    }
}
