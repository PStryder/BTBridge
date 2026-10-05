"""MCP server exposing BattleTech to an AI agent.

Two tool families:
  catalog_* / check_mech_build  - offline, from the installed game's JSON (no game needed)
  game_* / campaign_* / mechlab_* / skirmish_* - live, via the BTBridge mod
"""

from __future__ import annotations

import time
from functools import lru_cache
from typing import Any
from urllib.parse import quote

from mcp.server.mcpserver import MCPServer

from .bridge import Bridge, BridgeError
from .catalog import GameData

mcp = MCPServer(
    "battletech",
    instructions=(
        "Tools for HBS BattleTech. catalog_* and check_mech_build work offline from game data. "
        "Live tools (game_status, campaign_*, mechlab_current, validate_mech_build, skirmish_*) need the game "
        "running with the BTBridge mod; call game_status first. Live data includes DLC content the offline "
        "catalog lacks. Builds use one mechdef format everywhere: get one from campaign_mech / mechlab_current "
        "(`spec`) or catalog_get_stock_mech, edit it, then validate. Campaign refits are two-step: "
        "campaign_refit_preview, show the player the plan, and campaign_refit_apply only on their explicit OK."
    ),
)


@lru_cache(maxsize=1)
def game_data() -> GameData:
    return GameData()


@lru_cache(maxsize=1)
def bridge() -> Bridge:
    return Bridge()


def _live(path: str, body: Any = None, method: str | None = None) -> Any:
    method = method or ("POST" if body is not None else "GET")
    try:
        if method == "GET":
            return bridge().get(path)
        if method == "DELETE":
            return bridge().delete(path)
        return bridge().post(path, body)
    except BridgeError as e:
        return {"error": str(e)}


# -- offline catalog ----------------------------------------------------------


@mcp.tool()
def catalog_search_components(query: str = "", type: str | None = None, category: str | None = None,
                              include_variants: bool = True, limit: int = 50) -> list[dict]:
    """Search weapons and equipment from the game data.

    type: Weapon | AmmunitionBox | HeatSink | JumpJet | Upgrade
    category (weapons only): Ballistic | Energy | Missile | AntiPersonnel
    include_variants: include +/++ manufacturer variants (non -STOCK ids).
    Base game only; DLC items (LB-X, UAC, etc.) are visible through the live tools.
    """
    return game_data().search_components(query, type, category, include_variants, limit)


@mcp.tool()
def catalog_get_component(component_id: str) -> dict:
    """Full stats and description for one component id."""
    c = game_data().components.get(component_id)
    if c is None:
        return {"error": f"unknown component {component_id!r}"}
    d = c.summary()
    d["details"] = (c.raw.get("Description") or {}).get("Details")
    return d


@mcp.tool()
def catalog_list_chassis(weight_class: str | None = None, min_tons: float = 0, max_tons: float = 200) -> list[dict]:
    """List mech chassis with tonnage, free tonnage, jump jet cap and total hardpoints.

    weight_class: LIGHT | MEDIUM | HEAVY | ASSAULT
    """
    return game_data().list_chassis(weight_class, min_tons, max_tons)


@mcp.tool()
def catalog_get_chassis(chassis_id: str) -> dict:
    """Per-location slots, armor caps and hardpoints for a chassis, plus its stock mech variants."""
    g = game_data()
    c = g.chassis.get(chassis_id)
    if c is None:
        return {"error": f"unknown chassis {chassis_id!r}"}
    d = c.summary()
    d["stock_mechs"] = g.stock_mechs_for_chassis(chassis_id)
    return d


@mcp.tool()
def catalog_get_stock_mech(mech_id: str) -> dict:
    """A stock mech's loadout in the game's mechdef format, with a build analysis."""
    g = game_data()
    m = g.mechs.get(mech_id)
    if m is None:
        return {"error": f"unknown mech {mech_id!r}"}
    return {
        "mechdef": {
            "ChassisID": m["ChassisID"],
            "Description": {"Id": m["Description"]["Id"], "Name": m["Description"].get("Name")},
            "Locations": [{k: loc.get(k) for k in ("Location", "AssignedArmor", "AssignedRearArmor")}
                          for loc in m.get("Locations", [])],
            "inventory": [{"ComponentDefID": i["ComponentDefID"], "MountedLocation": i["MountedLocation"]}
                          for i in m.get("inventory", [])],
        },
        "analysis": g.analyze_mechdef(m),
    }


@mcp.tool()
def check_mech_build(mechdef: dict) -> dict:
    """Pre-check a proposed build offline: tonnage, slots, hardpoints, armor caps, jump jets, ammo.

    mechdef uses the game's format:
      {"ChassisID": "...",
       "Locations": [{"Location": "Head", "AssignedArmor": 45, "AssignedRearArmor": -1}, ...],
       "inventory": [{"ComponentDefID": "...", "MountedLocation": "LeftArm"}, ...]}
    Get a starting point from catalog_get_stock_mech. The in-game validator is authoritative.
    """
    return game_data().analyze_mechdef(mechdef)


