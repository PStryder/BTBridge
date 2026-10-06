"""OPERATOR CHEAT LAYER: a separate MCP server (`btai-cheats-mcp`).

Register this server only when you want cheats available. The ordinary `btai-mcp` server has no
cheat tools. In the game, cheats additionally need the restart-time capability
(--btbridge-allow-cheats or mod.json AllowCheats) and a human arming them with the in-game hotkey.
There is no tool to arm or reconfigure. See docs/CHEATS_DESIGN.md.
"""

from __future__ import annotations

from typing import Any

from mcp.server.mcpserver import MCPServer

from .bridge import BridgeError
from .server import bridge

mcp = MCPServer(
    "battletech-cheats",
    instructions=(
        "OPERATOR CHEATS. Use these tools ONLY when the human operator has explicitly asked for that "
        "exact change in this conversation. Never use them because cheating would help, to recover from "
        "a bad situation, or to finish a task faster. Every call must quote the operator's instruction "
        "in operator_request; it is written to an audit log together with before/after values. Cheats "
        "work only while the operator has armed them in-game, never in Ironman campaigns, and they stay "
        "unsaved (reload is the undo) until the game next saves for any reason."
    ),
)

MIN_REQUEST = 8


def _call(method: str, path: str, body: Any = None) -> Any:
    try:
        if method == "GET":
            return bridge().get(path)
        return bridge().post(path, body)
    except BridgeError as e:
        return {"error": str(e)}


def _cheat(op: str, args: dict, operator_request: str, dry_run: bool) -> dict:
    if not operator_request or len(operator_request.strip()) < MIN_REQUEST:
        return {"error": "operator_request is required: quote the operator's instruction verbatim"}
    preview = _call("POST", "/cheat/preview", {"op": op, "args": args, "operator_request": operator_request})
    if not isinstance(preview, dict) or "error" in preview or dry_run:
        return {"preview": preview} if isinstance(preview, dict) and "error" not in preview else preview
    result = _call("POST", "/cheat/execute", {"plan_id": preview["plan_id"]})
    return {"preview": preview, "result": result}


@mcp.tool()
def cheat_status() -> dict:
    """Cheat capability, whether the operator has armed it (and time/ops left), the campaign, and
    which executed cheats are still unsaved."""
    return _call("GET", "/cheat/status")


@mcp.tool()
def cheat_disarm() -> dict:
    """Close the cheat window now (safe direction; arming is the operator's in-game hotkey only)."""
    return _call("POST", "/cheat/disarm", {})


@mcp.tool()
def cheat_audit(limit: int = 20) -> list | dict:
    """Recent cheat audit records: arm/disarm, executed operations with before/after, persistence."""
    return _call("GET", f"/cheat/audit?limit={limit}")


@mcp.tool()
def cheat_add_funds(amount: int, operator_request: str, dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Add (or, negative, remove) C-bills. Refuses overflow and dropping below
    the game-over debt limit. dry_run=True returns only the preview."""
    return _cheat("add_funds", {"amount": amount}, operator_request, dry_run)


@mcp.tool()
def cheat_add_component(component_id: str, operator_request: str, count: int = 1, damaged: bool = False,
                        dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Put weapons/ammo/heat sinks/jump jets/upgrades into storage (1-50)."""
    return _cheat("add_component", {"id": component_id, "count": count, "damaged": damaged}, operator_request, dry_run)


@mcp.tool()
def cheat_remove_component(component_id: str, operator_request: str, count: int = 1, damaged: bool = False,
                           dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Remove components from storage (not more than are stored)."""
    return _cheat("remove_component", {"id": component_id, "count": count, "damaged": damaged}, operator_request, dry_run)


@mcp.tool()
def cheat_add_mech(mech_def_id: str, operator_request: str, destination: str = "bay", bay: int | None = None,
                   dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Add a stock mech (mechdef id) to an empty bay (a given one, or the first
    free) or to storage. Occupied bays are refused (the game would overwrite them)."""
    args: dict = {"mech_def_id": mech_def_id, "destination": destination}
    if bay is not None:
        args["bay"] = bay
    return _cheat("add_mech", args, operator_request, dry_run)


@mcp.tool()
def cheat_complete_mech_work(operator_request: str, dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Finish all queued mechlab work orders now (normal completion handlers)."""
    return _cheat("complete_mech_work", {}, operator_request, dry_run)


@mcp.tool()
def cheat_complete_argo_upgrade(operator_request: str, dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Finish the Argo upgrade being built now (normal completion + notification)."""
    return _cheat("complete_argo_upgrade", {}, operator_request, dry_run)


@mcp.tool()
def cheat_heal_pilots(operator_request: str, dry_run: bool = False) -> dict:
    """OPERATOR REQUEST ONLY. Finish all medbay recovery now."""
    return _cheat("heal_pilots", {}, operator_request, dry_run)


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
