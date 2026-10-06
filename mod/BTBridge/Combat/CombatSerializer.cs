using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using UnityEngine;

namespace BTBridge.Combat
{
    /// <summary>
    /// Combat state for the agent. Positions are world x/z (metres) plus axial hex q/r; facing is
    /// degrees (0 = +z, clockwise). Enemies are reported only at the visibility the viewing team
    /// actually has, so the agent plays under the same fog of war as a human.
    /// </summary>
    public static class CombatSerializer
    {
        private static readonly ArmorLocation[] ArmorLocations =
        {
            ArmorLocation.Head, ArmorLocation.CenterTorso, ArmorLocation.CenterTorsoRear,
            ArmorLocation.LeftTorso, ArmorLocation.LeftTorsoRear, ArmorLocation.RightTorso, ArmorLocation.RightTorsoRear,
            ArmorLocation.LeftArm, ArmorLocation.RightArm, ArmorLocation.LeftLeg, ArmorLocation.RightLeg,
        };

        public static object Position(CombatGameState combat, Vector3 p)
        {
            var hex = combat.HexGrid.HexAxialRound(combat.HexGrid.CartesianToHexAxial(p));
            return new { x = Round(p.x), z = Round(p.z), y = Round(p.y), q = (int)hex.x, r = (int)hex.y };
        }

        public static float Facing(Quaternion rotation) => Round(rotation.eulerAngles.y);

        public static float Round(float v) => (float)Math.Round(v, 1);

        public static object State(CombatGameState combat, Team viewer)
        {
            var td = combat.TurnDirector;
            var units = new List<object>();
            foreach (var actor in combat.AllActors)
            {
                if (actor == null || actor.IsDead)
                {
                    continue;
                }
                bool own = actor.team == viewer;
                bool enemy = viewer != null && actor.team != null && viewer.IsEnemy(actor.team);
                var visibility = own || viewer == null ? VisibilityLevel.LOSFull : Visibility(viewer, actor);
                if (enemy && visibility == VisibilityLevel.None)
                {
                    continue;
                }
                units.Add(Actor(combat, actor, own ? "own" : enemy ? "enemy" : "allied", visibility));
            }
            return new
            {
                round = td.CurrentRound,
                phase = td.CurrentPhase,
                phases = new { first = td.FirstPhase, last = td.LastPhase },
                interleaved = td.IsInterleaved,
                active_team = (td.ActiveTurnActor as Team)?.DisplayName,
                mission_over = td.IsMissionOver,
                units,
            };
        }

        public static VisibilityLevel Visibility(Team viewer, AbstractActor target)
        {
            try
            {
                return viewer.VisibilityCache.VisibilityToTarget(target).VisibilityLevel;
            }
            catch
            {
                return VisibilityLevel.None;
            }
        }

        public static string KindOf(AbstractActor a) =>
            a is Mech ? "mech" : a is Vehicle ? "vehicle" : a is Turret ? "turret" : a.GetType().Name;

        /// <summary>The name a team may use for a unit: real only with full line of sight (or friendly).</summary>
        public static string ContactName(Team viewer, AbstractActor a)
        {
            bool friendly = viewer != null && (a.team == viewer || viewer.IsFriendly(a.team));
            return BTBridge.Logic.ContactRules.Name(friendly, viewer == null ? 0 : (int)Visibility(viewer, a), a.DisplayName, KindOf(a));
        }

        /// <summary>Whether a team currently detects a unit at all (a blip counts: its position is known).</summary>
        public static bool Detected(Team viewer, AbstractActor a) =>
            viewer != null && (a.team == viewer || viewer.IsFriendly(a.team) || Visibility(viewer, a) > VisibilityLevel.None);

