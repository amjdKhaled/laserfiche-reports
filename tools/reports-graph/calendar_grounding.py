"""Deterministic periods for relative Arabic date phrases.

The planner model sometimes turns «هذا الشهر» into today's date or the first day of the month.
Phrases with one fixed meaning are resolved here from the question text and today's date, and
replace the single date condition the model planned, or are added when the model planned none
while the question names creation or modification. Anything less certain is left unchanged.
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
    # English (2026-10-29). «past week» is left out: it may mean the last seven days.
    ("day", -2, r"(?<!\w)the day before yesterday" + _END),
    ("day", -1, r"(?<!\w)yesterday" + _END),
    ("day", 0, r"(?<!\w)today" + _END),
    ("week", -1, r"(?<!\w)(?:last|previous) week" + _END),
    ("week", 0, r"(?<!\w)(?:this|current) week" + _END),
    ("month", -1, r"(?<!\w)(?:last|previous) month" + _END),
    ("month", 0, r"(?<!\w)(?:this|current) month" + _END),
    ("year", -1, r"(?<!\w)(?:last|previous) year" + _END),
    ("year", 0, r"(?<!\w)(?:this|current) year" + _END),
]
# A range word just before the phrase makes it an open range («منذ بداية الشهر»، «قبل هذا الشهر»،
# «حتى اليوم»); those stay with the planner.
_RANGE_WORDS = {"منذ", "حتي", "لغايه", "قبل", "بعد", "من", "الي", "اخر", "since", "until", "till", "before", "after", "from", "to"}
_START_WORDS = {"بدايه", "نهايه", "اول", "مطلع", "اواخر", "start", "beginning", "end", "of"}


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


_MONTHS = {name: number for number, names in enumerate([
    ("يناير", "january"), ("فبراير", "february"), ("مارس", "march"), ("ابريل", "april"), ("مايو", "may"),
    ("يونيو", "يونيه", "june"), ("يوليو", "يوليه", "july"), ("اغسطس", "august"), ("سبتمبر", "september"),
    ("اكتوبر", "october"), ("نوفمبر", "november"), ("ديسمبر", "december")], 1) for name in names}
_NAMED_MONTH = re.compile(_P + r"(?:شهر\s+)?(" + "|".join(_MONTHS) + r")\s+(?:(?:سنه|عام)\s+)?(\d{4})" + _END)


def named_month(question):
    """(year, month) for exactly one «أكتوبر 2026» in the question, otherwise None."""
    found = {(int(year), _MONTHS[name]) for name, year in _NAMED_MONTH.findall(normalize_arabic(question))}
    return found.pop() if len(found) == 1 else None


def ground_named_month(filters, question, date_fields=("created", "modified")):
    """Put the question's named month on the one in_period date condition that has its year but lost the month.

    Seen 2026-10-29: «عليها وسم تم الرفض واتعملت في أكتوبر 2026» was planned as period={year: 2026}."""
    named = named_month(question)
    targets = [node for node in _date_conditions(filters, set(date_fields)) if node.get("operator") == "in_period"]
    if named is None or len(targets) != 1:
        return filters
    period = targets[0].get("period") or {}
    if period.get("year") != named[0] or period.get("day") is not None or period.get("month") == named[1]:
        return filters
    return _replace(filters, targets[0], {**targets[0], "period": {"year": named[0], "month": named[1]}})


def whole_periods(node, date_fields=("created", "modified")):
    """A date range covering exactly one month or year («أغسطس 2026» planned as 08-01..08-31) as that period,
    so the backend computes the bounds and the last day counts in full."""
    if not isinstance(node, dict):
        return node
    if node.get("conditions") is not None:
        children = [whole_periods(child, date_fields) for child in node["conditions"]]
        return node if all(a is b for a, b in zip(children, node["conditions"])) else {**node, "conditions": children}
    if node.get("operator") not in ("between", "date_between") or node.get("field") not in date_fields:
        return node
    try:
        low, high = date.fromisoformat(str(node.get("value"))[:10]), date.fromisoformat(str(node.get("upper"))[:10])
    except ValueError:
        return node
    if low.day != 1 or len(str(node.get("value"))) > 10 or len(str(node.get("upper"))) > 10:
        return node
    if low.month == 1 and high in (date(low.year, 12, 31), date(low.year + 1, 1, 1)):
        return {"field": node["field"], "operator": "in_period", "period": {"year": low.year}}
    _, end = period_bounds("month", 0, low)
    if high in (end - timedelta(days=1), end):
        return {"field": node["field"], "operator": "in_period", "period": {"year": low.year, "month": low.month}}
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


# Word stems after attached و/ف/ب/ل and «ال». Masculine «معدل» is left out: it also means "rate".
_CREATED_WORDS = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?(?:انش[اءئؤ]|منشا|اضيف|اضاف|اتضاف|اتعمل)|(?<!\w)(?:created|added|uploaded|creation)(?!\w)")
_MODIFIED_WORDS = re.compile(r"(?<!\w)[وفبل]{0,2}(?:ال)?(?:معدله|تعديل|اتعدل|عدلت|عدلها)|(?<!\w)(?:modified|updated|changed|edited|modification)(?!\w)")


def period_field(question):
    """created or modified when the question names exactly one of them, otherwise None."""
    text = normalize_arabic(question)
    created, modified = bool(_CREATED_WORDS.search(text)), bool(_MODIFIED_WORDS.search(text))
    return "created" if created and not modified else "modified" if modified and not created else None


def _date_mentions(node, date_fields):
    """Every condition that already constrains a date field or a period, whatever its operator."""
    if not isinstance(node, dict):
        return []
    if node.get("conditions") is not None:
        return [found for child in node["conditions"] for found in _date_mentions(child, date_fields)]
    return [node] if node.get("field") in date_fields or node.get("period") or node.get("relative") else []


def add_missing_period(filters, question, today, date_fields=("created", "modified")):
    """Return filters with the question's one fixed period added on creation or modification when
    the model planned no date condition at all («ما الوثائق التي تم انشاها بتاريخ اليوم» searched the
    whole repository on 2026-10-26), or the filters unchanged in every less certain case."""
    period, field = relative_period(question), period_field(question)
    if period is None or field is None or not today or not (filters is None or isinstance(filters, dict)):
        return filters
    if _date_mentions(filters, set(date_fields)):
        return filters
    today = date.fromisoformat(today) if isinstance(today, str) else today
    condition = grounded_condition(field, *period, today)
    return condition if filters is None else {"logic": "and", "conditions": [filters, condition]}