# -- live game (requires BattleTech running with the BTBridge mod) -----------


@mcp.tool()
def game_status() -> dict:
    """Is the game reachable, and is a campaign / combat / mechlab active?"""
    return _live("/health")


@mcp.tool()
def campaign_company() -> dict:
    """Company name, funds, date, MechTech skill and bay capacity (campaign must be loaded)."""
    return _live("/sim/company")


@mcp.tool()
def campaign_mechbay() -> dict:
    """Every mech in the bays with full loadout, armor, stats, and the refit work queue."""
    return _live("/sim/mechbay")


@mcp.tool()
def campaign_storage() -> dict:
    """Stored components (with counts and damaged flags), stored mechs, and salvaged mech parts."""
    return _live("/sim/storage")


@mcp.tool()
def campaign_mech(bay: int) -> dict:
    """One active mech in full, plus its loadout as an editable `spec` (the mechdef format the
    build tools accept). Edit the spec and pass it to campaign_refit_preview."""
    return _live(f"/sim/mech?bay={bay}")


@mcp.tool()
def mechlab_current() -> dict:
    """The build currently open in the mechlab, including unsaved edits (`current`, `current_spec`)
    and the game's validation errors."""
    return _live("/mechlab/current")


# -- live build tools -----------------------------------------------------------


@mcp.tool()
def validate_mech_build(mechdef: dict) -> dict:
    """Validate a build with the game's own mechlab rules (authoritative, includes DLC parts).

    Same mechdef format as check_mech_build. `can_field` is false if any blocking validation
    type fires (overweight, slots, hardpoints, jump jets, no weapons, invalid manifest).
    Returns the built mech with the mechlab stat bars.
    """
    return _live("/mech/validate", {"mechdef": mechdef})


@mcp.tool()
def campaign_refit_preview(bay: int, mechdef: dict) -> dict:
    """Plan a campaign refit of the mech in `bay` to the target build. Changes nothing.

    Returns the step list (removals to storage, moves, installs from storage, armor changes),
    C-bill and day cost, missing parts, the game's validation of the result, and a plan_id
    when the plan can be applied. Parts can only come from company storage.
    Show the player the steps and cost before asking whether to apply.
    """
    return _live("/sim/refit/preview", {"mech": str(bay), "mechdef": mechdef})


@mcp.tool()
def campaign_refit_apply(plan_id: str) -> dict:
    """Commit a previewed refit to the campaign: spends C-bills, pulls parts from storage, and queues
    the mechlab work order exactly as confirming in the mechlab would.

    ONLY call this after the player has explicitly approved this specific plan. It refuses if the
    mech, storage, or funds changed since the preview, or if the mechlab is open.
    """
    return _live("/sim/refit/apply", {"plan_id": plan_id})


@mcp.tool()
def skirmish_custom() -> dict:
    """The player's saved custom skirmish mechs and lances."""
    return _live("/skirmish/custom")


@mcp.tool()
def skirmish_pilots() -> dict:
    """Pilots usable in skirmish lances, with skills."""
    return _live("/skirmish/pilots")


@mcp.tool()
def skirmish_save_mech(mechdef: dict, name: str, replace_id: str | None = None) -> dict:
    """Validate and save a custom skirmish mech (appears in the skirmish mechbay).

    Not saved if a blocking validation error fires. Pass replace_id to overwrite one of the
    custom mechs (ids start with mechdef_CUSTOM_). Skirmish only; never touches a campaign.
    """
    return _live("/skirmish/mechs", {"mechdef": mechdef, "name": name, "replace_id": replace_id})


@mcp.tool()
def skirmish_delete_mech(mech_id: str) -> dict:
    """Delete a custom skirmish mech (only mechs tagged unit_custom can be deleted)."""
    return _live(f"/skirmish/mechs?id={quote(mech_id)}", method="DELETE")


@mcp.tool()
def skirmish_save_lance(name: str, units: list[dict], replace_id: str | None = None) -> dict:
    """Save a custom skirmish lance of 1-4 units: [{"mech_id": ..., "pilot_id": ...}].

    mech_id is a stock mechdef id or a custom skirmish mech id; pilot ids come from skirmish_pilots.
    """
    return _live("/skirmish/lances", {"name": name, "units": units, "replace_id": replace_id})


@mcp.tool()
def skirmish_delete_lance(lance_id: str) -> dict:
    """Delete a custom skirmish lance."""
    return _live(f"/skirmish/lances?id={quote(lance_id)}", method="DELETE")


# -- combat ---------------------------------------------------------------------


@mcp.tool()
def combat_control(player: str | None = None, enemy: str | None = None,
                   decision_timeout_seconds: float | None = None) -> dict:
    """Get or set who commands each side. Takes effect from the NEXT mission start.

    player: Human (vanilla; still readable for advice) | Agent (you command the player's lance;
            the stock AI offers suggestions) | BuiltinAI (stock AI plays the player's lance).
    enemy:  StockAI (vanilla) | Agent (you command every AI team hostile to the player).
    decision_timeout_seconds: 0 = wait for you forever; otherwise take the stock AI's suggestion
    after that long.
    """
    body = {k: v for k, v in {"player": player, "enemy": enemy,
                              "decision_timeout_seconds": decision_timeout_seconds}.items() if v is not None}
    return _live("/combat/control", body) if body else _live("/combat/control")


