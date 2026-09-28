"""pipe_client against a fake bridge: discovery, envelopes, busy/timeout, no retry on writes."""

from __future__ import annotations

import json
import time

import pytest

from revit_bridge_mcp import pipe_client
from revit_bridge_mcp.pipe_client import BridgeClient, BridgeError

from .conftest import envelope, status_handler


def make_client(prefix, sessions, **kw) -> BridgeClient:
    kw.setdefault("timeout_s", 5)
    return BridgeClient(prefix=prefix, sessions_dir=sessions, **kw)


# ── discovery ────────────────────────────────────────────────────────────────
def test_discovers_single_instance(prefix, sessions, bridges):
    bridges(111, status_handler("A.rvt"))
    c = make_client(prefix, sessions)
    assert pipe_client.list_pipes(prefix) == [f"{prefix}111"]
    assert c.choose_pid() == 111
    env = c.call("list_documents")
    assert env["ok"] and env["result"] == {"echo": {"timeout_s": 5.0}}


def test_session_files_merge_with_live_pipes(prefix, sessions, bridges):
    bridges(111, status_handler("A.rvt"))
    (sessions / "111.json").write_text(json.dumps({"pid": 111, "revitYear": "2027"}))
    (sessions / "999.json").write_text(json.dumps({"pid": 999, "revitYear": "2025"}))  # stale
    c = make_client(prefix, sessions)
    inst = {i.pid: i for i in c.instances(with_status=True)}
    assert inst[111].alive and inst[111].session["revitYear"] == "2027"
    assert inst[111].to_dict()["active_doc"] == {"title": "A.rvt"}
    assert not inst[999].alive


def test_prefers_instance_with_active_document(prefix, sessions, bridges):
    bridges(111, status_handler(None))
    bridges(222, status_handler("Scratch.rvt"))
    c = make_client(prefix, sessions)
    assert c.choose_pid() == 222


def test_ambiguous_instances_raise_with_listing(prefix, sessions, bridges):
    bridges(111, status_handler("A.rvt"))
    bridges(222, status_handler("B.rvt"))
    c = make_client(prefix, sessions)
    with pytest.raises(BridgeError) as ei:
        c.choose_pid()
    assert ei.value.code == "AMBIGUOUS_INSTANCE"
    assert "111" in str(ei.value) and "222" in str(ei.value)


def test_pinned_pid_wins_and_missing_pid_errors(prefix, sessions, bridges):
    bridges(111, status_handler("A.rvt"))
    bridges(222, status_handler("B.rvt"))
    c = make_client(prefix, sessions, pid=111)
    assert c.choose_pid() == 111
    c.select(333)
    with pytest.raises(BridgeError) as ei:
        c.choose_pid()
    assert ei.value.code == "BRIDGE_UNAVAILABLE"


def test_no_bridge_running(prefix, sessions):
    c = make_client(prefix, sessions)
    with pytest.raises(BridgeError) as ei:
        c.call("bridge_status")
    assert ei.value.code == "BRIDGE_UNAVAILABLE"


# ── envelope ─────────────────────────────────────────────────────────────────
def test_envelope_passthrough_including_failures(prefix, sessions, bridges):
    def handler(req):
        if req["method"] == "delete_elements":
            return envelope(req, {"count": 3, "requires_confirm": True}, ok=False,
                            error={"code": "CONFIRM_REQUIRED", "message": "needs confirm"})
        return envelope(req, [1, 2], warnings=[{"message": "w"}], tx_name="Claude: x")

    bridges(111, handler)
    c = make_client(prefix, sessions)
    env = c.call("create_walls", {"level": "L1", "dry_run": True, "unused": None}, write=True)
    assert env["ok"] and env["result"] == [1, 2] and env["tx_name"] == "Claude: x"
    assert env["warnings"][0]["message"] == "w"
    preview = c.call("delete_elements", {"ids": [1, 2, 3]}, write=True)
    assert preview["ok"] is False and preview["error"]["code"] == "CONFIRM_REQUIRED"
    assert preview["result"]["requires_confirm"] is True


def test_none_params_stripped_and_timeout_forwarded(prefix, sessions, bridges):
    b = bridges(111, status_handler("A.rvt"))
    c = make_client(prefix, sessions)
    c.call("query_elements", {"category": "Walls", "level": None}, timeout_s=12)
    q = [r for r in b.requests if r["method"] == "query_elements"][-1]
    assert q["params"] == {"category": "Walls", "timeout_s": 12.0}


