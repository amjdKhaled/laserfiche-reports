"""Real-model language QA against a synthetic live catalog; no repository writes.

Calls the RUNNING graph /route endpoint. Never substitutes a fake model.
This validates query plans, not the contents of a real Laserfiche repository.
100,000 cases are compositional variants of ten intent families, not 100,000
independently authored business requirements. Supports interruption/resume.
"""
import argparse
from collections import Counter
from datetime import date, timedelta
import hashlib
import json
from pathlib import Path
import time
from urllib.parse import urlsplit
from urllib.request import build_opener, ProxyHandler, Request

VERSION = 'arabic-plans-1'
PREFIXES = ('اعرض لي', 'ورني', 'طلع لي', 'أبغى أشوف', 'ابي اشوف', 'جيب لي', 'ممكن تعرض لي',
            'لو سمحت ورني', 'عطني', 'أعطني', 'ابي تقرير عن', 'ابغى تقرير عن', 'وريني',
            'ممكن تطلع لي', 'أرغب بعرض', 'أريد قائمة', 'اعطيني', 'لو تكرمت اعرض', 'هات لي', 'أحتاج أشوف')
FIELDS = [{'name': 'نشط لغاية في المركز - م', 'fieldType': 'Date'},
          {'name': 'نشط لغاية في المركز - هـ', 'fieldType': 'String'},
          {'name': 'إجراء الوثيقة', 'fieldType': 'String'}, {'name': 'الإدارة', 'fieldType': 'String'},
          {'name': 'التكلفة', 'fieldType': 'Number'}]
CATALOG = {'fields': FIELDS, 'templates': ['عقود', 'مراسلات'], 'tags': [], 'tagStatus': 'complete',
           'entryProperties': ['entryId', 'name', 'created', 'modified', 'template', 'creator', 'pageCount']}


def leaf(field, operator, value):
    return {'field': field, 'operator': operator, 'value': value}


