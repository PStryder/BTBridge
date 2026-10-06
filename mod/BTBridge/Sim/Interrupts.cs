using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.StringInterpolation;
using BattleTech.UI;
using BTBridge.Bridge;
using BTBridge.Logic;
using HBS;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BTBridge.Sim
{
    /// <summary>
    /// The campaign's modal layer. Every interrupt goes through SimGameInterruptManager; this reads
    /// the one waiting and resolves it through the UI module's own handler, because several handlers
    /// carry game effects on close: placing a bought mech, awarding rewards, quarterly morale.
    /// </summary>
    public static class Interrupts
    {
        // Set by patches (SimPatches) so events can be answered and reported without the UI.
        public static SGEventPanel EventPanel;
        public static SimGameEventDef EventDef;
        public static int EventEntryUid = -1;
        public static int AnsweredEventEntryUid = -2;
        public static SimGameEventResultSet LastEventResult;

        private static readonly string[] PopupModules =
        {
            "PauseNotification", "GenericPopup", "SGEventPanel", "SGCaptainsQuartersStatusScreen",
            "MechPlacementPopup", "RewardsPopup", "SG_FlashpointInfoPopup", "SGFlashpointEndScreen",
            // Found in the first career test: modules some interrupts render with.
            "HeavyMetalContentReviewPopup", "MercNetUpdatePopup", "ImageAndTextInfoPopup",
            "SG_CareerModeEndScoreDisplay", "SGCampaignOutcomeScreen",
        };

        public static SimGameInterruptManager.Entry CurrentEntry(SimGameState sim) =>
            Reflect.Get(sim.InterruptQueue, "curPopup") as SimGameInterruptManager.Entry;

        public static List<SimGameInterruptManager.Entry> Queued(SimGameState sim) =>
            (Reflect.Get(sim.InterruptQueue, "popups") as List<SimGameInterruptManager.Entry>) ?? new List<SimGameInterruptManager.Entry>();

        public static List<UIModule> VisiblePopups()
        {
            var found = new List<UIModule>();
            var ui = BTBridge.Ui.GameUi.Existing();
            if (ui == null)
            {
                return found;
            }
            ui.Find(m =>
            {
                if (m != null && m.Visible && m.gameObject.activeInHierarchy && PopupModules.Contains(m.GetType().Name))
                {
                    found.Add(m);
                }
                return false;
            });
            return found;
        }

        private static T Visible<T>() where T : UIModule => VisiblePopups().OfType<T>().FirstOrDefault();

        /// <summary>
        /// SimGameState.TimeMoving dereferences RoomManager, which is null while a mission is
        /// loaded (the sim UI is detached). Found in the first career test (/sim/status NRE on launch).
        /// </summary>
        private static bool SafeTimeMoving(SimGameState sim) =>
            sim.RoomManager != null && UnityGameInstance.BattleTechGame?.Combat == null && sim.TimeMoving;

        public static SimFacts Facts(SimGameState sim)
        {
            var game = UnityGameInstance.BattleTechGame;
            return new SimFacts
            {
                UxAttached = sim.UXAttached,
                ShipSet = sim.HasSimShipBeenSet,
                Saving = sim.Saving,
                InCombat = game?.Combat != null,
                ContractCompleting = sim.CompletedContract != null,
                MilestoneContractPending = sim.PendingMilestoneContract != null,
                InterruptOpen = sim.InterruptQueue != null && sim.InterruptQueue.IsOpen,
                InterruptQueued = sim.InterruptQueue != null && sim.InterruptQueue.HasQueue,
                ConversationOn = sim.ConversationManager != null && sim.ConversationManager.IsOn,
                VideoPlaying = sim.VideoPlayerActive,
                InTransition = sim.TravelManager != null && sim.TravelManager.InTransition,
                TimeMoving = SafeTimeMoving(sim),
                MechLabOpen = sim.RoomManager?.MechBayRoom != null && sim.RoomManager.MechBayRoom.mechLabOpen,
                LanceConfigOpen = sim.RoomManager?.CmdCenterRoom != null && sim.RoomManager.CmdCenterRoom.lanceConfigOpen,
                VisiblePopups = VisiblePopups().Count,
            };
        }

        public static object Status(SimGameState sim)
        {
            var facts = Facts(sim);
            var blockers = Idle.Blockers(facts);
            var cur = CurrentEntry(sim);
            return new
            {
                idle = blockers.Count == 0,
                blockers,
                operator_messages = Ui.ChatOverlay.UnreadForAgent(),
                room = sim.CurRoomState.ToString(),
                travel_state = sim.TravelManager?.TravelState.ToString(),
                time_moving = SafeTimeMoving(sim),
                date = sim.CurrentDate.ToString("yyyy-MM-dd"),
                days_passed = sim.DaysPassed,
                system = sim.CurSystem?.Name,
                funds = sim.Funds,
                interrupt = cur == null ? null : cur.type.ToString(),
                queued = Queued(sim).Select(e => e.type.ToString()).ToList(),
                conversation = facts.ConversationOn,
                visible_popups = VisiblePopups().Select(m => m.GetType().Name).ToList(),
                time_job = TimeControl.View(),
                contract_job = Contracts.JobView(),
                travel_job = Navigation.JobView(),
            };
        }

        // -- reading -------------------------------------------------------------------------

        public static object Describe(SimGameState sim)
        {
            if (sim.ConversationManager != null && sim.ConversationManager.IsOn)
            {
                return Conversation(sim);
            }
            var cur = CurrentEntry(sim);
            if (cur == null)
            {
                var stray = VisiblePopups();
                if (stray.Count > 0)
                {
                    return new { open = true, kind = "Popup", module = stray[0].GetType().Name, answer = new[] { "{\"choice\": \"enter\"}", "{\"choice\": \"escape\"}" } };
                }
                return new { open = false, queued = Queued(sim).Select(e => e.type.ToString()).ToList() };
            }
            var p = cur.parameters ?? new List<object>();
            switch (cur.type)
            {
                case SimGameInterruptManager.InterruptType.PauseNotification:
                case SimGameInterruptManager.InterruptType.TravelContractNotification:
                    return new
                    {
                        open = true,
                        kind = cur.type.ToString(),
                        title = p.ElementAtOrDefault(0) as string,
                        message = p.ElementAtOrDefault(1) as string,
                        primary = p.ElementAtOrDefault(5) as string,
                        secondary = p.ElementAtOrDefault(7) as string,
                        answer = new[] { "{\"choice\": \"primary\"}", "{\"choice\": \"secondary\"}" },
                        note = "a 'Mech purchased/added notification places the mech only when answered",
                    };
                case SimGameInterruptManager.InterruptType.GenericPopup:
                case SimGameInterruptManager.InterruptType.MechwarriorHasDiedNotification:
                    return new
                    {
                        open = true,
                        kind = cur.type.ToString(),
                        title = p.ElementAtOrDefault(0) as string,
                        message = p.ElementAtOrDefault(1) as string,
                        buttons = ButtonLabels(Visible<GenericPopup>()),
                        answer = new[] { "{\"button\": index}", "{\"choice\": \"enter\" | \"escape\"}" },
                    };
                case SimGameInterruptManager.InterruptType.EventPopup:
                    return Event(sim);
                case SimGameInterruptManager.InterruptType.FinancialReport:
                    return new
                    {
                        open = true,
                        kind = "FinancialReport",
                        funds = sim.Funds,
                        current_level = sim.ExpenditureLevel.ToString(),
                        levels = Enum.GetValues(typeof(EconomyScale)).Cast<EconomyScale>().Select(l => new
                        {
                            level = l.ToString(),
                            quarterly_cost = sim.GetExpenditures(l),
                            morale_change = sim.ExpenditureMoraleValue != null && sim.ExpenditureMoraleValue.ContainsKey(l) ? sim.ExpenditureMoraleValue[l] : 0,
                        }).ToList(),
                        answer = new[] { "{\"expense_level\": \"Normal\"}" },
                        note = "the chosen level is applied with its morale change, once per quarter",
                    };
                case SimGameInterruptManager.InterruptType.MechPlacementPopup:
                {
                    var popup = Visible<MechPlacementPopup>();
                    var mech = popup == null ? null : Reflect.Get(popup, "purchasedMech") as MechDef;
                    var chassis = popup == null ? null : Reflect.Get(popup, "purchasedChassis") as ChassisDef;
                    return new
                    {
                        open = true,
                        kind = "MechPlacementPopup",
                        mech = mech?.Description?.Name ?? chassis?.Description?.Name,
                        note = "all bays are full",
                        answer = new[] { "{\"action\": \"store\"}" },
                    };
                }
                case SimGameInterruptManager.InterruptType.RewardsPopup:
                {
                    var popup = Visible<RewardsPopup>();
                    var items = popup == null ? null : (Reflect.Get(popup, "allShopDefItems") as List<ShopDefItem>)?.Select(i => new { id = i.ID, type = i.Type.ToString(), count = i.Count }).ToList();
                    return new { open = true, kind = "RewardsPopup", ready = popup != null, items, answer = new[] { "{\"choice\": \"collect\"}" } };
                }
                case SimGameInterruptManager.InterruptType.FlashpointEnteredSystemNotification:
                    return new { open = true, kind = cur.type.ToString(), answer = new[] { "{\"accept\": true | false}" } };
                case SimGameInterruptManager.InterruptType.HeavyMetalLootPopup:
                    return new
                    {
                        open = true,
                        kind = cur.type.ToString(),
                        title = "Heavy Metal starter content",
                        note = "accepting adds the Heavy Metal career starter item collection (a rewards popup follows)",
                        answer = new[] { "{\"accept\": true | false}" },
                    };
                default:
                    return new
                    {
                        open = true,
                        kind = cur.type.ToString(),
                        modules = VisiblePopups().Select(m => m.GetType().Name).ToList(),
                        answer = new[] { "{\"choice\": \"enter\"}", "{\"choice\": \"escape\"}" },
                    };
            }
        }

        private static List<string> ButtonLabels(GenericPopup popup)
        {
            if (popup == null)
            {
                return new List<string>();
            }
            var buttons = Reflect.Get(popup, "buttons") as System.Collections.IList;
            var labels = new List<string>();
            if (buttons == null)
            {
                return labels;
            }
            foreach (var b in buttons)
            {
                string text = null;
                try
                {
                    text = Reflect.Get(b, "Text")?.ToString();
                }
                catch (MissingMemberException)
                {
                }
                labels.Add(text ?? (b as Component)?.gameObject.name);
            }
            return labels;
        }

        private static string Text(SimGameState sim, string template) =>
            string.IsNullOrEmpty(template) ? template : GameText.Plain(Interpolator.Interpolate(template, sim.Context, true));

        private static object Event(SimGameState sim)
        {
            var evt = EventDef;
            if (evt == null || EventPanel == null)
            {
                return new { open = true, kind = "EventPopup", ready = false, note = "event panel not shown yet; retry shortly" };
            }
            bool answered = AnsweredEventEntryUid == EventEntryUid;
            return new
            {
                open = true,
                kind = "EventPopup",
                ready = true,
                answered,
                title = Text(sim, evt.Description?.Name),
                description = Text(sim, evt.Description?.Details),
                options = evt.Options.Select((o, i) => new
                {
                    index = i,
                    text = Text(sim, o.Description?.Name),
                    requirement = Text(sim, o.Description?.Details),
                    available = sim.MeetsRequirements(o.RequirementList),
                }).ToList(),
                result = answered ? ResultText(sim) : null,
                answer = answered ? new[] { "{\"choice\": \"dismiss\"}" } : new[] { "{\"option\": index}" },
            };
        }

        private static List<string> ResultText(SimGameState sim)
        {
            if (LastEventResult == null)
            {
                return null;
            }
            try
            {
                return sim.BuildSimGameResults(LastEventResult.Results, sim.Context).Select(r => GameText.Plain(r.Text?.ToString())).ToList();
            }
            catch (Exception e)
            {
                return new List<string> { "(could not describe results: " + e.Message + ")" };
            }
        }

        private static object Conversation(SimGameState sim)
        {
            var cm = sim.ConversationManager;
            bool locked = cm.IsInputLocked();
            object node = Reflect.Get(cm, "currentNode");
            string text = null;
            try
            {
                text = node == null ? null : Reflect.Get(node, "text") as string;
            }
            catch (MissingMemberException)
            {
            }
            var responses = new List<object>();
            if (Reflect.Get(cm, "responseData") is System.Collections.IEnumerable data)
            {
                foreach (var r in data)
                {
                    var link = Reflect.Get(r, "link");
                    string label = null;
                    try
                    {
                        label = link == null ? null : Reflect.Get(link, "responseText") as string;
                    }
                    catch (MissingMemberException)
                    {
                    }
                    responses.Add(new { index = (int)Reflect.Get(r, "index"), text = label, enabled = (bool)Reflect.Get(r, "isEnabled") });
                }
            }
            return new
            {
                open = true,
                kind = "Conversation",
                busy = locked || (bool)Reflect.Get(cm, "isAnimating"),
                waiting_for_continue = (bool)Reflect.Get(cm, "waitForContinue"),
                node = CurrentNodeIndex(cm),
                text,
                responses,
                answer = new[] { "{\"response\": index, \"node\": node}", "{\"choice\": \"continue\"}" },
            };
        }

        // -- answering ----------------------------------------------------------------------------

        public static object Resolve(SimGameState sim, JObject answer)
        {
            answer = answer ?? new JObject();
            if (sim.ConversationManager != null && sim.ConversationManager.IsOn)
            {
                return AnswerConversation(sim, answer);
            }
            var cur = CurrentEntry(sim);
            if (cur == null)
            {
                var stray = VisiblePopups().FirstOrDefault();
                if (stray != null)
                {
                    return PressKey(stray, answer.Value<string>("choice") ?? "enter");
                }
                if (sim.InterruptQueue.HasQueue)
                {
                    sim.InterruptQueue.DisplayIfAvailable();
                    return new { resolved = false, note = "an interrupt was queued; it is being shown now, read it again" };
                }
                throw new BridgeException(409, "nothing is waiting");
            }
            switch (cur.type)
            {
                case SimGameInterruptManager.InterruptType.PauseNotification:
                case SimGameInterruptManager.InterruptType.TravelContractNotification:
                {
                    var n = Visible<PauseNotification>() ?? throw new BridgeException(409, "notification not on screen yet; retry");
                    string choice = answer.Value<string>("choice") ?? "primary";
                    var button = Reflect.Get(n, choice == "secondary" ? "SecondaryButton" : "PrimaryButton") as Component;
                    if (button == null || !button.gameObject.activeInHierarchy)
                    {
                        throw new BridgeException(400, $"this notification has no {choice} button");
                    }
                    Reflect.Click(button);
                    return Done(cur, choice);
                }
                case SimGameInterruptManager.InterruptType.GenericPopup:
                case SimGameInterruptManager.InterruptType.MechwarriorHasDiedNotification:
                {
                    var popup = Visible<GenericPopup>() ?? throw new BridgeException(409, "popup not on screen yet; retry");
                    int? index = answer.Value<int?>("button");
                    if (index.HasValue)
                    {
                        var buttons = Reflect.Get(popup, "buttons") as System.Collections.IList;
                        if (buttons == null || index.Value < 0 || index.Value >= buttons.Count)
                        {
                            throw new BridgeException(400, "no such button");
                        }
                        Reflect.Click(buttons[index.Value]);
                        return Done(cur, "button " + index.Value);
                    }
                    return PressKey(popup, answer.Value<string>("choice") ?? "enter");
                }
                case SimGameInterruptManager.InterruptType.EventPopup:
                    return AnswerEvent(sim, answer);
                case SimGameInterruptManager.InterruptType.FinancialReport:
                {
                    var screen = Visible<SGCaptainsQuartersStatusScreen>() ?? throw new BridgeException(409, "report not on screen yet; retry");
                    string level = answer.Value<string>("expense_level") ?? sim.ExpenditureLevel.ToString();
                    EconomyScale scale;
                    try
                    {
                        scale = (EconomyScale)Enum.Parse(typeof(EconomyScale), level, ignoreCase: true);
                    }
                    catch (ArgumentException)
                    {
                        throw new BridgeException(400, "expense_level must be one of " + string.Join(", ", Enum.GetNames(typeof(EconomyScale))));
                    }
                    // Same as OnExpenditureLevelConfirmed: level + its morale change, then dismiss.
                    sim.SetExpenditureLevel(scale, updateMorale: true);
                    screen.Dismiss();
                    return Done(cur, "expense level " + scale);
                }
                case SimGameInterruptManager.InterruptType.MechPlacementPopup:
                {
                    var popup = Visible<MechPlacementPopup>() ?? throw new BridgeException(409, "placement popup not on screen yet; retry");
                    // Mirrors ConfirmStoreMech for the newly acquired unit.
                    if (Reflect.Get(popup, "purchasedMech") is MechDef mech)
                    {
                        sim.UnreadyMech(-1, mech);
                    }
                    else if (Reflect.Get(popup, "purchasedChassis") is ChassisDef chassis)
                    {
                        var stored = new MechDef(chassis.Description, chassis.Description.Id, new MechComponentRef[0], sim.DataManager);
                        sim.AddMech(-1, stored, active: false, forcePlacement: true, displayMechPopup: false);
                    }
                    Reflect.Call(popup, "Close");
                    return Done(cur, "stored");
                }
                case SimGameInterruptManager.InterruptType.RewardsPopup:
                {
                    var popup = Visible<RewardsPopup>() ?? throw new BridgeException(409, "rewards are still being rolled; retry");
                    popup.OnClose();
                    return Done(cur, "collected");
                }
                case SimGameInterruptManager.InterruptType.FlashpointEnteredSystemNotification:
                {
                    var popup = VisiblePopups().FirstOrDefault(m => m.GetType().Name == "SG_FlashpointInfoPopup")
                        ?? throw new BridgeException(409, "flashpoint popup not on screen yet; retry");
                    bool accept = answer.Value<bool?>("accept") ?? true;
                    Reflect.Call(popup, accept ? "OnConfirm" : "OnCancel");
                    return Done(cur, accept ? "accepted" : "declined");
                }
                case SimGameInterruptManager.InterruptType.HeavyMetalLootPopup:
                {
                    var popup = VisiblePopups().FirstOrDefault(m => m.GetType().Name == "HeavyMetalContentReviewPopup")
                        ?? throw new BridgeException(409, "Heavy Metal popup not on screen yet; retry");
                    bool accept = answer.Value<bool?>("accept") ?? true;
                    // Close(accepted) runs the accept/decline callback (accept queues the rewards popup).
                    Reflect.Call(popup, "Close", accept);
                    return Done(cur, accept ? "accepted" : "declined");
                }
                default:
                {
                    var module = VisiblePopups().FirstOrDefault() ?? throw new BridgeException(409, $"{cur.type} not on screen yet; retry");
                    return PressKey(module, answer.Value<string>("choice") ?? "enter");
                }
            }
        }

        private static object Done(SimGameInterruptManager.Entry entry, string how)
        {
            Log.Info($"interrupt {entry?.type} resolved: {how}");
            return new { resolved = true, interrupt = entry?.type.ToString(), how };
        }

        private static object PressKey(UIModule module, string choice)
        {
            bool handled = choice == "escape" ? module.HandleEscapeKeypress() : module.HandleEnterKeypress();
            if (!handled)
            {
                throw new BridgeException(409, $"{module.GetType().Name} did not accept '{choice}'");
            }
            Log.Info($"popup {module.GetType().Name}: pressed {choice}");
            return new { resolved = true, module = module.GetType().Name, how = choice };
        }

        private static object AnswerEvent(SimGameState sim, JObject answer)
        {
            var panel = EventPanel ?? throw new BridgeException(409, "event panel not shown yet; retry");
            bool answered = AnsweredEventEntryUid == EventEntryUid;
            if (answered)
            {
                // Dismiss only once the panel has switched to its result view (0.2 s coroutine).
                string state = Reflect.Get(panel, "curState")?.ToString();
                if (state != "RESULT")
                {
                    throw new BridgeException(409, "the event result is still being shown; retry in a moment");
                }
                panel.Dismiss();
                return new { resolved = true, interrupt = "EventPopup", how = "dismissed" };
            }
            int index = answer.Value<int?>("option") ?? throw new BridgeException(400, "answer with {\"option\": index}");
            var options = EventDef.Options;
            if (index < 0 || index >= options.Length)
            {
                throw new BridgeException(400, "no such option");
            }
            if (!sim.MeetsRequirements(options[index].RequirementList))
            {
                throw new BridgeException(400, "requirements for that option are not met");
            }
            // OnOptionSelected applies results immediately; a second call before the panel reaches its
            // result view would apply them again, so mark the event answered first.
            AnsweredEventEntryUid = EventEntryUid;
            LastEventResult = null;
            panel.OnOptionSelected(options[index]);
            Log.Info($"event '{EventDef.Description?.Id}' answered with option {index}");
            return new
            {
                resolved = false,
                interrupt = "EventPopup",
                how = "option " + index,
                result = ResultText(sim),
                next = "read the interrupt again, then {\"choice\": \"dismiss\"}",
            };
        }

        private static int CurrentNodeIndex(SimGameConversationManager cm)
        {
            var node = Reflect.Get(cm, "currentNode");
            return node == null ? -1 : (int)Reflect.Get(node, "index");
        }

        /// <summary>Exactly what the dialog UI offers: SimGameConversationManager's responseData.</summary>
        private static List<BTBridge.Logic.OfferedResponse> OfferedResponses(SimGameConversationManager cm)
        {
            var offered = new List<BTBridge.Logic.OfferedResponse>();
            if (Reflect.Get(cm, "responseData") is System.Collections.IEnumerable data)
            {
                foreach (var r in data)
                {
                    offered.Add(new BTBridge.Logic.OfferedResponse { Index = (int)Reflect.Get(r, "index"), Enabled = (bool)Reflect.Get(r, "isEnabled") });
                }
            }
            return offered;
        }

        private static object AnswerConversation(SimGameState sim, JObject answer)
        {
            var cm = sim.ConversationManager;
            if (cm.IsInputLocked() || (bool)Reflect.Get(cm, "isAnimating"))
            {
                throw new BridgeException(409, "the conversation is animating; retry in a moment");
            }
            int? response = answer.Value<int?>("response");
            if (response.HasValue)
            {
                var problem = BTBridge.Logic.ConversationRules.Problem(response.Value, answer.Value<int?>("node"),
                    CurrentNodeIndex(cm), OfferedResponses(cm));
                if (problem != null)
                {
                    throw new BridgeException(problem.StartsWith("the conversation has moved on") ? 409 : 400, problem);
                }
                cm.SelectResponse(response.Value);
                return new { resolved = false, kind = "Conversation", how = "response " + response.Value };
            }
            if (!(bool)Reflect.Get(cm, "waitForContinue"))
            {
                throw new BridgeException(400, "this line needs a response: {\"response\": index}");
            }
            cm.InputContinue();
            return new { resolved = false, kind = "Conversation", how = "continue" };
        }
    }
}
