"""Named-pipe client for the RevitBridge add-in.

Protocol: newline-delimited JSON over ``\\\\.\\pipe\\ccorp-revitbridge-<pid>``.
Request ``{"id", "method", "params"}`` → reply envelope
``{id, ok, result, warnings[], errors[], changed{...}, tx_name, elapsed_ms, doc, error?}``.

Instance selection (first match wins):
  1. ``REVIT_BRIDGE_PID`` (or :meth:`BridgeClient.select`)
  2. the only live instance
  3. the only live instance with an active document
  4. otherwise an error listing the instances

Safety: **write methods are never retried.** A broken pipe is detected *before* sending
(and the connection re-opened), but once a write request has been sent, any failure is
reported, not retried — the add-in may already have applied it.
"""

from __future__ import annotations

import json
import os
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path

from .config import logger, settings

PIPE_PREFIX = "ccorp-revitbridge-"
PIPE_ROOT = "\\\\.\\pipe\\"
# Slack on top of the add-in's own timeout so its TIMEOUT envelope arrives first.
CLIENT_MARGIN_S = 5.0
POLL_S = 0.01


class BridgeError(RuntimeError):
    """Transport-level failure (no bridge, pipe broken, timeout). Surfaced as a tool error."""

    def __init__(self, code: str, message: str, hint: str | None = None):
        super().__init__(f"[{code}] {message}" + (f" — {hint}" if hint else ""))
        self.code = code
        self.hint = hint


def default_sessions_dir() -> Path:
    return Path(os.environ.get("APPDATA", "")) / "CCorp" / "RevitBridge" / "sessions"


# ── Low-level pipe connection (pywin32) ─────────────────────────────────────
class PipeConnection:
    """One client handle to a bridge pipe. Blocking writes, polled reads with deadlines."""

    def __init__(self, name: str):
        import pywintypes  # type: ignore
        import win32file  # type: ignore
        import win32pipe  # type: ignore

        self.name = name
        path = PIPE_ROOT + name
        deadline = time.monotonic() + 3.0
        while True:
            try:
                self._h = win32file.CreateFile(
                    path,
                    win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                    0, None, win32file.OPEN_EXISTING, 0, None,
                )
                break
            except pywintypes.error as exc:
                if exc.winerror == 231 and time.monotonic() < deadline:  # ERROR_PIPE_BUSY
                    try:
                        win32pipe.WaitNamedPipe(path, 500)
                    except pywintypes.error:
                        time.sleep(0.05)
                    continue
                raise BridgeError(
                    "BRIDGE_UNAVAILABLE", f"Cannot open pipe {name}: {exc.strerror}",
                    "Is Revit running with the RevitBridge add-in, and is the bridge switched On?",
                ) from exc
        win32pipe.SetNamedPipeHandleState(self._h, win32pipe.PIPE_READMODE_BYTE, None, None)
        self._buf = b""

    def alive(self) -> bool:
        """True if the server end is still connected (checked without sending anything)."""
        import pywintypes  # type: ignore
        import win32pipe  # type: ignore

        try:
            win32pipe.PeekNamedPipe(self._h, 0)
            return True
        except pywintypes.error:
            return False

    def send_line(self, obj: dict) -> None:
        import pywintypes  # type: ignore
        import win32file  # type: ignore

        data = (json.dumps(obj, separators=(",", ":")) + "\n").encode("utf-8")
        try:
            win32file.WriteFile(self._h, data)
        except pywintypes.error as exc:
            raise BridgeError("PIPE_BROKEN", f"Write to {self.name} failed: {exc.strerror}") from exc

    def read_line(self, deadline: float) -> dict | None:
        """Read one JSON line, or None if the deadline passes first."""
        import pywintypes  # type: ignore
        import win32file  # type: ignore
        import win32pipe  # type: ignore

        while b"\n" not in self._buf:
            try:
                _, avail, _ = win32pipe.PeekNamedPipe(self._h, 0)
                if avail:
                    _, chunk = win32file.ReadFile(self._h, avail)
                    self._buf += chunk
                    continue
            except pywintypes.error as exc:
                raise BridgeError("PIPE_BROKEN", f"Read from {self.name} failed: {exc.strerror}") from exc
            if time.monotonic() >= deadline:
                return None
            time.sleep(POLL_S)
        line, _, self._buf = self._buf.partition(b"\n")
        return json.loads(line.decode("utf-8"))

    def close(self) -> None:
        try:
            import win32file  # type: ignore

            win32file.CloseHandle(self._h)
        except Exception:  # noqa: BLE001
            pass


