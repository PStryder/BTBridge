"""Bridge client against a fake server that speaks the mod's envelope protocol."""

import json
import socket
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from btai.bridge import Bridge, BridgeError, BridgeUnavailable

ROUTES = {
    "/health": (200, {"ok": True, "data": {"bridge_version": "0.1.0", "sim_loaded": False}}),
    "/sim/company": (409, {"ok": False, "error": "no campaign (sim game) is loaded"}),
}


class FakeMod(BaseHTTPRequestHandler):
    def do_GET(self):
        status, body = ROUTES.get(self.path, (404, {"ok": False, "error": f"no route GET {self.path}"}))
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self._reply(200, {"ok": True, "data": {"echo": body, "path": self.path}})

    def do_DELETE(self):
        self._reply(200, {"ok": True, "data": {"deleted": self.path}})

    def _reply(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, *args):
        pass


@pytest.fixture
def fake_bridge():
    server = HTTPServer(("127.0.0.1", 0), FakeMod)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    yield Bridge(f"http://127.0.0.1:{server.server_port}", timeout=2)
    server.shutdown()


def test_unwraps_ok_envelope(fake_bridge):
    assert fake_bridge.get("/health") == {"bridge_version": "0.1.0", "sim_loaded": False}


def test_error_envelope_raises_with_mod_message(fake_bridge):
    with pytest.raises(BridgeError, match="no campaign"):
        fake_bridge.get("/sim/company")


def test_unknown_route(fake_bridge):
    with pytest.raises(BridgeError, match="no route"):
        fake_bridge.get("/nope")


def test_post_sends_json_body(fake_bridge):
    assert fake_bridge.post("/sim/refit/apply", {"plan_id": "refit-1"}) == {
        "echo": {"plan_id": "refit-1"},
        "path": "/sim/refit/apply",
    }


def test_delete(fake_bridge):
    assert fake_bridge.delete("/skirmish/mechs?id=x") == {"deleted": "/skirmish/mechs?id=x"}


def test_tools_route_through_bridge(fake_bridge, monkeypatch):
    from btai import server

    monkeypatch.setattr(server, "bridge", lambda: fake_bridge)
    preview = server.campaign_refit_preview(2, {"ChassisID": "c"})
    assert preview["path"] == "/sim/refit/preview"
    assert preview["echo"] == {"mech": "2", "mechdef": {"ChassisID": "c"}}
    assert server.skirmish_delete_mech("mechdef_CUSTOM_a b")["deleted"] == "/skirmish/mechs?id=mechdef_CUSTOM_a%20b"


def test_unavailable_when_nothing_listening():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]  # closed again on exit: nothing listens here
    with pytest.raises(BridgeUnavailable, match="Is the game running"):
        Bridge(f"http://127.0.0.1:{port}", timeout=2).get("/health")


def test_live_tools_degrade_to_error_payload(monkeypatch):
    from btai import server

    server.bridge.cache_clear()
    monkeypatch.setenv("BTBRIDGE_URL", "http://127.0.0.1:9")
    try:
        assert "error" in server.game_status()
    finally:
        server.bridge.cache_clear()
