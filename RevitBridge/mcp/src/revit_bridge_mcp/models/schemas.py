"""Pydantic models for structured tool inputs.

FastMCP derives a tool's JSON schema from its type hints, so using these models
as argument types gives Claude clear, validated structures to work with.

Units: Revit internal units. A ``Length`` is a number in FEET or a string with units
("12'-6\\"", "150 in", "450 mm", "3.2 m"). A ``Point`` is [x, y] or [x, y, z] of Lengths.
"""

from __future__ import annotations

from typing import Any, Literal, Optional, Union

from pydantic import BaseModel, Field

Length = Union[float, str]
Point = list[Length]
IdOrName = Union[int, str]


class LineSpec(BaseModel):
    """A straight segment between two points (feet)."""

    start: Point = Field(description="[x, y, z] start point, feet or unit strings.")
    end: Point = Field(description="[x, y, z] end point, feet or unit strings.")


class BBox(BaseModel):
    min: Point = Field(description="[x, y, z] minimum corner (feet).")
    max: Point = Field(description="[x, y, z] maximum corner (feet).")


class ParamFilter(BaseModel):
    """In-memory parameter filter applied after the Revit collector filters."""

    param: str = Field(description="Parameter name, BuiltInParameter (e.g. 'ALL_MODEL_MARK') or shared-param GUID. "
                                   "Type parameters are checked if the instance has no such parameter.")
    op: Literal["eq", "ne", "gt", "ge", "lt", "le", "contains", "startswith", "has_value", "empty"] = "eq"
    value: Optional[Any] = Field(default=None, description="Numbers compare in internal units (feet); strings compare "
                                                           "against the display value, case-insensitive.")


class ParamSet(BaseModel):
    """One parameter write for set_parameters."""

    id: int = Field(description="Element id.")
    name: Optional[str] = Field(default=None, description="Parameter name as shown in Properties.")
    builtin: Optional[str] = Field(default=None, description="BuiltInParameter name, e.g. 'ALL_MODEL_MARK'.")
    guid: Optional[str] = Field(default=None, description="Shared parameter GUID.")
    value: Any = Field(description="New value. Lengths: feet or unit strings. Yes/No: true/false. "
                                   "Element-id params: an id or a name (types/levels). Text: string.")


class TypeParamSet(BaseModel):
    """One type-parameter write for set_type_parameters."""

    type: IdOrName = Field(description="Type id or 'Family: Type' name.")
    name: Optional[str] = None
    builtin: Optional[str] = None
    guid: Optional[str] = None
    value: Any


class PlacementSpec(BaseModel):
    """A single family-instance placement."""

    point: Point = Field(description="Insertion point [x, y, z] (feet).")
    rotation: Optional[Union[float, str]] = Field(default=None, description="Rotation about Z, degrees (or '1.2 rad').")
    host: Optional[int] = Field(default=None, description="Host element id (e.g. a wall for doors/windows).")


class ScheduleFilter(BaseModel):
    field: str
    op: Literal["eq", "ne", "gt", "ge", "lt", "le", "contains", "not_contains", "begins_with", "has_value", "no_value"] = "eq"
    value: Optional[Any] = None


class ScheduleSort(BaseModel):
    field: str
    descending: bool = False
