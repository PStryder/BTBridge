using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BTBridge.Bridge;
using BTBridge.Logic;

namespace BTBridge.Sim
{
    /// <summary>
    /// Pilots, hiring, store, Argo upgrades, finances, flashpoints. Each write mirrors the method the
    /// corresponding screen calls, plus the checks that screen performs (several game methods, such as
    /// HirePilot and Shop.Purchase, do none of their own).
    /// </summary>
    public static class Company
    {
        private static void RequireQuiet(SimGameState sim)
        {
            if (UnityGameInstance.BattleTechGame?.Combat != null)
            {
                throw new BridgeException(409, "in combat");
            }
            if (sim.InterruptQueue.IsOpen || sim.InterruptQueue.HasQueue)
            {
                throw new BridgeException(409, "an interrupt is waiting; resolve it first");
            }
        }

        private static IEnumerable<Pilot> AllPilots(SimGameState sim)
        {
            if (sim.Commander != null)
            {
                yield return sim.Commander;
            }
            foreach (var p in sim.PilotRoster)
            {
                yield return p;
            }
        }

        private static Pilot FindPilot(SimGameState sim, string key) =>
            AllPilots(sim).FirstOrDefault(p => p.GUID == key || string.Equals(p.Callsign, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeException(404, $"no pilot '{key}'");

        // -- pilots --------------------------------------------------------------------------------

        public static object Pilots(SimGameState sim) => new
        {
            max_roster = sim.GetMaxMechWarriors(),
            pilots = AllPilots(sim).Select(p => PilotView(sim, p)).ToList(),
        };

        private static object PilotView(SimGameState sim, Pilot p) => new
        {
            guid = p.GUID,
            callsign = p.Callsign,
            commander = p == sim.Commander,
            skills = new { gunnery = p.Gunnery, piloting = p.Piloting, guts = p.Guts, tactics = p.Tactics },
            xp = new { unspent = p.UnspentXP, spent = p.SpentXP },
            health = p.Health,
            injuries = p.Injuries,
            can_pilot = p.CanPilot,
            // Passive stat abilities often have an empty display name; fall back to the id.
            abilities = p.Abilities?.Select(a => string.IsNullOrEmpty(a.Def?.Description?.Name) ? a.Def?.Description?.Id : a.Def.Description.Name).ToList(),
            salary = p == sim.Commander ? 0 : sim.GetMechWarriorValue(p.pilotDef),
        };

        private static int SkillValue(Pilot p, string stat)
        {
            switch (stat)
            {
                case "Gunnery": return p.Gunnery;
                case "Piloting": return p.Piloting;
                case "Guts": return p.Guts;
                default: return p.Tactics;
            }
        }

        /// <summary>Raise a skill, one pip at a time exactly as SGBarracksAdvancementPanel.SetTempPilotSkill does.</summary>
        public static object Train(SimGameState sim, string pilotKey, string skill, int target, bool confirm)
        {
            RequireQuiet(sim);
            var pilot = FindPilot(sim, pilotKey);
            string stat;
            List<int> pips;
            int cost;
            try
            {
                stat = Skills.Normalize(skill);
                pips = Skills.PipsToBuy(SkillValue(pilot, stat), target, sim.GetLevelCost, pilot.UnspentXP, out cost);
            }
            catch (RuleException e)
            {
                throw new BridgeException(400, e.Message);
            }
            var current = pilot;
            var gained = new List<object>();
            foreach (int pip in pips)
            {
                var def = current.ToPilotDef(true);
                def.DataManager = sim.DataManager;
                def.ForceRefreshAbilityDefs();
                if (sim.AbilityTree.TryGetValue(stat, out var byLevel) && byLevel.TryGetValue(pip, out var abilities))
                {
                    foreach (var ability in abilities)
                    {
                        if (sim.CanPilotTakeAbility(def, ability))
                        {
                            def.abilityDefNames.Add(ability.Description.Id);
                            gained.Add(new { name = string.IsNullOrEmpty(ability.Description.Name) ? ability.Description.Id : ability.Description.Name, primary = ability.IsPrimaryAbility, at_level = pip + 1 });
                        }
                    }
                }
                def.ForceRefreshAbilityDefs();
                var next = new Pilot(def, current.GUID, true);
                next.pilotDef.DataManager = current.pilotDef.DataManager;
                next.SpendExperience(0, "Advancement", (uint)sim.GetLevelCost(pip));
                next.ModifyPilotStat_Barracks(0, "Advancement", stat, (uint)(pip + 1));
                current = next;
            }
            if (!confirm)
            {
                return new
                {
                    preview = true,
                    pilot = pilot.Callsign,
                    skill = stat,
                    from = SkillValue(pilot, stat),
                    to = target,
                    xp_cost = cost,
                    abilities_gained = gained,
                    note = "primary abilities are permanent (max 2 primaries + 1 specialist); repeat with confirm: true",
                };
            }
            sim.UpgradePilot(current);
            Log.Info($"trained {pilot.Callsign}: {stat} -> {target} for {cost} XP");
            return new { trained = true, pilot = PilotView(sim, current), abilities_gained = gained };
        }

        public static object HiringHall(SimGameState sim)
        {
            var system = sim.CurSystem;
            return new
            {
                system = system.Name,
                can_hire_here = sim.TravelManager?.TravelState == SimGameTravelStatus.IN_SYSTEM,
                roster = new { current = sim.PilotRoster.Count, max = sim.GetMaxMechWarriors() },
                pilots = (system.AvailablePilots ?? new List<PilotDef>()).Select(d => new
                {
                    id = d.Description.Id,
                    callsign = d.Description.Callsign,
                    skills = new { gunnery = d.BaseGunnery, piloting = d.BasePiloting, guts = d.BaseGuts, tactics = d.BaseTactics },
                    hiring_cost = system.GetPurchaseCostAfterReputationModifier(sim.GetMechWarriorHiringCost(d)),
                    salary = sim.GetMechWarriorValue(d),
                }).ToList(),
            };
        }

        /// <summary>StarSystem.HirePilot does no checks; these mirror SG_HiringHall_Screen.CanHireSelectedPilot.</summary>
        public static object Hire(SimGameState sim, string pilotDefId)
        {
            RequireQuiet(sim);
            var system = sim.CurSystem;
            var def = system.AvailablePilots?.FirstOrDefault(d => d.Description.Id == pilotDefId)
                ?? throw new BridgeException(404, $"no pilot '{pilotDefId}' in this hiring hall");
            if (sim.TravelManager?.TravelState != SimGameTravelStatus.IN_SYSTEM)
            {
                throw new BridgeException(409, "can only hire while in a system");
            }
            if (sim.PilotRoster.Count >= sim.GetMaxMechWarriors())
            {
                throw new BridgeException(400, "the barracks are full");
            }
            var candidate = new Pilot(def, "hire-check", false);
            if (!sim.CanMechWarriorBeHiredAccordingToMRBRating(candidate))
            {
                throw new BridgeException(400, "your MRB rating is too low for this pilot");
            }
            if (!sim.CanMechWarriorBeHiredAccordingToMorale(candidate))
            {
                throw new BridgeException(400, "company morale is too low for this pilot");
            }
            int cost = system.GetPurchaseCostAfterReputationModifier(sim.GetMechWarriorHiringCost(def));
            if (cost > sim.Funds)
            {
                throw new BridgeException(400, $"hiring costs {cost:N0}, funds {sim.Funds:N0}");
            }
            system.HirePilot(def);
            Log.Info($"hired {def.Description.Callsign} for {cost}");
            return new { hired = def.Description.Callsign, cost, funds = sim.Funds };
        }

        public static object Dismiss(SimGameState sim, string pilotKey)
        {
            RequireQuiet(sim);
            var pilot = FindPilot(sim, pilotKey);
            if (pilot == sim.Commander)
            {
                throw new BridgeException(400, "the commander cannot be dismissed");
            }
            bool ok = sim.DismissPilot(pilot);
            return new { dismissed = ok, pilot = pilot.Callsign };
        }

        // -- store ----------------------------------------------------------------------------------

        private static Shop GetShop(SimGameState sim, string which)
        {
            var s = sim.CurSystem;
            switch ((which ?? "system").ToLowerInvariant())
            {
                case "system":
                    return s.CanUseSystemStore() ? s.SystemShop : throw new BridgeException(400, "the system store is not available to you here");
                case "faction":
                    return s.CanUseFactionStore() ? s.FactionShop : throw new BridgeException(400, "the faction store needs an alliance with the owner");
                case "black_market":
                    return s.CanUseBlackMarketStore() ? s.BlackMarketShop : throw new BridgeException(400, "no black market access here");
                default:
                    throw new BridgeException(400, "shop must be system | faction | black_market");
            }
        }

        public static object Store(SimGameState sim, string which)
        {
            var shop = GetShop(sim, which);
            return new
            {
                shop = which ?? "system",
                funds = sim.Funds,
                items = shop.ActiveInventory.Select(i => new
                {
                    id = i.ID,
                    type = i.Type.ToString(),
                    name = SafeName(shop, i),
                    count = i.IsInfinite ? (int?)null : i.Count,
                    price = shop.GetPrice(i, i.IsInfinite ? Shop.PurchaseType.Normal : Shop.PurchaseType.Special, shop.ThisShopType),
                }).ToList(),
            };
        }

        private static string SafeName(Shop shop, ShopDefItem item)
        {
            try
            {
                return shop.GetItemDescription(item)?.UIName ?? shop.GetItemDescription(item)?.Name;
            }
            catch
            {
                return null;
            }
        }

        public static object Buy(SimGameState sim, string which, string id, int count)
        {
            RequireQuiet(sim);
            if (sim.RoomManager?.MechBayRoom != null && sim.RoomManager.MechBayRoom.mechLabOpen)
            {
                throw new BridgeException(409, "close the mechlab first");
            }
            var shop = GetShop(sim, which);
            count = Math.Max(1, Math.Min(count, 20));
            var bought = 0;
            int spent = 0;
            for (int n = 0; n < count; n++)
            {
                var item = shop.ActiveInventory.FirstOrDefault(i => i.ID == id && (i.IsInfinite || i.Count > 0));
                if (item == null)
                {
                    if (bought == 0)
                    {
                        throw new BridgeException(404, $"'{id}' is not (or no longer) in stock");
                    }
                    break;
                }
                var type = item.IsInfinite ? Shop.PurchaseType.Normal : Shop.PurchaseType.Special;
                int price = shop.GetPrice(item, type, shop.ThisShopType);
                // Shop.Purchase decrements limited stock before checking funds; check first.
                if (price > sim.Funds)
                {
                    if (bought == 0)
                    {
                        throw new BridgeException(400, $"costs {price:N0}, funds {sim.Funds:N0}");
                    }
                    break;
                }
                if (!shop.Purchase(item.ID, type, item.Type))
                {
                    break;
                }
                bought++;
                spent += price;
            }
            Log.Info($"bought {bought}x {id} for {spent}");
            return new
            {
                bought,
                spent,
                funds = sim.Funds,
                note = "a bought 'Mech arrives through a notification interrupt; answer it to place the mech",
            };
        }

        public static object Sellables(SimGameState sim, string which)
        {
            var shop = GetShop(sim, which);
            return new
            {
                items = shop.GetAllInventoryShopItems().Select(i => new { id = i.ID, type = i.Type.ToString(), count = i.Count, name = SafeName(shop, i) }).ToList(),
            };
        }

        public static object Sell(SimGameState sim, string which, string id, string type, int count)
        {
            RequireQuiet(sim);
            var shop = GetShop(sim, which);
            int sold = 0;
            for (int n = 0; n < Math.Max(1, Math.Min(count, 50)); n++)
            {
                var item = shop.GetAllInventoryShopItems().FirstOrDefault(i => i.ID == id && (type == null || i.Type.ToString() == type) && i.Count > 0);
                if (item == null)
                {
                    break;
                }
                if (item.Type == ShopItemType.MechPart)
                {
                    throw new BridgeException(400, "mech parts cannot be sold");
                }
                if (!shop.SellInventoryItem(item))
                {
                    break;
                }
                sold++;
            }
            if (sold == 0)
            {
                throw new BridgeException(404, $"nothing sellable matching '{id}'");
            }
            return new { sold, funds = sim.Funds };
        }

        // -- Argo ------------------------------------------------------------------------------------

        public static object Argo(SimGameState sim)
        {
            var upgrades = sim.DataManager.ShipUpgradeDefs.Select(kv => kv.Value).Where(u => u != null).ToList();
            return new
            {
                in_progress = sim.CurrentUpgradeEntry?.Description,
                owned = upgrades.Where(u => sim.HasShipUpgrade(u.Description.Id)).Select(u => u.Description.Name).ToList(),
                available = upgrades
                    .Where(u => !sim.HasShipUpgrade(u.Description.Id) && !sim.UpgradeInProgress(u.Description.Id) && sim.HasShipUpgrade(u.RequiredModules))
                    .Select(u => new
                    {
                        id = u.Description.Id,
                        name = u.Description.Name,
                        details = u.Description.Details,
                        location = u.Location.ToString(),
                        price = UpgradePrice(sim, u),
                        added_monthly_cost = u.AdditionalCost,
                        tech_cost = u.TechCost,
                        requirements_met = u.Requirements == null || sim.MeetsRequirements(u.Requirements),
                    }).ToList(),
                note = "one upgrade builds at a time; queueing another cancels and refunds the current one",
            };
        }

        private static int UpgradePrice(SimGameState sim, ShipModuleUpgrade u) =>
            (int)Math.Ceiling(u.PurchaseCost * sim.Constants.CareerMode.ArgoUpgradeCostMultiplier);

        public static object BuyUpgrade(SimGameState sim, string id)
        {
            RequireQuiet(sim);
            if (!sim.DataManager.ShipUpgradeDefs.TryGet(id ?? "", out var u))
            {
                throw new BridgeException(404, $"no upgrade '{id}'");
            }
            if (sim.HasShipUpgrade(id) || sim.UpgradeInProgress(id))
            {
                throw new BridgeException(400, "already owned or in progress");
            }
            if (!sim.HasShipUpgrade(u.RequiredModules))
            {
                throw new BridgeException(400, "prerequisite upgrades are missing");
            }
            if (u.Requirements != null && !sim.MeetsRequirements(u.Requirements))
            {
                throw new BridgeException(400, "requirements are not met");
            }
            if (sim.CurrentUpgradeEntry != null)
            {
                throw new BridgeException(409, "another upgrade is building; queueing would cancel it (not done automatically)");
            }
            int price = UpgradePrice(sim, u);
            if (price > sim.Funds)
            {
                throw new BridgeException(400, $"costs {price:N0}, funds {sim.Funds:N0}");
            }
            sim.QueueArgoUpgrade(u);
            Log.Info($"argo upgrade queued: {u.Description.Name}");
            return new { queued = u.Description.Name, price, funds = sim.Funds };
        }

        // -- finances + reputation ----------------------------------------------------------------

        public static object Finances(SimGameState sim)
        {
            var factions = sim.DataManager != null ? FactionEnumeration.FactionList : null;
            return new
            {
                funds = sim.Funds,
                quarterly_expenses = sim.GetExpenditures(),
                days_left_in_quarter = sim.DayRemainingInQuarter,
                expense_level = sim.ExpenditureLevel.ToString(),
                morale = sim.Morale,
                mrb_level = sim.GetCurrentMRBLevel(),
                reputation = factions?.Where(f => f.DoesGainReputation).Select(f => new
                {
                    faction = f.FriendlyName,
                    standing = sim.GetReputation(f).ToString(),
                    allied = sim.IsFactionAlly(f),
                }).ToList(),
            };
        }

        // -- flashpoints -----------------------------------------------------------------------------

        public static object Flashpoints(SimGameState sim) => new
        {
            active = sim.ActiveFlashpoint?.Def?.Description?.Name,
            available = (sim.AvailableFlashpoints ?? new List<Flashpoint>()).Select(f => new
            {
                id = f.Def.Description.Id,
                name = f.Def.Description.Name,
                details = f.Def.Description.Details,
                system = f.CurSystem?.Name,
                system_id = f.CurSystem?.ID,
                employer = f.EmployerValue?.FriendlyName,
                status = f.CurStatus.ToString(),
                days_remaining = f.GetRemainingTime(),
            }).ToList(),
        };

        public static object AcceptFlashpoint(SimGameState sim, string id)
        {
            RequireQuiet(sim);
            if (sim.ActiveFlashpoint != null)
            {
                throw new BridgeException(409, $"flashpoint '{sim.ActiveFlashpoint.Def.Description.Name}' is already active");
            }
            if (sim.HasTravelContract)
            {
                throw new BridgeException(409, "a travel contract is active; accepting would break it (not supported)");
            }
            var fp = sim.AvailableFlashpoints?.FirstOrDefault(f => f.Def.Description.Id == id)
                ?? throw new BridgeException(404, $"no available flashpoint '{id}'");
            sim.SetActiveFlashpoint(fp);
            Log.Info($"flashpoint accepted: {fp.Def.Description.Name}");
            return new
            {
                accepted = fp.Def.Description.Name,
                status = fp.CurStatus.ToString(),
                next = fp.CurSystem == sim.CurSystem ? "milestones run as days pass" : $"travel to {fp.CurSystem?.Name} ({fp.CurSystem?.ID})",
            };
        }
    }
}
