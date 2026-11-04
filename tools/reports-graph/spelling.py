"""Correct misspelled planning words before the model and the grounding read the question.

Users type fast: «كرت ارشفيف», «قلاب التعمم», «tempalte», «اغسطوس». Listing every misspelling cannot keep up (the
user, 2026-10-29), so a word that is no known word is replaced by the one planning word or catalog name it is a single
slip away from: a missing, extra, wrong or swapped letter. English words written in Arabic letters («تمبلييت»,
«دوكيومنتس», «فولدرز») are recognized by their consonants. Short words need a closer match, so real words stay:
«مجلس» never becomes «مجلد», and «تم الرفع» becomes the tag «تم الرفض» only right after «وسم».
"""
import re

from calendar_grounding import normalize_arabic
from catalog_grounding import _LINK, _TAG_CUE, _TEMPLATE_CUE

# Words that decide a plan, in display spelling and without «ال». Nouns and months only: verbs have too many right
# forms («تعدلت», «اتعدلت», «عدلت») to tell a slip from a form.
_DOMAIN = (
    "قالب قوالب تمبلت تمبليت تيمبلت كرت كارت بطاقة فهرسة أرشفة أرشيف مجلد مجلدات فولدر فولدرات دوسيه وسم وسوم "
    "وثيقة وثائق مستند مستندات ملفات محتويات محذوفات مؤرشفة مؤرشف اتأرشفت فرعية "
    "يناير فبراير مارس أبريل مايو يونيو يوليو أغسطس سبتمبر أكتوبر نوفمبر ديسمبر "
    "يونيه يوليه امبارح نهارده أسبوع "
    "template templates folder folders subfolder subfolders document documents files tagged archived created modified "
    "today yesterday week weeks month months year years january february march april june july august september "
    "october november december"
).split()
# Real words one slip away from a planning word.
_KEEP = {"مجلات", "مجلس", "قوارب", "مستعد", "وثائقي", "اتعلمت", "older", "holder", "match", "matches", "documented"}
# English planning words users write in Arabic letters, and their Arabic meaning.
_TRANSLITERATED = {"template": "قالب", "templates": "قوالب", "folder": "مجلد", "folders": "مجلدات",
                   "document": "وثيقة", "documents": "وثائق"}
_PREFIXES = ("", "و", "ف", "ب", "ل", "ال", "وال", "فال", "بال", "لل", "وب", "ول", "وبال")
_SUFFIXES = ("", "ها", "ه", "هم")
_EDGE = re.compile(r"^([\"'«“(]*)(.*?)([\"'»”)?؟!.,،:]*)$")
# Arabic keyboard rows: a wrong letter is a slip only next to the intended key or for a letter of the same sound,
# so «الرفع» is no slip of «الرفض».
_ROWS = ("ضصثقفغعهخحجد", "شسيبلاتنمكط", "ءؤرىةوزظ")
_SOUNDS = ("اأإآءؤئ", "يىئ", "هة", "ذز", "ظضد", "سصث", "تط", "قك", "هح")


_PLACES = {c: (r, i) for r, row in enumerate(_ROWS) for i, c in enumerate(row)}


def _substitution(x, y):
    """Cost of writing y for x: half for the same sound («مأرشفة» for «مؤرشفة» beats a missing letter), one for a
    neighbouring key or any English letter, two otherwise (a delete plus an insert)."""
    if x == y:
        return 0
    if any(x in group and y in group for group in _SOUNDS):
        return 0.5
    if x.isascii() or y.isascii():
        return 1
    if x in _PLACES and y in _PLACES:
        (r1, i1), (r2, i2) = _PLACES[x], _PLACES[y]
        if abs(r1 - r2) <= 1 and abs(i1 - i2) <= 1:
            return 1
    return 2


def _osa(a, b, limit):
    """Edit distance with adjacent swaps (optimal string alignment), or limit + 1 once it is exceeded."""
    if abs(len(a) - len(b)) > limit:
        return limit + 1
    previous2, previous = None, list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        current = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + _substitution(a[i - 1], b[j - 1]))
            if i > 1 and j > 1 and a[i - 1] == b[j - 2] and a[i - 2] == b[j - 1]:
                current[j] = min(current[j], previous2[j - 2] + 1)
        if min(current) > limit:
            return limit + 1
        previous2, previous = previous, current
    return previous[-1]


def _swapped(a, b):
    return len(a) == len(b) and sum(x != y for x, y in zip(a, b)) == 2 and sorted(a) == sorted(b) and any(
        a[i] == b[i + 1] and a[i + 1] == b[i] for i in range(len(a) - 1))


def _distance(word, target):
    """Slips between the words: one allowed for 5–8 letters, two for longer ones, only a swap for 4 letters;
    None when too far."""
    if len(word) < 4 or len(target) < 4:
        return None
    if len(word) <= 4 or (word.isascii() and len(word) <= 5):
        return 1 if _swapped(word, target) else None
    limit = 2 if len(word) >= 9 else 1
    distance = _osa(word, target, limit)
    return distance if distance <= limit else None


_AR = str.maketrans({"ب": "b", "ت": "t", "ث": "s", "ج": "g", "ح": "h", "خ": "k", "د": "d", "ذ": "z", "ر": "r", "ز": "s",
                     "س": "s", "ش": "s", "ص": "s", "ض": "d", "ط": "t", "ظ": "z", "غ": "g", "ف": "f", "ق": "k", "ك": "k",
                     "ل": "l", "م": "m", "ن": "n", "ه": "h", "ا": "", "و": "", "ي": "", "ى": "", "ع": "", "ء": "", "ئ": "",
                     "ؤ": "", "ة": ""})


