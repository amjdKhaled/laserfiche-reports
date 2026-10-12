"""Generated contract QA. Does NOT measure natural-language/model accuracy.

Run: python qa_contracts.py --count 100000 --output qa-contracts.json
Each case exercises production parsing/validation or explicit-menu resolution.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import random
import time

from clarification_options import selected_option, catalog_selection
from server import planner_request, validate_plan_schema, validate_selected_catalog_option

FAMILIES = ('calendar_period', 'numeric_comparison', 'literal_injection', 'unknown_field',
            'year_is_not_date_literal', 'relative_on_number', 'ordered_text', 'nested_logic',
            'conflicting_period_bounds', 'invalid_calendar', 'numeric_aggregate', 'text_aggregate',
            'text_calendar_bucket', 'missing_metric_field', 'invalid_having_index', 'menu_roundtrip',
            'wrong_selected_field', 'mixed_group_leaf', 'fractional_integer', 'invalid_date_text')


def case(index, seed):
    rng = random.Random(seed + index)
    family = FAMILIES[index % len(FAMILIES)]
    names = [label + ' ' + str(index) for label in ('أجل الحفظ', 'التكلفة', 'الإدارة', 'عدد المعاملات')]
    d, n, s, integer = names
    catalog = {'fields': [{'name': name, 'fieldType': kind} for name, kind in
        zip(names, ('Date', 'Number', 'String', rng.choice(('Long', 'Short', 'Integer', 'LongInteger'))))]}
    year, value = rng.randint(1900, 2300), str(rng.randint(-100000, 100000))
    node = {'field': d, 'operator': 'through_period', 'period': {'year': year}}
    draft = {'reports': [{'operation': 'search', 'title': 'تقرير تحقق',
                         'selection': {'requiresFilter': True, 'filters': node}}]}
    valid = family in ('calendar_period', 'numeric_comparison', 'nested_logic', 'numeric_aggregate', 'menu_roundtrip')
    selection = None
    if family == 'calendar_period':
        node['operator'] = rng.choice(('through_period', 'after_period', 'in_period', 'before_period', 'from_period'))
    elif family == 'numeric_comparison':
        node = {'field': n, 'operator': rng.choice(('equals', 'greater_than', 'less_or_equal')), 'value': value + '.25'}
    elif family == 'literal_injection':
        node = {'field': s, 'operator': 'equals', 'value': 'قسم' + rng.choice(('"', '{', '}', '[', ']', '*', '?', '\n')) + value}
    elif family == 'unknown_field':
        node['field'] = 'حقل محذوف ' + str(index)
    elif family == 'year_is_not_date_literal':
        node = {'field': d, 'operator': 'greater_than', 'value': str(year)}
    elif family == 'relative_on_number':
        node = {'field': n, 'operator': 'less_than', 'relative': {'unit': 'month', 'offset': -rng.randint(1, 12)}}
    elif family == 'ordered_text':
        node = {'field': s, 'operator': 'less_than', 'value': value}
    elif family == 'nested_logic':
        node = {'logic': 'and', 'conditions': [node, {'logic': 'or', 'conditions': [
            {'field': n, 'operator': 'greater_or_equal', 'value': value},
            {'field': s, 'operator': 'not_equals', 'value': 'قسم ' + value}]}]}
    elif family == 'conflicting_period_bounds':
        node['value'] = f'{year}-01-01'
    elif family == 'invalid_calendar':
        node['period'].update(month=2, day=30)
    elif family == 'wrong_selected_field':
        selection = {'kind': 'field', 'name': n}
    elif family == 'mixed_group_leaf':
        node = {'logic': 'and', 'conditions': [node], 'field': ''}
    elif family == 'fractional_integer':
        node = {'field': integer, 'operator': 'equals', 'value': value + '.5'}
    elif family == 'invalid_date_text':
        node = {'field': d, 'operator': 'equals', 'value': f'{year}-13-{rng.randint(10, 28)}'}
    draft['reports'][0]['selection']['filters'] = node
    if family in ('numeric_aggregate', 'text_aggregate', 'text_calendar_bucket', 'missing_metric_field', 'invalid_having_index'):
        report = draft['reports'][0]
        report.update(operation='group', groupFields=[{'field': s}], metrics=[{'function': 'sum', 'field': n}])
        if family == 'text_aggregate': report['metrics'][0]['field'] = s
        if family == 'text_calendar_bucket': report['groupFields'][0]['bucket'] = 'month'
        if family == 'missing_metric_field': report['metrics'][0].pop('field')
        if family == 'invalid_having_index': report['having'] = {'metric': 1, 'operator': 'greater_than', 'value': 10}
    return family, catalog, draft, valid, selection


def run(count=100000, seed=20261012):
    started = time.monotonic()
    passed, failed, examples, unique = Counter(), Counter(), [], set()
    for index in range(count):
        family, catalog, draft, expected, selection = case(index, seed)
        error = None
        if family == 'menu_roundtrip':
            name = catalog['fields'][0]['name']
            prompt = f'1. حقل «{name}»\n2. حقل «{catalog["fields"][1]["name"]}»'
            reply = ('1', '١', '۱', name)[index // len(FAMILIES) % 4]
            actual = catalog_selection(selected_option(reply, {'prompt': prompt}), catalog)
            accepted = actual is not None and actual['name'] == name
        else:
            try:
                request = planner_request(json.dumps(draft, ensure_ascii=False), 'طلب اختبار')
                validate_plan_schema(request, catalog)
                validate_selected_catalog_option(request, selection)
                accepted = True
            except (ValueError, TypeError) as exception:
                accepted, error = False, str(exception)[:300]
        # Unique field names and values make every tested input distinct.
        unique.add(catalog['fields'][0]['name'])
        if accepted == expected:
            passed[family] += 1
        else:
            failed[family] += 1
            if len(examples) < 20:
                examples.append({'index': index, 'family': family, 'expectedAccepted': expected,
                                 'actualAccepted': accepted, 'error': error, 'draft': draft})
    return {'suite': 'generated_contract_validation', 'modelCalls': 0,
            'naturalLanguageAccuracyMeasured': False, 'seed': seed, 'cases': count,
            'uniqueInputs': len(unique), 'passed': sum(passed.values()), 'failed': sum(failed.values()),
            'durationSeconds': round(time.monotonic() - started, 3),
            'families': {name: {'passed': passed[name], 'failed': failed[name]} for name in FAMILIES},
            'failureExamples': examples}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--count', type=int, default=100000)
    parser.add_argument('--seed', type=int, default=20261012)
    parser.add_argument('--output', default='qa-contracts.json')
    args = parser.parse_args()
    if args.count < 1: parser.error('count must be positive')
    result = run(args.count, args.seed)
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: result[k] for k in ('suite', 'cases', 'uniqueInputs', 'passed', 'failed', 'modelCalls')}, ensure_ascii=False))
    raise SystemExit(bool(result['failed']))
