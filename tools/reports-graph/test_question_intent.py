import json
import unittest
import tempfile
from pathlib import Path

from jsonschema import Draft202012Validator, ValidationError
from server import QuestionIntent, planner_schema_for_catalog, plan_reports, validate_question_intent, planner_request, grounded_intent, planning_history
from test_graph import FakeModel
from test_plan_intent_review import review

CATALOG = {'fields': [{'name': 'موعد انتهاء السريان - ميلادي', 'fieldType': 'Date'}]}


def intent(kind='count', shape='upper_bound'):
    return {'outputs': [{'resultType': kind, 'meaning': 'عدد السجلات التي تنتهي في السنة المحددة أو قبلها', 'conditionShape': shape, 'requestText': 'كم سجل', **({'upperBoundText': '2041'} if shape == 'upper_bound' else {'lowerBoundText': '2041'} if shape == 'lower_bound' else {})}]}


def query(operation='search', operator='less_than'):
    result = {'reports': [{'operation': operation, 'title': 'النتائج', 'selection': {'requiresFilter': True,
              'filters': {'field': CATALOG['fields'][0]['name'], 'operator': operator, 'value': '2042-01-01'}}}]}
    if operation == 'group':
        result['reports'][0].update(groupFields=[{'field': CATALOG['fields'][0]['name']}], metrics=[{'function': 'count'}])
    else:
        result['reports'][0].update(countOnly=True, content=False)
    if operator == 'between':
        result['reports'][0]['selection']['filters']['upper'] = '2043-01-01'
    return result


def indexed(plan):
    return {'outputs': {'output' + str(i): report for i, report in enumerate(plan['reports'])}}


class IndependentIntentTests(unittest.TestCase):
    def test_one_sided_count_grammar_excludes_grouping_and_invented_range(self):
        schema = planner_schema_for_catalog(CATALOG, QuestionIntent.model_validate(intent())).model_json_schema()
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema).validate(indexed(query()))
        for invalid in [query('group'), query(operator='between'), query(operator='greater_than')]:
            with self.subTest(invalid=invalid), self.assertRaises(ValidationError):
                Draft202012Validator(schema).validate(indexed(invalid))
        listing = query(); listing['reports'][0]['countOnly'] = False
        with self.assertRaises(ValidationError):
            Draft202012Validator(schema).validate(indexed(listing))

    def test_typed_contract_cannot_bypass_independent_interpretation(self):
        interpretation = QuestionIntent.model_validate(intent())
        for invalid in [query('group'), query(operator='between')]:
            with self.assertRaises(ValueError):
                validate_question_intent(planner_request(json.dumps(invalid), 'كم منها؟'), interpretation)

    def test_interpretation_is_blind_to_catalog_and_bad_draft_is_not_retry_context(self):
        model = FakeModel([json.dumps(intent()), json.dumps(query('group')), json.dumps(query()), json.dumps(review())])
        result = plan_reports(model, {'question': 'كم سجل ساري لغاية 2041 وما دون؟', 'catalog': CATALOG},
                              interpret_intent=True, review_intent=True)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertEqual(result['reports'][0]['filters']['operator'], 'less_than')
        self.assertNotIn('catalog', json.loads(model.calls[0][1].content))
        self.assertNotIn('proposedPlan', model.calls[0][1].content)
        self.assertFalse(any(message.type == 'ai' for message in model.calls[2]))
        self.assertIn('questionIntent', model.calls[2][1].content)
        self.assertEqual(json.loads(model.calls[3][1].content)['questionIntent'], QuestionIntent.model_validate(intent()).model_dump())

    def test_legitimate_aggregation_and_explicit_ranges_remain_available(self):
        schema = planner_schema_for_catalog(CATALOG, QuestionIntent.model_validate(intent('statistics', 'range'))).model_json_schema()
        grouped = query('group', 'between')
        Draft202012Validator(schema).validate(indexed(grouped))
        validate_question_intent(planner_request(json.dumps(grouped), 'وزع حسب التاريخ خلال فترة'),
                                 QuestionIntent.model_validate(intent('statistics', 'range')))

    def test_unresolved_field_can_still_clarify(self):
        schema = planner_schema_for_catalog({}, QuestionIntent.model_validate(intent())).model_json_schema()
        clarification = {'reports': [{'operation': 'clarify', 'title': 'توضيح', 'selection': {'requiresFilter': False}}],
                         'clarification': 'ما المقصود بمعيار السريان؟'}
        Draft202012Validator(schema).validate(clarification)
        validate_question_intent(planner_request(json.dumps(clarification), 'سؤال'), QuestionIntent.model_validate(intent()))


