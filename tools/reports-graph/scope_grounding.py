"""Deterministic folder scope for «مجلد X» in a question.

The planner model sometimes plans «في مجلد الوارد» as a template or a name filter, or invents a
template. An explicit «مجلد X» has one meaning: every folder named X (the backend resolves the
name with Arabic spelling normalization), its direct contents unless the question adds «وما تحته».
"""
import re

from calendar_grounding import normalize_arabic

_SUBFOLDERS = (r"\s+(?:و\s*|مع\s+|بما\s+فيه(?:ا)?\s+)?(?:كل\s+|جميع\s+)?"
               r"(?:(?:ما|اللي)\s+(?:تحته|تحتها|بداخله|داخله|يندرج(?:\s+تحته)?(?:\s+المجلد)?)"
               r"|(?:المجلدات|مجلداته)(?:\s+(?:اللي|التي))?\s+(?:الفرعيه|تحته|بداخله|داخله))(?!\w)")
# The folder name ends before a relative clause, a qualifier or punctuation.
_NAME_END = (r"(?=" + _SUBFOLDERS + r"|\s+(?:التي|اللتي|الذي|اللذي|اللي|فقط|خلال|المنشاه|المنشئه|المعدله|"
             r"والتي|واللتي|حيث|بتاريخ|منذ|قبل|بعد|حتي)(?!\w)|\s*[؟?.,،:;!]|$)")
_FOLDER = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?مجلد\s+(?:اسمه\s+|باسم\s+)?(?P<name>[^؟?.,،:;!]+?)" + _NAME_END)


def folder_scope(question):
    """(folder name, include subfolders) for exactly one «مجلد X» in the question, otherwise None."""
    text = normalize_arabic(question)
    matches = list(_FOLDER.finditer(text))
    if len(matches) != 1:
        return None
    name = matches[0].group("name").strip()
    if (not name or len(name) > 120 or re.search(r"(?<!\w)[وفبل]{0,2}(?:ال)?مجلد(?!\w)", name) or
            re.fullmatch(r"(?:رقم\s*)?\d+", name)):
        return None  # Two folders, or a folder ID («المجلد رقم 42»), stay with the planner.
    return _original_spelling(question, name), bool(re.match(_SUBFOLDERS, text[matches[0].end():]))


_VARIANTS = {"ا": "[اأإآ]", "ه": "[هة]", "ي": "[يى]"}


def _original_spelling(question, normalized):
    """The folder name as the user wrote it (e.g. with ة), for the report heading."""
    pattern = r"\s+".join("".join(_VARIANTS.get(c, re.escape(c)) for c in word) for word in normalized.split())
    match = re.search(pattern, question, re.IGNORECASE)
    return match.group(0) if match else normalized


def _mentioned(value, question):
    return isinstance(value, str) and normalize_arabic(value).replace(" ", "") in normalize_arabic(question).replace(" ", "")


def _without_invented_scope(node, folder, question):
    """Drop template/name conditions the question does not state (the folder already scopes the report)."""
    if not isinstance(node, dict):
        return node
    if node.get("conditions") is not None:
        kept = [child for child in (_without_invented_scope(c, folder, question) for c in node["conditions"]) if child is not None]
        return None if not kept else kept[0] if len(kept) == 1 and node.get("logic") == "and" else {**node, "conditions": kept}
    if node.get("field") in ("template", "name") and (
            normalize_arabic(str(node.get("value") or "")) == normalize_arabic(folder) or not _mentioned(node.get("value"), question)):
        return None
    return node


def ground_folder_scope(plan, question):
    """Return the plan dict with the explicit folder scope applied, or the plan unchanged."""
    scope = folder_scope(question)
    if scope is None or plan.get("operation") != "search":
        return plan
    folder, subfolders = scope
    grounded = {**plan, "folderName": folder, "folderId": None, "includeSubfolders": subfolders}
    if grounded.get("template") and (normalize_arabic(grounded["template"]) == normalize_arabic(folder) or
                                     not _mentioned(grounded["template"], question)):
        grounded["template"] = None
    if grounded.get("filters") is not None:
        grounded["filters"] = _without_invented_scope(grounded["filters"], folder, question)
    if grounded.get("name") is not None and normalize_arabic(grounded["name"]) == normalize_arabic(folder):
        grounded["name"] = None
    return grounded
