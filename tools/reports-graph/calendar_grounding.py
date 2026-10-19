"""Deterministic periods for relative Arabic date phrases.

The planner model sometimes turns «هذا الشهر» into today's date or the first day of the month.
Phrases with one fixed meaning are resolved here from the question text and today's date, and
replace the single date condition the model planned. Anything less certain is left unchanged.
"""
import re
from datetime import date, timedelta

_DIACRITICS = re.compile("[ؐ-ًؚ-ٰٟۖ-ۭـ]")
_LETTERS = str.maketrans({"أ": "ا", "إ": "ا", "آ": "ا", "ة": "ه", "ى": "ي"})


def normalize_arabic(text):
    """أ/إ/آ→ا، ة→ه، ى→ي, without diacritics or tatweel; Arabic-Indic digits become ASCII."""
    text = _DIACRITICS.sub("", text or "").translate(_LETTERS)
    text = text.translate(str.maketrans("٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹", "01234567890123456789"))
    return re.sub(r"\s+", " ", text).strip().lower()


_P = r"(?<!\w)(?:[وفبل]{0,2})"   # attached و/ف/ب/ل, e.g. «بالامس»، «والشهر»
_END = r"(?!\w)"
# Ordered: a longer phrase wins over the shorter phrase it contains.
_PHRASES = [
    ("day", -2, _P + r"(?:اول (?:من )?(?:امس|امبارح)|قبل امس)" + _END),
    ("day", -1, _P + r"(?:(?:ال)?(?:امس|بارحه)|امبارح)" + _END),
    ("day", 0, _P + r"(?:اليوم|النهارده|انهارده)" + _END),
    ("week", -1, _P + r"(?:الاسبوع (?:الماضي|السابق|الفائت)|الاسبوع اللي فات)" + _END),
    ("week", 0, _P + r"(?:(?:هذا|هاذا) الاسبوع|الاسبوع (?:الحالي|الجاري))" + _END),
    ("month", -1, _P + r"(?:الشهر (?:الماضي|السابق|الفائت)|الشهر اللي فات)" + _END),
    ("month", 0, _P + r"(?:(?:هذا|هاذا) الشهر|الشهر (?:الحالي|الجاري))" + _END),
    ("year", -1, _P + r"(?:(?:السنه|العام) (?:الماضي|الماضيه|السابق|السابقه|الفائت|الفائته)|السنه اللي فاتت)" + _END),
    ("year", 0, _P + r"(?:(?:هذه|هذي|هاذي) السنه|(?:هذا|هاذا) العام|(?:السنه|العام) (?:الحالي|الحاليه|الجاري|الجاريه))" + _END),
]
# A range word just before the phrase makes it an open range («منذ بداية الشهر»، «قبل هذا الشهر»،
# «حتى اليوم»); those stay with the planner.
_RANGE_WORDS = {"منذ", "حتي", "لغايه", "قبل", "بعد", "من", "الي", "اخر"}
_START_WORDS = {"بدايه", "نهايه", "اول", "مطلع", "اواخر"}


def _is_word(word, words):
    """Membership allowing one or two attached letters (و/ف/ب/ل), e.g. «ومنذ»، «لبداية»."""
    return any(word[cut:] in words for cut in range(0, 3) if cut == 0 or set(word[:cut]) <= set("وفبل"))


def relative_period(question):
    """(unit, offset) for exactly one fixed relative phrase in the question, otherwise None."""
    text = normalize_arabic(question)
    found = []
    for unit, offset, pattern in _PHRASES:
        match = re.search(pattern, text)
        if not match:
            continue
        before = text[:match.start()].split()[-2:]
        if before and (_is_word(before[-1], _RANGE_WORDS | _START_WORDS) or
                       (len(before) == 2 and _is_word(before[0], _RANGE_WORDS) and _is_word(before[1], _START_WORDS))):
            return None
        found.append((unit, offset))
        text = text[:match.start()] + " " + text[match.end():]
    return found[0] if len(found) == 1 else None


def period_bounds(unit, offset, today):
    """[start, end) dates of the period; weeks start on Sunday, as in the backend."""
    if unit == "day":
        start = today + timedelta(days=offset)
        return start, start + timedelta(days=1)
    if unit == "week":
        start = today - timedelta(days=(today.weekday() + 1) % 7) + timedelta(weeks=offset)
        return start, start + timedelta(days=7)
    if unit == "month":
        months = today.year * 12 + today.month - 1 + offset
        start = date(months // 12, months % 12 + 1, 1)
        return start, date((months + 1) // 12, (months + 1) % 12 + 1, 1)
    start = date(today.year + offset, 1, 1)
    return start, date(start.year + 1, 1, 1)


def grounded_condition(field, unit, offset, today):
    """The filter (as a dict) selecting the whole period on a date field."""
    start, end = period_bounds(unit, offset, today)
    if unit == "day":
        return {"field": field, "operator": "in_period", "period": {"year": start.year, "month": start.month, "day": start.day}}
    if unit == "month":
        return {"field": field, "operator": "in_period", "period": {"year": start.year, "month": start.month}}
    if unit == "year":
        return {"field": field, "operator": "in_period", "period": {"year": start.year}}
    return {"logic": "and", "conditions": [
        {"field": field, "operator": "from_period", "period": {"year": start.year, "month": start.month, "day": start.day}},
        {"field": field, "operator": "before_period", "period": {"year": end.year, "month": end.month, "day": end.day}}]}


_REPLACEABLE = ("in_period", "equals", "between", "date_between")


def _is_date_condition(node, date_fields):
    return (node.get("conditions") is None and node.get("operator") in _REPLACEABLE and
            (node.get("period") is not None or node.get("field") in date_fields))


def _date_conditions(node, date_fields):
    if not isinstance(node, dict):
        return []
    if node.get("conditions") is not None:
        return [found for child in node["conditions"] for found in _date_conditions(child, date_fields)]
    return [node] if _is_date_condition(node, date_fields) else []


def _replace(node, target, replacement):
    if node is target:
        return replacement
    if isinstance(node, dict) and node.get("conditions") is not None:
        return {**node, "conditions": [_replace(child, target, replacement) for child in node["conditions"]]}
    return node


def ground_report_filters(filters, question, today, date_fields=("created", "modified")):
    """Return filters with the one relative date condition replaced by the exact period, or
    the filters unchanged when the question or the plan is not a certain single-period case."""
    period = relative_period(question)
    if period is None or not isinstance(filters, dict) or not today:
        return filters
    today = date.fromisoformat(today) if isinstance(today, str) else today
    targets = _date_conditions(filters, set(date_fields))
    if len(targets) != 1:
        return filters
    return _replace(filters, targets[0], grounded_condition(targets[0]["field"], *period, today))
