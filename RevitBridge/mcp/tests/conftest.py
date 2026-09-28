"""A fake RevitBridge: a real Windows named-pipe server speaking the bridge's line protocol.

Each test gets its own pipe prefix so it never collides with a live Revit bridge
(``ccorp-revitbridge-*``). The handler decides per request what the "add-in" does:
return a reply dict, ``"close"`` (drop the connection without replying) or ``"hang"``
(never reply).
"""

from __future__ import annotations

import json
import threading
import time
import uuid
from typing import Callable

import pytest

win32pipe = pytest.importorskip("win32pipe")
import pywintypes  # noqa: E402
import win32file  # noqa: E402

from revit_bridge_mcp import pipe_client  # noqa: E402


def envelope(req: dict, result=None, ok: bool = True, error: dict | None = None, **extra) -> dict:
    env = {
        "id": req["id"], "method": req["method"], "ok": ok, "result": result, "warnings": [],
        "errors": [], "changed": {"created": [], "modified": [], "deleted": []}, "tx_name": None,
        "elapsed_ms": 1, "doc": None,
    }
    if error:
        env["error"] = error
    env.update(extra)
    return env


class FakeBridge:
    def __init__(self, prefix: str, pid: int, handler: Callable[[dict], object]):
        self.name = f"{prefix}{pid}"
        self.pid = pid
        self.handler = handler
        self.requests: list[dict] = []
        self._stop = False
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._ready = threading.Event()

    def start(self) -> "FakeBridge":
        self._thread.start()
        self._ready.wait(5)
        return self

    def _new_instance(self):
        return win32pipe.CreateNamedPipe(
            pipe_client.PIPE_ROOT + self.name,
            win32pipe.PIPE_ACCESS_DUPLEX,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
            win32pipe.PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, None,
        )

    def _serve(self) -> None:
        h = self._new_instance()
        self._ready.set()
        while not self._stop:
            try:
                win32pipe.ConnectNamedPipe(h, None)
            except pywintypes.error as exc:
                if exc.winerror != 535:  # ERROR_PIPE_CONNECTED
                    break
            if self._stop:
                break
            conn = h
            h = self._new_instance()  # keep a listening instance available at all times
            threading.Thread(target=self._client, args=(conn,), daemon=True).start()
        try:
            win32file.CloseHandle(h)
        except Exception:  # noqa: BLE001
            pass

    def _client(self, h) -> None:
        # Polls instead of blocking in ReadFile so stop() can close the connection from this
        # thread (sync I/O on a pipe handle serializes, so another thread can't disconnect it).
        buf = b""
        try:
            while not self._stop:
                _, avail, _ = win32pipe.PeekNamedPipe(h, 0)
                if not avail:
                    time.sleep(0.005)
                    continue
                _, chunk = win32file.ReadFile(h, avail)
                buf += chunk
                while b"\n" in buf:
                    line, _, buf = buf.partition(b"\n")
                    req = json.loads(line)
                    self.requests.append(req)
                    reply = self.handler(req)
                    if reply == "close":
                        return
                    if reply == "hang":
                        continue
                    win32file.WriteFile(h, (json.dumps(reply) + "\n").encode())
        except pywintypes.error:
            pass
        finally:
            try:
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception:  # noqa: BLE001
                pass

    def stop(self) -> None:
        """Like the Revit process exiting: every open connection is closed server-side."""
        if self._stop:
            return
        self._stop = True
        try:  # unblock ConnectNamedPipe
            hc = win32file.CreateFile(pipe_client.PIPE_ROOT + self.name,
                                      win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                                      0, None, win32file.OPEN_EXISTING, 0, None)
            win32file.CloseHandle(hc)
        except pywintypes.error:
            pass
        self._thread.join(2)
        time.sleep(0.05)  # let connection threads notice _stop and close their handles


@pytest.fixture
def prefix() -> str:
    return f"ccorp-rbtest-{uuid.uuid4().hex[:8]}-"


@pytest.fixture
def bridges(prefix):
    started: list[FakeBridge] = []

    def make(pid: int, handler: Callable[[dict], object]) -> FakeBridge:
        b = FakeBridge(prefix, pid, handler).start()
        started.append(b)
        return b

    yield make
    for b in started:
        b.stop()


@pytest.fixture
def sessions(tmp_path):
    d = tmp_path / "sessions"
    d.mkdir()
    return d


def status_handler(active_doc: str | None):
    """Handler answering bridge_status with an optional active document, echo otherwise."""

    def handler(req):
        if req["method"] == "bridge_status":
            doc = {"title": active_doc} if active_doc else None
            return envelope(req, {"pid": 0, "active_doc": doc})
        return envelope(req, {"echo": req["params"]})

    return handler
