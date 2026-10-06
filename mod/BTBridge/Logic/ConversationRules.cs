using System.Collections.Generic;
using System.Linq;

namespace BTBridge.Logic
{
    public sealed class OfferedResponse
    {
        public int Index;
        public bool Enabled;
    }

    /// <summary>
    /// SimGameConversationManager.SelectResponse indexes the node's branches and runs the link's
    /// actions without rechecking its conditions; availability is enforced only by the dialog
    /// UI, which the bridge bypasses (review finding). A response must be one the UI offers now,
    /// enabled, and for the node the agent read.
    /// </summary>
    public static class ConversationRules
    {
        /// <returns>null when the answer may be submitted, otherwise why not.</returns>
        public static string Problem(int requested, int? answeredNode, int currentNode, IList<OfferedResponse> offered)
        {
            if (answeredNode == null)
            {
                return "include \"node\" from the conversation you read, so a stale answer can't land on a later line";
            }
            if (answeredNode.Value != currentNode)
            {
                return $"the conversation has moved on (now at node {currentNode}, answer was for {answeredNode}); read it again";
            }
            var match = offered.FirstOrDefault(r => r.Index == requested);
            if (match == null)
            {
                return $"response {requested} is not one of the responses offered ({string.Join(", ", offered.Select(r => r.Index.ToString()).ToArray())})";
            }
            if (!match.Enabled)
            {
                return $"response {requested} is not available (its requirements are not met)";
            }
            return null;
        }
    }
}
