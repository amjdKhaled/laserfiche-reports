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
        return 'لم أتمكن من تحديد البحث بثقة. هذه أقرب أسماء موجودة في المستودع؛ هل تقصد أحدها؟\n' + options + '\nاذكر الخيار المقصود والشرط المطلوب، وسأبحث بناءً عليه.'
    return 'لم أتمكن من تحديد البحث بثقة. هل تقصد البحث في اسم الوثيقة، أم قيمة أحد الحقول، أم محتوى الصفحات؟ وضح المعلومة والشرط المطلوبين.'
