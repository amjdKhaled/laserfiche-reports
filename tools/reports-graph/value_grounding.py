"""Deterministic field conditions from List-field choices and the repository glossary.

«ما هي الوثائق التي حالتها تحت الاجراء» names a value, not a field: the Demo repository offers «تحت الاجراء» in five
List fields («إ جراء الوثيقة» and its two look-alikes, «هل تريد ارشفة الوثيقة؟», «حالة المعاملة»). The model planned
no condition, or one of the look-alikes only (2026-10-29). A value that is a choice of List fields selects documents
with that choice in any of them. The glossary (glossary.json, edited by the repository owner) maps business words
that are no field value, such as «مؤرشفة» = «هل تريد ارشفة الوثيقة؟» is «نعم».
"""
import json
import os
import re
from pathlib import Path

from calendar_grounding import normalize_arabic
from catalog_grounding import _LINK, _NEGATION, _TAG_CUE, _TEMPLATE_CUE, _and, mentioned_tags, template_mentions
from scope_grounding import _FOLDER_WORD, without_unasked_fields

_LEAD = r"(?<!\w)[وفبل]{0,2}"
_NEGATED = r"(?<!\w)" + _NEGATION
# A name right after «مجلد»، «وسم» or «قالب» is that folder, tag or template even when a field offers it as a choice.
_NAMED = r"(?:" + r"(?<!\w)[وفبل]{0,2}(?:ال)?" + _FOLDER_WORD + r"|" + _TAG_CUE + r"|" + _TEMPLATE_CUE + r")" + _LINK + r"$"
# Choices too common to mean a field value on their own («نعم» answers many questions).
_GENERIC = {"نعم", "لا", "yes", "no", "نشط", "اخري", "اخرى", "other", "none"}


def _key(text):
    return normalize_arabic(text)


def _word(word):
    """A word with or without the feminine ending: choice «جاهز للاتلاف» answers «جاهزة للإتلاف»."""
    return re.escape(word[:-1]) + "ه?" if word.endswith("ه") and len(word) > 3 else re.escape(word) + "ه?"


def _pattern(phrase):
    return _LEAD + r"(?:ال)?" + r"\s+".join(_word(word) for word in _key(phrase).split()) + r"(?!\w)"


def _choices(catalog):
    """{normalized choice: [(field, choice as stored)]}; a bilingual «تحت الإجراء / In Progress» also answers to each part."""
    found = {}
    for field in catalog.get("fields") or []:
        for value in field.get("listValues") or []:
            parts = [value] + ([part.strip() for part in value.split("/")] if "/" in value else [])
            for part in parts:
                key = _key(part)
                if len(key.replace(" ", "")) < 4 or key in _GENERIC or key.isdigit():
                    continue
                pairs = found.setdefault(key, [])
                if (field["name"], value) not in pairs:
                    pairs.append((field["name"], value))
    return found


def _blank(text, names):
    for name in sorted({n for n in names if n}, key=len, reverse=True):
        text = re.sub(r"\s*".join(re.escape(c) for c in _key(name).replace(" ", "")), " ", text)
    return text


def mentioned_value(question, catalog, grounded_names=()):
    """(phrase, [(field, value)]) for the one List choice the question names, otherwise None."""
    found = _value(question, catalog, grounded_names)
    return found and found[:2]


def _value(question, catalog, grounded_names=()):
    """(phrase, [(field, value)], words as asked) for the one List choice the question names, otherwise None.

    Template, tag and folder names are removed first: «وسم تم نشر الوثيقة» is a tag even if a field offers that choice.
    A negated value («مش تحت الاجراء») stays with the planner."""
    text = _blank(_key(question), grounded_names)
    hits = []
    for key, pairs in _choices(catalog).items():
        for match in re.finditer(_pattern(key), text):
            if re.search(_NAMED, text[:match.start()]):
                continue
            hits.append((match.start(), match.end(), key, pairs, match.group(0)))
    # The longest phrase wins over a phrase inside it.
    hits.sort(key=lambda hit: hit[0] - hit[1])
    kept = []
    for hit in hits:
        if not any(hit[0] < other[1] and other[0] < hit[1] for other in kept):
            kept.append(hit)
    if len({hit[2] for hit in kept}) != 1:
        return None
    start, _, key, pairs, asked = kept[0]
    if re.search(_NEGATED, text[:start]):
        return None
    return key, pairs, asked


