using System;
using System.Collections.Generic;
using System.Linq;

namespace BTBridge.Logic
{
    // Pure rules with no game types, so BTBridge.Tests can exercise them without the game.

    public sealed class RuleException : Exception
    {
        public RuleException(string message) : base(message)
        {
        }
    }

    public sealed class NegotiatedTerms
    {
        public float Pay;
        public float Salvage;
        public float Reputation;
    }

    public static class Negotiation
    {
        /// <summary>
        /// Contract terms as fractions of the maximum, as SGContractsWidget produces them.
        /// When the employer gains reputation, pay + salvage + reputation = 1 and the reputation share
        /// is whatever is left; otherwise salvage = 1 - pay. Contract.SetNegotiatedValues accepts a
        /// sum up to 1.01, so allow that rounding slack.
        /// </summary>
        public static NegotiatedTerms Resolve(float pay, float? salvage, bool employerGainsReputation)
        {
            if (pay < 0f || pay > 1f)
            {
                throw new RuleException("pay must be between 0 and 1 (a fraction of the maximum)");
            }
            if (salvage.HasValue && (salvage.Value < 0f || salvage.Value > 1f))
            {
                throw new RuleException("salvage must be between 0 and 1 (a fraction of the maximum)");
            }
            if (!employerGainsReputation)
            {
                float implied = 1f - pay;
                if (salvage.HasValue && Math.Abs(salvage.Value - implied) > 0.01f)
                {
                    throw new RuleException($"this employer gives no reputation, so salvage is 1 - pay = {implied:0.##}");
                }
                return new NegotiatedTerms { Pay = pay, Salvage = implied, Reputation = 0f };
            }
            float s = salvage ?? 0f;
            if (pay + s > 1.01f)
            {
                throw new RuleException("pay + salvage cannot exceed 1; the remainder becomes reputation");
            }
            return new NegotiatedTerms { Pay = pay, Salvage = s, Reputation = Math.Max(0f, 1f - pay - s) };
        }
    }

    public sealed class SalvageOption
    {
        public string Id;
        public bool Damaged;
        public int Count;
    }

    public sealed class SalvagePick
    {
        public string Id;
        public bool Damaged;
    }

    public static class Salvage
    {
        /// <summary>
        /// Validate priority salvage picks against the stacked potential list. Each pick takes one
        /// unit from a stack; Contract.FinalizeSalvage neither caps nor dedupes, so do it here.
        /// Returns, per pick, the index of the stack it draws from.
        /// </summary>
        public static List<int> Match(IList<SalvageOption> potential, IList<SalvagePick> picks, int maxPicks)
        {
            if (picks.Count > maxPicks)
            {
                throw new RuleException($"{picks.Count} picks but only {maxPicks} priority salvage pick(s) allowed");
            }
            var remaining = potential.Select(p => p.Count).ToArray();
            var result = new List<int>();
            foreach (var pick in picks)
            {
                int index = -1;
                for (int i = 0; i < potential.Count; i++)
                {
                    if (potential[i].Id == pick.Id && potential[i].Damaged == pick.Damaged && remaining[i] > 0)
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                {
                    throw new RuleException($"no {(pick.Damaged ? "damaged " : "")}'{pick.Id}' left in the potential salvage");
                }
                remaining[index]--;
                result.Add(index);
            }
            return result;
        }
    }

    public static class Skills
    {
        public const int MaxSkill = 10;

        public static readonly string[] Names = { "Gunnery", "Piloting", "Guts", "Tactics" };

