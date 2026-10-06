"""The cheat layer is a separate server, every cheat carries the operator's request, and the
tools go preview -> execute (or stop at preview for dry runs)."""

import asyncio
import re
from pathlib import Path

import pytest

from btai import cheats, server

ROUTES_CS = Path(__file__).resolve().parents[2] / "mod" / "BTBridge" / "Routes.cs"
REQ = "Give the company 250 million C-bills please"

EXPECTED_CHEAT_TOOLS = {
    "cheat_status", "cheat_disarm", "cheat_audit", "cheat_add_funds", "cheat_add_component",
    "cheat_remove_component", "cheat_add_mech", "cheat_complete_mech_work", "cheat_complete_argo_upgrade",
    "cheat_heal_pilots",
}


def _names(srv):
    return {t.name for t in asyncio.run(srv.list_tools())}


def test_ordinary_server_has_no_cheat_tools():
    assert not [n for n in _names(server.mcp) if "cheat" in n]


def test_cheat_server_exposes_exactly_the_documented_tools():
    assert _names(cheats.mcp) == EXPECTED_CHEAT_TOOLS


def test_no_tool_can_arm_or_configure():
    for name in _names(cheats.mcp):
        assert not re.search(r"(^|_)(arm|enable|config|settings|hotkey)($|_)", name), name


class Recorder:
    def __init__(self, preview=None, execute=None):
        self.calls = []
        self.preview = preview or {"plan_id": "cheat-1", "summary": "add 250,000,000 C-bills"}
        self.execute = execute or {"plan_id": "cheat-1", "after": {"funds": 250_001_000}}

    def get(self, path):
        self.calls.append(("GET", path, None))
        return {"ok": True}

    def post(self, path, body):
        self.calls.append(("POST", path, body))
        return self.preview if path == "/cheat/preview" else self.execute


@pytest.fixture
def rec(monkeypatch):
    r = Recorder()
    monkeypatch.setattr(cheats, "bridge", lambda: r)
    return r


def test_preview_then_execute(rec):
    out = cheats.cheat_add_funds(250_000_000, REQ)
    assert [c[1] for c in rec.calls] == ["/cheat/preview", "/cheat/execute"]
    assert rec.calls[0][2] == {"op": "add_funds", "args": {"amount": 250_000_000}, "operator_request": REQ}
    assert rec.calls[1][2] == {"plan_id": "cheat-1"}
    assert out["result"]["after"]["funds"] == 250_001_000


def test_dry_run_stops_at_preview(rec):
    out = cheats.cheat_add_mech("mechdef_atlas_AS7-D", REQ, dry_run=True)
    assert [c[1] for c in rec.calls] == ["/cheat/preview"]
    assert out["preview"]["plan_id"] == "cheat-1"


@pytest.mark.parametrize("request_text", ["", "   ", "do it"])
def test_operator_request_is_required(rec, request_text):
    out = cheats.cheat_add_component("Weapon_PPC_PPC_0-STOCK", request_text, count=2)
    assert "error" in out
    assert rec.calls == []


def test_failed_preview_does_not_execute(monkeypatch):
    r = Recorder()

    def post(path, body):
        r.calls.append(("POST", path, body))
        from btai.bridge import BridgeError
        raise BridgeError("cheats are not armed (the operator arms them in-game)")

    r.post = post
    monkeypatch.setattr(cheats, "bridge", lambda: r)
    out = cheats.cheat_add_funds(1_000_000, REQ)
    assert "not armed" in out["error"]
    assert [c[1] for c in r.calls] == ["/cheat/preview"]


@pytest.mark.parametrize("tool,kwargs,op,args", [
    (cheats.cheat_add_component, {"component_id": "Ammo_LRM", "count": 3}, "add_component",
     {"id": "Ammo_LRM", "count": 3, "damaged": False}),
    (cheats.cheat_remove_component, {"component_id": "Ammo_LRM"}, "remove_component",
     {"id": "Ammo_LRM", "count": 1, "damaged": False}),
    (cheats.cheat_add_mech, {"mech_def_id": "m", "bay": 4}, "add_mech", {"mech_def_id": "m", "destination": "bay", "bay": 4}),
    (cheats.cheat_add_mech, {"mech_def_id": "m", "destination": "storage"}, "add_mech",
     {"mech_def_id": "m", "destination": "storage"}),
    (cheats.cheat_complete_mech_work, {}, "complete_mech_work", {}),
    (cheats.cheat_complete_argo_upgrade, {}, "complete_argo_upgrade", {}),
    (cheats.cheat_heal_pilots, {}, "heal_pilots", {}),
])
def test_ops_send_the_right_preview(rec, tool, kwargs, op, args):
    tool(operator_request=REQ, **kwargs)
    assert rec.calls[0] == ("POST", "/cheat/preview", {"op": op, "args": args, "operator_request": REQ})


def test_cheat_routes_are_declared_in_the_mod():
    text = ROUTES_CS.read_text(encoding="utf-8")
    for path in ("/cheat/status", "/cheat/preview", "/cheat/execute", "/cheat/disarm", "/cheat/audit"):
        assert f'Path = "{path}"' in text
