"""Every live MCP tool must send the request the mod's route table expects.

A recorder stands in for the bridge; each case checks method, path and body. Route paths are
also checked against the mod's Routes.cs, so a renamed route breaks this test.
"""

import re
from pathlib import Path

import pytest

from btai import server

ROUTES_CS = Path(__file__).resolve().parents[2] / "mod" / "BTBridge" / "Routes.cs"


class Recorder:
    def __init__(self):
        self.calls = []

    def get(self, path):
        self.calls.append(("GET", path, None))
        return {"ok": True}

    def post(self, path, body):
        self.calls.append(("POST", path, body))
        return {"ok": True}

    def delete(self, path):
        self.calls.append(("DELETE", path, None))
        return {"ok": True}


@pytest.fixture
def rec(monkeypatch):
    r = Recorder()
    monkeypatch.setattr(server, "bridge", lambda: r)
    return r


CASES = [
    (server.overlay_say, {"type": "commentary", "text": "Nice shot."}, "POST", "/overlay/say",
     {"type": "commentary", "text": "Nice shot."}),
    (server.overlay_history, {}, "GET", "/overlay/history", None),
    (server.overlay_inbox, {}, "GET", "/overlay/inbox", None),
    (server.overlay_ack, {"up_to_id": 3}, "POST", "/overlay/inbox/ack", {"up_to_id": 3}),
    (server.sim_status, {}, "GET", "/sim/status", None),
    (server.sim_interrupt, {}, "GET", "/sim/interrupt", None),
    (server.sim_answer_interrupt, {"answer": {"option": 1}}, "POST", "/sim/interrupt", {"option": 1}),
    (server.sim_run_time, {"days": 3}, "POST", "/sim/time", {"days": 3, "until_event": False}),
    (server.sim_run_time, {"until_event": True, "day_seconds": 0.2}, "POST", "/sim/time",
     {"days": None, "until_event": True, "day_seconds": 0.2}),
    (server.sim_stop_time, {}, "POST", "/sim/time", {"stop": True}),
    (server.contracts_list, {}, "GET", "/sim/contracts", None),
    (server.contract_accept, {"index": 2, "name": "Raid", "pay": 0.6, "salvage": 0.2}, "POST",
     "/sim/contracts/accept", {"index": 2, "name": "Raid", "pay": 0.6, "salvage": 0.2}),
    (server.contract_launch, {"units": [{"bay": 0, "pilot": "Glitch"}]}, "POST", "/sim/contracts/launch",
     {"units": [{"bay": 0, "pilot": "Glitch"}]}),
    (server.mission_status, {}, "GET", "/combat/mission", None),
    (server.mission_begin, {}, "POST", "/combat/begin", {}),
    (server.combat_dialog, {}, "GET", "/combat/dialog", None),
    (server.dialog_transcript, {"limit": 5}, "GET", "/dialog/transcript?limit=5&since=0", None),
    (server.dialog_transcript, {"repeats": True, "since_id": 7}, "GET", "/dialog/transcript?limit=50&since=7&repeats=true", None),
    (server.combat_dialog_continue, {}, "POST", "/combat/dialog/continue", {}),
    (server.mission_withdraw, {}, "POST", "/combat/withdraw", {}),
    (server.mission_exit, {}, "POST", "/combat/exit", {}),
    (server.aar_status, {}, "GET", "/sim/aar", None),
    (server.aar_continue, {"salvage": [{"id": "Weapon_PPC", "damaged": False}]}, "POST", "/sim/aar",
     {"salvage": [{"id": "Weapon_PPC", "damaged": False}]}),
    (server.aar_continue, {}, "POST", "/sim/aar", {"salvage": []}),
    (server.starmap, {"jumps": 3}, "GET", "/sim/starmap?jumps=3", None),
    (server.travel, {"system_id": "starsystemdef_Detroit", "confirm": True}, "POST", "/sim/travel",
     {"system": "starsystemdef_Detroit", "confirm": True}),
    (server.travel_status, {}, "GET", "/sim/travel", None),
    (server.pilots, {}, "GET", "/sim/pilots", None),
    (server.pilot_train, {"pilot": "Glitch", "skill": "gunnery", "to": 5}, "POST", "/sim/pilots/train",
     {"pilot": "Glitch", "skill": "gunnery", "to": 5, "confirm": False}),
    (server.hiring_hall, {}, "GET", "/sim/hiring", None),
    (server.hire_pilot, {"pilot_def_id": "pilot_x"}, "POST", "/sim/pilots/hire", {"id": "pilot_x"}),
    (server.dismiss_pilot, {"pilot": "Glitch"}, "POST", "/sim/pilots/dismiss", {"pilot": "Glitch"}),
    (server.store, {"shop": "black_market"}, "GET", "/sim/store?shop=black_market", None),
    (server.store_sellable, {}, "GET", "/sim/store/sellable?shop=system", None),
    (server.store_buy, {"item_id": "Weapon_PPC", "count": 2}, "POST", "/sim/store/buy",
     {"shop": "system", "id": "Weapon_PPC", "count": 2}),
    (server.store_sell, {"item_id": "Weapon_PPC"}, "POST", "/sim/store/sell",
     {"shop": "system", "id": "Weapon_PPC", "count": 1, "type": None}),
    (server.argo_upgrades, {}, "GET", "/sim/argo", None),
    (server.argo_upgrade, {"upgrade_id": "argoUpgrade_x"}, "POST", "/sim/argo/upgrade", {"id": "argoUpgrade_x"}),
    (server.finances, {}, "GET", "/sim/finances", None),
    (server.flashpoints, {}, "GET", "/sim/flashpoints", None),
    (server.flashpoint_accept, {"flashpoint_id": "fp_x"}, "POST", "/sim/flashpoints/accept", {"id": "fp_x"}),
    (server.combat_briefing, {"side": "enemy"}, "GET", "/combat/briefing?side=enemy", None),
    (server.combat_set_orders, {"orders": [{"unit": "u1"}]}, "POST", "/combat/orders",
     {"orders": [{"unit": "u1"}], "replace": True}),
    (server.combat_decide, {"decision_id": "d1", "unit_guid": "u1", "order": {"action": "brace"}}, "POST",
     "/combat/decision", {"id": "d1", "unit": "u1", "order": {"action": "brace"}}),
    (server.combat_control, {"player": "Agent", "enemy": "StockAI"}, "POST", "/combat/control",
     {"player": "Agent", "enemy": "StockAI"}),
    (server.combat_reachable, {"move": "jump", "x": 1.5, "z": -2.0}, "GET",
     "/combat/reachable?move=jump&limit=20&x=1.5&z=-2.0", None),
]


