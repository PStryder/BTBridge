"""MCP server exposing BattleTech to an AI agent.

Two tool families:
  catalog_* / check_mech_build  - offline, from the installed game's JSON (no game needed)
  game_* / campaign_* / mechlab_* / skirmish_* - live, via the BTBridge mod
"""

from __future__ import annotations

from functools import lru_cache
from typing import Any

from mcp.server.mcpserver import MCPServer

from .bridge import Bridge, BridgeError
from .catalog import GameData

mcp = MCPServer(
    "battletech",
    instructions=(
        "Tools for HBS BattleTech. catalog_* and check_mech_build work offline from game data. "
        "Live tools (game_status, campaign_*, mechlab_current, skirmish_*) need the game running with the "
        "BTBridge mod; call game_status first. Live data includes DLC content the offline catalog lacks."
    ),
)


@lru_cache(maxsize=1)
def game_data() -> GameData:
    return GameData()


@lru_cache(maxsize=1)
def bridge() -> Bridge:
    return Bridge()


def _live(path: str) -> Any:
    try:
        return bridge().get(path)
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
def mechlab_current() -> dict:
    """The build currently open in the mechlab, including unsaved edits and the game's validation errors."""
    return _live("/mechlab/current")


@mcp.tool()
def skirmish_custom_mechs() -> dict:
    """The player's saved custom skirmish mechs."""
    return _live("/skirmish/custom")


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
