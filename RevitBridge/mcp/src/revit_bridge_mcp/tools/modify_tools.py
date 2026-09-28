"""Modify tools: parameters, types, transforms, delete, joins, worksets, phases, families,
shared parameters."""

from __future__ import annotations

from typing import Any, Literal, Optional, Union

from ..models import IdOrName, LineSpec, ParamSet, Point, TypeParamSet
from ._common import call, write_tool


def _w(method: str, params: dict, timeout_s: float | None = None) -> dict:
    return call(method, params, write=True, timeout_s=timeout_s)


def register(mcp) -> None:
    @write_tool(mcp)
    def set_parameters(updates: list[ParamSet], stop_on_error: bool = True, dry_run: bool = False,
                       dismiss_warnings: bool = False, confirm: bool = False,
                       doc: Optional[str] = None) -> dict:
        """Batch-set instance parameters: [{id, name|builtin|guid, value}]. Unit-aware: lengths
        accept feet or "12'-6\\"" / "150 mm"; other units via Revit display strings ("45°").
        stop_on_error=False applies what it can and reports failures per row."""
        return _w("set_parameters", dict(updates=updates, stop_on_error=stop_on_error, dry_run=dry_run,
                                         dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def set_type_parameters(updates: list[TypeParamSet], dry_run: bool = False,
                            dismiss_warnings: bool = False, doc: Optional[str] = None) -> dict:
        """Set type parameters: [{type ('Family: Type' or id), name|builtin|guid, value}].
        Affects every instance of the type."""
        return _w("set_type_parameters", dict(updates=updates, dry_run=dry_run,
                                              dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def change_type(ids: list[int], type: IdOrName, dry_run: bool = False,
                    dismiss_warnings: bool = False, confirm: bool = False,
                    doc: Optional[str] = None) -> dict:
        """Change elements to another type of the same category ('Family: Type' or id)."""
        return _w("change_type", dict(ids=ids, type=type, dry_run=dry_run,
                                      dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def duplicate_type(type: IdOrName, new_name: str, params: Optional[dict[str, Any]] = None,
                       reuse_existing: bool = False, dry_run: bool = False,
                       doc: Optional[str] = None) -> dict:
        """Duplicate a type under a new name and set its parameters, e.g. create 'W10X22' from
        'W Shapes: W10X12' with params {"d": "10.2 in"}. reuse_existing=True updates an
        existing type of that name instead of failing."""
        return _w("duplicate_type", dict(type=type, new_name=new_name, params=params,
                                         reuse_existing=reuse_existing, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def move(ids: list[int], vector: Point, dry_run: bool = False, dismiss_warnings: bool = False,
             confirm: bool = False, doc: Optional[str] = None) -> dict:
        """Move elements by a vector [dx, dy, dz] (feet)."""
        return _w("move", dict(ids=ids, vector=vector, dry_run=dry_run,
                               dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def rotate(ids: list[int], angle: Union[float, str], point: Optional[Point] = None,
               dry_run: bool = False, dismiss_warnings: bool = False, confirm: bool = False,
               doc: Optional[str] = None) -> dict:
        """Rotate elements about a vertical axis through `point` (default: their bbox centre).
        angle in degrees (counter-clockwise), or '1.2 rad'."""
        return _w("rotate", dict(ids=ids, angle=angle, point=point, dry_run=dry_run,
                                 dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def copy(ids: list[int], vector: Point, dry_run: bool = False, dismiss_warnings: bool = False,
             confirm: bool = False, doc: Optional[str] = None) -> dict:
        """Copy elements by a vector (feet). Returns the new ids."""
        return _w("copy", dict(ids=ids, vector=vector, dry_run=dry_run,
                               dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def mirror(ids: list[int], axis: LineSpec, copy: bool = True, dry_run: bool = False,
               dismiss_warnings: bool = False, confirm: bool = False,
               doc: Optional[str] = None) -> dict:
        """Mirror elements about the vertical plane through `axis` (a line in plan).
        copy=False mirrors in place."""
        return _w("mirror", dict(ids=ids, axis=axis, copy=copy, dry_run=dry_run,
                                 dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def array(ids: list[int], count: int, spacing: Point, dry_run: bool = False,
              dismiss_warnings: bool = False, confirm: bool = False,
              doc: Optional[str] = None) -> dict:
        """Linear array as independent copies (no array group): `count` total including the
        original, each offset by `spacing` [dx, dy, dz] feet from the previous."""
        return _w("array", dict(ids=ids, count=count, spacing=spacing, dry_run=dry_run,
                                dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc))

    @write_tool(mcp)
    def delete_elements(ids: list[int], confirm: bool = False, dry_run: bool = False,
                        doc: Optional[str] = None) -> dict:
        """Delete elements. Without confirm=True returns a PREVIEW (counts by category, a
        sample of 20, and dependent elements that would also be deleted) and changes nothing.
        Only pass confirm=True when the user explicitly asked for the deletion."""
        return _w("delete_elements", dict(ids=ids, confirm=confirm, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def join_geometry(ids: Optional[list[int]] = None, pairs: Optional[list[list[int]]] = None,
                      mode: Literal["join", "unjoin", "switch"] = "join", dry_run: bool = False,
                      dismiss_warnings: bool = False, doc: Optional[str] = None) -> dict:
        """Join/unjoin/switch join order. pairs=[[a,b],...] or ids (first joined with each other)."""
        return _w("join_geometry", dict(ids=ids, pairs=pairs, mode=mode, dry_run=dry_run,
                                        dismiss_warnings=dismiss_warnings, doc=doc))

    @write_tool(mcp)
    def set_workset(ids: list[int], workset: str, dry_run: bool = False,
                    doc: Optional[str] = None) -> dict:
        """Move elements to a user workset (name or id). Workshared models only."""
        return _w("set_workset", dict(ids=ids, workset=workset, dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def set_phase(ids: list[int], created: Optional[str] = None, demolished: Optional[str] = None,
                  dry_run: bool = False, doc: Optional[str] = None) -> dict:
        """Set Phase Created and/or Phase Demolished (name or id; demolished='none' clears)."""
        return _w("set_phase", dict(ids=ids, created=created, demolished=demolished,
                                    dry_run=dry_run, doc=doc))

    @write_tool(mcp)
    def load_family(path: str, overwrite: bool = False, dry_run: bool = False,
                    doc: Optional[str] = None) -> dict:
        """Load an .rfa family. overwrite=True reloads an already-loaded family (and its
        parameter values)."""
        return _w("load_family", dict(path=path, overwrite=overwrite, dry_run=dry_run, doc=doc),
                  timeout_s=180)

    @write_tool(mcp)
    def bind_shared_parameter(
        name: str,
        categories: list[str],
        binding: Literal["instance", "type"] = "instance",
        group: str = "Analysis",
        spec: Literal["text", "number", "integer", "length", "area", "volume", "angle", "yesno",
                      "url", "force", "moment"] = "text",
        create_if_missing: bool = False,
        properties_group: str = "Data",
        description: Optional[str] = None,
        file: Optional[str] = None,
        dry_run: bool = False,
        doc: Optional[str] = None,
    ) -> dict:
        """Bind a shared parameter from the firm file (CC_SharedParameters.txt on G:) to categories.
        If the definition is missing it is added to the shared file ONLY when
        create_if_missing=True (group/spec/description are used then). Spec ids are correct:
        number=SpecTypeId.Number, integer=SpecTypeId.Int.Integer, text=SpecTypeId.String.Text.
        Example: CC_Robot_Id (text, instance, group Analysis) on Structural Framing/Columns."""
        return _w("bind_shared_parameter", dict(name=name, categories=categories, binding=binding,
                                                group=group, spec=spec,
                                                create_if_missing=create_if_missing,
                                                properties_group=properties_group,
                                                description=description, file=file,
                                                dry_run=dry_run, doc=doc))
