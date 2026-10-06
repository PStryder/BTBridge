using System;
using System.Collections.Generic;
using System.Linq;

namespace BTBridge.Logic
{
    // OPERATOR CHEAT LAYER: pure rules. No game types, so BTBridge.Tests can exercise them.
    // See docs/CHEATS_DESIGN.md. Nothing here is reachable unless the cheat capability is on.

    /// <summary>
    /// The arming state machine. Capability is fixed at startup; arming is a human act (hotkey);
    /// the window closes on expiry, budget exhaustion, explicit disarm, or campaign change.
    /// </summary>
    public sealed class CheatGate
    {
        public bool Capability { get; }
        public DateTime? ArmedUntil { get; private set; }
        public int? OpsLeft { get; private set; }
        public string ArmSession { get; private set; }
        private int sessions;

        public CheatGate(bool capability)
        {
            Capability = capability;
        }

        public bool IsArmed(DateTime now)
        {
            if (ArmedUntil.HasValue && now >= ArmedUntil.Value)
            {
                Disarm();
            }
            return ArmedUntil.HasValue;
        }

        public void Arm(DateTime now, int minutes, int maxOps)
        {
            if (!Capability)
            {
                throw new RuleException("cheat capability is off");
            }
            if (minutes < 1 || minutes > 240)
            {
                throw new RuleException("arming window must be 1-240 minutes");
            }
            ArmedUntil = now.AddMinutes(minutes);
            OpsLeft = maxOps > 0 ? maxOps : (int?)null;
            ArmSession = "arm-" + (++sessions) + "-" + now.ToString("HHmmss");
        }

        public void Disarm()
        {
            ArmedUntil = null;
            OpsLeft = null;
            ArmSession = null;
        }

        /// <summary>Throws unless an operation may run now.</summary>
        public void RequireExecutable(DateTime now, bool ironman)
        {
            if (!Capability)
            {
                throw new RuleException("cheat capability is off");
            }
            if (ironman)
            {
                throw new RuleException("cheats are not available in Ironman campaigns");
            }
            if (!IsArmed(now))
            {
                throw new RuleException("cheats are not armed (the operator arms them in-game)");
            }
        }

        /// <summary>Count an executed operation against the budget; disarms when it runs out.</summary>
        public void Consume()
        {
            if (OpsLeft.HasValue)
            {
                OpsLeft = OpsLeft.Value - 1;
                if (OpsLeft.Value <= 0)
                {
                    Disarm();
                }
            }
        }

        public TimeSpan? Remaining(DateTime now) => IsArmed(now) ? ArmedUntil.Value - now : (TimeSpan?)null;
    }

    /// <summary>A previewed cheat, bound to the campaign, this load of it, and the before-state.</summary>
    public sealed class CheatPlan
    {
        public string Id;
        public string Op;
        public string CampaignId;
        public int LoadEpoch;
        public string Fingerprint;
        public DateTime Created;
        public string OperatorRequest;
        public object Payload;
        public object Result;
        public bool Executed;
    }

    public sealed class PlanLedger
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
        private readonly Dictionary<string, CheatPlan> plans = new Dictionary<string, CheatPlan>();
        private int next;

        public CheatPlan Create(string op, string campaignId, int loadEpoch, string fingerprint, DateTime now, string operatorRequest, object payload)
        {
            if (string.IsNullOrWhiteSpace(operatorRequest) || operatorRequest.Trim().Length < 8)
            {
                throw new RuleException("operator_request is required: quote the operator's instruction");
            }
            Prune(now);
            var plan = new CheatPlan
            {
                Id = "cheat-" + (++next),
                Op = op,
                CampaignId = campaignId,
                LoadEpoch = loadEpoch,
                Fingerprint = fingerprint,
                Created = now,
                OperatorRequest = operatorRequest.Trim(),
                Payload = payload,
            };
            plans[plan.Id] = plan;
            return plan;
        }

        /// <summary>
        /// The plan to execute, or (for a replay) the already-executed plan whose Result should be
        /// returned unchanged. Throws if the plan is unknown, expired, or no longer matches.
        /// </summary>
        public CheatPlan Claim(string id, string campaignId, int loadEpoch, string fingerprint, DateTime now) =>
            Claim(id, campaignId, loadEpoch, _ => fingerprint, now);

