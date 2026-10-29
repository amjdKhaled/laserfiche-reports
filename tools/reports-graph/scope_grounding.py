"""Deterministic folder scope for «مجلد X» or «فولدر X» in a question.

The planner model sometimes plans «في مجلد الوارد» as a template or a name filter, or invents a
template. An explicit «مجلد X» has one meaning: every folder named X (the backend resolves the
name with Arabic spelling normalization), its direct contents unless the question adds «وما تحته».
"""
import re

from calendar_grounding import normalize_arabic

_SUBFOLDERS = (r"\s+(?:و\s*|مع\s+|بما\s+فيه(?:ا)?\s+)?(?:كل\s+|جميع\s+)?"
               r"(?:(?:ما|اللي)\s+(?:تحته|تحتها|بداخله|داخله|يندرج(?:\s+تحته)?(?:\s+المجلد)?)"
               r"|(?:المجلدات|مجلداته)(?:\s+(?:اللي|التي))?\s+(?:الفرعيه|تحته|بداخله|داخله))(?!\w)"
               # English (2026-10-29): «and its subfolders», «including subfolders», «and everything under it», «recursively».
               r"|\s+(?:(?:and|including|with|plus)\s+(?:all\s+)?(?:of\s+)?(?:its\s+|the\s+)?(?:sub-?folders|everything\s+(?:under|inside|below)\s+it)"
               r"|recursively)(?!\w)")
# The folder name ends before a relative clause, a qualifier or punctuation.
_NAME_END = (r"(?=" + _SUBFOLDERS + r"|\s+(?:التي|اللتي|الذي|اللذي|اللي|فقط|خلال|المنشاه|المنشئه|المعدله|"
             r"والتي|واللتي|حيث|بتاريخ|منذ|قبل|بعد|حتي|that|which|where|created|modified|updated|added|only|during|since|"
             r"before|after|between|from|in\s+(?:the\s+)?(?:last|this|past|current|previous))(?!\w)|\s*[؟?.,،:;!]|$)")
# Every word users write for a folder (2026-10-29): «ملف» is left out, it means a document.
_FOLDER_WORD = r"(?:مجلد|فولدر|دوسيه|دايركتوري|folder|directory)"
_FOLDER = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?" + _FOLDER_WORD + r"\s+(?:اسمه\s+|اسمها\s+|باسم\s+|named\s+|called\s+)?"
                     r"[\"'«“]?(?P<name>[^؟?.,،:;!\"'»”]+?)[\"'»”]?" + _NAME_END)
# English names the folder first: «documents in the Publications folder».
_FOLDER_AFTER = re.compile(r"(?<!\w)(?:in|inside|under|within|of|from)\s+(?:the\s+)?[\"']?(?P<name>[^?.,;:!\"']+?)[\"']?\s+(?:folder|directory)(?!\w)")
# «ايه اللي جوا فولدر X» and «ملفات فولدر X» ask for its contents: documents and subfolders (2026-10-29).
# «الوثائق في مجلد X» still means documents only.
_FILES = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?(?:محتوي|محتويات|ملفات|عناصر|entries|contents|files)(?!\w)")
_INSIDE = re.compile(r"(?<!\w)[وفبل]{0,2}(?:جوا|جوه|بداخل|داخل|inside)(?!\w)")
# «المجلدات داخل مجلد X» lists only its subfolders.
_SUBFOLDER_LIST = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?(?:مجلدات|فولدرات|دوسيهات|folders|sub-?folders|directories)(?!\w)")
_DOCUMENTS = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?(?:وثيقه|وثائق|وثايق|مستند|مستندات|documents?)(?!\w)")


def _without_template_names(text, templates):
    """Blank out template names that contain a folder word: «قالبها مجلد - سري» names a template, not a folder."""
    for name in sorted(templates, key=len, reverse=True):
        key = normalize_arabic(name).replace(" ", "")
        if re.search(_FOLDER_WORD, key):
            text = re.sub(r"\s*".join(re.escape(c) for c in key) + r"(?!\w)", " ", text)
    return text


def folder_scope(question, templates=()):
    """(folder name, include subfolders) for exactly one «مجلد X» in the question, otherwise None."""
    text = _without_template_names(normalize_arabic(question), templates)
    matches = list(_FOLDER.finditer(text)) or list(_FOLDER_AFTER.finditer(text))
    if len(matches) != 1:
        return None
    name = matches[0].group("name").strip()
    if (not name or len(name) > 120 or re.search(r"(?<!\w)[وفبل]{0,2}(?:ال)?" + _FOLDER_WORD + r"(?!\w)", name) or
            re.fullmatch(r"(?:رقم\s*)?\d+", name)):
        return None  # Two folders, or a folder ID («المجلد رقم 42»), stay with the planner.
    after = text[matches[0].end():]
    return _original_spelling(question, name), bool(re.match(_SUBFOLDERS, after) or re.search(r"(?<!\w)(?:and|including)\s+(?:all\s+)?(?:its\s+)?sub-?folders(?!\w)", text))


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


_BUILTINS = ("created", "modified", "name", "template", "entryId", "creator", "pageCount")


def without_unasked_fields(node, question, grounded_names):
    """Drop metadata conditions the question never states once a folder, tag or template explains it.

    Seen 2026-10-29: «عليها وسم تم الرفض» came back with Status = Rejected or Status is_not_empty, and
    «مجلد الطلبات المرفوضة» with Status = Rejected. A condition stays when the question, without the grounded
    names, still mentions its field or its value."""
    if not isinstance(node, dict):
        return node
    if node.get("conditions") is not None:
        kept = [child for child in (without_unasked_fields(c, question, grounded_names) for c in node["conditions"]) if child is not None]
        return None if not kept else kept[0] if len(kept) == 1 and node.get("logic") == "and" else {**node, "conditions": kept}
    if node.get("tag") is not None or node.get("field") in _BUILTINS or node.get("field") is None:
        return node
    rest = normalize_arabic(question)
    for name in grounded_names:
        rest = rest.replace(normalize_arabic(name), " ")
    if _mentioned(node.get("field"), rest) or _mentioned(node.get("value"), rest):
        return node
    return None


def ground_folder_scope(plan, question, templates=()):
    """Return the plan dict with the explicit folder scope applied, or the plan unchanged."""
    scope = folder_scope(question, templates)
    if scope is None or plan.get("operation") not in ("search", "folders"):
        return plan
    folder, subfolders = scope
    grounded = {**plan, "folderName": folder, "folderId": None, "includeSubfolders": subfolders}
    text = normalize_arabic(question)
    if _DOCUMENTS.search(text) or plan.get("entryType") == "all":
        pass
    elif _FILES.search(text):
        grounded["operation"], grounded["entryType"] = "search", "all"
    elif _SUBFOLDER_LIST.search(text):
        grounded["operation"], grounded["entryType"] = "folders", "folder"
    elif _INSIDE.search(text):
        grounded["operation"], grounded["entryType"] = "search", "all"
    if grounded.get("template") and (normalize_arabic(grounded["template"]) == normalize_arabic(folder) or
                                     not _mentioned(grounded["template"], question)):
        grounded["template"] = None
    if grounded.get("filters") is not None:
        grounded["filters"] = without_unasked_fields(_without_invented_scope(grounded["filters"], folder, question), question, [folder])
    if grounded.get("name") is not None and normalize_arabic(grounded["name"]) == normalize_arabic(folder):
        grounded["name"] = None
    return grounded
