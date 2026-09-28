"""Query tools: elements, types, families, levels, views, sheets, schedules, worksets, links."""

from __future__ import annotations

from typing import Literal, Optional, Union

from ..models import BBox, ParamFilter
from ._common import call, read_tool

Fields = Union[Literal["basic", "params", "all"], list[str]]
Geometry = Literal["none", "bbox", "location", "solid_summary", "all"]


def register(mcp) -> None:
    @read_tool(mcp)
    def query_elements(
        category: Optional[Union[str, list[str]]] = None,
        class_name: Optional[str] = None,
        level: Optional[str] = None,
        view: Optional[str] = None,
        bbox: Optional[BBox] = None,
        bbox_inside: bool = False,
        filters: Optional[list[ParamFilter]] = None,
        name_contains: Optional[str] = None,
        name_equals: Optional[str] = None,
        type_name: Optional[str] = None,
        include_types: bool = False,
        max_results: int = 200,
        fields: Fields = "basic",
        geometry: Geometry = "none",
        doc: Optional[str] = None,
    ) -> dict:
        """Find elements and get an analysis breakdown (counts by category, level and type).

        Args:
            category: 'Walls', 'Structural Framing', 'OST_Doors' ... or a list of them.
            class_name: Revit API class, e.g. 'Wall', 'Floor', 'FamilyInstance'.
            level: Level name or id (ElementLevelFilter).
            view: Only elements visible in this view (name or id).
            bbox: Intersecting (or, with bbox_inside, fully inside) this box, feet.
            filters: Parameter filters, e.g. [{"param":"Mark","op":"eq","value":"B1"}].
            name_contains / name_equals / type_name: text filters (case-insensitive).
            max_results: Elements returned in detail; `total` is always the full count.
            fields: 'basic' | 'params' | 'all' (instance+type params) | [parameter names].
            geometry: none | bbox | location | solid_summary | all.
        """
        return call("query_elements", {
            "category": category, "class": class_name, "level": level, "view": view, "bbox": bbox,
            "bbox_inside": bbox_inside, "filters": filters, "name_contains": name_contains,
            "name_equals": name_equals, "type_name": type_name, "include_types": include_types,
            "max_results": max_results, "fields": fields, "geometry": geometry, "doc": doc,
        })

    @read_tool(mcp)
    def get_elements(ids: list[int], fields: Fields = "params", geometry: Geometry = "location",
                     doc: Optional[str] = None) -> dict:
        """Details for specific element ids: basic info plus parameters and geometry.
        fields: basic | params | all | [names]; geometry: none | bbox | location | solid_summary | all."""
        return call("get_elements", {"ids": ids, "fields": fields, "geometry": geometry, "doc": doc})

    @read_tool(mcp)
    def list_types(category: Optional[str] = None, class_name: Optional[str] = None,
                   name_contains: Optional[str] = None, max_results: int = 500,
                   doc: Optional[str] = None) -> dict:
        """Element types (family symbols, wall/floor types, ...) as 'Family: Type' with ids."""
        return call("list_types", {"category": category, "class": class_name,
                                   "name_contains": name_contains, "max_results": max_results, "doc": doc})

    @read_tool(mcp)
    def list_families(category: Optional[str] = None, doc: Optional[str] = None) -> dict:
        """Loaded families (optionally one category) with their types."""
        return call("list_families", {"category": category, "doc": doc})

    @read_tool(mcp)
    def list_levels(doc: Optional[str] = None) -> dict:
        """Levels sorted by elevation (feet) with display strings."""
        return call("list_levels", {"doc": doc})

    @read_tool(mcp)
    def list_grids(doc: Optional[str] = None) -> dict:
        """Grids with their curves (feet)."""
        return call("list_grids", {"doc": doc})

    @read_tool(mcp)
    def list_views(type: Optional[str] = None, include_templates: bool = False,
                   doc: Optional[str] = None) -> dict:
        """Views. type: a ViewType ('FloorPlan', 'Section', 'ThreeD', 'DraftingView', 'Schedule',
        'DrawingSheet', ...) or 'plan' / '3d'. Includes the sheet each view is placed on."""
        return call("list_views", {"type": type, "include_templates": include_templates, "doc": doc})

    @read_tool(mcp)
    def list_sheets(doc: Optional[str] = None) -> dict:
        """Sheets with number, name, placed views and title block."""
        return call("list_sheets", {"doc": doc})

    @read_tool(mcp)
    def list_schedules(doc: Optional[str] = None) -> dict:
        """Schedules (excluding revision/keynote internals) with category and field count."""
        return call("list_schedules", {"doc": doc})

    @read_tool(mcp)
    def get_schedule_data(id: Optional[int] = None, name: Optional[str] = None, max_rows: int = 2000,
                          doc: Optional[str] = None) -> dict:
        """A schedule's table as text: header rows, visible column names and body rows."""
        return call("get_schedule_data", {"id": id, "name": name, "max_rows": max_rows, "doc": doc})

    @read_tool(mcp)
    def get_selection(fields: Fields = "basic", geometry: Geometry = "none", max_results: int = 500) -> dict:
        """What the user currently has selected in the active document."""
        return call("get_selection", {"fields": fields, "geometry": geometry, "max_results": max_results})

    @read_tool(mcp)
    def list_worksets(doc: Optional[str] = None) -> dict:
        """User worksets (workshared models): open/editable/owner/active."""
        return call("list_worksets", {"doc": doc})

    @read_tool(mcp)
    def list_phases(doc: Optional[str] = None) -> dict:
        """Phases in sequence order."""
        return call("list_phases", {"doc": doc})

    @read_tool(mcp)
    def list_design_options(doc: Optional[str] = None) -> dict:
        """Design options with their option set and primary flag."""
        return call("list_design_options", {"doc": doc})

    @read_tool(mcp)
    def list_links(doc: Optional[str] = None) -> dict:
        """Revit links (loaded state, path) and CAD links/imports."""
        return call("list_links", {"doc": doc})
