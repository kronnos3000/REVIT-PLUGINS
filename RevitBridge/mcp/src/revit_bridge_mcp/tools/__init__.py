"""Tool modules for the revit-bridge MCP server.

Each module exposes a ``register(mcp)`` function that attaches its tools to the
shared FastMCP instance. ``server.py`` calls them all during start-up. Tool names
are identical to the add-in's CommandRouter keys.
"""

from . import create_tools, modify_tools, query_tools, script_tools, session_tools, structural_tools, ui_tools

REGISTRARS = [
    session_tools,
    query_tools,
    ui_tools,
    create_tools,
    modify_tools,
    structural_tools,
    script_tools,
]


def register_all(mcp) -> None:
    """Register every tool module onto the given FastMCP instance."""
    for module in REGISTRARS:
        module.register(mcp)


__all__ = ["register_all", "REGISTRARS"]