        /// <summary>
        /// As above, computing the current fingerprint only after the campaign and load epoch are
        /// confirmed, so the before-state is never re-read against a different campaign.
        /// </summary>
        public CheatPlan Claim(string id, string campaignId, int loadEpoch, Func<CheatPlan, string> currentFingerprint, DateTime now)
        {
            if (string.IsNullOrEmpty(id) || !plans.TryGetValue(id, out var plan))
            {
                throw new RuleException($"no cheat plan '{id}'; preview again");
            }
            if (plan.Executed)
            {
                return plan;
            }
            if (now - plan.Created > Lifetime)
            {
                plans.Remove(id);
                throw new RuleException("the preview expired; preview again");
            }
            if (plan.CampaignId != campaignId || plan.LoadEpoch != loadEpoch)
            {
                plans.Remove(id);
                throw new RuleException("campaign or load changed since preview; preview again");
            }
            if (plan.Fingerprint != currentFingerprint(plan))
            {
                plans.Remove(id);
                throw new RuleException("state changed since preview; preview again");
            }
            return plan;
        }

        public void MarkExecuted(CheatPlan plan, object result)
        {
            plan.Executed = true;
            plan.Result = result;
        }

        private void Prune(DateTime now)
        {
            foreach (var id in plans.Values.Where(p => !p.Executed && now - p.Created > Lifetime).Select(p => p.Id).ToList())
            {
                plans.Remove(id);
            }
        }
    }

    public sealed class PersistedRecord
    {
        public List<string> PlanIds;
        public string Reason;
        public DateTime When;
    }

    /// <summary>Which executed cheats are not in any save yet.</summary>
    public sealed class SaveTracker
    {
        private readonly List<string> unsaved = new List<string>();
        public DateTime? LastSave { get; private set; }
        public string LastSaveReason { get; private set; }
        public string PendingReason;

        public IReadOnlyList<string> Unsaved => unsaved;

        /// <summary>"clean" if nothing else was unsaved before this cheat, else "dirty".</summary>
        public string OnCheatExecuted(string planId)
        {
            string state = unsaved.Count == 0 ? "clean" : "dirty";
            unsaved.Add(planId);
            return state;
        }

        /// <summary>The campaign was written to a save; returns what that made permanent (or null).</summary>
        public PersistedRecord OnSaved(DateTime now)
        {
            LastSave = now;
            LastSaveReason = PendingReason ?? "manual or unrecorded";
            PendingReason = null;
            if (unsaved.Count == 0)
            {
                return null;
            }
            var record = new PersistedRecord { PlanIds = unsaved.ToList(), Reason = LastSaveReason, When = now };
            unsaved.Clear();
            return record;
        }

        /// <summary>A save was loaded or the campaign changed: unsaved cheats are gone.</summary>
        public void OnLoad()
        {
            unsaved.Clear();
            PendingReason = null;
        }
    }

    public static class CheatGuards
    {
        public const int MaxComponentsPerOp = 50;

        /// <summary>New funds after adding delta; refuses int overflow and dropping below the debt limit.</summary>
        public static int FundsAfter(int funds, long delta, int maximumDebt)
        {
            if (delta == 0)
            {
                throw new RuleException("amount must not be zero");
            }
            long after = funds + delta;
            if (after > int.MaxValue)
            {
                throw new RuleException($"funds would exceed {int.MaxValue:N0}");
            }
            if (after < maximumDebt)
            {
                throw new RuleException($"funds would fall below the game-over debt limit ({maximumDebt:N0})");
            }
            return (int)after;
        }

        public static void ComponentCount(int count, bool removing, int have)
        {
            if (count < 1 || count > MaxComponentsPerOp)
            {
                throw new RuleException($"count must be 1-{MaxComponentsPerOp}");
            }
            if (removing && count > have)
            {
                throw new RuleException($"only {have} in storage");
            }
        }

        /// <summary>The bay to use: the requested one if valid and empty, else the first free one.</summary>
        public static int Bay(int? requested, int maxBays, ICollection<int> occupied, int firstFree)
        {
            if (requested.HasValue)
            {
                if (requested.Value < 0 || requested.Value >= maxBays)
                {
                    throw new RuleException($"bay must be 0-{maxBays - 1}");
                }
                if (occupied.Contains(requested.Value))
                {
                    throw new RuleException($"bay {requested.Value} is occupied (the game would overwrite it)");
                }
                return requested.Value;
            }
            if (firstFree < 0 || firstFree >= maxBays)
            {
                throw new RuleException("all bays are full; use destination storage");
            }
            return firstFree;
        }
    }
}
