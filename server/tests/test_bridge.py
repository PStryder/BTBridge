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
