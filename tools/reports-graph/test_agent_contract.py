import json
import unittest
from agent_cases import CASES
from evaluate_planner import includes
from server import plan_reports, ReportRequest, validate_plan_schema, build_graph
from test_graph import FakeModel

class AgentContractTests(unittest.TestCase):
    def test_document_intent_cannot_execute_group_or_unfiltered_fallback(self):
        payload = {'question': 'كشف مشروط للوثائق', 'catalog': {'fields': [{'name': 'أجل الإنجاز', 'fieldType': 'Date'}]}}
        good = {'resultType': 'documents', 'requiresFilter': True, 'operation': 'search', 'title': 'وثائق',
                'question': payload['question'], 'limit': 50,
                'filters': {'field': 'أجل الإنجاز', 'operator': 'less_than', 'relative': {'unit': 'day'}}}
        for changes in ({'operation': 'group'}, {'filters': None}):
            bad = {**good, **changes}
            model = FakeModel([json.dumps({'reports': [bad]}), json.dumps({'reports': [good]})])
            result = plan_reports(model, payload)
            self.assertEqual(result['reports'][0]['operation'], 'search')
            self.assertIsNotNone(result['reports'][0]['filters'])
            self.assertEqual(len(model.calls), 2)
            with self.assertRaises(ValueError):
                plan_reports(FakeModel([json.dumps({'reports': [bad]})] * 2), payload)

    def test_compact_contract_preserves_named_properties_and_bound_generation(self):
        from report_reasoning import compact_schema
        contract = ReportRequest.model_json_schema()
        small = compact_schema(contract)
        self.assertIn('title', small['$defs']['RoutePlan']['properties'])
        self.assertIn('resultType', small['$defs']['RoutePlan']['required'])
        self.assertIn('requiresFilter', small['$defs']['RoutePlan']['required'])
        self.assertLess(len(json.dumps(small)), len(json.dumps(contract)))
        class BoundModel(FakeModel):
            def bind(self, **kwargs):
                self.options = kwargs
                return self
        plan = {'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'وثائق', 'question': 'كشف', 'limit': 50}
        model = BoundModel([json.dumps({'reports': [plan]})])
        plan_reports(model, {'question': 'كشف', 'catalog': {'fields': []}})
        self.assertEqual(model.options['options']['num_predict'], 2048)
        self.assertEqual(model.options['options']['num_ctx'], 8192)
        self.assertEqual(len(model.calls), 1)

    def test_listing_defaults_to_all_rows_but_explicit_limit_can_be_preserved(self):
        for options, expected in [({}, True), ({'allResults': False, 'limit': 5}, False)]:
            plan = {'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'تقرير الوثائق', 'question': 'كشف الوثائق', 'limit': 50, **options}
            model = FakeModel([json.dumps({'reports': [plan]}, ensure_ascii=False)])
            result = plan_reports(model, {'question': plan['question'], 'catalog': {'fields': []}})
            self.assertEqual(result['reports'][0]['allResults'], expected)
            if not expected:
                self.assertEqual(result['reports'][0]['limit'], 5)

    def test_held_out_cases_exercise_contract_but_fake_model_is_not_language_evaluation(self):
        self.assertGreaterEqual(len(CASES), 30)
        for case in CASES:
            with self.subTest(question=case['question']):
                plan = {'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'تقرير', 'question': case['question'], 'limit': 50, **case['expect']}
                if plan.get('content') and 'filters' not in plan:
                    plan['operation'] = 'content'
                if plan.get('filters', {}).get('operator') == 'between':
                    plan['filters'] = {**plan['filters'], 'upper': '900'}
                plan['resultType'] = ('statistics' if plan['operation'] == 'group' else 'content' if plan.get('content') else 'count' if plan.get('countOnly') else 'details' if plan['operation'] in ('metadata','folder_information') else 'schema' if plan['operation'] in ('schema','templates') else 'documents')
                plan['requiresFilter'] = bool(plan.get('filters'))
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
        bad = {'reports': [{'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'تقرير', 'question': 'جديد تماما', 'limit': 20,
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
            if changes.get('operation') == 'group': changes['resultType'] = 'statistics'
            request = ReportRequest.model_validate({'reports': [{'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'تقرير', 'question': 'سؤال', 'limit': 10, **changes}]})
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