def test_stale_replies_are_discarded(prefix, sessions, bridges):
    bridges(111, lambda req: envelope(req, "fresh"))
    c = make_client(prefix, sessions)
    conn = c._connection()
    # Inject a stale line into the client's buffer before the real reply arrives.
    conn._buf = (json.dumps(envelope({"id": "stale", "method": "x"}, "old")) + "\n").encode()
    env = c.call("bridge_status")
    assert env["result"] == "fresh"


# ── busy / timeout ───────────────────────────────────────────────────────────
def test_revit_busy_is_returned_not_retried(prefix, sessions, bridges):
    b = bridges(111, lambda req: envelope(req, None, ok=False, error={
        "code": "REVIT_BUSY", "message": "not picked up", "hint": "modal dialog"}))
    c = make_client(prefix, sessions)
    env = c.call("create_walls", {"level": "L1"}, write=True)
    assert env["error"]["code"] == "REVIT_BUSY"
    assert sum(r["method"] == "create_walls" for r in b.requests) == 1
    env = c.call("list_levels")  # reads aren't retried on a *reply* either
    assert env["error"]["code"] == "REVIT_BUSY"
    assert sum(r["method"] == "list_levels" for r in b.requests) == 1


def test_client_timeout_when_no_reply(prefix, sessions, bridges, monkeypatch):
    monkeypatch.setattr(pipe_client, "CLIENT_MARGIN_S", 0.2)
    bridges(111, lambda req: "hang")
    c = make_client(prefix, sessions, timeout_s=0.5)
    t0 = time.monotonic()
    with pytest.raises(BridgeError) as ei:
        c.call("create_walls", {"level": "L1"}, write=True)
    assert ei.value.code == "TIMEOUT"
    assert "Do not repeat a write blindly" in str(ei.value)
    assert time.monotonic() - t0 < 3


def test_add_in_timeout_envelope_passes_through(prefix, sessions, bridges):
    bridges(111, lambda req: envelope(req, None, ok=False, error={
        "code": "TIMEOUT", "message": "STILL RUNNING inside Revit"}))
    c = make_client(prefix, sessions)
    env = c.call("open_document", {"path": "x.rvt"}, write=True)
    assert env["error"]["code"] == "TIMEOUT"


# ── no retry on writes ───────────────────────────────────────────────────────
def test_write_is_never_retried_after_pipe_break(prefix, sessions, bridges):
    b = bridges(111, lambda req: "close" if req["method"] == "create_walls" else envelope(req, "ok"))
    c = make_client(prefix, sessions)
    with pytest.raises(BridgeError) as ei:
        c.call("create_walls", {"level": "L1"}, write=True)
    assert ei.value.code == "PIPE_BROKEN"
    assert "NOT retried" in str(ei.value)
    assert sum(r["method"] == "create_walls" for r in b.requests) == 1
    # The client recovers for the next call.
    assert c.call("bridge_status")["ok"]


def test_read_is_retried_once_after_pipe_break(prefix, sessions, bridges):
    seen = {"n": 0}

    def handler(req):
        if req["method"] == "list_levels":
            seen["n"] += 1
            if seen["n"] == 1:
                return "close"
        return envelope(req, "levels")

    b = bridges(111, handler)
    c = make_client(prefix, sessions)
    env = c.call("list_levels")
    assert env["result"] == "levels"
    assert sum(r["method"] == "list_levels" for r in b.requests) == 2


def test_stale_connection_reopened_before_write(prefix, sessions, bridges):
    """A dead connection is detected (PeekNamedPipe) and replaced BEFORE a write is sent."""
    b = bridges(111, lambda req: envelope(req, "ok"))
    c = make_client(prefix, sessions)
    assert c.call("bridge_status")["ok"]
    b.stop()
    b2 = bridges(111, lambda req: envelope(req, "second"))  # same pid: "Revit restarted"
    time.sleep(0.2)
    env = c.call("create_walls", {"level": "L1"}, write=True)
    assert env["result"] == "second"
    assert sum(r["method"] == "create_walls" for r in b2.requests) == 1
