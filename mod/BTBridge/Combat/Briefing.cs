using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using UnityEngine;

namespace BTBridge.Combat
{
    /// <summary>
    /// The whole board from one side's point of view, in one call: what a human gets by clicking
    /// through their lance and hovering the enemies, plus the initiative bar. Nothing here uses
    /// information the viewing team does not have; contacts that drop out of sight are reported
    /// at their last seen position, from this side's own memory.
    /// </summary>
    public static class Briefing
    {
        private sealed class Sighting
        {
            public string Name;
            public string Kind;
            public Vector3 Position;
            public float Facing;
            public int Round;
            public int Phase;
        }

        // viewer team GUID -> hostile actor GUID -> last time this team could see it
        private static readonly Dictionary<string, Dictionary<string, Sighting>> Memory = new Dictionary<string, Dictionary<string, Sighting>>();
        private static CombatGameState memoryCombat;
        private static int frame;

        /// <summary>Called from the frame pump; records sightings for every team twice a second.</summary>
        public static void Tick()
        {
            if (++frame % 30 != 0)
            {
                return;
            }
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            if (combat == null)
            {
                return;
            }
            if (!ReferenceEquals(combat, memoryCombat))
            {
                Memory.Clear();
                memoryCombat = combat;
            }
            try
            {
                foreach (var viewer in combat.Teams)
                {
                    if (viewer.units.Count == 0)
                    {
                        continue;
                    }
                    if (!Memory.TryGetValue(viewer.GUID, out var seen))
                    {
                        seen = Memory[viewer.GUID] = new Dictionary<string, Sighting>();
                    }
                    foreach (var enemy in combat.GetAllEnemiesOf(viewer))
                    {
                        if (enemy.IsDead || Visibility(viewer, enemy) == VisibilityLevel.None)
                        {
                            continue;
                        }
                        seen[enemy.GUID] = new Sighting
                        {
                            Name = Visibility(viewer, enemy) >= VisibilityLevel.LOSFull ? enemy.DisplayName : "Unknown contact",
                            Kind = Kind(enemy),
                            Position = enemy.CurrentPosition,
                            Facing = CombatSerializer.Facing(enemy.CurrentRotation),
                            Round = combat.TurnDirector.CurrentRound,
                            Phase = combat.TurnDirector.CurrentPhase,
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("sighting update failed: " + e.Message);
            }
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

        private static string Kind(AbstractActor a) => a is Mech ? "mech" : a is Vehicle ? "vehicle" : a is Turret ? "turret" : a.GetType().Name;

        public static object Build(CombatGameState combat, Team viewer, string side)
        {
            Tick();
            var td = combat.TurnDirector;
            var own = viewer.units.Where(u => !u.IsDead).ToList();
            var hostiles = combat.GetAllEnemiesOf(viewer).Where(e => !e.IsDead).ToList();
            var visible = hostiles.Where(e => Visibility(viewer, e) != VisibilityLevel.None).ToList();

            var ownUnits = own.Select(u =>
            {
                var d = CombatSerializer.Actor(combat, u, "own", VisibilityLevel.LOSFull);
                d["terrain"] = Terrain(combat, u.CurrentPosition);
                d["vs"] = visible.Select(e => Engagement(combat, u, e)).ToList();
                return d;
            }).ToList();

            var contacts = visible.Select(e =>
            {
                var vis = Visibility(viewer, e);
                var d = CombatSerializer.Actor(combat, e, "enemy", vis);
                if (vis >= VisibilityLevel.LOSFull)
                {
                    d["terrain"] = Terrain(combat, e.CurrentPosition);
                }
                return d;
            }).ToList();

            var visibleGuids = new HashSet<string>(visible.Select(e => e.GUID));
            var lost = new List<object>();
            if (Memory.TryGetValue(viewer.GUID, out var seen))
            {
                foreach (var kv in seen)
                {
                    var actor = combat.FindActorByGUID(kv.Key);
                    if (visibleGuids.Contains(kv.Key) || actor == null || actor.IsDead)
                    {
                        continue;
                    }
                    var s = kv.Value;
                    lost.Add(new
                    {
                        guid = kv.Key,
                        name = s.Name,
                        kind = s.Kind,
                        last_seen = new { round = s.Round, phase = s.Phase },
                        position = CombatSerializer.Position(combat, s.Position),
                        facing = s.Facing,
                    });
                }
            }

            return new
            {
                side,
                viewer_team = viewer.DisplayName,
                round = td.CurrentRound,
                phase = td.CurrentPhase,
                in_combat = td.IsInterleaved,
                active_team = (td.ActiveTurnActor as Team)?.DisplayName,
                mission_over = td.IsMissionOver,
                // The mission's goals: the player lance gets no objective guidance from the encounter.
                objectives = side == "player" ? Objectives.Live(combat) : null,
                mission = side == "player" ? Objectives.Briefing(combat.ActiveContract) : null,
                turn_order = TurnOrder(combat, viewer, own, visible),
                own_units = ownUnits,
                contacts,
                lost_contacts = lost,
                notes = new[]
                {
                    "positions are world x/z metres plus axial hex q/r; facing in degrees (0 = +z, clockwise)",
                    "phase: the stored value; display = the number on the initiative bar",
                    "vs[].from_direction: which side of the target your shots would land on",
                    "contacts at Blip visibility show position only; lost_contacts are your own last sighting",
                },
            };
        }

        /// <summary>The initiative bar: per phase, who acts and who already has.</summary>
        private static object TurnOrder(CombatGameState combat, Team viewer, List<AbstractActor> own, List<AbstractActor> visible)
        {
            var td = combat.TurnDirector;
            int lo = Math.Min(td.FirstPhase, td.LastPhase);
            int hi = Math.Max(td.FirstPhase, td.LastPhase);
            int step = td.FirstPhase <= td.LastPhase ? 1 : -1;
            var phases = new List<object>();
            for (int p = td.FirstPhase; p >= lo && p <= hi; p += step)
            {
                int phase = p;
                Func<AbstractActor, string, object> entry = (a, side) => new
                {
                    guid = a.GUID,
                    name = side == "enemy" && Visibility(viewer, a) < VisibilityLevel.LOSFull ? "Unknown contact" : a.DisplayName,
                    side,
                    activated = a.HasActivatedThisRound,
                };
                var units = own.Where(a => a.Initiative == phase).Select(a => entry(a, "own"))
                    .Concat(visible.Where(a => a.Initiative == phase).Select(a => entry(a, "enemy")))
                    .ToList();
                phases.Add(new
                {
                    phase,
                    display = AbstractActor.InitiativeToString(phase),
                    current = phase == td.CurrentPhase,
                    units,
                });
            }
            return new
            {
                current_phase = td.CurrentPhase,
                current_display = AbstractActor.InitiativeToString(td.CurrentPhase),
                active_team = (td.ActiveTurnActor as Team)?.DisplayName,
                phases,
            };
        }

        /// <summary>One own unit against one visible hostile, from where the unit stands now.</summary>
        private static object Engagement(CombatGameState combat, AbstractActor unit, AbstractActor target)
        {
            float dist = Vector3.Distance(unit.CurrentPosition, target.CurrentPosition);
            string lof = "unknown";
            string direction = "unknown";
            bool los = false;
            try
            {
                los = unit.HasLOSToTargetUnit(target);
                lof = combat.LOS.GetLineOfFire(unit, target, out _).ToString();
                direction = combat.HitLocation.GetAttackDirection(unit.CurrentPosition, target).ToString();
            }
            catch (Exception e)
            {
                Log.Warn($"engagement {unit.DisplayName} -> {target.DisplayName}: {e.Message}");
            }
            var weapons = unit.Weapons.Select(w =>
            {
                bool fire = false;
                float hit = 0f;
                try
                {
                    fire = w.WillFireAtTarget(target);
                    if (fire)
                    {
                        hit = w.GetToHitFromPosition(target, 1, unit.CurrentPosition, target.CurrentPosition, true, target.IsEvasive);
                    }
                }
                catch
                {
                    fire = false;
                }
                return new { uid = w.uid, name = w.Name, will_fire = fire, hit_chance = (float)Math.Round(hit, 3), expected_damage = CombatSerializer.Round(hit * w.DamagePerShot * w.ShotsWhenFired) };
            }).Where(w => w.will_fire).ToList();
            return new
            {
                target = target.GUID,
                target_name = CombatSerializer.ContactName(unit.team, target),
                distance = CombatSerializer.Round(dist),
                line_of_sight = los,
                line_of_fire = lof,
                from_direction = direction,
                weapons_that_fire = weapons,
                expected_damage_all = CombatSerializer.Round(weapons.Sum(w => w.expected_damage)),
            };
        }

        public static object Terrain(CombatGameState combat, Vector3 pos)
        {
            try
            {
                var mask = combat.MapMetaData.GetPriorityDesignMaskAtPos(pos);
                if (mask == null)
                {
                    return new { name = "open" };
                }
                return new
                {
                    name = mask.Description?.Name ?? mask.Id,
                    to_hit_modifier = mask.toHitFromModifier,
                    targetability_modifier = mask.targetabilityModifier,
                    damage_taken_multiplier = mask.allDamageTakenMultiplier,
                    stability_damage_multiplier = mask.stabilityDamageMultiplier,
                    heat_sink_multiplier = mask.heatSinkMultiplier,
                    visibility_multiplier = mask.visibilityMultiplier,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
