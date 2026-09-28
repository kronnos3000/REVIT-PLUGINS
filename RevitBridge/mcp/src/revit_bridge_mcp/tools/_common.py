"""Shared helpers for tool modules: calling the bridge and decorating write tools."""

from __future__ import annotations

from typing import Any, Callable

from pydantic import BaseModel

from ..pipe_client import client

WRITE_NOTE = """

Write tool: each call runs in ONE Revit transaction group named "Claude: <tool>", so ONE
Ctrl+Z in Revit undoes the whole call. Common flags:
  dry_run=True          run it, report `changed`, then roll back (model untouched)
  confirm=True          required for destructive/bulk ops (>5,000 elements); without it you get a preview
  dismiss_warnings=True delete Revit warnings instead of leaving them for the user
Returns the bridge envelope: {ok, result, warnings[], errors[], changed{created,modified,deleted},
tx_name, elapsed_ms, doc, error?}. Lengths are FEET (numbers) or strings with units
("12'-6\\"", "150 mm", "3.2 m"). Never repeat a failed write without checking the model first."""

READ_NOTE = """

Returns the bridge envelope {ok, result, warnings[], errors[], doc, elapsed_ms, error?}.
Lengths are in feet."""


def _dump(v: Any) -> Any:
    if isinstance(v, BaseModel):
        return v.model_dump(exclude_none=True)
    if isinstance(v, list):
        return [_dump(x) for x in v]
    if isinstance(v, dict):
        return {k: _dump(x) for k, x in v.items() if x is not None}
    return v


def call(method: str, params: dict | None = None, *, write: bool = False,
         timeout_s: float | None = None) -> dict:
    """Send ``method`` to the selected Revit instance and return its envelope."""
    return client().call(method, _dump(params or {}), write=write, timeout_s=timeout_s)


def write_tool(mcp) -> Callable:
    """Register a write tool, appending the shared safety note to its description."""

    def deco(fn: Callable) -> Callable:
        fn.__doc__ = (fn.__doc__ or "").rstrip() + WRITE_NOTE
        return mcp.tool()(fn)

    return deco


def read_tool(mcp) -> Callable:
    def deco(fn: Callable) -> Callable:
        fn.__doc__ = (fn.__doc__ or "").rstrip() + READ_NOTE
        return mcp.tool()(fn)

    return deco
