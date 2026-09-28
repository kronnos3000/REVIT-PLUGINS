"""UI, navigation and export tools. None of these change the model."""

from __future__ import annotations

from typing import Literal, Optional, Union

from ._common import call, read_tool


def register(mcp) -> None:
    @read_tool(mcp)
    def select(ids: list[int]) -> dict:
        """Set the selection in the active document (empty list clears it)."""
        return call("select", {"ids": ids})

    @read_tool(mcp)
    def zoom_to(ids: list[int], select: bool = True) -> dict:
        """Zoom the active view to these elements (Revit may switch to a view that shows them)."""
        return call("zoom_to", {"ids": ids, "select": select})

    @read_tool(mcp)
    def open_view(id: Optional[int] = None, name: Optional[str] = None) -> dict:
        """Make a view (or sheet: name, number or id) the active view in the active document."""
        return call("open_view", {"id": id, "name": name})

    @read_tool(mcp)
    def capture_view(view: Optional[str] = None, width: int = 1600,
                     format: Literal["png", "jpg"] = "png", max_inline_kb: int = 400,
                     doc: Optional[str] = None) -> dict:
        """Render a view to an image file under %APPDATA%\\CCorp\\RevitBridge\\captures. Returns the
        path, and base64 when the file is at most max_inline_kb. view: name or id (default: active)."""
        return call("capture_view", {"view": view, "width": width, "format": format,
                                     "max_inline_kb": max_inline_kb, "doc": doc}, timeout_s=180)

    @read_tool(mcp)
    def export_image(views: Optional[list[str]] = None, width: int = 2400, dpi: int = 150,
                     format: Literal["png", "jpg", "tif"] = "png", prefix: str = "export",
                     folder: Optional[str] = None, doc: Optional[str] = None) -> dict:
        """Export one or more views/sheets as images. Returns the created file paths."""
        return call("export_image", {"views": views, "width": width, "dpi": dpi, "format": format,
                                     "prefix": prefix, "folder": folder, "doc": doc}, timeout_s=600)

    @read_tool(mcp)
    def export_pdf(sheets: Optional[list[str]] = None, views: Optional[list[str]] = None,
                   combine: bool = True, naming: Optional[str] = None, folder: Optional[str] = None,
                   doc: Optional[str] = None) -> dict:
        """Export sheets (number, name or id) and/or views to PDF with Revit's PDF exporter.
        combine=True makes one file named `naming` (default <model>_<timestamp>). For
        token-driven batch naming use the CCorpPrint tool in Revit."""
        return call("export_pdf", {"sheets": sheets, "views": views, "combine": combine,
                                   "naming": naming, "folder": folder, "doc": doc}, timeout_s=900)

    @read_tool(mcp)
    def export_dwg(views: Optional[list[str]] = None, setup: Optional[str] = None,
                   naming: Optional[str] = None, merged_views: bool = True,
                   folder: Optional[str] = None, doc: Optional[str] = None) -> dict:
        """Export views/sheets to DWG, optionally with a named DWG export setup from the model."""
        return call("export_dwg", {"views": views, "setup": setup, "naming": naming,
                                   "merged_views": merged_views, "folder": folder, "doc": doc},
                    timeout_s=900)

    @read_tool(mcp)
    def export_ifc(version: Optional[str] = None, view: Optional[str] = None,
                   options: Optional[dict[str, Union[str, bool, int]]] = None,
                   naming: Optional[str] = None, folder: Optional[str] = None,
                   doc: Optional[str] = None) -> dict:
        """Export the model to IFC. version: an IFCVersion name (e.g. 'IFC2x3CV2', 'IFC4RV');
        view: limit to elements visible in a view; options: extra IFC exporter options."""
        return call("export_ifc", {"version": version, "view": view, "options": options,
                                   "naming": naming, "folder": folder, "doc": doc}, timeout_s=1800)
