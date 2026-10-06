using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattleTech;
using BattleTech.Save.SaveGameStructure;
using BTBridge.Bridge;
using BTBridge.Logic;
using BTBridge.Sim;
using BTBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTBridge.Cheats
{
    /// <summary>
    /// OPERATOR CHEAT LAYER. Explicit operator requests only; see docs/CHEATS_DESIGN.md.
    /// Every mutation goes through the game's own state-changing methods (mirroring
    /// SimGameState_Debug), is bound to a previewed plan (campaign + load epoch + before-state),
    /// runs only while a human has armed the layer in-game, and is written to cheats_audit.jsonl.
    /// </summary>
    public static class CheatService
    {
        public const string Source = "BTBRIDGE_CHEAT";
        public const string MarkerTag = "btbridge_cheats_used";
        public const string MarkerStat = "BTBRIDGE_CheatOps";

        public static CheatGate Gate { get; private set; } = new CheatGate(false);
        private static readonly PlanLedger Ledger = new PlanLedger();
        public static readonly SaveTracker Saves = new SaveTracker();
        private static readonly object AuditLock = new object();
        private static string auditPath;
        private static SimGameState lastSim;
        private static int loadEpoch;

        /// <summary>Transient line for the overlay (arm/disarm/refusals).</summary>
        public static string Notice;
        public static DateTime NoticeUntil;

        public static void Init(string modDir)
        {
            Gate = new CheatGate(CheatConfig.Capability);
            auditPath = Path.Combine(modDir ?? ".", "cheats_audit.jsonl");
            Log.Info($"[CHEAT] capability {(CheatConfig.Capability ? "ON via " + CheatConfig.CapabilitySource : "off")}");
        }

        private static DateTime Now => DateTime.UtcNow;

        private static SimGameState Sim => UnityGameInstance.BattleTechGame?.Simulation;

        // -- campaign identity and lifecycle -------------------------------------------------------

        /// <summary>Per frame (overlay): notice campaign switches, expire the window.</summary>
        public static void Tick()
        {
            var sim = Sim;
            if (!ReferenceEquals(sim, lastSim))
            {
                lastSim = sim;
                OnCampaignChanged(sim == null ? "left the campaign" : "campaign instance changed");
            }
            bool wasArmed = Gate.ArmedUntil.HasValue;
            if (wasArmed && !Gate.IsArmed(Now))
            {
                Audit(new { kind = "disarmed", why = "window expired" });
                Say("BTBridge cheats disarmed (window expired)");
            }
        }

        public static void OnRehydrate()
        {
            OnCampaignChanged("save loaded");
        }

        private static void OnCampaignChanged(string why)
        {
            loadEpoch++;
            Saves.OnLoad();
            if (Gate.ArmedUntil.HasValue)
            {
                Gate.Disarm();
                Audit(new { kind = "disarmed", why });
                Say($"BTBridge cheats disarmed ({why})");
            }
        }

        public static void OnSaveRequested(SaveReason reason)
        {
            Saves.PendingReason = reason.ToString();
        }

        public static void OnSaved()
        {
            var record = Saves.OnSaved(Now);
            if (record != null)
            {
                Audit(new
                {
                    kind = "persisted",
                    plan_ids = record.PlanIds,
                    save_reason = record.Reason,
                    note = "these cheats are now part of a save",
                });
                Log.Info($"[CHEAT] persisted by save ({record.Reason}): {string.Join(", ", record.PlanIds.ToArray())}");
            }
        }

        private static string CampaignId(SimGameState sim) => sim?.InstanceGUID;

        // -- arming (human only: hotkey / overlay) ------------------------------------------------

        public static void ToggleArm()
        {
            var sim = Sim;
            if (Gate.IsArmed(Now))
            {
                Gate.Disarm();
                Audit(new { kind = "disarmed", why = "operator hotkey" });
                Say("BTBridge cheats DISARMED");
                return;
            }
            if (sim == null)
            {
                Say("BTBridge cheats: load a campaign first");
                return;
            }
            if (sim.IsIronmanCampaign)
            {
                Say("BTBridge cheats are not available in Ironman campaigns");
                return;
            }
            Gate.Arm(Now, CheatConfig.ArmMinutes, CheatConfig.ArmMaxOps);
            Audit(new { kind = "armed", minutes = CheatConfig.ArmMinutes, max_ops = CheatConfig.ArmMaxOps, session = Gate.ArmSession });
            Say($"BTBridge cheats ARMED for {CheatConfig.ArmMinutes} min");
        }

        public static object Disarm()
        {
            bool was = Gate.IsArmed(Now);
            Gate.Disarm();
            if (was)
            {
                Audit(new { kind = "disarmed", why = "api" });
                Say("BTBridge cheats disarmed");
            }
            return new { armed = false, was_armed = was };
        }

        private static void Say(string text)
        {
            Notice = text;
            NoticeUntil = DateTime.Now.AddSeconds(6);
            Log.Info("[CHEAT] " + text);
            Ui.ChatOverlay.System(text);
        }

        // -- status and audit -------------------------------------------------------------------

        public static object Status()
        {
            var sim = Sim;
            var remaining = Gate.Remaining(Now);
            return new
            {
                capability = Gate.Capability,
                capability_source = CheatConfig.CapabilitySource,
                armed = remaining.HasValue,
                seconds_left = remaining.HasValue ? (int?)remaining.Value.TotalSeconds : null,
                ops_left = Gate.OpsLeft,
                arm_session = Gate.ArmSession,
                config = new
                {
                    arm_hotkey = CheatConfig.ArmHotkey,
                    settings_hotkey = CheatConfig.SettingsHotkey,
                    arm_minutes = CheatConfig.ArmMinutes,
                    arm_max_ops = CheatConfig.ArmMaxOps,
                    source = CheatConfig.PanelOverrides ? "in-game panel" : "mod.json",
                    note = "read-only here: only the operator can change these (mod.json or the in-game panel)",
                },
                campaign = sim == null ? null : new
                {
                    id = sim.InstanceGUID,
                    load_epoch = loadEpoch,
                    company = sim.CompanyName,
                    ironman = sim.IsIronmanCampaign,
                    marked = sim.CompanyTags.Contains(MarkerTag),
                },
                saves = new
                {
                    unsaved_cheats = Saves.Unsaved,
                    last_save_utc = Saves.LastSave,
                    last_save_reason = Saves.LastSaveReason,
                    note = Saves.Unsaved.Count > 0 ? "the next save of any kind (including an autosave) makes these permanent" : null,
                },
            };
        }

        private static void Audit(object record)
        {
            var sim = Sim;
            var envelope = new JObject
            {
                ["utc"] = Now.ToString("o"),
                ["campaign"] = sim == null ? null : JToken.FromObject(new
                {
                    id = sim.InstanceGUID,
                    load_epoch = loadEpoch,
                    company = sim.CompanyName,
                    date = sim.CurrentDate.ToString("yyyy-MM-dd"),
                    days_passed = sim.DaysPassed,
                    ironman = sim.IsIronmanCampaign,
                }),
            };
            foreach (var p in JObject.FromObject(record).Properties())
            {
                envelope[p.Name] = p.Value;
            }
            string line = envelope.ToString(Formatting.None);
            Log.Info("[CHEAT] " + line);
            lock (AuditLock)
            {
                try
                {
                    File.AppendAllText(auditPath, line + Environment.NewLine);
                }
                catch (Exception e)
                {
                    Log.Error("[CHEAT] audit write failed", e);
                }
            }
        }

        public static object AuditTail(int limit)
        {
            lock (AuditLock)
            {
                if (!File.Exists(auditPath))
                {
                    return new List<JToken>();
                }
                return File.ReadAllLines(auditPath).Reverse().Take(Math.Max(1, Math.Min(limit, 200)))
                    .Select(l => { try { return JToken.Parse(l); } catch { return (JToken)l; } }).ToList();
            }
        }

        // -- preview / execute ---------------------------------------------------------------------

        private static SimGameState RequireSim()
        {
            var sim = Sim ?? throw new BridgeException(409, "no campaign is loaded");
            if (!ReferenceEquals(sim, lastSim))
            {
                Tick();
            }
            return sim;
        }

        private static void RequireAllowed(SimGameState sim)
        {
            try
            {
                Gate.RequireExecutable(Now, sim.IsIronmanCampaign);
            }
            catch (RuleException e)
            {
                throw new BridgeException(403, e.Message);
            }
            if (UnityGameInstance.BattleTechGame?.Combat != null)
            {
                throw new BridgeException(409, "not during combat");
            }
            if (sim.Saving || (sim.TravelManager != null && sim.TravelManager.InTransition))
            {
                throw new BridgeException(409, "the game is saving or animating travel; retry shortly");
            }
            if (sim.InterruptQueue.IsOpen)
            {
                throw new BridgeException(409, "an interrupt is open; resolve it first");
            }
        }

        public static object Preview(string op, JObject args, string operatorRequest)
        {
            var sim = RequireSim();
            RequireAllowed(sim);
            args = args ?? new JObject();
            var spec = Ops.Describe(sim, op, args);
            CheatPlan plan;
            try
            {
                plan = Ledger.Create(op, CampaignId(sim), loadEpoch, spec.Fingerprint, Now, operatorRequest, args);
            }
            catch (RuleException e)
            {
                throw new BridgeException(400, e.Message);
            }
            Log.Info($"[CHEAT] preview {plan.Id} {op}: {spec.Summary}");
            return new
            {
                plan_id = plan.Id,
                op,
                summary = spec.Summary,
                before = spec.Before,
                expected_after = spec.ExpectedAfter,
                side_effects = spec.SideEffects,
                expires_in_seconds = (int)PlanLedger.Lifetime.TotalSeconds,
                persistence = "not saved: reloading a save from before execution undoes it, until the game saves again (any save, including autosaves)",
            };
        }

        public static object Execute(string planId)
        {
            var sim = RequireSim();
            CheatPlan plan;
            try
            {
                // Campaign and load epoch are checked first; only then is the before-state
                // recomputed from the plan's own op and arguments and compared.
                plan = Ledger.Claim(planId, CampaignId(sim), loadEpoch,
                    p => Ops.Describe(sim, p.Op, (JObject)p.Payload).Fingerprint, Now);
            }
            catch (RuleException e)
            {
                throw new BridgeException(409, e.Message);
            }
            // A retry of an executed plan only reports the stored result, so it is answered even
            // after the window closed (it used to fail the arming check first). New work is gated.
            if (plan.Executed)
            {
                return new { replay = true, plan_id = planId, result = plan.Result, note = "already executed; nothing changed" };
            }
            RequireAllowed(sim);
            var outcome = Ops.Execute(sim, plan.Op, (JObject)plan.Payload);
            Mark(sim);
            string saveState = Saves.OnCheatExecuted(plan.Id);
            // Read before Consume: spending the last budgeted op disarms and clears the session.
            var armSession = Gate.ArmSession;
            Gate.Consume();
            var result = new
            {
                plan_id = plan.Id,
                op = plan.Op,
                before = outcome.Before,
                after = outcome.After,
                save_state = saveState,
                last_save_utc = Saves.LastSave,
                last_save_reason = Saves.LastSaveReason,
                persistence = "unsaved: the next save of any kind (including an autosave) will make this permanent; reload a save from before this to undo",
            };
            Ledger.MarkExecuted(plan, result);
            Audit(new
            {
                kind = "executed",
                plan_id = plan.Id,
                op = plan.Op,
                args = plan.Payload,
                operator_request = plan.OperatorRequest,
                arm_session = armSession,
                before = outcome.Before,
                after = outcome.After,
                save_state = saveState,
                last_save_utc = Saves.LastSave,
                last_save_reason = Saves.LastSaveReason,
                persistence = "unsaved until the next save of any kind",
            });
            return result;
        }

        /// <summary>The campaign records that it has been cheated in (operator decision: on by default).</summary>
        private static void Mark(SimGameState sim)
        {
            if (!sim.CompanyTags.Contains(MarkerTag))
            {
                sim.CompanyTags.Add(MarkerTag);
            }
            if (!sim.CompanyStats.ContainsStatistic(MarkerStat))
            {
                sim.CompanyStats.AddStatistic(MarkerStat, 0);
            }
            sim.CompanyStats.ModifyStat(Source, 0, MarkerStat, StatCollection.StatOperation.Int_Add, 1);
        }
    }
}
