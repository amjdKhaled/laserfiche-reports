"""Pick one contiguous source window; never splice text to manufacture quotations."""
import re
import unicodedata

STOP = set("ما ماهي هي هو هل من في عن على الى كيف ايش وش اريد ابغا ابي اعرض اذكر لخص تقرير الوثيقة الوثائق المستند المستندات the a an is are of in on for what which show document documents report id".split())


def normalized(text):
    result, offsets = [], []
    for index, char in enumerate(text):
        if unicodedata.category(char) == "Mn" or char == "ـ":
            continue
        value = str(unicodedata.decimal(char)) if char.isdecimal() else {"أ": "ا", "إ": "ا", "آ": "ا", "ى": "ي"}.get(char, char.lower())
        for c in value:
            result.append(c)
            offsets.append(index)
    return "".join(result), offsets


def focused_window(text, question, limit):
    if len(text) <= limit:
        return text, 0
    query, _ = normalized(question)
    terms = {word for word in re.findall(r"\w+", query) if word not in STOP and (len(word) > 1 or word.isdigit())}
    searchable, offsets = normalized(text)
    hits = [(offsets[m.start()], word) for word in terms
            for m in list(re.finditer(r"(?<!\w)" + re.escape(word) + r"(?!\w)", searchable))[:40]]
    starts = {0} | {max(0, min(len(text) - limit, position - limit // 3)) for position, _ in hits}
    start = max(starts, key=lambda s: (len({w for p, w in hits if s <= p < s + limit}), -s))
    # Move the start to a sentence/line boundary when nearby, preserving negation.
    boundary = max(text.rfind("\n", max(0, start - 120), start), text.rfind(". ", max(0, start - 120), start))
    if boundary >= 0:
        start = boundary + 1
    return text[start:start + limit], start