def _load_glossary():
    path = Path(os.environ.get("REPORTS_GLOSSARY") or Path(__file__).with_name("glossary.json"))
    try:
        return json.loads(path.read_text(encoding="utf-8")).get("terms") or []
    except (OSError, ValueError):
        return []


GLOSSARY = _load_glossary()


def glossary_conditions(question, catalog, glossary=None):
    """[(condition, words, word as asked)] of the glossary terms the question uses, for fields and templates this
    repository has: «مؤرشفة» is a field value, «القرارات» the documents of template «القرار»."""
    text = _key(question)
    fields = {f["name"] for f in catalog.get("fields") or []}
    templates = set(catalog.get("templates") or [])
    found = []
    for term in GLOSSARY if glossary is None else glossary:
        condition = term.get("condition") or {}
        if condition.get("field") not in fields and not (condition.get("field") == "template" and condition.get("value") in templates):
            continue
        for word in term.get("words") or []:
            match = re.search(_pattern(word), text)
            if match and not re.search(_NEGATED, text[:match.start()]):
                found.append((condition, term.get("words") or [], match.group(0)))
                break
    return found


def _overlaps(phrase, value):
    """The model's stand-in for a grounded value: «إتلاف» for «جاهز للاتلاف», «مؤرشفة» for the glossary word."""
    phrase, value = _key(phrase), _key(value)
    return phrase in value or (len(value.replace(" ", "")) >= 4 and value in phrase)


def _without_fields(node, fields, phrases=()):
    """Drop the model's conditions on the grounded fields, or with a grounded value or word under another field."""
    if not isinstance(node, dict):
        return node
    if node.get("conditions") is not None:
        kept = [child for child in (_without_fields(c, fields, phrases) for c in node["conditions"]) if child is not None]
        return None if not kept else kept[0] if len(kept) == 1 else {**node, "conditions": kept}
    if node.get("tag") is None and (node.get("field") in fields or
                                    (isinstance(node.get("value"), str) and any(_overlaps(p, node["value"]) for p in phrases))):
        return None
    return node


def ground_field_values(plan, question, catalog, glossary=None):
    """Return the plan dict with the named List choice and glossary terms applied, or the plan unchanged."""
    if plan.get("operation") not in ("search", "group", "content"):
        return plan
    names = [plan.get("folderName"), *(name for name, _ in template_mentions(question, catalog)),
             *(name for name, _ in mentioned_tags(question, catalog))]
    value = _value(question, catalog, names)
    terms = glossary_conditions(question, catalog, glossary)
    if value is None and not terms:
        return plan
    # Conditions the question never states go first: with «القرارات المؤرشفة» the model also asked for
    # Status = «تم الارشفة» (2026-10-29).
    asked = [name for name in names if name] + ([value[2]] if value else []) + [word for _, _, word in terms]
    filters = without_unasked_fields(plan.get("filters"), question, asked)
    grounded = dict(plan)
    if value is not None:
        phrase, pairs, _ = value
        filters = _without_fields(filters, {field for field, _ in pairs}, [phrase])
        leaves = [{"field": field, "operator": "equals", "value": choice} for field, choice in pairs]
        filters = _and(filters, leaves[0] if len(leaves) == 1 else {"logic": "or", "conditions": leaves})
    for condition, words, _ in terms:
        filters = _and(_without_fields(filters, {condition["field"]}, words), dict(condition))
        if condition["field"] == "template":
            grounded["template"] = None
    if filters == plan.get("filters") and grounded.get("template") == plan.get("template"):
        return plan
    return {**grounded, "filters": filters, "requiresFilter": True}
