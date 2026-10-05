"""MCP server exposing BattleTech to an AI agent.

Two tool families:
  catalog_* / check_mech_build  - offline, from the installed game's JSON (no game needed)
  game_* / campaign_* / mechlab_* / skirmish_* - live, via the BTBridge mod
"""

from __future__ import annotations

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


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
