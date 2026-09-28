"""FastMCP server entrypoint for Autodesk Revit (via the RevitBridge add-in).

Builds the FastMCP instance, registers every tool module, and runs over stdio
(the transport Claude Desktop uses).
"""

from __future__ import annotations

import sys

from mcp.server.fastmcp import FastMCP

from . import __version__, tools
from .config import logger

INSTRUCTIONS = """\
This server drives Autodesk Revit through the CCorp RevitBridge add-in, which runs inside
Revit.exe and executes each call on Revit's main thread.

Preflight: call bridge_status first. If several Revit sessions are open, list_instances and
select_instance(pid).

Units: Revit internal units. Lengths are FEET; angles are degrees on input unless you write
"rad". Any length argument also accepts a string with units: "12'-6\\"", "150 in", "450 mm",
"3.2 m". Coordinates are [x, y, z] in feet.

Safety:
- Every write is ONE undo step in Revit (TransactionGroup "Claude: <tool>").
- dry_run=True runs a write and rolls it back, returning what would change.
- delete_elements, save_as over an existing file, synchronize_with_central, relinquish and
  closing a modified doc need confirm=True; without it they return a preview. Do not pass
  confirm=True unless the user explicitly asked for that action.
- Never synchronize with central or delete without explicit user instruction.
- Never retry a failed/timed-out write blindly: inspect the model first.
- Never run write tests against a real project model; use a scratch copy.

Use typed tools first. Use execute_csharp only when no typed tool fits; if the same
execute_csharp pattern is needed twice, say so so it can become a typed tool.
"""


def build_server() -> FastMCP:
    """Create the FastMCP instance with all tools registered."""
    mcp = FastMCP("revit-bridge", instructions=INSTRUCTIONS)
    tools.register_all(mcp)
    return mcp


mcp = build_server()


def _selfcheck() -> int:
    """List registered tools without connecting to Revit (CI-friendly)."""
    import asyncio

    try:
        tool_list = asyncio.run(mcp.list_tools())
        names = [t.name for t in tool_list]
    except Exception:  # noqa: BLE001 - fall back to the internal manager
        names = sorted(getattr(mcp, "_tool_manager")._tools.keys())  # type: ignore[attr-defined]

    print(f"[selfcheck] revit_bridge_mcp v{__version__}", file=sys.stderr)
    print(f"[selfcheck] {len(names)} tools registered:", file=sys.stderr)
    for n in sorted(names):
        print(f"   - {n}", file=sys.stderr)
    print("[selfcheck] OK", file=sys.stderr)
    return 0


def main() -> None:
    """Console entrypoint. Runs the stdio server, or --selfcheck to validate wiring."""
    if "--selfcheck" in sys.argv:
        raise SystemExit(_selfcheck())
    logger.info("Starting revit-bridge MCP server (revit_bridge_mcp v%s) over stdio", __version__)
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
