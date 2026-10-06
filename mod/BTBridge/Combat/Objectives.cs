using System;
using System.Linq;
using BattleTech;
using BattleTech.Framework;
using BattleTech.StringInterpolation;

namespace BTBridge.Combat
{
    /// <summary>
    /// What the mission wants: the contract's briefing text and the live objectives with status and
    /// progress, as the combat HUD's objective list shows them. The player lance gets no objective
    /// guidance from the encounter (unlike enemy AI teams), so an agent needs this to play to win.
    /// </summary>
    public static class Objectives
    {
        private static string Text(Contract c, string template)
        {
            if (string.IsNullOrEmpty(template) || c?.GameContext == null)
            {
                return template;
            }
            try
            {
                return BTBridge.Logic.GameText.Plain(Interpolator.Interpolate(template, c.GameContext, true));
            }
            catch
            {
                return template;
            }
        }

        public static object PlayerObjectives(Contract c) =>
            c?.Override?.contractObjectiveList?.Select(o => new
            {
                title = Text(c, o.title),
                description = Text(c, o.description),
                primary = o.isPrimary,
            }).ToList();

        public static object Briefing(Contract c) => c == null ? null : new
        {
            contract = c.Name,
            type = c.ContractTypeValue?.Name,
            short_description = c.ShortDescription,
            long_description = c.LongDescription,
            // contractObjectiveList is what the player is shown (the combat HUD lists it). The
            // encounter's objectiveList also holds the AI's hidden objectives; never expose it.
            planned_objectives = PlayerObjectives(c),
        };

        public static object Live(CombatGameState combat)
        {
            var contract = combat?.ActiveContract;
            var layer = combat?.EncounterLayerData ?? UnityEngine.Object.FindObjectOfType<EncounterLayerData>();
            if (layer == null)
            {
                return null;
            }
            try
            {
                var contractObjectives = layer.contractObjectiveGameLogicList ?? new ContractObjectiveGameLogic[0];
                var detailed = layer.GetComponentsInChildren<ObjectiveGameLogic>(true)
                    .Where(o => o != null && o.displayToUser && !o.IsHidden)
                    .ToList();
                return new
                {
                    contract = contractObjectives.Select(o => new
                    {
                        title = Text(contract, o.title),
                        description = Text(contract, o.description),
                        primary = o.primary,
                        status = o.CurrentObjectiveStatus.ToString(),
                    }).ToList(),
                    detailed = detailed.Select(o => new
                    {
                        title = Text(contract, o.title),
                        status = o.CurrentObjectiveStatus.ToString(),
                        progress = SafeProgress(o),
                    }).ToList(),
                };
            }
            catch (Exception e)
            {
                return new { error = e.Message };
            }
        }

        private static string SafeProgress(ObjectiveGameLogic o)
        {
            try
            {
                return o.showProgress ? o.GetProgressText()?.ToString() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
