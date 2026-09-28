"""Session tools: bridge status, instance selection, documents, save/sync/close."""

from __future__ import annotations

from typing import Optional

from ..pipe_client import client
from ._common import call, read_tool


def register(mcp) -> None:
    @read_tool(mcp)
    def bridge_status() -> dict:
        """Report the connected Revit session: pid, Revit year/build, bridge version, active
        document and view. Call this first (preflight) in every chat."""
        return call("bridge_status")

    @mcp.tool()
    def list_instances() -> list[dict]:
        """List Revit sessions running the RevitBridge add-in (from the sessions folder plus a
        pipe liveness check), with each one's active document. Use select_instance to choose
        one when several are open."""
        return [i.to_dict() for i in client().instances(with_status=True)]

    @mcp.tool()
    def select_instance(pid: Optional[int] = None) -> dict:
        """Pin all following calls to the Revit session with this process id. Pass no pid to
        go back to automatic selection (the only instance, or the one with an active doc)."""
        client().select(pid)
        return {"selected_pid": pid, "mode": "pinned" if pid else "auto"}

    @read_tool(mcp)
    def list_documents() -> dict:
        """List open documents (projects and families) with title, path, active flag,
        workshared/modified/read-only state."""
        return call("list_documents")

    @read_tool(mcp)
    def activate_document(doc: str) -> dict:
        """Make an open (saved) document the active one. doc = title or full path."""
        return call("activate_document", {"doc": doc})

    @read_tool(mcp)
    def open_document(path: str, detach: bool = False, audit: bool = False, activate: bool = True,
                      timeout_s: float = 300) -> dict:
        """Open an .rvt/.rfa file. Quote-free full path (spaces, '&' are fine).

        Args:
            path: Full file path.
            detach: Detach from central (preserving worksets) — use for scratch copies of
                workshared models.
            audit: Run Revit's audit while opening.
            activate: Make it the active document (needed for select/zoom/open_view).
            timeout_s: Opening big models on the G: drive can take minutes.
        """
        return call("open_document", {"path": path, "detach": detach, "audit": audit,
                                      "activate": activate}, timeout_s=timeout_s)

    @read_tool(mcp)
    def new_document(template: Optional[str] = None, save_path: Optional[str] = None,
                     activate: bool = True) -> dict:
        """Create a new project from a template (default: Revit's default project template),
        save it (default %TEMP%\\RevitBridge\\scratch_<timestamp>.rvt) and activate it. Use this
        for scratch/test work — never test writes on a real project model."""
        return call("new_document", {"template": template, "save_path": save_path,
                                     "activate": activate}, timeout_s=180)

    @read_tool(mcp)
    def save_document(doc: Optional[str] = None) -> dict:
        """Save the document in place (Ctrl+S). Not an undoable step."""
        return call("save_document", {"doc": doc}, write=True, timeout_s=300)

    @read_tool(mcp)
    def save_as(path: Optional[str] = None, confirm: bool = False, as_central: bool = False,
                doc: Optional[str] = None) -> dict:
        """Save As. Without path, saves next to the current file as <name>_v2, _v3, ...
        (first free). Overwriting an existing file needs confirm=True; without it you get a
        preview of the file that would be replaced."""
        return call("save_as", {"path": path, "confirm": confirm, "as_central": as_central,
                                "doc": doc}, write=True, timeout_s=300)

    @read_tool(mcp)
    def close_document(doc: Optional[str] = None, save: bool = False, confirm: bool = False) -> dict:
        """Close a document. Closing a modified document without save=True needs confirm=True.
        Revit cannot close the ACTIVE document via the API unless another saved document can
        be activated first (done automatically)."""
        return call("close_document", {"doc": doc, "save": save, "confirm": confirm}, write=True)

    @read_tool(mcp)
    def synchronize_with_central(confirm: bool = False, comment: Optional[str] = None,
                                 relinquish: bool = True, doc: Optional[str] = None) -> dict:
        """Synchronize a workshared model with central. NEVER call with confirm=True unless the
        user explicitly asked to sync in this conversation. Without confirm returns a preview."""
        return call("synchronize_with_central", {"confirm": confirm, "comment": comment,
                                                 "relinquish": relinquish, "doc": doc},
                    write=True, timeout_s=600)

    @read_tool(mcp)
    def relinquish(confirm: bool = False, doc: Optional[str] = None) -> dict:
        """Relinquish all borrowed elements/worksets in a workshared model. Needs confirm=True."""
        return call("relinquish", {"confirm": confirm, "doc": doc}, write=True, timeout_s=300)

    @read_tool(mcp)
    def get_warnings(max_results: int = 500, doc: Optional[str] = None) -> dict:
        """The document's Revit warnings, grouped by message with counts, plus the first
        max_results individual warnings with their element ids."""
        return call("get_warnings", {"max_results": max_results, "doc": doc})

    @read_tool(mcp)
    def get_project_info(doc: Optional[str] = None) -> dict:
        """Project Information parameters, site location, length unit, phases, level count."""
        return call("get_project_info", {"doc": doc})