def _side_query(side: str | None) -> str:
    return f"?side={quote(side)}" if side else ""


@mcp.tool()
def combat_briefing(side: str | None = None) -> dict:
    """START EACH ROUND HERE. The whole board in one call, from `side`'s point of view only
    (player | enemy; default: the side you command):
    - turn_order: the initiative bar; per phase which units act and who already has
    - own_units: full state, terrain/cover at their spot, and `vs`: per visible hostile the distance,
      line of sight/fire, which side of the target you'd hit, and per-weapon hit chances from here
    - contacts: hostiles at your visibility (blips = position only)
    - lost_contacts: hostiles you saw before but can't now, at your last sighting
    Then plan the round with combat_set_orders."""
    return _live("/combat/briefing" + _side_query(side))


@mcp.tool()
def combat_state(side: str | None = None) -> dict:
    """Compact battlefield summary from `side`'s view (player | enemy). combat_briefing is richer."""
    return _live("/combat/state" + _side_query(side))


@mcp.tool()
def combat_set_orders(orders: list[dict], replace: bool = True) -> dict:
    """Plan the round: standing orders that execute instantly when each unit's decision opens.

    Each order: {"unit": guid,
                 "sequence": n (optional; lower activates first among units in the same phase),
                 "move": {"position": {"x","z"}, "move": "walk|sprint|backward|jump",
                          "facing": deg | "face_unit": guid}   (optional; or {"candidate": i} is
                          not available ahead of time),
                 "attack": {"target": guid, "weapons": [uid, ...]}   (optional; omit weapons = all),
                 "on_invalid": "wait" (default: decision waits for you) | "suggestion" | "brace"}
    A unit with neither move nor attack braces. Orders are validated against the live situation
    when the unit activates; if one no longer fits (target dead, spot taken, no line of fire) the
    decision shows standing_order_error. Orders expire at the end of the round.
    """
    return _live("/combat/orders", {"orders": orders, "replace": replace})


@mcp.tool()
def combat_orders() -> dict:
    """The current round's standing orders and which steps have executed."""
    return _live("/combat/orders")


@mcp.tool()
def combat_decision() -> dict:
    """The open decision, if any: the unit to act, stage (move | attack), the stock AI's suggestion,
    the AI's top-ranked move candidates (with score factors), and per-weapon hit chances against
    each visible enemy."""
    return _live("/combat/decision")


@mcp.tool()
def combat_wait_for_decision(max_wait_seconds: float = 60) -> dict:
    """Block until a decision opens for your lance (or the mission ends / time runs out), then return it.
    Use this between decisions instead of polling combat_decision."""
    deadline = time.monotonic() + max(1.0, min(max_wait_seconds, 300.0))
    last: Any = None
    while time.monotonic() < deadline:
        last = _live("/combat/decision")
        if isinstance(last, dict) and (last.get("open") or "error" in last):
            return last
        time.sleep(0.5)
    return {"open": False, "timed_out": True, "last": last}


@mcp.tool()
def combat_reachable(move: str = "walk", x: float | None = None, z: float | None = None, limit: int = 20) -> dict:
    """Where the deciding unit can get to this turn: reachable points for a move type
    (walk | sprint | backward | jump), nearest first to (x, z) (default: the unit itself), each with
    the nearest visible enemy. Move orders snap to the nearest reachable point within 25 m."""
    query = f"/combat/reachable?move={quote(move)}&limit={limit}"
    if x is not None and z is not None:
        query += f"&x={x}&z={z}"
    return _live(query)


@mcp.tool()
def combat_decide(decision_id: str, unit_guid: str, order: dict) -> dict:
    """Answer the open decision for the unit it belongs to (unit_guid from the decision's `unit.guid`;
    refused if it doesn't match, since weapon uids repeat across units). The mod validates the order
    before the game executes it.

    order.action:
      "accept"  - do what the stock AI suggested
      "move"    - {"candidate": i} from the decision's candidates, or {"position": {"x","z"},
                  "move": "walk|sprint|backward|jump"}; optional "facing" (degrees) or
                  "face_unit" (guid). Sprinting forfeits the attack this round.
      "attack"  - {"target": guid, "weapons": [uid, ...]} (omit weapons = all that can fire).
                  Firing ends the unit's activation.
      "brace"   - end the activation, bracing (evasion/stability benefit).
    After a move, the same unit gets an attack-stage decision. Pilots with Ace Pilot
    (CanMoveAfterShooting) may fire first and then get a move decision.
    """
    return _live("/combat/decision", {"id": decision_id, "unit": unit_guid, "order": order})


@mcp.tool()
def combat_history() -> list | dict:
    """The last 50 decisions: unit, round, stage, what was chosen, and how long it took."""
    return _live("/combat/history")


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
