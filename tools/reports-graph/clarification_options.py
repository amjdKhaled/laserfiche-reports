"""Rank real catalog labels for clarification only; never change a query."""
import re
import unicodedata
from difflib import SequenceMatcher


def key(text):
    text = unicodedata.normalize('NFKC', str(text)).lower()
    text = ''.join(c for c in text if not unicodedata.combining(c) and c != 'ـ')
    return re.sub(r'[^\w\s]', ' ', text.translate(str.maketrans('أإآىة', 'ااايه')))


def suggestions(question, catalog, limit=3):
    words = key(question).split()
    ranked = []
    labels = [('حقل', f.get('name', '')) for f in catalog.get('fields', [])]
    labels += [('قالب', n) for n in catalog.get('templates', [])]
    for kind, label in labels:
        tokens = [t for t in key(label).split() if len(t) > 1]
        if not tokens or not words:
            continue
        matches = [max(SequenceMatcher(None, token, word).ratio() for word in words) for token in tokens]
        score = sum(v if v >= .7 else 0 for v in matches) / len(tokens)
        if score >= .65:
            ranked.append((score, len(tokens), kind, label))
    ranked.sort(key=lambda x: (-x[0], -x[1], x[3]))
    return list(dict.fromkeys((kind, label) for _, _, kind, label in ranked))[:limit]


def clarification(question, catalog):
    choices = suggestions(question, catalog)
    if choices:
        options = '\n'.join(f'{i}. {kind} «{label}»' for i, (kind, label) in enumerate(choices, 1))
        return 'أحتاج توضيح المقصود حتى أبحث بدقة. هذه أقرب أسماء موجودة في المستودع؛ هل تقصد أحدها؟\n' + options + '\nاكتب رقم الخيار أو اسمه، وسأستخدمه مع شروط سؤالك السابق.'
    return 'أحتاج توضيح المقصود حتى أبحث بدقة. هل تقصد البحث في اسم الوثيقة، أم قيمة أحد الحقول، أم محتوى الصفحات؟ وضح المعلومة والشرط المطلوبين.'


def selected_option(reply, context):
    """Resolve only an explicit menu number, never infer a search predicate."""
    if not context:
        return None
    number = unicodedata.normalize('NFKC', reply).strip()
    matches = menu_options(context.get('prompt', ''))
    if not number.isdecimal() or len(number) > 3:
        # Exact displayed names are also UI selections; no natural-language
        # question matching or inference of predicates takes place here.
        choices = [label for _, label in matches if number in (label, option_name(label))]
    else:
        choices = [label for index, label in matches if int(index) == int(number)]
    return choices[0] if len(choices) == 1 else None


def menu_options(prompt):
    return re.findall(r'^\s*([0-9٠-٩۰-۹]+)[.)、-]\s+(.+?)\s*$', prompt, re.MULTILINE)


def option_name(label):
    for prefix in ('حقل «', 'قالب «'):
        if label.startswith(prefix) and label.endswith('»'):
            return label[len(prefix):-1]
    return label


def catalog_selection(label, catalog):
    """A menu is not authority: resolve its exact name against the live schema."""
    if label is None:
        return None
    name = option_name(label)
    candidates = []
    if not label.startswith('قالب «'):
        candidates += [{'kind': 'field', 'name': f['name'], 'fieldType': f.get('fieldType', 'String')}
                       for f in catalog.get('fields', []) if f['name'] == name]
    if not label.startswith('حقل «'):
        candidates += [{'kind': 'template', 'name': t} for t in catalog.get('templates', []) if t == name]
    return candidates[0] if len(candidates) == 1 else None


def repeats_clarification(prompt, context):
    if not prompt or not context:
        return False
    previous = context.get('prompt', '')
    if ' '.join(prompt.split()) == ' '.join(previous.split()):
        return True
    old = {label for _, label in menu_options(previous)}
    new = {label for _, label in menu_options(prompt)}
    return bool(old) and old == new