def scenario(index):
    family, variant, value = index % 10, (index // 10) % len(PREFIXES), index // 200
    prefix, year = PREFIXES[variant], 2000 + value
    dept = 'الإدارة ' + str(value)
    state = 'تحت الإجراء'
    df = leaf('الإدارة', 'equals', dept)
    spec = {'count': False, 'operation': 'search'}
    history = []
    if family in (0, 1):
        condition = f'بعد نهاية سنة {year} ميلادي' if family == 0 else f'حتى نهاية سنة {year} ميلادي وما قبلها'
        question = f'{prefix} الوثائق اللي حقل «نشط لغاية في المركز - م» فيها {condition}'
        spec['filter'] = leaf(FIELDS[0]['name'], 'greater_or_equal' if family == 0 else 'less_than', f'{year+1}-01-01')
    elif family == 2:
        question = f'{prefix} الوثائق اللي التكلفة فيها {value} أو أقل'
        spec['filter'] = leaf('التكلفة', 'less_or_equal', str(value))
    elif family == 3:
        spec.update(count=True, filter={'logic': 'and', 'conditions': [leaf('إجراء الوثيقة', 'equals', state), df]})
        # Ensure phrasing variants remain distinguishable without changing meaning.
        question = PREFIXES[variant] + ' عدد الوثائق اللي إجراء الوثيقة فيها تحت الإجراء والإدارة تساوي «' + dept + '»'
    elif family == 4:
        question = f'{prefix} وثائق الإدارة «{dept}» اللي ما تغيرت من أكثر من شهر'
        spec['filter'] = {'logic': 'and', 'conditions': [df, leaf('modified', 'less_than', '2026-09-12')]}
    elif family == 5:
        question = f'{prefix} الوثائق اللي إجراء الوثيقة فيها ما يساوي مرفوض والإدارة «{dept}»'
        spec['filter'] = {'logic': 'and', 'conditions': [df, leaf('إجراء الوثيقة', 'not_equals', 'مرفوض')]}
    elif family == 6:
        question = f'{prefix} الوثائق المكتملة أو تحت الإجراء في حقل إجراء الوثيقة، بشرط الإدارة «{dept}»'
        spec['filter'] = {'logic': 'and', 'conditions': [df, {'logic': 'or', 'conditions': [
            leaf('إجراء الوثيقة', 'equals', 'مكتملة'), leaf('إجراء الوثيقة', 'equals', state)]}]}
    elif family == 7:
        original = f'{prefix} الوثائق اللي نشط لغاية في المركز فيها بعد سنة {year} ميلادي'
        history = [{'role': 'user', 'text': original}, {'role': 'assistant', 'kind': 'clarification',
            'clarificationQuestion': original,
            'text': 'أي حقل تقصد؟\n1. حقل «نشط لغاية في المركز - م»\n2. حقل «نشط لغاية في المركز - هـ»'}]
        question = ('1', '١', '۱', FIELDS[0]['name'])[variant % 4]
        spec['filter'] = leaf(FIELDS[0]['name'], 'greater_or_equal', f'{year+1}-01-01')
    elif family == 8:
        original = f'{prefix} الوثائق اللي إجراء الوثيقة فيها تحت الإجراء والإدارة «{dept}»'
        history = [{'role': 'user', 'text': original}, {'role': 'assistant', 'kind': 'answer',
                   'text': 'عُرض جدول الوثائق المطابقة لإجراء الوثيقة تحت الإجراء والإدارة «' + dept + '».'}]
        question = ('طيب كم عددها؟', 'كم وحدة؟', 'ابي عددها بس', 'احسبها لي')[variant % 4]
        spec.update(count=True, filter={'logic': 'and', 'conditions': [df, leaf('إجراء الوثيقة', 'equals', state)]})
    else:
        question = f'{prefix} عدد الوثائق حسب الإدارة اللي تكلفتها أكبر من {value}'
        spec.update(operation='group', filter=leaf('التكلفة', 'greater_than', str(value)), group='الإدارة')
    return {'id': index, 'family': family, 'payload': {'question': question, 'history': history,
        'catalog': CATALOG, 'today': '2026-10-12'}, 'expected': spec}


def normalized_filter(node):
    """Canonicalize equivalent supported bounds for the oracle, not for routing."""
    if not node: return None
    if node.get('conditions') is not None:
        children = [normalized_filter(c) for c in node['conditions']]
        flat = []
        for child in children:
            if child and child[0] == node['logic']: flat.extend(child[1])
            else: flat.append(child)
        return flat[0] if len(flat) == 1 else (node['logic'], tuple(sorted(flat, key=repr)))
    field, op, value = node.get('field'), node.get('operator'), node.get('value')
    if node.get('period'):
        p = node['period']; start = date(p['year'], p.get('month') or 1, p.get('day') or 1)
        end = (start + timedelta(days=1) if p.get('day') else
               date(start.year + (start.month == 12), start.month % 12 + 1, 1) if p.get('month') else date(start.year+1, 1, 1))
        bounds = {'through_period': ('less_than', end), 'after_period': ('greater_or_equal', end),
                  'before_period': ('less_than', start), 'from_period': ('greater_or_equal', start)}
        if op in bounds: op, value = bounds[op]; value = value.isoformat()
        elif op == 'in_period': return normalized_filter({'logic': 'and', 'conditions': [
            leaf(field, 'greater_or_equal', start.isoformat()), leaf(field, 'less_than', end.isoformat())]})
    if node.get('relative'):
        r = node['relative']
        if r.get('unit') == 'month' and r.get('offset', 0) == -1 and r.get('boundary') == 'rolling': value = '2026-09-12'
        else: value = ('unsupported-relative-in-this-oracle', repr(r))
    op = {'date_before': 'less_than', 'date_after': 'greater_than'}.get(op, op)
    return ('leaf', field, op, str(value), node.get('upper'), node.get('tag'))


def verify(result, expected):
    reports = result.get('reports', [])
    if len(reports) != 1: return 'wrong_report_count'
    r = reports[0]
    if r.get('operation') == 'clarify': return 'unexpected_clarification'
    if r.get('operation') != expected['operation']: return 'wrong_operation'
    if bool(r.get('countOnly')) != expected['count'] or r.get('content'): return 'wrong_output'
    if any(r.get(k) for k in ('folderId', 'folderName', 'entryIds', 'template', 'name', 'field', 'from', 'to')):
        return 'unexpected_scope_or_selector'
    if normalized_filter(r.get('filters')) != normalized_filter(expected['filter']): return 'wrong_filter'
    if expected.get('group'):
        if r.get('groupFields') != [{'field': expected['group'], 'bucket': None}]: return 'wrong_group'
        if r.get('metrics') not in ([], [{'function': 'count', 'field': None}]): return 'wrong_metric'
        if r.get('having') or r.get('rollup'): return 'unexpected_aggregate_constraint'
    elif r.get('sort') or r.get('sortField') or r.get('page', 1) != 1 or not r.get('allResults', True):
        return 'unexpected_paging_or_sort'
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--count', type=int, default=100000)
    parser.add_argument('--url', default='http://127.0.0.1:5050')
    parser.add_argument('--output', default='qa-language.jsonl')
    parser.add_argument('--resume', action='store_true')
    parser.add_argument('--timeout', type=float, default=0, help='0: unlimited model response wait')
    args = parser.parse_args()
    url = urlsplit(args.url)
    if url.scheme != 'http' or url.hostname not in ('127.0.0.1', 'localhost', '::1') or url.username or url.password:
        parser.error('Use a local HTTP graph endpoint without credentials')
    if not 1 <= args.count <= 100000 or args.timeout < 0: parser.error('count must be 1..100000; timeout nonnegative')
    target = Path(args.output); target.parent.mkdir(parents=True, exist_ok=True)
    summary_path = target.with_suffix('.summary.json')
    summary = {'suite': 'real_model_synthetic_catalog', 'corpusVersion': VERSION, 'requested': args.count,
               'executed': 0, 'passed': 0, 'failed': 0, 'routeRequests': 0, 'repositoryQueries': 0}
    def save(): summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    opener = build_opener(ProxyHandler({}))
    try:
        with opener.open(args.url.rstrip('/') + '/health', timeout=10) as r: health = json.load(r)
        if not health.get('model') or health.get('status') != 'ready':
            raise ValueError('Expected a ready graph with an identified model')
    except Exception as error:
        summary.update(status='blocked', reason=str(error)); save()
        print('BLOCKED: actual Qwen graph is unavailable; no language tests were executed.'); return 2
    identity = hashlib.sha256(json.dumps([VERSION, health], sort_keys=True).encode()).hexdigest()
    summary.update(status='running', health=health)
    seen = set(); failures = Counter()
    if target.exists():
        if not args.resume: parser.error('Output exists; use --resume or a different path')
        for line in target.read_text(encoding='utf-8').splitlines():
            old = json.loads(line)
            if old['identity'] != identity: parser.error('Model/runtime/corpus changed; use a new output file')
            if old['id'] in seen or not 0 <= old['id'] < args.count: parser.error('Incompatible resume case IDs')
            seen.add(old['id']); summary['executed'] += 1; summary['routeRequests'] += 1
            summary['failed' if old['error'] else 'passed'] += 1
            if old['error']: failures[old['error']] += 1
    save()
    try:
        with target.open('a', encoding='utf-8') as stream:
            for index in range(args.count):
                if index in seen: continue
                item = scenario(index); started = time.monotonic(); result = None
                summary['routeRequests'] += 1
                try:
                    request = Request(args.url.rstrip('/') + '/route', json.dumps(item['payload'], ensure_ascii=False).encode(),
                                      headers={'Content-Type': 'application/json', 'X-Request-ID': 'language-qa-' + str(index)})
                    with opener.open(request, timeout=args.timeout or None) as r: result = json.load(r)
                    error = verify(result, item['expected'])
                except Exception as exception: error = type(exception).__name__ + ': ' + str(exception)[:200]
                stream.write(json.dumps({'id': index, 'identity': identity, 'family': item['family'],
                    'question': item['payload']['question'], 'history': item['payload']['history'],
                    'expected': item['expected'], 'result': result, 'error': error,
                    'seconds': round(time.monotonic()-started, 3)}, ensure_ascii=False) + '\n'); stream.flush()
                summary['executed'] += 1; summary['failed' if error else 'passed'] += 1
                if error: failures[error] += 1
                summary['failureKinds'] = dict(failures); save()
                print(f"{summary['executed']}/{args.count} passed={summary['passed']} failed={summary['failed']}", flush=True)
    except KeyboardInterrupt:
        summary['status'] = 'interrupted'; save(); return 130
    summary['status'] = 'completed'; save(); return int(summary['failed'] > 0)


if __name__ == '__main__':
    raise SystemExit(main())
