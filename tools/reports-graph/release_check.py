"""Small real-model release gate, including every question advertised in the UI.

No fake responses, repository writes, or claims of live repository correctness.
"""
import argparse
import json
from pathlib import Path
from urllib.request import Request, ProxyHandler, build_opener

CATALOG = {
    'fields': [{'name': n, 'fieldType': t} for n, t in [
        ('إجراء الوثيقة', 'List'), ('اجراء الوثيقة', 'List'), ('إجراء  الوثيقة', 'List'),
        ('نشط لغاية في المركز - م', 'Date'), ('نشط لغاية في المركز - هـ', 'String'),
        ('حفظ في المركز لغاية - م', 'Date'), ('آخر موعد للرد', 'Date'), ('الإدارة', 'String')]],
    'templates': ['الارشفة المباشرة للوثائق'], 'tags': [], 'tagStatus': 'complete',
    'entryProperties': ['entryId', 'name', 'created', 'modified', 'template', 'creator', 'pageCount']}

CASES = [
    ('اعرض الوثائق التي أنشئت هذا الشهر', 'documents', 'created', 'month'),
    ('كم عدد الوثائق في المستودع؟', 'count', None, None),
    ('ما الحقول والقوالب المتاحة للبحث؟', 'schema', None, None),
    ('ماهي الوثائق اللتي تحتوي على حقل اجراء الوثيقة؟', 'documents', 'إجراء الوثيقة', 'is_not_empty'),
    ('كم وثيقة نشطة في المركز لغاية م 2036 و ما اقل؟', 'count', 'نشط لغاية في المركز - م', 'through_period'),
    ('اعرض اللي انتهى آخر موعد للرد عليها قبل اليوم', 'documents', 'آخر موعد للرد', 'before_today'),
]


def leaves(node):
    if not node:
        return []
    if node.get('conditions'):
        return [leaf for child in node['conditions'] for leaf in leaves(child)]
    return [node]


def verify(result, kind, field, condition):
    plans = result['reports']
    if len(plans) != 1 or plans[0]['resultType'] != kind or plans[0]['operation'] == 'clarify':
        return False
    plan = plans[0]
    if any(plan.get(k) for k in ('folderId', 'folderName', 'entryIds', 'template', 'name', 'field', 'from', 'to', 'groupFields', 'metrics', 'having')):
        return False
    if plan.get('content') or not plan.get('allResults', True) or plan.get('limit', 50) != 50:
        return False
    if field is None:
        return not plan.get('requiresFilter') and not plan.get('filters')
    from clarification_options import key
    from evaluate_planner import normalize_plan
    from qa_language import normalized_filter
    expected = {
        'month': {'field': field, 'operator': 'in_period', 'period': {'year': 2026, 'month': 10}},
        'through_period': {'field': field, 'operator': 'through_period', 'period': {'year': 2036}},
        'before_today': {'field': field, 'operator': 'less_than', 'value': '2026-10-12'},
        'is_not_empty': {'field': field, 'operator': 'is_not_empty'}}[condition]
    def canonical(node):
        node = dict(node)
        if node.get('field'):
            node['field'] = ''.join(key(node['field']).split())
        if node.get('conditions'):
            node['conditions'] = [canonical(child) for child in node['conditions']]
        return node
    actual = normalize_plan({'filters': plan.get('filters')}, '2026-10-12').get('filters')
    if not actual:
        return False
    target = normalized_filter(canonical(expected))
    # Equivalent spelling variants may be searched with OR, but no unrelated
    # restriction or alternative is allowed to make this release gate pass.
    if condition == 'is_not_empty' and actual.get('logic') == 'or':
        return all(normalized_filter(canonical(p)) == target for p in leaves(actual))
    return normalized_filter(canonical(actual)) == target



def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://127.0.0.1:8766')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    results = []
    opener = build_opener(ProxyHandler({}))
    model = None
    try:
        with opener.open(args.base_url.rstrip('/') + '/health', timeout=10) as response:
            health = json.load(response)
        model = health.get('model')
        if health.get('status') != 'ready' or model != 'gpt-oss:20b':
            raise RuntimeError('Ready graph with gpt-oss:20b is required: ' + str(health))
        for question, kind, field, condition in CASES:
            payload = {'question': question, 'catalog': CATALOG, 'today': '2026-10-12', 'timezone': 'Asia/Riyadh', 'history': []}
            request = Request(args.base_url.rstrip('/') + '/route', data=json.dumps(payload, ensure_ascii=False).encode(), headers={'Content-Type': 'application/json'})
            with opener.open(request, timeout=None) as response:
                result = json.load(response)
            passed = verify(result, kind, field, condition)
            results.append({'question': question, 'passed': passed, 'plan': result})
            print(('PASS' if passed else 'FAIL') + ': ' + question, flush=True)
    except Exception as error:
        results.append({'passed': False, 'error': type(error).__name__ + ': ' + str(error)[:500]})
    output = {'suite': 'real-model-release-check', 'model': model, 'repositoryQueries': 0,
              'passed': sum(r['passed'] for r in results), 'totalRequired': len(CASES), 'cases': results}
    path = Path(args.output)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding='utf-8')
    raise SystemExit(0 if len(results) == len(CASES) and all(r['passed'] for r in results) else 1)


if __name__ == '__main__':
    main()
