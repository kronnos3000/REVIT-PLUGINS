"""MCP server for Autodesk Revit, hosted by Claude.

Talks to the CCorp RevitBridge add-in (running inside Revit.exe) over a Windows named
pipe, ``\\\\.\\pipe\\ccorp-revitbridge-<RevitPID>``. The add-in owns the Revit API; this
package only translates MCP tool calls into bridge requests.
"""

__version__ = "1.3.0"
__all__ = ["__version__"]