# ── Discovery ────────────────────────────────────────────────────────────────
@dataclass
class Instance:
    pid: int
    pipe: str
    alive: bool
    session: dict = field(default_factory=dict)
    status: dict | None = None

    def to_dict(self) -> dict:
        d = {"pid": self.pid, "pipe": self.pipe, "alive": self.alive, **self.session}
        if self.status:
            d["active_doc"] = self.status.get("active_doc")
            d["revit_year"] = self.status.get("revit_year", d.get("revitYear"))
        return d


def list_pipes(prefix: str = PIPE_PREFIX) -> list[str]:
    try:
        names = os.listdir(PIPE_ROOT)
    except OSError:
        return []
    return sorted(n for n in names if n.startswith(prefix) and n[len(prefix):].isdigit())


# ── Client ───────────────────────────────────────────────────────────────────
class BridgeClient:
    """Thread-safe client. One persistent connection to the selected Revit instance."""

    def __init__(
        self,
        prefix: str = PIPE_PREFIX,
        sessions_dir: Path | None = None,
        pid: int | None = None,
        timeout_s: float | None = None,
    ):
        self.prefix = prefix
        self.sessions_dir = sessions_dir or default_sessions_dir()
        self.pinned_pid = pid if pid is not None else settings.pid
        self.timeout_s = timeout_s or settings.timeout_s
        self._conn: PipeConnection | None = None
        self._conn_pid: int | None = None
        self._lock = threading.RLock()

    # -- discovery -------------------------------------------------------------
    def _read_session(self, pid: int) -> dict:
        try:
            return json.loads((self.sessions_dir / f"{pid}.json").read_text(encoding="utf-8"))
        except Exception:  # noqa: BLE001
            return {}

    def instances(self, with_status: bool = False) -> list[Instance]:
        """Live pipes plus any session files whose pipe is gone (alive=False)."""
        found: dict[int, Instance] = {}
        for name in list_pipes(self.prefix):
            pid = int(name[len(self.prefix):])
            found[pid] = Instance(pid=pid, pipe=name, alive=True, session=self._read_session(pid))
        if self.sessions_dir.is_dir():
            for f in self.sessions_dir.glob("*.json"):
                if f.stem.isdigit() and int(f.stem) not in found:
                    pid = int(f.stem)
                    found[pid] = Instance(pid=pid, pipe=self.prefix + f.stem, alive=False,
                                          session=self._read_session(pid))
        if with_status:
            for inst in found.values():
                if inst.alive:
                    try:
                        env = self._oneshot(inst.pipe, "bridge_status", {}, 5.0)
                        inst.status = env.get("result") if env.get("ok") else None
                    except BridgeError:
                        inst.alive = False
        return sorted(found.values(), key=lambda i: i.pid)

    def _oneshot(self, pipe: str, method: str, params: dict, timeout_s: float) -> dict:
        conn = PipeConnection(pipe)
        try:
            return self._exchange(conn, method, params, timeout_s)
        finally:
            conn.close()

    def choose_pid(self) -> int:
        if self.pinned_pid is not None:
            if self.prefix + str(self.pinned_pid) not in list_pipes(self.prefix):
                raise BridgeError(
                    "BRIDGE_UNAVAILABLE",
                    f"No RevitBridge pipe for pid {self.pinned_pid}.",
                    "Unset REVIT_BRIDGE_PID or call select_instance with a live pid (list_instances).",
                )
            return self.pinned_pid
        live = [i for i in self.instances() if i.alive]
        if not live:
            raise BridgeError(
                "BRIDGE_UNAVAILABLE", "No running Revit with the RevitBridge add-in was found.",
                "Start Revit (2025/2027) with RevitBridge installed and make sure "
                "CCorp Tools → Claude Bridge is On.",
            )
        if len(live) == 1:
            return live[0].pid
        with_status = [i for i in self.instances(with_status=True) if i.alive]
        with_doc = [i for i in with_status if i.status and i.status.get("active_doc")]
        if len(with_doc) == 1:
            return with_doc[0].pid
        listing = "; ".join(
            f"pid {i.pid} ({(i.status or {}).get('active_doc', {}) and i.status['active_doc'].get('title')})"
            for i in with_status
        )
        raise BridgeError(
            "AMBIGUOUS_INSTANCE", f"{len(with_status)} Revit instances are running: {listing}.",
            "Call select_instance(pid) or set REVIT_BRIDGE_PID.",
        )

    def select(self, pid: int | None) -> None:
        with self._lock:
            self.pinned_pid = pid
            self._drop()

    # -- calls -----------------------------------------------------------------
    def _drop(self) -> None:
        if self._conn is not None:
            self._conn.close()
        self._conn = None
        self._conn_pid = None

    def _connection(self) -> PipeConnection:
        pid = self.choose_pid()
        if self._conn is not None and (self._conn_pid != pid or not self._conn.alive()):
            logger.info("Pipe to pid %s is stale; reconnecting.", self._conn_pid)
            self._drop()
        if self._conn is None:
            self._conn = PipeConnection(self.prefix + str(pid))
            self._conn_pid = pid
        return self._conn

    @staticmethod
    def _exchange(conn: PipeConnection, method: str, params: dict, timeout_s: float) -> dict:
        req_id = uuid.uuid4().hex
        conn.send_line({"id": req_id, "method": method, "params": params})
        deadline = time.monotonic() + timeout_s + CLIENT_MARGIN_S
        while True:
            reply = conn.read_line(deadline)
            if reply is None:
                raise BridgeError(
                    "TIMEOUT", f"No reply to {method} within {timeout_s + CLIENT_MARGIN_S:.0f} s.",
                    "The call may still be running in Revit. Do not repeat a write blindly — "
                    "inspect the model first.",
                )
            if reply.get("id") == req_id:
                return reply
            logger.warning("Discarding stale reply %s", reply.get("id"))

    def call(self, method: str, params: dict | None = None, *, write: bool = False,
             timeout_s: float | None = None) -> dict:
        """Send one request and return the add-in's envelope.

        Read methods get one reconnect-and-retry on a broken pipe. Write methods never do.
        """
        params = {k: v for k, v in (params or {}).items() if v is not None}
        t = float(timeout_s or params.get("timeout_s") or self.timeout_s)
        params.setdefault("timeout_s", t)
        attempts = 1 if write else 2
        with self._lock:
            for attempt in range(attempts):
                conn = self._connection()
                try:
                    env = self._exchange(conn, method, params, t)
                    logger.debug("%s -> ok=%s (%s ms)", method, env.get("ok"), env.get("elapsed_ms"))
                    return env
                except BridgeError as exc:
                    self._drop()  # the connection is out of sync or dead either way
                    if exc.code == "PIPE_BROKEN" and attempt + 1 < attempts:
                        logger.info("Pipe broke during %s (read); retrying once.", method)
                        continue
                    if write and exc.code == "PIPE_BROKEN":
                        raise BridgeError(
                            "PIPE_BROKEN", f"The pipe broke during write '{method}'.",
                            "NOT retried: it may or may not have been applied. Check the model "
                            "before trying again.",
                        ) from exc
                    raise
        raise BridgeError("PIPE_BROKEN", f"{method} failed after reconnect.")  # pragma: no cover


_client: BridgeClient | None = None


def client() -> BridgeClient:
    """Process-wide client used by the MCP tools."""
    global _client
    if _client is None:
        _client = BridgeClient()
    return _client
