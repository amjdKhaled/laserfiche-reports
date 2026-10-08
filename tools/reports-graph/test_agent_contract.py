import json
import unittest
from agent_cases import CASES
from evaluate_planner import includes
from server import plan_reports, ReportRequest, validate_plan_schema, build_graph
from test_graph import FakeModel

class AgentContractTests(unittest.TestCase):
    def test_real_http_transport_enforces_nonstreaming_planner_deadline(self):
        import threading
        import time
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from langchain_ollama import ChatOllama
        from httpx import TimeoutException
        requests = []
        class SlowHandler(BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_POST(self):
                requests.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
                time.sleep(0.8)
                try:
                    self.send_response(200); self.end_headers(); self.wfile.write(b'{}')
                except (BrokenPipeError, ConnectionResetError): pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), SlowHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            model = ChatOllama(model='qwen2.5:7b', base_url=f'http://127.0.0.1:{server.server_port}', client_kwargs={'trust_env': False})
            started = time.monotonic()
            with self.assertRaises(TimeoutException):
                plan_reports(model, {'question': 'وثائق', 'catalog': {}}, budget_seconds=0.2)
            self.assertLess(time.monotonic() - started, 0.7)
            self.assertEqual(len(requests), 1)
            self.assertFalse(requests[0]['stream'])
        finally:
            server.shutdown(); server.server_close(); thread.join()

    def test_compact_contract_fills_only_nonsemantic_defaults(self):
        from server import PlannerOutputSchema
        schema = PlannerOutputSchema.model_json_schema()['$defs']['RoutePlan']
        for key in ('resultType', 'question', 'value', 'groupBy', 'from', 'to'):
            self.assertNotIn(key, schema['properties'])
        self.assertNotIn('limit', schema['required'])
        self.assertIn('selection', schema['required'])
        self.assertNotIn('requiresFilter', schema['properties'])
        question = 'عرض وثائق بشروط من المستودع'
        model = FakeModel([json.dumps({'reports': [{'operation': 'search', 'title': 'وثائق', 'requiresFilter': True,
            'filters': {'field': 'أجل الإنجاز', 'operator': 'less_than', 'relative': {'unit': 'day'}}}]})])
        result = plan_reports(model, {'question': question, 'catalog': {'fields': [{'name': 'أجل الإنجاز', 'fieldType': 'Date'}]}})['reports'][0]
        self.assertEqual(result['question'], question)
        self.assertEqual(result['limit'], 50)
        self.assertTrue(result['requiresFilter'])
        self.assertEqual(result['filters']['field'], 'أجل الإنجاز')
        self.assertEqual(len(model.calls), 1)

    def test_generation_schema_rejects_missing_selection_before_model_output(self):
        from jsonschema import Draft202012Validator
        from server import PlannerOutputSchema
        schema = PlannerOutputSchema.model_json_schema()
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        base = {'operation': 'search', 'title': 'كشف الوثائق'}
        invalid = [dict(base, requiresFilter=True), dict(base, selection={'requiresFilter': True}),
                   dict(base, selection={'requiresFilter': True, 'filters': {}}),
                   dict(base, selection={'requiresFilter': True, 'entryIds': []}),
                   dict(base, selection={'requiresFilter': True, 'name': ''}),
                   dict(base, selection={'requiresFilter': True, 'field': 'الموعد'}),
                   dict(base, selection={'requiresFilter': False, 'name': 'أ'}),
                   dict(base, selection={'requiresFilter': True, 'filters': {'field': 'الموعد', 'operator': 'less_than'}})]
        for plan in invalid:
            with self.subTest(plan=plan):
                self.assertFalse(validator.is_valid({'reports': [plan]}))
        for selection in [{'requiresFilter': False}, {'requiresFilter': True, 'entryIds': [618]},
                          {'requiresFilter': True, 'folder': {'id': 10}}, {'requiresFilter': True, 'template': 'عقود'},
                          {'requiresFilter': True, 'filters': {'field': 'الموعد', 'operator': 'less_than', 'relative': {'unit': 'day'}}}]:
            self.assertTrue(validator.is_valid({'reports': [{**base, 'selection': selection}]}))

    def test_nested_selection_preserves_all_scopes_and_resolves_against_live_schema(self):
        from server import planner_request
        selection = {'requiresFilter': True, 'template': 'عقود', 'folderId': 10,
                     'filters': {'logic': 'and', 'conditions': [
                         {'field': 'الموعد', 'operator': 'less_than', 'relative': {'unit': 'day'}},
                         {'field': 'الحالة', 'operator': 'not_equals', 'value': 'مكتمل'}]}}
        plan = {'operation': 'search', 'title': 'تقرير وثائق', 'selection': selection}
        catalog = {'fields': [{'name': 'الموعد', 'fieldType': 'Date'}, {'name': 'الحالة', 'fieldType': 'String'}], 'templates': ['عقود']}
        model = FakeModel([json.dumps({'reports': [plan]})])
        result = plan_reports(model, {'question': 'طلب طبيعي', 'catalog': catalog})['reports'][0]
        self.assertEqual(result['template'], 'عقود')
        self.assertEqual(result['folderId'], 10)
        self.assertEqual(result['filters']['conditions'][0]['field'], 'الموعد')
        self.assertEqual(result['resultType'], 'documents')
        self.assertEqual(len(model.calls), 1)
        with self.assertRaises(ValueError):
            planner_request(json.dumps({'reports': [{**plan, 'requiresFilter': False}]}), 'طلب')
        with self.assertRaises(ValueError):
            validate_plan_schema(planner_request(json.dumps({'reports': [plan]}), 'طلب'), {'fields': [], 'templates': ['عقود']})

    def test_repair_cannot_receive_a_fresh_deadline(self):
        from unittest.mock import patch
        invalid = {'reports': [{'operation': 'search', 'title': 'وثائق', 'requiresFilter': False, 'limit': 300}]}
        model = FakeModel([json.dumps(invalid)] * 2)
        with patch('server.time.monotonic', side_effect=[0, 0, 0, 0, 121]):
            with self.assertRaises(TimeoutError):
                plan_reports(model, {'question': 'وثائق', 'catalog': {}}, budget_seconds=120)
        self.assertEqual(len(model.calls), 1)

    def test_logged_count_annotation_does_not_discard_valid_document_search_or_retry(self):
        filter_ = {'field': 'موعد التسليم', 'operator': 'less_than', 'relative': {'unit': 'day'}}
        for options in ({}, {'countOnly': False}):
            plan = {'resultType': 'count', 'requiresFilter': True, 'operation': 'search', 'title': 'تقرير وثائق',
                    'question': 'طلب تقرير مشروط', 'limit': 50, 'filters': filter_, **options}
            model = FakeModel([json.dumps({'reports': [plan]})])
            result = plan_reports(model, {'question': plan['question'], 'catalog': {'fields': [{'name': 'موعد التسليم', 'fieldType': 'Date'}]}})
            actual = result['reports'][0]
            self.assertEqual(actual['resultType'], 'documents')
            self.assertFalse(actual['countOnly'])
            self.assertEqual(actual['filters']['field'], filter_['field'])
            self.assertEqual(actual['filters']['operator'], filter_['operator'])
            self.assertTrue(actual['allResults'])
            self.assertEqual(len(model.calls), 1)

    def test_primary_tool_contract_derives_output_without_changing_explicit_modes(self):
        from server import planner_request, PlannerOutputSchema
        base = {'requiresFilter': False, 'title': 'تقرير', 'question': 'طلب', 'limit': 50}
        for options, expected in [({'operation': 'search'}, 'documents'), ({'operation': 'search', 'countOnly': True}, 'count'),
                                  ({'operation': 'group'}, 'statistics'), ({'operation': 'search', 'content': True}, 'content'),
                                  ({'operation': 'metadata', 'entryIds': [618]}, 'details'), ({'operation': 'templates'}, 'schema')]:
            actual = planner_request(json.dumps({'reports': [{**base, **options}]})).reports[0]
            self.assertEqual(actual.resultType, expected)
            self.assertEqual(actual.operation, options['operation'])
        schema = PlannerOutputSchema.model_json_schema()['$defs']['RoutePlan']
        self.assertNotIn('resultType', schema['properties'])
        self.assertIn('operation', schema['required'])

    def test_output_derivation_never_hides_unknown_fields_or_missing_required_filters(self):
        base = {'resultType': 'count', 'requiresFilter': True, 'operation': 'search', 'title': 'وثائق', 'question': 'مشروط', 'limit': 50}
        for changes in ({}, {'filters': {'field': 'حقل مخترع', 'operator': 'equals', 'value': 'أ'}}):
            model = FakeModel([json.dumps({'reports': [{**base, **changes}]})] * 2)
            with self.assertRaises(ValueError):
                plan_reports(model, {'question': 'طلب مشروط', 'catalog': {'fields': []}})

    def test_real_ollama_client_transmits_schema_and_compact_catalog(self):
        import threading
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from langchain_ollama import ChatOllama
        requests = []
        plan = {'selection': {'requiresFilter': True, 'filters': {'field': 'حقل فعلي', 'operator': 'less_than', 'relative': {'unit': 'day'}}}, 'operation': 'search', 'title': 'وثائق'}
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_POST(self):
                import time
                time.sleep(0.1)  # Unlimited planning overrides an inherited short client timeout.
                requests.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
                body = json.dumps({'model': 'qwen2.5:7b', 'message': {'role': 'assistant', 'content': json.dumps({'reports': [plan]})}, 'done': True, 'done_reason': 'stop'}).encode() + b'\n'
                self.send_response(200)
                self.send_header('Content-Length', str(len(body)))
                self.end_headers(); self.wfile.write(body)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            model = ChatOllama(model='qwen2.5:7b', base_url=f'http://127.0.0.1:{server.server_port}', client_kwargs={'trust_env': False, 'timeout': 0.01})
            result = plan_reports(model, {'question': 'وثائق', 'catalog': {'fields': [{'name': 'حقل فعلي', 'fieldType': 'Date'}]}})
            self.assertEqual(result['reports'][0]['operation'], 'search')
            self.assertEqual(len(requests), 1)
            executable = requests[0]['format']['$defs']['ExecutablePlan']['anyOf'][0]
            self.assertNotIn('resultType', executable['properties'])
            self.assertIn('selection', executable['required'])
            self.assertEqual(requests[0]['options']['num_predict'], 1536)
            self.assertFalse(requests[0]['stream'])
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
        self.assertEqual(model.options['options']['num_predict'], 1536)
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