class GroundedInterpretationRegressionTests(unittest.TestCase):
    def test_log_regression_two_outputs_with_range_and_lower_bound_cannot_mix_grammars(self):
        interpreted = QuestionIntent.model_validate({'outputs': [
            {**intent()['outputs'][0], 'conditionShape': 'range'},
            intent(shape='lower_bound')['outputs'][0]]})
        schema = planner_schema_for_catalog(CATALOG, interpreted).model_json_schema()
        first = query(operator='between')['reports'][0]
        second = query(operator='greater_or_equal')['reports'][0]
        valid = {'outputs': {'output0': first, 'output1': second}}
        Draft202012Validator(schema).validate(valid)
        # Exactly the v6.3 failure: both plans use between, but only the first
        # output requests a range. Reject it DURING generation, not just parsing.
        invalid = {'outputs': {'output0': first, 'output1': first}}
        with self.assertRaises(ValidationError):
            Draft202012Validator(schema).validate(invalid)
        parsed = planner_request(json.dumps(valid), 'مطلبان مستقلان')
        validate_question_intent(parsed, interpreted)
        self.assertEqual([p.filters.operator for p in parsed.reports], ['between', 'greater_or_equal'])

    def test_duplicate_current_request_and_reused_range_boundary_are_rejected(self):
        question = 'كم وثيقة نشطة في المركز لغاية 2036 و ما اقل؟'
        duplicate = {'outputs': [
            {'resultType': 'count', 'meaning': 'عدد الوثائق', 'requestText': 'كم وثيقة', 'conditionShape': 'other'},
            {'resultType': 'count', 'meaning': 'عدد آخر', 'requestText': 'كم وثيقة', 'conditionShape': 'other'}]}
        with self.assertRaisesRegex(ValueError, 'distinct'):
            grounded_intent(QuestionIntent.model_validate(duplicate), question)
        invented = {'outputs': [{**duplicate['outputs'][0], 'conditionShape': 'range',
                                 'lowerBoundText': '2036', 'upperBoundText': '2036'}]}
        with self.assertRaisesRegex(ValueError, 'reuse'):
            grounded_intent(QuestionIntent.model_validate(invented), question)

    def test_bad_interpretation_is_repaired_before_planning_and_can_return_count(self):
        question = 'كم وثيقة نشطة في المركز لغاية 2036 و ما اقل؟'
        bad = {'outputs': [{'resultType': 'count', 'meaning': 'فترة', 'requestText': 'كم وثيقة',
                            'conditionShape': 'range', 'lowerBoundText': '2036', 'upperBoundText': '2036'}]}
        good = {'outputs': [{'resultType': 'count', 'meaning': 'العدد لحد السنة المذكورة وما قبلها',
                             'requestText': 'كم وثيقة', 'conditionShape': 'upper_bound', 'upperBoundText': 'لغاية 2036'}]}
        plan = indexed(query())
        plan['outputs']['output0']['selection']['filters']['value'] = '2037-01-01'
        model = FakeModel([json.dumps(bad), json.dumps(good), json.dumps(plan), json.dumps(review())])
        history = [{'role': 'user', 'text': question}, {'role': 'assistant', 'text': 'Failed answer'},
                   {'role': 'user', 'text': question}, {'role': 'assistant', 'text': 'Failed again'}]
        with tempfile.TemporaryDirectory() as folder:
            trace = Path(folder) / 'trace.jsonl'
            result = plan_reports(model, {'question': question, 'catalog': CATALOG, 'history': history},
                                  review_intent=True, interpret_intent=True, trace_path=trace)
            records = [json.loads(line) for line in trace.read_text().splitlines()]
            self.assertEqual(records[0]['payload']['history'], history)
            self.assertEqual(records[1]['stage'], 'QuestionIntent')
            self.assertEqual(records[1]['response'], json.dumps(bad))
            self.assertIn('schema', records[1])
        self.assertEqual(len(model.calls), 4)
        self.assertEqual(json.loads(model.calls[0][1].content)['history'], [])
        self.assertEqual(len(result['reports']), 1)
        self.assertEqual(result['reports'][0]['filters']['value'], '2037-01-01')

    def test_genuine_independent_outputs_have_distinct_current_question_evidence(self):
        question = 'كم عدد المقبول وكم عدد المرفوض؟'
        interpretation = QuestionIntent.model_validate({'outputs': [
            {'resultType': 'count', 'meaning': 'عدد المقبول', 'requestText': 'كم عدد المقبول', 'conditionShape': 'other'},
            {'resultType': 'count', 'meaning': 'عدد المرفوض', 'requestText': 'كم عدد المرفوض', 'conditionShape': 'other'}]})
        self.assertIs(grounded_intent(interpretation, question), interpretation)

    def test_followup_context_is_preserved_but_repeated_attempt_response_is_removed(self):
        history = [{'role': 'user', 'text': 'وثائق المجلد المحدد'}, {'role': 'assistant', 'text': 'نتيجة'},
                   {'role': 'user', 'text': 'كم عددها؟'}, {'role': 'assistant', 'text': 'فشل'}]
        self.assertEqual(planning_history(history, 'كم عددها؟'), history[:2])
        self.assertEqual(planning_history(history, 'رتبها'), history)

    def test_semantic_rejection_can_correct_the_intent_instead_of_locking_it_in(self):
        question = 'كم سجل قبل 2041؟'
        wrong = intent(shape='lower_bound')
        correct = intent()
        model = FakeModel([json.dumps(wrong), json.dumps(indexed(query(operator='greater_than'))),
                           json.dumps(review(conditionsMatch=False, issues=['Reversed the original comparison'])),
                           json.dumps(correct), json.dumps(indexed(query())), json.dumps(review())])
        result = plan_reports(model, {'question': question, 'catalog': CATALOG}, interpret_intent=True, review_intent=True)
        self.assertEqual(result['reports'][0]['filters']['operator'], 'less_than')
        self.assertEqual(len(model.calls), 6)
        self.assertIn('Reversed', model.calls[3][-1].content)

    def test_real_ollama_http_receives_per_output_schema_and_unlimited_model_budget(self):
        import threading
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from langchain_ollama import ChatOllama
        question = 'كم سجل قبل 2041؟'
        replies = iter([intent(), indexed(query()), review()])
        received = []
        class OllamaHandler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                received.append(body)
                reply = {'model': 'qwen2.5:7b', 'message': {'role': 'assistant', 'content': json.dumps(next(replies))},
                         'done': True, 'done_reason': 'stop'}
                data = json.dumps(reply).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        server = ThreadingHTTPServer(('127.0.0.1', 0), OllamaHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            model = ChatOllama(model='qwen2.5:7b', base_url=f'http://127.0.0.1:{server.server_port}',
                               client_kwargs={'trust_env': False, 'timeout': None})
            result = plan_reports(model, {'question': question, 'catalog': CATALOG},
                                  budget_seconds=None, interpret_intent=True, review_intent=True)
            self.assertEqual(result['reports'][0]['resultType'], 'count')
            self.assertEqual(len(received), 3)
            self.assertTrue(all(call['stream'] is False for call in received))
            schema = received[1]['format']
            Draft202012Validator(schema).validate(indexed(query()))
            with self.assertRaises(ValidationError):
                Draft202012Validator(schema).validate(indexed(query(operator='between')))
            self.assertIn('requestText', received[0]['format']['$defs']['RequestedOutput']['required'])
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


class ClarificationFollowupTests(unittest.TestCase):
    def history(self):
        return [{'role': 'user', 'text': 'كم سجل ينتهي قبل 2041؟'},
                {'role': 'assistant', 'kind': 'clarification', 'clarificationQuestion': 'كم سجل ينتهي قبل 2041؟',
                 'text': 'هل تقصد موعد انتهاء السريان الميلادي أم الهجري؟'}]

    def test_selected_field_reply_keeps_original_count_and_bound(self):
        reading = intent()
        reading['contextMode'] = 'clarification_reply'
        reading['outputs'][0]['upperBoundText'] = 'قبل 2041'
        model = FakeModel([json.dumps(reading), json.dumps(indexed(query())), json.dumps(review())])
        result = plan_reports(model, {'question': 'أقصد الميلادي', 'history': self.history(), 'catalog': CATALOG},
                              interpret_intent=True, review_intent=True)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertEqual(result['reports'][0]['filters']['operator'], 'less_than')
        self.assertEqual(json.loads(model.calls[0][1].content)['clarificationContext']['question'], self.history()[0]['text'])
        self.assertEqual(json.loads(model.calls[2][1].content)['questionIntent']['contextMode'], 'clarification_reply')

    def test_failed_answer_is_removed_and_pending_question_survives_retry(self):
        history = [*self.history(), {'role': 'user', 'text': 'أقصد الميلادي'},
                   {'role': 'assistant', 'kind': 'error', 'text': 'تعذر إكمال السؤال: فشل النموذج'}]
        self.assertEqual(planning_history(history, 'أقصد الميلادي'), self.history())
        reading = intent(); reading['contextMode'] = 'clarification_reply'
        model = FakeModel([json.dumps(reading), json.dumps(indexed(query())), json.dumps(review())])
        result = plan_reports(model, {'question': 'أقصد الميلادي', 'history': history, 'catalog': CATALOG},
                              interpret_intent=True, review_intent=True)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertNotIn('فشل النموذج', model.calls[0][1].content)

    def test_new_independent_question_cannot_quote_pending_original_request(self):
        reading = QuestionIntent.model_validate(intent())
        with self.assertRaises(ValueError):
            grounded_intent(reading, 'اعرض القوالب', self.history(), {'question': self.history()[0]['text'], 'prompt': 'توضيح'})
        with self.assertRaises(ValueError):
            grounded_intent(QuestionIntent.model_validate({**intent(), 'contextMode': 'clarification_reply'}), 'اختيار')

    def test_genuine_followup_can_quote_original_request_without_a_clarification_marker(self):
        reading = QuestionIntent.model_validate({**intent(), 'contextMode': 'followup'})
        self.assertIs(grounded_intent(reading, 'طيب العدد فقط', [{'role': 'user', 'text': 'كم سجل قبل 2041؟'}]), reading)

    def test_another_clarification_preserves_original_request_not_short_reply(self):
        reading = {**intent(), 'contextMode': 'clarification_reply'}
        clarification = {'reports': [{'operation': 'clarify', 'title': 'توضيح المعيار', 'selection': {'requiresFilter': False}}],
                         'clarification': 'حدد التقويم المقصود.'}
        model = FakeModel([json.dumps(reading), json.dumps(clarification), json.dumps(review())])
        result = plan_reports(model, {'question': 'أقصد تاريخ السريان', 'history': self.history(), 'catalog': CATALOG},
                              interpret_intent=True, review_intent=True)
        self.assertEqual(result['clarificationQuestion'], self.history()[0]['text'])
        self.assertEqual(result['reports'][0]['operation'], 'clarify')
