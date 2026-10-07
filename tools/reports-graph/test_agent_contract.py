import json
import unittest
from agent_cases import CASES
from evaluate_planner import includes
from server import plan_reports, ReportRequest, validate_plan_schema, build_graph
from test_graph import FakeModel

class AgentContractTests(unittest.TestCase):
    def test_real_ollama_client_transmits_schema_and_compact_catalog(self):
        import threading
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from langchain_ollama import ChatOllama
        requests = []
        plan = {'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'وثائق', 'question': 'وثائق', 'limit': 50}
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_POST(self):
                requests.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
                body = json.dumps({'model': 'qwen2.5:7b', 'message': {'role': 'assistant', 'content': json.dumps({'reports': [plan]})}, 'done': True, 'done_reason': 'stop'}).encode() + b'\n'
                self.send_response(200)
                self.send_header('Content-Length', str(len(body)))
                self.end_headers(); self.wfile.write(body)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            model = ChatOllama(model='qwen2.5:7b', base_url=f'http://127.0.0.1:{server.server_port}', client_kwargs={'trust_env': False})
            result = plan_reports(model, {'question': 'وثائق', 'catalog': {'fields': [{'name': 'حقل فعلي', 'fieldType': 'Date'}]}})
            self.assertEqual(result['reports'][0]['operation'], 'search')
            self.assertEqual(len(requests), 1)
            self.assertIn('resultType', requests[0]['format']['$defs']['RoutePlan']['required'])
            self.assertEqual(requests[0]['options']['num_predict'], 2048)
            self.assertEqual(json.loads(requests[0]['messages'][1]['content'])['catalog']['fields'], [['حقل فعلي', 'Date', False]])
            self.assertNotIn('JSON Schema:', requests[0]['messages'][0]['content'])
        finally:
            server.shutdown(); server.server_close(); thread.join()

    def test_clarification_preserves_desired_intent_without_query_or_second_call(self):
        plan = {'resultType': 'documents', 'requiresFilter': True, 'operation': 'clarify', 'title': 'توضيح', 'question': 'ما الحقل المقصود؟', 'limit': 50}
        model = FakeModel([json.dumps({'reports': [plan], 'clarification': 'ما حقل الموعد المقصود؟'})])
        result = plan_reports(model, {'question': 'كشف مشروط', 'catalog': {'fields': []}})
        self.assertEqual(result['clarification'], 'ما حقل الموعد المقصود؟')
        self.assertEqual(len(model.calls), 1)
        with self.assertRaises(ValueError):
            ReportRequest.model_validate({'reports': [{**plan, 'filters': {'field': 'اسم', 'operator': 'equals', 'value': 'أ'}}]})

    def test_repair_keeps_draft_and_precise_error_instead_of_truncated_exception(self):
        import io
        from contextlib import redirect_stdout
        good = {'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'وثائق', 'question': 'وثائق', 'limit': 50}
        bad = {**good, 'limit': 300}
        model = FakeModel([json.dumps({'reports': [bad]}), json.dumps({'reports': [good]})])
        stream = io.StringIO()
        with redirect_stdout(stream):
            plan_reports(model, {'question': 'وثائق', 'catalog': {'fields': []}})
        self.assertIn('reports.0.limit', stream.getvalue())
        self.assertIn('300', model.calls[1][-2].content)
        self.assertIn('reports.0.limit', model.calls[1][-1].content)

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
        self.assertNotIn('JSON Schema:', model.calls[0][0].content)
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