def _consonants(word):
    """Consonant skeleton shared by «تمبليت» and «template»: tmblt."""
    if word.isascii():
        word = re.sub(r"[aeiouyw]", "", word.replace("ph", "f").replace("ck", "k"))
        word = word.translate(str.maketrans("cqpvjxz", "kkbfgks"))
    else:
        word = word.translate(_AR)
    return re.sub(r"(.)\1+", r"\1", word)


_SKELETONS = {_consonants(english): arabic for english, arabic in _TRANSLITERATED.items()}


def _splits(word):
    for prefix in _PREFIXES:
        for suffix in _SUFFIXES:
            if word.startswith(prefix) and word.endswith(suffix) and len(word) - len(prefix) - len(suffix) >= 3:
                yield prefix, word[len(prefix):len(word) - len(suffix)], suffix


def _words(text):
    return {w for w in re.split(r"[^\w]+", normalize_arabic(text)) if w}


def _vocabulary(catalog, glossary):
    known = set(_KEEP) | {normalize_arabic(w) for w in _DOMAIN}
    for name in catalog.get("templates") or []:
        known |= _words(name)
    for tag in catalog.get("tags") or []:
        known |= _words(tag.get("name") or "")
    for field in catalog.get("fields") or []:
        known |= _words(field.get("name") or "")
        for value in field.get("listValues") or []:
            known |= _words(value)
    domain = {normalize_arabic(w): w for w in _DOMAIN}
    for term in glossary or []:
        for word in term.get("words") or []:
            if " " not in word.strip():
                domain[normalize_arabic(word)] = word
                known.add(normalize_arabic(word))
    return known, domain


def _correct_word(token, known, domain):
    lead, core, trail = _EDGE.match(token).groups()
    word = normalize_arabic(core)
    if not word or any(c.isdigit() for c in word) or word in known or any(stem in known for _, stem, _ in _splits(word)):
        return None
    found = []
    for prefix, stem, suffix in _splits(word):
        for key, display in domain.items():
            distance = _distance(stem, key)
            if distance is not None:
                found.append((distance, prefix + display + suffix))
    if not found:
        # Only then an English word in Arabic letters: «التمبلييت» → «القالب».
        for prefix, stem, suffix in _splits(word):
            skeleton = _consonants(stem.removesuffix("ات"))
            if not stem.isascii() and len(skeleton) >= 4 and skeleton in _SKELETONS:
                found.append((1, prefix + _SKELETONS[skeleton] + suffix))
    if not found:
        return None
    best = min(distance for distance, _ in found)
    readings = {normalize_arabic(text): text for distance, text in found if distance == best}
    if len(readings) != 1:
        return None
    return lead + next(iter(readings.values())) + trail


def _compact(text):
    return re.sub(r"[^\w]|_", "", normalize_arabic(text))


def _digits(text):
    return re.sub(r"\D", "", text)


def _allowed(length, cued):
    if cued:
        return 0 if length < 5 else 1 if length < 10 else 2 if length < 20 else 3
    return 0 if length < 9 else 1 if length < 16 else 2 if length < 24 else 3


def _names(catalog):
    """(name, cue regex or None, needs a cue): templates and tags after their cue, multi-word List choices anywhere."""
    for name in catalog.get("templates") or []:
        yield name, _TEMPLATE_CUE
    for tag in catalog.get("tags") or []:
        yield tag["name"], _TAG_CUE
    for field in catalog.get("fields") or []:
        for value in field.get("listValues") or []:
            if len(value.split()) >= 2:
                yield value, None


def _correct_names(tokens, catalog):
    """Replace a misspelled catalog name by the name: «قالب التعمم» → «قالب التعميم»."""
    names = [(name, cue, _compact(name)) for name, cue in _names(catalog)]
    exact = {compact for _, _, compact in names}
    found = []
    for start in range(len(tokens)):
        before = normalize_arabic(" ".join(tokens[:start]))
        for name, cue, compact in names:
            size = len(name.split())
            cued = cue is not None and re.search(cue + _LINK + r"$", before + " ") is not None
            if (cue is not None and not cued) or (cue is None and size < 2):
                continue
            limit = _allowed(len(compact), cued)
            if limit == 0:
                continue
            for count in range(max(1, size - 1), size + 2):
                window = tokens[start:start + count]
                if len(window) < count:
                    break
                text = _compact(" ".join(window))
                if text in exact or _digits(text) != _digits(compact):
                    continue
                distance = _osa(text, compact, limit)
                if 0 < distance <= limit:
                    found.append((distance, start, count, name))
    chosen, used = [], set()
    for distance, start, count, name in sorted(found, key=lambda f: (f[0], -len(f[3]))):
        span = set(range(start, start + count))
        rivals = {_compact(f[3]) for f in found if f[0] == distance and f[1] == start and f[2] == count}
        if span & used or len(rivals) > 1:
            continue
        used |= span
        chosen.append((start, count, name))
    corrections = []
    for start, count, name in sorted(chosen, reverse=True):
        trail = re.search(r"[?؟!.,،:]*$", tokens[start + count - 1]).group(0)
        corrections.append((" ".join(tokens[start:start + count]), name))
        tokens[start:start + count] = [name + trail]
    return corrections[::-1]


def correct_spelling(question, catalog, glossary=None):
    """(question with misspellings corrected, [(as written, corrected)]); the question unchanged when nothing is."""
    known, domain = _vocabulary(catalog, glossary)
    tokens = re.findall(r"\S+", question or "")
    corrections = []
    for index, token in enumerate(tokens):
        replacement = _correct_word(token, known, domain)
        if replacement and replacement != token:
            corrections.append((token, replacement))
            tokens[index] = replacement
    corrections += _correct_names(tokens, catalog)
    return (" ".join(tokens), corrections) if corrections else (question, [])
