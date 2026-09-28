"""execute_csharp — full Revit API access when no typed tool fits."""

from __future__ import annotations

from typing import Literal, Optional

from ._common import call


def register(mcp) -> None:
    @mcp.tool()
    def execute_csharp(
        code: str,
        mode: Literal["read", "write"] = "read",
        usings: Optional[list[str]] = None,
        timeout_s: float = 60,
        dry_run: bool = False,
        dismiss_warnings: bool = False,
        confirm: bool = False,
        doc: Optional[str] = None,
    ) -> dict:
        """Run C# against the live Revit API (escape hatch — prefer typed tools; if you need the
        same script twice, say so so it can become a typed tool).

        `code` is the BODY of `public static object Run(ScriptContext ctx) { ... }`; `return` any
        value and it is serialized to JSON (ElementId -> long, XYZ -> [x,y,z] feet, Element ->
        {id,name,category}). Pre-imported: System, System.Linq, System.Collections.Generic,
        Autodesk.Revit.DB (+ .Structure, .Architecture), Autodesk.Revit.UI, Newtonsoft.Json.Linq.

        ctx: Doc, UIDoc (null if doc not active), UIApp, App, Log(string), ToFeet(value, unit),
        ToFeet("12'-6\\""), FormatLength(feet), Get(id), Collect(BuiltInCategory),
        Result.Set(key, value), Result.Elements(els), Result.ElementsWithParams(els), Result.Table(cols, rows).

        mode="read": no transaction; any model change throws. mode="write": runs inside an open
        Transaction within the TransactionGroup "Claude: execute_csharp" — ONE Ctrl+Z; do not start
        your own Transaction (use SubTransaction if you need stages); dry_run=True rolls it back.
        Revit internal units: feet, radians. Compile errors return line/column relative to your
        code. timeout_s cannot interrupt a script that is already running in Revit.

        Example: `return new FilteredElementCollector(ctx.Doc).OfClass(typeof(Wall)).GetElementCount();`
        """
        return call("execute_csharp", dict(code=code, mode=mode, usings=usings, dry_run=dry_run,
                                           dismiss_warnings=dismiss_warnings, confirm=confirm, doc=doc),
                    write=(mode == "write"), timeout_s=timeout_s)
