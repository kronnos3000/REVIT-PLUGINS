"""Live smoke test for RevitBridge (needs a running Revit with the add-in).

    status -> list_documents -> query -> dry-run create -> real create (scratch doc)
    -> verify -> delete with confirm  [-> one-Ctrl+Z check]  [-> execute_csharp]

Safety: it NEVER writes to a document it did not create. It makes a new scratch project
from a template (default: the firm's TEMPLATE CC PABLO.rte, else Revit's default template),
saved under %TEMP%, and checks every write goes to that doc.

Usage (from RevitBridge/mcp):
    $env:REVIT_BRIDGE_PID = "<pid of the Revit to test>"     # recommended
    .\\.venv\\Scripts\\python.exe tools\\smoke.py [--template PATH] [--undo-check] [--keep-open]

--undo-check sends ONE real Ctrl+Z keystroke to that Revit window and verifies the last
bridge call is undone as a single step (it briefly takes keyboard focus).
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from revit_bridge_mcp.pipe_client import BridgeError, client  # noqa: E402

FIRM_TEMPLATE = (r"G:\Shared drives\4-10A   Design & Planning\AXS_ARCHITECTURAL (RESOURCES)"
                 r"\00_Revit Template_25\TEMPLATE CC PABLO.rte")

results: list[tuple[str, bool, str]] = []


def step(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
    return ok


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--template", default=None)
    ap.add_argument("--undo-check", action="store_true")
    ap.add_argument("--keep-open", action="store_true", help="leave the scratch doc open")
    ns = ap.parse_args()
    c = client()

    def call(method, write=False, timeout_s=None, **params):
        return c.call(method, params, write=write, timeout_s=timeout_s)

    # 1. status
    try:
        st = call("bridge_status")
    except BridgeError as exc:
        step("bridge_status", False, str(exc))
        return 1
    s = st["result"]
    step("bridge_status", st["ok"], f"pid {s['pid']}, Revit {s['revit_year']} ({s['version_build']}), "
                                    f"bridge {s['bridge_version']}, {s['commands']} commands")
    step("list_documents", call("list_documents")["ok"])

    # 2. scratch doc
    template = ns.template or (FIRM_TEMPLATE if os.path.exists(FIRM_TEMPLATE) else None)
    nd = call("new_document", template=template, timeout_s=300)
    if not step("new_document (scratch)", nd["ok"], (nd.get("error") or {}).get("message", "")):
        return 1
    scratch = nd["result"]["doc"]
    if not (scratch["path"] and "scratch_" in scratch["path"]):
        step("scratch doc guard", False, f"unexpected doc {scratch}")
        return 1
    print(f"       scratch: {scratch['path']}")

    def w(method, **params):
        env = call(method, write=True, doc=scratch["path"], **params)
        if env["doc"] and env["doc"]["path"] != scratch["path"]:
            raise SystemExit(f"SAFETY: write went to {env['doc']} instead of the scratch doc")
        return env

    level = call("list_levels", doc=scratch["path"])["result"]
    lvl = next((l["name"] for l in level if abs(l["elevation"]) < 1e-6), level[0]["name"])

    # 3. query
    q0 = call("query_elements", category="Walls", max_results=0, doc=scratch["path"])
    step("query_elements", q0["ok"], f"{q0['result']['total']} walls")
    base = q0["result"]["total"]
    pts = [[0, 0], [20, 0], [20, 15], [0, 15]]

    # 4. dry run
    dr = w("create_walls", level=lvl, points=pts, closed=True, height=9, dry_run=True)
    q1 = call("query_elements", category="Walls", max_results=0, doc=scratch["path"])["result"]["total"]
    step("dry_run create_walls", dr["ok"] and dr.get("dry_run") and len(dr["changed"]["created"]) == 4
         and q1 == base, f"changed.created={len(dr['changed']['created'])}, walls {base}->{q1}")

    # 5. real create + verify
    cr = w("create_walls", level=lvl, points=pts, closed=True, height="9'-0\"")
    ids = cr["result"]["ids"] if cr["ok"] else []
    step("create_walls", cr["ok"] and len(ids) == 4, f"tx '{cr.get('tx_name')}', ids {ids}")
    ge = call("get_elements", ids=ids, fields=["Unconnected Height"], geometry="location",
              doc=scratch["path"])
    heights = {round(e["parameters"]["Unconnected Height"]["value"], 4) for e in ge["result"]["elements"]}
    step("verify (get_elements)", ge["ok"] and heights == {9.0}, f"heights {heights}")
    sp = w("set_parameters", updates=[{"id": ids[0], "name": "Comments", "value": "smoke"}])
    step("set_parameters", sp["ok"])

    # 6. delete with confirm
    pv = w("delete_elements", ids=ids)
    step("delete preview (no confirm)", (not pv["ok"]) and pv["error"]["code"] == "CONFIRM_REQUIRED",
         f"preview count {pv['result'] and pv['result'].get('count')}")
    dl = w("delete_elements", ids=ids, confirm=True)
    q2 = call("query_elements", category="Walls", max_results=0, doc=scratch["path"])["result"]["total"]
    step("delete_elements confirm", dl["ok"] and q2 == base, f"walls back to {q2}")

    # 7. execute_csharp
    ex = call("execute_csharp", code="return new FilteredElementCollector(ctx.Doc).OfClass(typeof(Level)).GetElementCount();",
              doc=scratch["path"])
    step("execute_csharp (read)", ex["ok"] and ex["result"]["value"] == len(level), f"levels {ex['result'] and ex['result']['value']}")

    # 8. one call == one Ctrl+Z
    if ns.undo_check:
        import win32com.client  # type: ignore

        cr = w("create_walls", level=lvl, points=pts, closed=True, height=8)
        mid = call("query_elements", category="Walls", max_results=0, doc=scratch["path"])["result"]["total"]
        sh = win32com.client.Dispatch("WScript.Shell")
        sh.AppActivate(int(s["pid"]))
        time.sleep(0.7); sh.SendKeys("{ESC}"); time.sleep(0.2); sh.SendKeys("^z"); time.sleep(2.0)
        after = call("query_elements", category="Walls", max_results=0, doc=scratch["path"])["result"]["total"]
        step("one Ctrl+Z undoes one call", mid == base + 4 and after == base, f"walls {base}->{mid}->{after}")

    if not ns.keep_open:
        call("save_document", write=True, doc=scratch["path"])

    failed = [r for r in results if not r[1]]
    print(f"\n{len(results) - len(failed)}/{len(results)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