        public static string Normalize(string stat)
        {
            var match = Names.FirstOrDefault(n => string.Equals(n, stat, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new RuleException($"unknown skill '{stat}' (Gunnery | Piloting | Guts | Tactics)");
        }

        /// <summary>
        /// The pips to buy, one level at a time as the barracks UI allows, raising a skill from
        /// current to target. Pip index i takes the skill to i + 1 and costs levelCost(i).
        /// </summary>
        public static List<int> PipsToBuy(int current, int target, Func<int, int> levelCost, int unspentXp, out int totalCost)
        {
            if (target <= current)
            {
                throw new RuleException($"target {target} must be above the current value {current}");
            }
            if (target > MaxSkill)
            {
                throw new RuleException($"skills max out at {MaxSkill}");
            }
            var pips = new List<int>();
            totalCost = 0;
            for (int pip = current; pip < target; pip++)
            {
                totalCost += levelCost(pip);
                pips.Add(pip);
            }
            if (totalCost > unspentXp)
            {
                throw new RuleException($"needs {totalCost} XP, pilot has {unspentXp}");
            }
            return pips;
        }
    }

    /// <summary>Counts days toward a target while time runs.</summary>
    public sealed class DayCounter
    {
        public int Target { get; private set; }
        public int Passed { get; private set; }
        public bool Active => Target > 0 && Passed < Target;

        public void Start(int days)
        {
            if (days < 1 || days > 365)
            {
                throw new RuleException("days must be between 1 and 365");
            }
            Target = days;
            Passed = 0;
        }

        /// <summary>Record a day; returns true when the target has just been reached.</summary>
        public bool OnDay()
        {
            if (!Active)
            {
                return false;
            }
            Passed++;
            return Passed >= Target;
        }

        public void Stop() => Target = 0;
    }

    /// <summary>Observable sim-game facts that decide whether the campaign is waiting for the player.</summary>
    public sealed class SimFacts
    {
        public bool UxAttached;
        public bool ShipSet;
        public bool Saving;
        public bool InCombat;
        public bool ContractCompleting;
        public bool MilestoneContractPending;
        public bool InterruptOpen;
        public bool InterruptQueued;
        public bool ConversationOn;
        public bool VideoPlaying;
        public bool InTransition;
        public bool TimeMoving;
        public bool MechLabOpen;
        public bool LanceConfigOpen;
        public int VisiblePopups;
    }

    public static class CampaignWrites
    {
        /// <summary>
        /// Why company writes that touch mechs (refit apply, repair) must wait. Combat owns the
        /// deployed mechs until the contract resolves: post-mission reconciliation replaces the bay
        /// mechs with the combat result, so a refit committed mid-mission would be overwritten or
        /// would return parts to storage twice. Reads stay available in every phase.
        /// </summary>
        public static List<string> Blockers(SimFacts f)
        {
            var b = new List<string>();
            if (!f.UxAttached || !f.ShipSet) b.Add("the campaign is loading");
            if (f.InCombat) b.Add("a mission is in progress (combat owns the deployed mechs)");
            if (f.ContractCompleting) b.Add("the finished contract is still being resolved");
            if (f.Saving) b.Add("the game is saving");
            if (f.MechLabOpen) b.Add("the mechlab is open");
            if (f.LanceConfigOpen) b.Add("lance configuration is open");
            return b;
        }
    }

    public static class PlanBinding
    {
        /// <summary>
        /// A plan is good only in the campaign and the load of it that made it. A plan previewed and
        /// then carried across a reload of an earlier save could otherwise pass the mech
        /// fingerprint and insert work-order and component ids the restored campaign never issued.
        /// </summary>
        public static bool Valid(string planCampaign, int planEpoch, string currentCampaign, int currentEpoch) =>
            !string.IsNullOrEmpty(planCampaign) && planCampaign == currentCampaign && planEpoch == currentEpoch;
    }

    public static class TravelRoute
    {
        /// <summary>
        /// Whether the starmap's computed route is the one asked for. Selecting a new destination
        /// updates CurSelected at once, but PotentialPath and the projected cost are only replaced
        /// when that destination's pathfinding completes; until then they still describe the
        /// previous one. A review reproduced committing A's route after asking for B.
        /// </summary>
        public static bool Matches(string pathFirstId, string pathLastId, string currentSystemId, string targetId) =>
            !string.IsNullOrEmpty(targetId)
            && pathLastId == targetId
            && pathFirstId == currentSystemId;
    }

    public static class Idle
    {
        /// <summary>Why the campaign is not idle; empty means it is waiting for the player.</summary>
        public static List<string> Blockers(SimFacts f)
        {
            var b = new List<string>();
            if (!f.UxAttached || !f.ShipSet) b.Add("loading");
            if (f.Saving) b.Add("saving");
            if (f.InCombat) b.Add("in combat");
            if (f.ContractCompleting) b.Add("resolving completed contract");
            if (f.MilestoneContractPending) b.Add("milestone contract pending");
            if (f.InterruptOpen) b.Add("interrupt open");
            else if (f.InterruptQueued) b.Add("interrupt queued");
            if (f.ConversationOn) b.Add("conversation");
            if (f.VideoPlaying) b.Add("video");
            if (f.InTransition) b.Add("travel transition");
            if (f.TimeMoving) b.Add("time moving");
            if (f.MechLabOpen) b.Add("mechlab open (blocks interrupts)");
            if (f.LanceConfigOpen) b.Add("lance configuration open");
            if (f.VisiblePopups > 0) b.Add("popup visible");
            return b;
        }
    }
}
