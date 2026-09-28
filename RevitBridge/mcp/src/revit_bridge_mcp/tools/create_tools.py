"""Create tools: levels, grids, walls, floors, roofs, families, framing, views, sheets, annotation."""

from __future__ import annotations

from typing import Literal, Optional, Union

from pydantic import BaseModel, Field

from ..models import BBox, Length, LineSpec, PlacementSpec, Point, ScheduleFilter, ScheduleSort
from ._common import call, write_tool


class LevelSpec(BaseModel):
    elevation: Length = Field(description="Elevation, feet or unit string.")
    name: Optional[str] = None


class GridSpec(BaseModel):
    start: Point
    end: Point
    name: Optional[str] = None


def _w(method: str, params: dict, timeout_s: float | None = None) -> dict:
    return call(method, params, write=True, timeout_s=timeout_s)


def register(mcp) -> None:
    @write_tool(mcp)
    def create_levels(levels: list[LevelSpec], create_plan_views: bool = True, dry_run: bool = False,
                      dismiss_warnings: bool = False, doc: Optional[str] = None) -> dict:
        """Create levels at elevations (feet), optionally with a floor plan view for each."""
        return _w("create_levels", dict(levels=levels, create_plan_views=create_plan_views,
                                        dry_run=dry_run, dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_grids(grids: list[GridSpec], dry_run: bool = False, dismiss_warnings: bool = False,
                     doc: Optional[str] = None) -> dict:
        """Create straight grids from start/end points (feet), optionally named ('A', '1')."""
        return _w("create_grids", dict(grids=grids, dry_run=dry_run,
                                       dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_walls(
        level: str,
        curves: Optional[list[LineSpec]] = None,
        points: Optional[list[Point]] = None,
        closed: bool = False,
        type: Optional[str] = None,
        top_level: Optional[str] = None,
        height: Optional[Length] = None,
        base_offset: Optional[Length] = None,
        top_offset: Optional[Length] = None,
        flip: bool = False,
        structural: bool = False,
        dry_run: bool = False,
        dismiss_warnings: bool = False,
        doc: Optional[str] = None,
    ) -> dict:
        """Create straight walls from `curves` ([{start,end}]) or a polyline of `points`
        (closed=True closes the loop). Z defaults to the level elevation.

        Args:
            level: Base level name or id.
            type: Wall type ('Basic Wall: Generic - 8"' or id); default the first basic wall type.
            top_level: Top constraint level; otherwise `height` (default 10').
        """
        return _w("create_walls", dict(level=level, curves=curves, points=points, closed=closed,
                                       type=type, top_level=top_level, height=height,
                                       base_offset=base_offset, top_offset=top_offset, flip=flip,
                                       structural=structural, dry_run=dry_run,
                                       dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_floor(level: str, boundary: list[Point], type: Optional[str] = None,
                     offset: Optional[Length] = None, structural: bool = False,
                     dry_run: bool = False, dismiss_warnings: bool = False,
                     doc: Optional[str] = None) -> dict:
        """Create a floor from a closed boundary polygon (points, auto-closed) on a level."""
        return _w("create_floor", dict(level=level, boundary=boundary, type=type, offset=offset,
                                       structural=structural, dry_run=dry_run,
                                       dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_roof_footprint(level: str, boundary: list[Point], type: Optional[str] = None,
                              slope_deg: Optional[float] = None, sloped_edges: Optional[list[int]] = None,
                              overhang: Optional[Length] = None, base_offset: Optional[Length] = None,
                              dry_run: bool = False, dismiss_warnings: bool = False,
                              doc: Optional[str] = None) -> dict:
        """Footprint roof from a closed boundary. slope_deg applies to all edges (hip) unless
        sloped_edges lists 0-based edge indexes (e.g. gable on a rectangle: [0, 2]).
        4:12 pitch = 18.43 deg; 6:12 = 26.57 deg."""
        return _w("create_roof_footprint", dict(level=level, boundary=boundary, type=type,
                                                slope_deg=slope_deg, sloped_edges=sloped_edges,
                                                overhang=overhang, base_offset=base_offset,
                                                dry_run=dry_run, dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def place_family_instances(
        type: str,
        placements: Optional[list[PlacementSpec]] = None,
        points: Optional[list[Point]] = None,
        family: Optional[str] = None,
        level: Optional[str] = None,
        rotation: Optional[Union[float, str]] = None,
        host: Optional[int] = None,
        structural_type: Optional[Literal["NonStructural", "Column", "Footing", "Beam", "Brace"]] = None,
        dry_run: bool = False,
        dismiss_warnings: bool = False,
        doc: Optional[str] = None,
    ) -> dict:
        """Place point-based families: doors, windows, columns, foundations, generic models,
        furniture. `type` is 'Family: Type' (or pass family + type separately, or an id).
        Doors/windows use `host` (wall id) or the nearest wall within 3 ft on the level."""
        return _w("place_family_instances", dict(type=type, family=family, placements=placements,
                                                 points=points, level=level, rotation=rotation,
                                                 host=host, structural_type=structural_type,
                                                 dry_run=dry_run, dismiss_warnings=dismiss_warnings,
                                                 doc=doc))

    @write_tool(mcp)
    def create_beams(type: str, lines: list[LineSpec], level: str,
                     z_justification: Optional[Literal["top", "center", "origin", "bottom"]] = None,
                     dry_run: bool = False, dismiss_warnings: bool = False,
                     doc: Optional[str] = None) -> dict:
        """Structural framing beams along lines (feet) with a reference level.
        type: a Structural Framing 'Family: Type' (e.g. 'W Shapes: W10X22', 'Dimension Lumber: 2x12')."""
        return _w("create_beams", dict(type=type, lines=lines, level=level,
                                       z_justification=z_justification, dry_run=dry_run,
                                       dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_columns(type: str, points: list[Point], base_level: str,
                       top_level: Optional[str] = None, base_offset: Optional[Length] = None,
                       top_offset: Optional[Length] = None, dry_run: bool = False,
                       dismiss_warnings: bool = False, doc: Optional[str] = None) -> dict:
        """Vertical columns (structural if the family is a Structural Column) at points,
        from base_level to top_level."""
        return _w("create_columns", dict(type=type, points=points, base_level=base_level,
                                         top_level=top_level, base_offset=base_offset,
                                         top_offset=top_offset, dry_run=dry_run,
                                         dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_braces(type: str, lines: list[LineSpec], level: str, dry_run: bool = False,
                      dismiss_warnings: bool = False, doc: Optional[str] = None) -> dict:
        """Structural braces along 3D lines (feet) with a reference level."""
        return _w("create_braces", dict(type=type, lines=lines, level=level, dry_run=dry_run,
                                        dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def create_view(
        kind: Literal["plan", "ceiling", "structural_plan", "section", "elevation", "3d", "drafting"],
        name: Optional[str] = None,
        level: Optional[str] = None,
        box: Optional[BBox] = None,
        direction: Optional[Point] = None,
        point: Optional[Point] = None,
        plan_view: Optional[str] = None,
        index: int = 1,
        scale: Optional[int] = None,
        template: Optional[str] = None,
        view_type: Optional[str] = None,
        dry_run: bool = False,
        doc: Optional[str] = None,
    ) -> dict:
        """Create a view. plan/ceiling/structural_plan need `level`; section needs `box`
        (model-space min/max, feet) and optional `direction` ([x,y] look direction, default +Y);
        elevation needs `point`, `plan_view` and `index` (0-3). scale = 1:N (96 = 1/8"=1')."""
        return _w("create_view", dict(kind=kind, name=name, level=level, box=box, direction=direction,
                                      point=point, plan_view=plan_view, index=index, scale=scale,
                                      template=template, view_type=view_type, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def duplicate_view(view: str, option: Literal["duplicate", "with_detailing", "as_dependent"] = "duplicate",
                       name: Optional[str] = None, dry_run: bool = False,
                       doc: Optional[str] = None) -> dict:
        """Duplicate a view (name or id)."""
        return _w("duplicate_view", dict(view=view, option=option, name=name, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_sheet(number: Optional[str] = None, name: Optional[str] = None,
                     titleblock: Optional[str] = None, dry_run: bool = False,
                     doc: Optional[str] = None) -> dict:
        """Create a sheet with a title block ('Family: Type'; default the first loaded one)."""
        return _w("create_sheet", dict(number=number, name=name, titleblock=titleblock,
                                       dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def place_viewport(sheet: str, view: str, point: Optional[Point] = None, dry_run: bool = False,
                       doc: Optional[str] = None) -> dict:
        """Place a view or schedule on a sheet. point = sheet coordinates in feet (default centre)."""
        return _w("place_viewport", dict(sheet=sheet, view=view, point=point, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_schedule(category: str, fields: list[str], name: Optional[str] = None,
                        filters: Optional[list[ScheduleFilter]] = None,
                        sorting: Optional[list[ScheduleSort]] = None, itemize: bool = True,
                        dry_run: bool = False, doc: Optional[str] = None) -> dict:
        """Create a schedule for a category with fields (as named in Revit), filters and sorting."""
        return _w("create_schedule", dict(category=category, fields=fields, name=name, filters=filters,
                                          sorting=sorting, itemize=itemize, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_text_note(text: str, point: Point, view: Optional[str] = None,
                         type: Optional[str] = None, width: Optional[Length] = None,
                         dry_run: bool = False, doc: Optional[str] = None) -> dict:
        """Place a text note in a view (default: active view). point in model feet."""
        return _w("create_text_note", dict(text=text, point=point, view=view, type=type, width=width,
                                           dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_tag(ids: list[int], view: str, leader: bool = False,
                   orientation: Literal["horizontal", "vertical"] = "horizontal",
                   offset: Optional[Point] = None, tag_type: Optional[str] = None,
                   dry_run: bool = False, doc: Optional[str] = None) -> dict:
        """Tag elements in a view (by category, or with a specific tag family type)."""
        return _w("create_tag", dict(ids=ids, view=view, leader=leader, orientation=orientation,
                                     offset=offset, tag_type=tag_type, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_dimension(ids: list[int], view: str, line: LineSpec, dry_run: bool = False,
                         doc: Optional[str] = None) -> dict:
        """Best-effort linear dimension between grids, levels, reference planes, lines or walls
        (walls use their exterior face). `line` is where the dimension line runs (feet)."""
        return _w("create_dimension", dict(ids=ids, view=view, line=line, dry_run=dry_run, doc=doc))
