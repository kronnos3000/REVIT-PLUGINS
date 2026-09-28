"""Structural / analytical tools for the Revit <-> Robot round trip.

Load values: numbers are US units — force lbf, line load plf, area load psf, moment lbf-ft
(negative z = gravity). Strings may carry units: "2.5 kip", "10 kN", "0.3 klf", "3 kN/m",
"40 psf", "2 kPa", "5 kip-ft".
"""

from __future__ import annotations

from typing import Literal, Optional, Union

from pydantic import BaseModel

from ..models import LineSpec, Point
from ._common import call, read_tool, write_tool

LoadValue = Union[float, str]
Vector = list[LoadValue]


class ReleaseSpec(BaseModel):
    """User-defined release: True = released (free) in that direction."""

    fx: bool = False
    fy: bool = False
    fz: bool = False
    mx: bool = False
    my: bool = False
    mz: bool = False


Release = Union[Literal["fixed", "pinned", "bending_moment"], ReleaseSpec]


def _w(method: str, params: dict) -> dict:
    return call(method, params, write=True)


def register(mcp) -> None:
    @read_tool(mcp)
    def list_analytical_members(ids: Optional[list[int]] = None, role: Optional[str] = None,
                                max_results: int = 2000, doc: Optional[str] = None) -> dict:
        """Analytical members (Revit 2023+): curve (feet), section type, shape, material,
        start/end releases, the associated physical element, and its CC_Robot_Id if bound.
        ids may be analytical or physical element ids. role: e.g. 'Beam', 'Column', 'Member'."""
        return call("list_analytical_members", {"ids": ids, "role": role,
                                                "max_results": max_results, "doc": doc})

    @write_tool(mcp)
    def create_analytical_members(ids: list[int], dry_run: bool = False,
                                  doc: Optional[str] = None) -> dict:
        """Create analytical members for physical beams/columns/braces that have none (Revit
        2023+ doesn't create them automatically): curve, section, material, role and the
        analytical<->physical association. Already-associated elements are skipped."""
        return _w("create_analytical_members", dict(ids=ids, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def set_analytical_releases(ids: list[int], start: Optional[Release] = None,
                                end: Optional[Release] = None, dry_run: bool = False,
                                doc: Optional[str] = None) -> dict:
        """Set member end releases: 'fixed' | 'pinned' | 'bending_moment' | {fx..mz: released}.
        ids may be analytical member ids or their physical framing/column ids."""
        return _w("set_analytical_releases", dict(ids=ids, start=start, end=end, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_point_load(force: Vector, host: int, point: Optional[Point] = None,
                          moment: Optional[Vector] = None, at: Optional[Literal["start", "end"]] = None,
                          load_case: Optional[str] = None, dry_run: bool = False,
                          doc: Optional[str] = None) -> dict:
        """Hosted point load (Revit 2024+ API has no free-standing point loads). host = analytical
        member or its physical beam/column id; place it with at='start'|'end' or a `point` (feet)
        on the member. force [fx,fy,fz] lbf (or unit strings), moment lbf-ft."""
        return _w("create_point_load", dict(force=force, point=point, moment=moment, host=host, at=at,
                                            load_case=load_case, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_line_load(force: Vector, line: Optional[LineSpec] = None, moment: Optional[Vector] = None,
                         host: Optional[int] = None, load_case: Optional[str] = None,
                         dry_run: bool = False, doc: Optional[str] = None) -> dict:
        """Uniform line load, force [fx,fy,fz] plf. Pass `line` (feet) and/or `host`
        (analytical member / physical beam id → full length)."""
        return _w("create_line_load", dict(force=force, line=line, moment=moment, host=host,
                                           load_case=load_case, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def create_area_load(force: Vector, host: int, boundary: Optional[list[Point]] = None,
                         load_case: Optional[str] = None, dry_run: bool = False,
                         doc: Optional[str] = None) -> dict:
        """Hosted uniform area load (Revit 2024+ API has no free-standing area loads), force
        [fx,fy,fz] psf. host = analytical panel (or a floor/wall that has one); optional closed
        `boundary` (feet) on the panel for a partial load."""
        return _w("create_area_load", dict(force=force, boundary=boundary, host=host,
                                           load_case=load_case, dry_run=dry_run, doc=doc))

    @read_tool(mcp)
    def list_load_cases(doc: Optional[str] = None) -> dict:
        """Load cases with number, nature, category and how many loads each carries."""
        return call("list_load_cases", {"doc": doc})

    @write_tool(mcp)
    def create_load_case(name: str,
                         category: Literal["dead", "live", "roof_live", "snow", "wind", "seismic",
                                           "temperature", "accidental"] = "dead",
                         nature: Optional[str] = None, dry_run: bool = False,
                         doc: Optional[str] = None) -> dict:
        """Create a load case; the load nature is reused by name or created."""
        return _w("create_load_case", dict(name=name, category=category, nature=nature,
                                           dry_run=dry_run, doc=doc))

    @read_tool(mcp)
    def get_structural_usage(ids: list[int], doc: Optional[str] = None) -> dict:
        """Structural type and usage (Girder, Joist, Purlin, ...) of framing instances."""
        return call("get_structural_usage", {"ids": ids, "doc": doc})

    @write_tool(mcp)
    def set_structural_usage(ids: list[int], usage: str, dry_run: bool = False,
                             doc: Optional[str] = None) -> dict:
        """Set structural usage on framing: Girder, Joist, Purlin, HorizontalBracing,
        KickerBracing, Other, Automatic."""
        return _w("set_structural_usage", dict(ids=ids, usage=usage, dry_run=dry_run, doc=doc))
