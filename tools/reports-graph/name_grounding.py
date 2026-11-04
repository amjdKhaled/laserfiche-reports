"""A file the question names: «الملف اللي اسمه X موجود؟», «فين ملف X», «is there a file named X?».

The user asks for one file after a list left it out (2026-10-29). The model planned «فين ملف قرار 1-4» as
Request Number = 1-4, and «is there a file named X?» or «فين ملف عقد التوريد» as a count of the whole repository.
The name becomes the one name condition.
"""
import re

from calendar_grounding import normalize_arabic

_QUOTE_OPEN, _QUOTE_CLOSE = "\"'«“", "\"'»”"
# Words after the name that end it: «… موجود؟», «… فين؟», «… in the X folder».
_END = (r"(?=\s*(?:$|[?؟!.,،]|\s(?:موجود|موجوده|موجودة|فين|وين|ولا|او|أو|في\s+(?:ال)?(?:مجلد|فولدر)|اللي|عندي|عندنا|"
        r"exists?|is\s+there|in\s+(?:the\s+)?folder|in\s+the|please)(?!\w)))")
_NAMED = re.compile(r"(?<!\w)(?:اسمه|اسمها|اسمو|إسمه|إسمها|بإسم|باسم|بعنوان|عنوانه|عنوانها|named|called|titled|"
                    r"with\s+the\s+name|whose\s+name\s+is)(?!\w)\s*[:=]?\s*(?P<name>[" + _QUOTE_OPEN + r"]?.+?[" + _QUOTE_CLOSE + r"]?)" + _END,
                    re.IGNORECASE)
# «في ملف X» is left alone: «الوثائق الموجودة في ملف X» asks for what is in X, not for a file named X.
_LOOKUP = re.compile(r"(?<!\w)(?:فين|وين|اين|أين|دور\s+على|دورلي\s+على|ابحث\s+عن|ابحثلي\s+عن|هل\s+يوجد|هات|هاتلي|اعرض|اعرضلي|"
                     r"افتح|افتحلي|where\s+is|find|is\s+there|do\s+we\s+have|show(?:\s+me)?|open)\s+(?:a\s+|the\s+)?(?:ال)?"
                     r"(?:ملف|فايل|وثيقة|وثيقه|مستند|file|document)\s+"
                     r"(?P<name>(?!(?:اللي|التي|الذي|that|which|named|called|اسمه|اسمها)(?!\w)).+?)" + _END, re.IGNORECASE)
# A name that starts with a planning word is a condition, not a name: «ملف قالبه القرار», «ملف اتعمل امبارح»; so is a
# partial match the planner reads itself: «اسمها يبدأ بمحضر», «named like X».
_NOT_A_NAME = re.compile(r"^(?:[وفبل]{0,2}(?:ال)?(?:قالب|تمبلت|كرت|وسم|تاج|مجلد|فولدر|تاريخ|اتعمل|اتعدل|انشئ|عدد|كل)(?:ها|ه|هم|ت)?|"
                         r"يبدا|يبتدي|بيبدا|تبدا|ينتهي|بينتهي|فيه|فيها|يحتوي|تحتوي|بيحتوي|زي|مثل|"
                         r"created|modified|with|from|in|of|template|tag|folder|starts?|ends?|begins?|contains?|includes?|like)(?!\w)",
                         re.IGNORECASE)


def mentioned_name(question, catalog):
    """The entry name the question asks for, as written, or None."""
    for pattern in (_NAMED, _LOOKUP):
        match = pattern.search(question.strip())
        if not match:
            continue
        name = match.group("name").strip().strip(_QUOTE_OPEN + _QUOTE_CLOSE).strip()
        key = normalize_arabic(name)
        catalog_names = {normalize_arabic(n) for n in catalog.get("templates") or []} | \
                        {normalize_arabic(t.get("name") or "") for t in catalog.get("tags") or []}
        if 0 < len(name) <= 100 and not _NOT_A_NAME.match(key) and key not in catalog_names:
            return name
    return None


def _dates_only(node):
    """Keep the model's date conditions («… اللي اتعمل امبارح»); every other condition was its guess at the name."""
    if not isinstance(node, dict):
        return None
    if node.get("conditions") is not None:
        kept = [child for child in (_dates_only(c) for c in node["conditions"]) if child is not None]
        return None if not kept else kept[0] if len(kept) == 1 else {**node, "conditions": kept}
    return node if node.get("field") in ("created", "modified") else None


def ground_entry_name(plan, question, catalog):
    """Return the plan dict with the named file as its name condition, or the plan unchanged."""
    if plan.get("operation") != "search" or plan.get("name") or plan.get("template"):
        return plan
    name = mentioned_name(question, catalog)
    if name is None:
        return plan
    leaf = {"field": "name", "operator": "contains", "value": name}
    dates = _dates_only(plan.get("filters"))
    filters = leaf if dates is None else {"logic": "and", "conditions": [dates, leaf]}
    if filters == plan.get("filters"):
        return plan
    return {**plan, "filters": filters, "requiresFilter": True}