        public static Dictionary<string, object> Actor(CombatGameState combat, AbstractActor a, string side, VisibilityLevel visibility)
        {
            // Fog of war: a sensor contact gets position (and kind at type-level returns) only.
            // Name and facing used to be filled in before the blip check (review finding).
            var view = BTBridge.Logic.ContactRules.For(side != "enemy", (int)visibility);
            var d = new Dictionary<string, object>
            {
                ["guid"] = a.GUID,
                ["name"] = BTBridge.Logic.ContactRules.Name(side != "enemy", (int)visibility, a.DisplayName, KindOf(a)),
                ["side"] = side,
                ["visibility"] = visibility.ToString(),
            };
            if (view.Position)
            {
                d["position"] = Position(combat, a.CurrentPosition);
            }
            if (view.Kind)
            {
                d["kind"] = KindOf(a);
            }
            if (!view.Identity)
            {
                return d;
            }
            d["team"] = a.team?.DisplayName;
            d["facing"] = Facing(a.CurrentRotation);
            d["status"] = new
            {
                operational = a.IsOperational,
                shutdown = a.IsShutDown,
                prone = a.IsProne,
                unsteady = a.IsUnsteady,
                evasive_pips = a.EvasivePipsCurrent,
            };
            d["turn"] = new
            {
                initiative = a.Initiative,
                activated = a.HasActivatedThisRound,
                moved = a.HasMovedThisRound,
                fired = a.HasFiredThisRound,
            };
            d["movement"] = new
            {
                walk = Round(a.MaxWalkDistance),
                sprint = Round(a.MaxSprintDistance),
                backward = Round(a.MaxBackwardDistance),
                jump = a is Mech jm ? Round(jm.JumpDistance) : 0f,
            };
            if (a is Mech m)
            {
                d["heat"] = new { current = m.CurrentHeat, max = m.MaxHeat, overheat_at = m.OverheatLevel };
                d["stability"] = new { current = Round(m.CurrentStability), unsteady_at = Round(m.UnsteadyThreshold), max = Round(m.MaxStability) };
                d["armor"] = ArmorLocations.ToDictionary(l => l.ToString(), l => new { current = Round(m.GetCurrentArmor(l)), max = Round(m.GetMaxArmor(l)) });
                d["structure"] = BTBridge.State.MechSerializer.Locations.ToDictionary(l => l.ToString(), l => new { current = Round(m.GetCurrentStructure(l)), max = Round(m.GetMaxStructure(l)) });
            }
            else
            {
                d["armor_total"] = Round(a.SummaryArmorCurrent);
                d["structure_total"] = Round(a.SummaryStructureCurrent);
            }
            d["weapons"] = a.Weapons.Select(w => Weapon(w)).ToList();
            return d;
        }

        public static object Weapon(Weapon w) => new
        {
            uid = w.uid,
            name = w.Name,
            category = w.WeaponCategoryValue?.Name,
            can_fire = w.CanFire,
            ammo = w.CurrentAmmo,
            damage = Round(w.DamagePerShot),
            shots = w.ShotsWhenFired,
            heat = Round(w.HeatGenerated),
            range = new { min = Round(w.MinRange), short_ = Round(w.ShortRange), medium = Round(w.MediumRange), max = Round(w.MaxRange) },
            indirect = w.IndirectFireCapable,
        };

        /// <summary>Every visible enemy with per-weapon fire/no-fire and hit chance from the unit's current spot.</summary>
        public static List<object> Targets(CombatGameState combat, AbstractActor unit)
        {
            var list = new List<object>();
            foreach (var enemy in combat.GetAllEnemiesOf(unit))
            {
                if (enemy == null || enemy.IsDead)
                {
                    continue;
                }
                var vis = unit.VisibilityToTargetUnit(enemy);
                if (vis == VisibilityLevel.None)
                {
                    continue;
                }
                float dist = Vector3.Distance(unit.CurrentPosition, enemy.CurrentPosition);
                var weapons = unit.Weapons.Select(w =>
                {
                    bool willFire = false;
                    float hit = 0f;
                    try
                    {
                        willFire = w.WillFireAtTarget(enemy);
                        if (willFire)
                        {
                            hit = w.GetToHitFromPosition(enemy, 1, unit.CurrentPosition, enemy.CurrentPosition, true, enemy.IsEvasive);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"to-hit for {w.Name} -> {enemy.DisplayName}: {e.Message}");
                    }
                    return new
                    {
                        uid = w.uid,
                        name = w.Name,
                        will_fire = willFire,
                        hit_chance = (float)Math.Round(hit, 3),
                        expected_damage = Round(hit * w.DamagePerShot * w.ShotsWhenFired),
                    };
                }).ToList();
                list.Add(new
                {
                    guid = enemy.GUID,
                    name = BTBridge.Logic.ContactRules.Name(false, (int)vis, enemy.DisplayName, KindOf(enemy)),
                    visibility = vis.ToString(),
                    distance = Round(dist),
                    position = Position(combat, enemy.CurrentPosition),
                    weapons,
                    expected_damage_all = Round(weapons.Sum(w => w.expected_damage)),
                });
            }
            return list;
        }
    }
}
