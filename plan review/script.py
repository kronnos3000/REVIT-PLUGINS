# -*- coding: utf-8 -*-
"""Apply CD100 review-comment text changes from a JSON changeset.

pyRevit pushbutton script (IronPython 2.7). Drop into:
  ccorp.extension/ccorp.tab/QA.panel/ApplyChangeset.pushbutton/script.py
with marina_cd100_changeset.json alongside it (or browse when prompted).

What it touches, in order:
  1. TextNote elements            -> TextNote.Text
  2. Generic Annotation instances -> writable string parameters
  3. Detail Item / Tag instances  -> writable string parameters
  4. ViewSchedule-driving params  -> string params on scheduled elements
Scoped per-sheet via the changeset "scope" list ("ALL" = whole model).

DRY_RUN=True prints the audit without opening a transaction. Read it,
resolve every MISSED entry (usually a line-break or unicode mismatch),
flip DRY_RUN, run again.
"""

import json
import os

from pyrevit import revit, DB, forms, script

output = script.get_output()
doc = revit.doc

DRY_RUN = True          # <-- flip to False for the live run
CASE_SENSITIVE = True

# ---------------------------------------------------------------- changeset
cs_path = os.path.join(os.path.dirname(__file__), "marina_cd100_changeset.json")
if not os.path.exists(cs_path):
    cs_path = forms.pick_file(file_ext="json", title="Pick changeset JSON")
    if not cs_path:
        script.exit()

with open(cs_path, "r") as f:
    changeset = json.load(f)

replacements = changeset.get("replacements", [])
new_notes = changeset.get("new_notes", [])

# Normalize: JSON \t and \n are literal here; drawings may use \r\n.
def variants(s):
    """Yield tolerant variants of the find-string (line-ending & tab/space)."""
    yield s
    yield s.replace("\n", "\r\n")
    yield s.replace("\t", " ")
    yield s.replace("\t", "  ")
    yield s.replace("\n", " ")

# ---------------------------------------------------------------- sheet scope
def build_sheet_view_index():
    """Map sheet number -> set of view ElementIds placed on it (incl. the sheet itself)."""
    idx = {}
    for sheet in DB.FilteredElementCollector(doc).OfClass(DB.ViewSheet):
        ids = set([sheet.Id])
        for vp_id in sheet.GetAllPlacedViews():
            ids.add(vp_id)
        idx[sheet.SheetNumber] = ids
    return idx

SHEET_INDEX = build_sheet_view_index()

def in_scope(elem, scope):
    if "ALL" in scope:
        return True
    owner = elem.OwnerViewId
    for sheet_num in scope:
        ids = SHEET_INDEX.get(sheet_num)
        if ids and owner in ids:
            return True
    return False

# ---------------------------------------------------------------- collectors
text_notes = list(DB.FilteredElementCollector(doc)
                  .OfClass(DB.TextNote)
                  .WhereElementIsNotElementType())

anno_instances = list(DB.FilteredElementCollector(doc)
                      .OfCategory(DB.BuiltInCategory.OST_GenericAnnotation)
                      .WhereElementIsNotElementType())

detail_items = list(DB.FilteredElementCollector(doc)
                    .OfCategory(DB.BuiltInCategory.OST_DetailComponents)
                    .WhereElementIsNotElementType())

def writable_string_params(elem):
    for p in elem.Parameters:
        if (p.StorageType == DB.StorageType.String
                and not p.IsReadOnly
                and p.AsString()):
            yield p

# ---------------------------------------------------------------- engine
audit = []   # (comment, target, status, detail)

def try_replace_textnote(tn, entry):
    hit = False
    txt = tn.Text
    for find_v in variants(entry["find"]):
        if find_v in txt:
            if not DRY_RUN:
                tn.Text = txt.replace(find_v, entry["replace"])
            audit.append((entry["comment"], "TextNote %s" % tn.Id,
                          "APPLIED" if not DRY_RUN else "WOULD APPLY",
                          find_v[:60]))
            hit = True
            break
    return hit

