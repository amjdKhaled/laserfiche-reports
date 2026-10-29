"""Deterministic template and tag scope from the live catalog.

The planner model dropped explicit templates and tags: «الوثائق التي قالبها القرار» and «عليها وسم تم الإعتماد»
came back as the whole repository (2026-10-29, four wordings each). A template or tag named after its cue word
(«قالب/تمبلت/كرت الفهرسة X», «وسم/تاج X») has one meaning, so it is applied here. Names are matched after Arabic
spelling normalization, with or without «ال» and spaces («تمبلت قرار» is the template «القرار»). Only a name that
exists in the catalog is applied; a partial or ambiguous name stays with the planner.
"""
import re

from calendar_grounding import normalize_arabic
from scope_grounding import without_unasked_fields

_LEAD = r"(?<!\w)[وفبل]{0,2}"
# «كرت/بطاقة الفهرسة» and «كرت الأرشفة/الأرشيف» are what users call a template (2026-10-29).
_TEMPLATE_CUE = (_LEAD + r"(?:ال)?(?:قالب|قوالب|تمبلت|تمبليت|تيمبلت|تمبلتس|تيمبلتس|تمبلات|تمبليتات|template|templates|"
                 r"(?:كرت|كارت|بطاقه|كروت|كروته|بطاقات)\s+(?:ال)?(?:فهرس(?:ه)?|ارشفه|ارشيف)|"
                 r"(?:index|archive|archiving|metadata)\s+cards?)(?:ها|ه|هم)?(?!\w)")
_TAG_CUE = _LEAD + r"(?:ال)?(?:وسم|وسوم|اوسمه|تاج|تاغ|tag|tags|tagged|labeled|labelled)(?:ها|ه|هم)?(?!\w)"
# Negation words, Egyptian and English: «مالهاش قالب»، «لا تحتوي على كرت فهرسة»، «without a template».
_NEGATORS = (r"(?:مش|مو|ما|ليس|ليست|لا|بدون|من\s+غير|غير|بلا|مالهاش|ملهاش|مالهوش|ملهوش|مفيهاش|ماعندهاش|معندهاش|ماعليهاش|"
             r"معليهاش|without|no|not|do\s+not|does\s+not|don'?t|doesn'?t|isn'?t|lacking|missing)")
# Words between the cue and the name: «قالبها القرار»، «قالب اسمه "القرار"»، «قالبها مش القرار»، «template named X».
_LINK = (r"\s*(?:(?:(?:اسمه|اسمها|بتاعه|بتاعها|بتاعتها|بتاعته|هو|هي|يساوي|يكون|تكون|is|named|called|equals|"
         r"مش|ليس|ليست|غير|not|isn'?t|with|as|the)(?!\w)|[=:])\s*)*[\"'«“]?\s*")
_NEGATION = _NEGATORS + r"\s+(?:\S+\s+){0,2}$"
_LINK_NEGATION = re.compile(r"(?<!\w)(?:مش|ليس|ليست|غير|not|isn'?t)(?!\w)")


def _name_pattern(name):
    """Regex for a catalog name in normalized text: optional «ال», any spacing, then a word boundary."""
    key = normalize_arabic(name)
    key = (key[2:] if key.startswith("ال") and len(key) > 3 else key).replace(" ", "")
    return r"(?:ال)?" + r"\s*".join(re.escape(c) for c in key) + r"(?!\w)"


def _cues(question, cue, names):
    """[(name, negated)] for each cue word: the unique longest catalog name right after it, None when no name
    follows, False when the name is ambiguous."""
    text = normalize_arabic(question)
    found = []
    for match in re.finditer(cue + _LINK, text):
        negated = bool(re.search(_NEGATION, text[:match.start()]) or _LINK_NEGATION.search(match.group(0)))
        position = match.end()
        while True:
            hits = [(name, found_at) for name in names if (found_at := re.match(_name_pattern(name), text[position:]))]
            longest = max((len(normalize_arabic(name).replace(" ", "")) for name, _ in hits), default=0)
            best = [(name, at) for name, at in hits if len(normalize_arabic(name).replace(" ", "")) == longest]
            found.append((best[0][0] if len(best) == 1 else None if not hits else False, negated))
            # A list continues the cue: «قالب القرار أو التعميم»، «template X or Y».
            joined = re.match(r"\s*(?:او|و|or|and|[,،])\s*", text[position + best[0][1].end():]) if len(best) == 1 else None
            if joined is None:
                break
            position += best[0][1].end() + joined.end()
            if not any(re.match(_name_pattern(name), text[position:]) for name in names):
                break
    return found


