import json
import unittest
from agent_cases import CASES
from evaluate_planner import includes
from server import plan_reports, ReportRequest, validate_plan_schema, build_graph
from test_graph import FakeModel

class AgentContractTests(unittest.TestCase):
    def test_held_out_cases_exercise_contract_but_fake_model_is_not_language_evaluation(self):
        self.assertGreaterEqual(len(CASES), 30)
        for case in CASES:
            with self.subTest(question=case['question']):
                plan = {'operation': 'search', 'title': 'تقرير', 'question': case['question'], 'limit': 50, **case['expect']}
                if plan.get('content') and 'filters' not in plan:
                    plan['operation'] = 'content'
                if plan.get('filters', {}).get('operator') == 'between':
                    plan['filters'] = {**plan['filters'], 'upper': '900'}
                model = FakeModel([json.dumps({'reports': [plan]}, ensure_ascii=False)])
                result = plan_reports(model, {k: v for k, v in case.items() if k != 'expect'})
                self.assertTrue(includes(result['reports'][0], case['expect']))
                self.assertEqual(len(model.calls), 1)
                sent = json.loads(model.calls[0][1].content)
                self.assertEqual(sent['question'], case['question'])
                self.assertEqual(sent['history'], case['history'])
                self.assertNotIn('expect', sent)

    def test_unknown_field_in_nested_filter_is_replanned_not_executed(self):
        payload = {'question': 'جديد تماما', 'catalog': {'fields': [{'name': 'أجل الإنجاز', 'fieldType': 'Date'}]}}
        bad = {'reports': [{'operation': 'search', 'title': 'تقرير', 'question': 'جديد تماما', 'limit': 20,
                          'filters': {'field': 'موعد وهمي', 'operator': 'less_than', 'value': '2026-10-07'}}]}
        good = json.loads(json.dumps(bad)); good['reports'][0]['filters']['field'] = 'أجل الإنجاز'
        model = FakeModel([json.dumps(bad), json.dumps(good)])
        self.assertEqual(plan_reports(model, payload)['reports'][0]['filters']['field'], 'أجل الإنجاز')
        self.assertEqual(len(model.calls), 2)
        with self.assertRaises(ValueError):
            plan_reports(FakeModel([json.dumps(bad), json.dumps(bad)]), payload)

    def test_aggregation_and_sort_fields_are_schema_validated(self):
        for changes in ({'sortField': 'fake'}, {'operation': 'group', 'groupFields': [{'field': 'fake'}]},
                        {'operation': 'group', 'metrics': [{'function': 'sum', 'field': 'fake'}]}, {'template': 'fake'}):
            request = ReportRequest.model_validate({'reports': [{'operation': 'search', 'title': 'تقرير', 'question': 'سؤال', 'limit': 10, **changes}]})
            with self.assertRaises(ValueError):
                validate_plan_schema(request, {'fields': [], 'entryProperties': ['created'], 'templates': []})

    def test_single_final_ocr_call_keeps_provenance_and_does_not_claim_review(self):
        draft = {'status': 'answered', 'rows': [{'topic': 'status', 'reference': 1, 'quote': 'لم تتم الموافقة على الطلب'}],
                 'findings': [{'text': 'لم تتم الموافقة على الطلب.', 'rowIds': [1]}]}
        model = FakeModel([json.dumps(draft)])
        result = build_graph(model, fast=True, review_content=False).invoke({'question': 'ما القرار؟', 'evidence': [{'entryId': 42, 'text': 'لم تتم الموافقة على الطلب', 'textSource': 'ocr'}]})
        self.assertEqual(len(model.calls), 1)
        self.assertTrue(result['verified'])
        self.assertFalse(result['reviewed'])
        self.assertEqual(result['quality']['semanticReview'], 'not_requested')
        self.assertIn('لم تتم الموافقة', result['answer'])
