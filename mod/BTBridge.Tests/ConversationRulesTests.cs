using System.Collections.Generic;
using BTBridge.Logic;
using Xunit;

namespace BTBridge.Tests
{
    public class ConversationRulesTests
    {
        private static readonly List<OfferedResponse> Offered = new List<OfferedResponse>
        {
            new OfferedResponse { Index = 0, Enabled = true },
            new OfferedResponse { Index = 2, Enabled = false }, // shown but requirements unmet
        };

        [Fact]
        public void An_offered_enabled_response_is_accepted()
        {
            Assert.Null(ConversationRules.Problem(0, answeredNode: 7, currentNode: 7, Offered));
        }

        [Fact]
        public void A_disabled_response_is_refused()
        {
            Assert.Contains("not available", ConversationRules.Problem(2, 7, 7, Offered));
        }

        [Fact]
        public void A_hidden_or_out_of_range_response_is_refused()
        {
            // Branch 1 exists on the node but the UI hides it (hideIfUnavailable); 9 doesn't exist.
            Assert.Contains("not one of the responses offered", ConversationRules.Problem(1, 7, 7, Offered));
            Assert.Contains("not one of the responses offered", ConversationRules.Problem(9, 7, 7, Offered));
        }

        [Fact]
        public void An_answer_for_an_earlier_line_is_refused()
        {
            Assert.Contains("moved on", ConversationRules.Problem(0, answeredNode: 6, currentNode: 7, Offered));
        }

        [Fact]
        public void An_answer_without_its_node_is_refused()
        {
            Assert.NotNull(ConversationRules.Problem(0, answeredNode: null, currentNode: 7, Offered));
        }
    }
}