def _named_before(question, cue_word, names):
    """[(name, negated)] for English word order, the name before its cue: «the General template», «the X tag»."""
    text = normalize_arabic(question)
    found = []
    for name in names:
        for match in re.finditer(r"(?<![\w])" + _name_pattern(name) + r"\s+" + cue_word + r"(?!\w)", text):
            found.append((name, bool(re.search(_NEGATION, text[:match.start()].removesuffix("the ")))))
    return found


def template_mentions(question, catalog):
    """[(template name, negated)] named after a template cue word, or before it in English."""
    found = {}
    names = catalog.get("templates") or []
    for name, negated in _cues(question, _TEMPLATE_CUE, names) + _named_before(question, r"templates?", names):
        if name:
            found.setdefault(name, negated)
    return list(found.items())


def mentioned_templates(question, catalog):
    return [name for name, negated in template_mentions(question, catalog) if not negated]


def without_template(question, catalog):
    """True for «الوثائق اللي مالهاش كرت فهرسة» / «documents without a template»: a negated cue naming no template."""
    return any(name is None and negated for name, negated in _cues(question, _TEMPLATE_CUE, catalog.get("templates") or []))


def mentioned_tags(question, catalog):
    """[(tag name, negated)] named after a tag cue word."""
    tags = {}
    names = [tag["name"] for tag in catalog.get("tags") or []]
    for name, negated in _cues(question, _TAG_CUE, names) + _named_before(question, r"tags?", names):
        if name:
            tags.setdefault(name, negated)
    return list(tags.items())


def _same(value, name):
    return isinstance(value, str) and normalize_arabic(value).replace(" ", "") == normalize_arabic(name).replace(" ", "")


def _without(node, names, templates_too=False):
    """Drop name/template/metadata leaves whose value is one of the grounded names (the model's stand-in for them),
    and with templates_too every template condition the model planned."""
    if not isinstance(node, dict):
        return node
    if node.get("conditions") is not None:
        kept = [child for child in (_without(c, names, templates_too) for c in node["conditions"]) if child is not None]
        return None if not kept else kept[0] if len(kept) == 1 else {**node, "conditions": kept}
    if node.get("tag") is None and (any(_same(node.get("value"), name) for name in names) or
                                    (templates_too and node.get("field") == "template")):
        return None
    return node


def _has_tag(node, name):
    if not isinstance(node, dict):
        return False
    return _same(node.get("tag"), name) or any(_has_tag(child, name) for child in node.get("conditions") or [])


def _and(filters, leaf):
    if filters is None:
        return leaf
    if filters.get("logic") == "and" and filters.get("conditions") is not None:
        return {**filters, "conditions": [*filters["conditions"], leaf]}
    return {"logic": "and", "conditions": [filters, leaf]}


def ground_catalog_scope(plan, question, catalog):
    """Return the plan dict with explicit templates and tags applied, or the plan unchanged."""
    if plan.get("operation") not in ("search", "group", "content"):
        return plan
    mentions, tags = template_mentions(question, catalog), mentioned_tags(question, catalog)
    untemplated = without_template(question, catalog)
    templates = [name for name, negated in mentions if not negated]
    excluded = [name for name, negated in mentions if negated]
    # A document has one template, so «قالب القرار أو التعميم» (or «… والتعميم») is either template (2026-10-29: the
    # model kept only one of them).
    either = templates if len(templates) > 1 else []
    if either:
        templates = []
    if not templates and not either and not excluded and not untemplated and not tags:
        return plan
    grounded = dict(plan)
    names = templates + either + excluded + [name for name, _ in tags]
    filters = _without(plan.get("filters"), names, templates_too=bool(either or excluded or untemplated))
    if grounded.get("name") is not None and any(_same(grounded["name"], name) for name in names):
        grounded["name"] = None
    if templates:
        grounded["template"] = templates[0]
    elif either or excluded or untemplated:
        grounded["template"] = None
    if either:
        filters = _and(filters, {"logic": "or", "conditions": [{"field": "template", "operator": "equals", "value": name} for name in either]})
    for name in excluded:
        filters = _and(filters, {"field": "template", "operator": "not_equals", "value": name})
    if untemplated and not excluded:
        filters = _and(filters, {"field": "template", "operator": "is_empty"})
    for name, negated in tags:
        if not _has_tag(filters, name):
            filters = _and(filters, {"tag": name, "operator": "not_tag" if negated else "has_tag"})
    grounded["filters"] = without_unasked_fields(filters, question, names)
    if grounded == plan:
        return plan
    grounded["requiresFilter"] = True
    return grounded
