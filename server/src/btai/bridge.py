"""Client for the BTBridge mod's localhost HTTP API (only reachable while the game runs)."""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any

DEFAULT_URL = "http://127.0.0.1:8787"


class BridgeError(RuntimeError):
    """The bridge answered with ok=false (e.g. no campaign loaded)."""


class BridgeUnavailable(BridgeError):
    """Nothing is listening: the game isn't running or the mod didn't load."""


class Bridge:
    def __init__(self, base_url: str | None = None, timeout: float = 10.0):
        self.base_url = (base_url or os.environ.get("BTBRIDGE_URL") or DEFAULT_URL).rstrip("/")
        self.timeout = timeout

    def get(self, path: str) -> Any:
        return self._call("GET", path)

    def post(self, path: str, body: Any) -> Any:
        return self._call("POST", path, body)

    def delete(self, path: str) -> Any:
        return self._call("DELETE", path)

    def _call(self, method: str, path: str, body: Any = None) -> Any:
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(self.base_url + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            # Non-2xx still carries the JSON envelope with the reason.
            try:
                payload = json.loads(e.read().decode("utf-8"))
            except (ValueError, OSError):
                raise BridgeError(f"HTTP {e.code} from bridge at {path}") from e
        except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
            raise BridgeUnavailable(
                f"BattleTech bridge not reachable at {self.base_url} ({e}). "
                "Is the game running with the BTBridge mod enabled?"
            ) from e
        if not payload.get("ok"):
            raise BridgeError(payload.get("error") or "bridge reported failure")
        return payload.get("data")