@pytest.mark.parametrize("tool,kwargs,method,path,body", CASES, ids=[f"{c[0].__name__}-{i}" for i, c in enumerate(CASES)])
def test_tool_request(rec, tool, kwargs, method, path, body):
    tool(**kwargs)
    assert rec.calls == [(method, path, body)]


def _declared_routes():
    text = ROUTES_CS.read_text(encoding="utf-8")
    return set(re.findall(r'Method = "(GET|POST|DELETE)", Path = "([^"]+)"', text))


def test_every_tool_targets_a_declared_route(rec):
    declared = _declared_routes()
    assert declared, "could not parse Routes.cs"
    for tool, kwargs, method, path, _ in CASES:
        assert (method, path.split("?")[0]) in declared, f"{tool.__name__}: {method} {path} is not a mod route"


def test_wait_returns_early_on_operator_message(monkeypatch):
    replies = iter([
        {"open": False},
        {"open": False, "operator_messages": [{"id": 1, "text": "focus the Atlas"}]},
    ])

    class B:
        def get(self, path):
            return next(replies)

    monkeypatch.setattr(server, "bridge", lambda: B())
    monkeypatch.setattr(server.time, "sleep", lambda s: None)
    out = server.combat_wait_for_decision(30)
    assert out["operator_messages"][0]["text"] == "focus the Atlas"


def test_no_tool_writes_the_operator_inbox():
    import asyncio
    names = {t.name for t in asyncio.run(server.mcp.list_tools())}
    assert {"overlay_inbox", "overlay_ack"} <= names
    assert not [n for n in names if "inbox" in n and n not in ("overlay_inbox",)]
    text = ROUTES_CS.read_text(encoding="utf-8")
    assert 'Path = "/overlay/inbox", Handler' in text
    assert "POST\", Path = \"/overlay/inbox\"" not in text