def try_replace_params(elem, entry, label):
    hit = False
    for p in writable_string_params(elem):
        val = p.AsString()
        for find_v in variants(entry["find"]):
            if find_v in val:
                if not DRY_RUN:
                    p.Set(val.replace(find_v, entry["replace"]))
                audit.append((entry["comment"], "%s %s :: %s" % (label, elem.Id, p.Definition.Name),
                              "APPLIED" if not DRY_RUN else "WOULD APPLY",
                              find_v[:60]))
                hit = True
                break
    return hit

def run_pass():
    for entry in replacements:
        scope = entry.get("scope", ["ALL"])
        found_any = False
        for tn in text_notes:
            if in_scope(tn, scope) and try_replace_textnote(tn, entry):
                found_any = True
        for fi in anno_instances:
            if in_scope(fi, scope) and try_replace_params(fi, entry, "GenericAnno"):
                found_any = True
        for di in detail_items:
            if in_scope(di, scope) and try_replace_params(di, entry, "DetailItem"):
                found_any = True
        if not found_any:
            audit.append((entry["comment"], "-", "MISSED",
                          "no element contained: %s" % entry["find"][:70]))

def place_new_notes():
    """Append changeset new_notes as TextNotes; user picks the point per note."""
    from Autodesk.Revit.UI.Selection import ObjectSnapTypes  # noqa
    default_type_id = doc.GetDefaultElementTypeId(DB.ElementTypeGroup.TextNoteType)
    for nn in new_notes:
        sheet_num = nn["scope"]
        target_view = None
        for sheet in DB.FilteredElementCollector(doc).OfClass(DB.ViewSheet):
            if sheet.SheetNumber == sheet_num:
                # place on the first drafting/plan view on that sheet
                for vid in sheet.GetAllPlacedViews():
                    target_view = doc.GetElement(vid)
                    break
                break
        if target_view is None:
            audit.append((nn["comment"], "-", "MISSED",
                          "sheet %s not found for new note" % sheet_num))
            continue
        if DRY_RUN:
            audit.append((nn["comment"], "View %s" % target_view.Name,
                          "WOULD PLACE NOTE", nn["text"][:60]))
            continue
        forms.alert("Pick point in view '%s' for note:\n\n%s"
                    % (target_view.Name, nn["text"][:120]),
                    title="Place note - %s" % nn["comment"])
        revit.uidoc.ActiveView = target_view
        pt = revit.uidoc.Selection.PickPoint("Pick insertion point")
        DB.TextNote.Create(doc, target_view.Id, pt, nn["text"], default_type_id)
        audit.append((nn["comment"], "View %s" % target_view.Name,
                      "PLACED", nn["text"][:60]))

# ---------------------------------------------------------------- execute
if DRY_RUN:
    run_pass()
    place_new_notes()
else:
    with revit.Transaction("CD100 changeset - review comment text edits"):
        run_pass()
        place_new_notes()

# ---------------------------------------------------------------- report
output.print_md("## Changeset audit --- %s" % ("DRY RUN" if DRY_RUN else "LIVE RUN"))
missed = [a for a in audit if a[2] == "MISSED"]
applied = [a for a in audit if a[2] != "MISSED"]

output.print_md("**%d applied / %d missed**" % (len(applied), len(missed)))
output.print_table(
    table_data=[[a[0], a[1], a[2], a[3]] for a in audit],
    columns=["Comment", "Target", "Status", "Match"],
    title="Detail")

if missed:
    output.print_md(
        "> **MISSED entries** usually mean the drawing text has a different "
        "line break, an en-dash vs hyphen, or lives in a family type "
        "parameter (edit the family) or a real ViewSchedule (edit the "
        "source element parameters --- see schedule pass note in header). "
        "Copy the exact string out of Revit into the changeset and re-run.")