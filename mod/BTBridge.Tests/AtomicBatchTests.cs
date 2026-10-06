using System;
using System.Collections.Generic;
using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class AtomicBatchTests
    {
        private static Dictionary<string, string> Plan() => new Dictionary<string, string> { ["A"] = "move" };

        [Fact]
        public void A_rejected_batch_leaves_the_previous_plan_untouched()
        {
            // The review's reproduction: plan {A: move}; new batch [B: attack, missing] → 400.
            var live = Plan();
            Assert.Throws<InvalidOperationException>(() => AtomicBatch.Apply(live, () =>
            {
                var staged = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("B", "attack") };
                throw new InvalidOperationException("no unit 'missing'");
#pragma warning disable CS0162
                return staged;
#pragma warning restore CS0162
            }, replace: true));
            Assert.Equal("move", live["A"]);
            Assert.False(live.ContainsKey("B"));
        }

        [Fact]
        public void A_valid_replacing_batch_replaces()
        {
            var live = Plan();
            AtomicBatch.Apply(live, () => new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("B", "attack") }, replace: true);
            Assert.False(live.ContainsKey("A"));
            Assert.Equal("attack", live["B"]);
        }

        [Fact]
        public void A_valid_merging_batch_merges()
        {
            var live = Plan();
            AtomicBatch.Apply(live, () => new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("B", "attack") }, replace: false);
            Assert.Equal("move", live["A"]);
            Assert.Equal("attack", live["B"]);
        }

        [Fact]
        public void A_batch_naming_a_unit_twice_is_rejected_whole()
        {
            var live = Plan();
            Assert.Throws<ArgumentException>(() => AtomicBatch.Apply(live, () => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("B", "attack"),
                new KeyValuePair<string, string>("B", "move"),
            }, replace: true));
            Assert.Equal("move", live["A"]);
            Assert.False(live.ContainsKey("B"));
        }
    }
}
