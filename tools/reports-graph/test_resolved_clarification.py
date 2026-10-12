"""Conversation/validation regressions with scripted model replies, not Qwen accuracy."""
import json
import unittest

from clarification_options import catalog_selection, selected_option
from server import plan_reports
from test_graph import FakeModel
from test_plan_intent_review import review

FIELD = 'نشط لغاية في المركز - م'
OTHER = 'نشط لغاية في المركز - هـ'
CATALOG = {'fields': [{'name': FIELD, 'fieldType': 'Date'},
                       {'name': OTHER, 'fieldType': 'String'},
                       {'name': 'إجراء الوثيقة', 'fieldType': 'String'}]}
QUESTION = 'اعطيني الوثائق اللي يحتوي فيها حقل نشط في المركز لغاية م اكبر من 2036'
MENU = f'أي حقل تقصد؟\n1. حقل «{FIELD}»\n2. حقل «{OTHER}»'


def payload(reply='1'):
    return {'question': reply, 'catalog': CATALOG, 'history': [
        {'role': 'user', 'text': QUESTION},
        {'role': 'assistant', 'kind': 'clarification', 'text': MENU,
         'clarificationQuestion': QUESTION}]}


def plan(field=FIELD):
    return {'contextMode': 'clarification_reply', 'reports': [
        {'operation': 'search', 'title': 'الوثائق', 'selection': {'requiresFilter': True,
         'filters': {'field': field, 'operator': 'after_period', 'period': {'year': 2036}}}}]}


def clarify():
    return {'contextMode': 'clarification_reply', 'reports': [
        {'operation': 'clarify', 'title': 'توضيح', 'selection': {'requiresFilter': False}}],
        'clarification': MENU}


class ResolvedClarificationTests(unittest.TestCase):
    def test_screenshot_repeated_menu_is_repaired_into_original_date_query(self):
        model = FakeModel([json.dumps(clarify()), json.dumps(plan()), json.dumps(review())])
        result = plan_reports(model, payload(), review_intent=True)
        self.assertEqual(result['reports'][0]['operation'], 'search')
        self.assertEqual(result['reports'][0]['filters']['field'], FIELD)
        self.assertEqual(result['reports'][0]['filters']['operator'], 'after_period')
        self.assertEqual(len(model.calls), 3)

    def test_selected_field_is_enforced_even_when_model_chooses_valid_wrong_field(self):
        wrong = plan('إجراء الوثيقة')
        wrong['reports'][0]['selection']['filters'] = {
            'field': 'إجراء الوثيقة', 'operator': 'equals', 'value': 'تحت الإجراء'}
        model = FakeModel([json.dumps(wrong), json.dumps(plan()), json.dumps(review())])
        result = plan_reports(model, payload(), review_intent=True)
        self.assertEqual(result['reports'][0]['filters']['field'], FIELD)

    def test_explicit_choice_does_not_depend_on_model_guessing_context_mode(self):
        draft = plan(); draft['contextMode'] = 'current'
        model = FakeModel([json.dumps(draft), json.dumps(review())])
        result = plan_reports(model, payload('١'), review_intent=True)
        self.assertEqual(result['reports'][0]['operation'], 'search')
        for call in model.calls:
            sent = json.loads(call[1].content)
            self.assertEqual(sent['question'], QUESTION)
            self.assertEqual(sent['currentReply'], '١')
            self.assertEqual(sent['clarificationContext']['selection']['name'], FIELD)

    def test_trace_rotation_preserves_previous_file_and_current_record(self):
        import tempfile
        from pathlib import Path
        from server import write_planner_trace
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'planner.jsonl'
            path.write_text('x' * 10_000_000)
            write_planner_trace(str(path), {'stage': 'test'})
            self.assertEqual(json.loads(path.read_text())['stage'], 'test')
            self.assertEqual(path.with_name(path.name + '.1').stat().st_size, 10_000_000)

    def test_reviewer_and_executor_receive_original_question_not_menu_digit(self):
        model = FakeModel([json.dumps(plan()), json.dumps(review())])
        result = plan_reports(model, payload(), review_intent=True)
        reviewed = json.loads(model.calls[-1][1].content)
        self.assertEqual(reviewed['proposedPlan']['reports'][0]['question'], QUESTION)
        self.assertEqual(result['reports'][0]['question'], QUESTION)

    def test_exhausted_repair_is_an_error_and_never_an_identical_menu(self):
        with self.assertRaises(ValueError):
            plan_reports(FakeModel([json.dumps(clarify())] * 2), payload(), review_intent=True)

    def test_exact_name_and_number_resolve_to_same_live_field(self):
        context = {'prompt': MENU}
        for reply in ('1', '١', '۱', FIELD, f'حقل «{FIELD}»'):
            self.assertEqual(catalog_selection(selected_option(reply, context), CATALOG)['name'], FIELD)

    def test_stale_or_ambiguous_catalog_names_are_not_forced(self):
        self.assertIsNone(catalog_selection('حقل «محذوف»', CATALOG))
        self.assertIsNone(catalog_selection(FIELD, {**CATALOG, 'templates': [FIELD]}))
        self.assertEqual(catalog_selection(f'قالب «{FIELD}»', {**CATALOG, 'templates': [FIELD]})['kind'], 'template')

    def test_new_independent_request_does_not_inherit_selection(self):
        draft = {'contextMode': 'current', 'reports': [{'operation': 'latest_created',
            'title': 'الأحدث', 'limit': 1, 'selection': {'requiresFilter': False}}]}
        model = FakeModel([json.dumps(draft), json.dumps(review())])
        result = plan_reports(model, payload('وش آخر وثيقة انضافت؟'), review_intent=True)
        self.assertEqual(result['reports'][0]['operation'], 'latest_created')
        self.assertEqual(json.loads(model.calls[0][1].content)['question'], 'وش آخر وثيقة انضافت؟')

    def test_count_and_upper_or_lower_year_bound_survive_all_number_forms(self):
        for reply in ('1', '١', '۱', FIELD):
            for operator, condition in [('after_period', 'بعد سنة 2036'),
                                        ('through_period', 'حتى نهاية سنة 2036 وما قبلها'),
                                        ('before_period', 'قبل سنة 2036'),
                                        ('from_period', 'من بداية سنة 2036 وما بعدها')]:
                with self.subTest(reply=reply, operator=operator):
                    draft = plan()
                    draft['reports'][0]['countOnly'] = True
                    draft['reports'][0]['selection']['filters']['operator'] = operator
                    data = payload(reply)
                    original = 'كم وثيقة تاريخ انتهاء نشاطها ' + condition + '؟'
                    data['history'][0]['text'] = original
                    data['history'][1]['clarificationQuestion'] = original
                    model = FakeModel([json.dumps(draft), json.dumps(review())])
                    result = plan_reports(model, data, review_intent=True)
                    report = result['reports'][0]
                    self.assertEqual(report['resultType'], 'count')
                    self.assertEqual(report['filters']['operator'], operator)
                    self.assertEqual(report['filters']['period']['year'], 2036)

    def test_a_new_missing_criterion_can_still_be_clarified(self):
        draft = clarify(); draft['clarification'] = 'هل تريد ما قبل السنة أم ما بعدها؟'
        result = plan_reports(FakeModel([json.dumps(draft), json.dumps(review())]), payload(), review_intent=True)
        self.assertEqual(result['clarification'], draft['clarification'])
        self.assertEqual(result['clarificationQuestion'], QUESTION)

    def test_other_requested_filters_and_reports_remain_available(self):
        draft = plan()
        date_filter = draft['reports'][0]['selection']['filters']
        draft['reports'][0]['selection']['filters'] = {'logic': 'and', 'conditions': [date_filter,
            {'field': 'إجراء الوثيقة', 'operator': 'not_equals', 'value': 'مرفوض'}]}
        draft['reports'].append({'operation': 'latest_modified', 'title': 'آخر تعديل',
            'limit': 1, 'selection': {'requiresFilter': False}})
        result = plan_reports(FakeModel([json.dumps(draft), json.dumps(review())]), payload(), review_intent=True)
        self.assertEqual(len(result['reports']), 2)
        self.assertEqual(len(result['reports'][0]['filters']['conditions']), 2)
        self.assertEqual(result['reports'][1]['limit'], 1)

    def test_template_selection_is_not_mistaken_for_a_metadata_field(self):
        data = payload('1')
        data['catalog'] = {**CATALOG, 'templates': ['عقود', 'مراسلات']}
        data['history'][-1]['text'] = '1. قالب «عقود»\n2. قالب «مراسلات»'
        draft = {'contextMode': 'clarification_reply', 'reports': [{'operation': 'search',
            'title': 'العقود', 'selection': {'requiresFilter': True, 'template': 'عقود'}}]}
        result = plan_reports(FakeModel([json.dumps(draft), json.dumps(review())]), data, review_intent=True)
        self.assertEqual(result['reports'][0]['template'], 'عقود')

    def test_reviewer_cannot_resurrect_the_answered_menu(self):
        rejection = review(fieldsMatch=False, issues=['choose field'], clarification=MENU)
        with self.assertRaises(ValueError):
            plan_reports(FakeModel([json.dumps(plan()), json.dumps(rejection)] * 2), payload(), review_intent=True)

    def test_selected_text_field_does_not_silently_become_a_gregorian_date(self):
        with self.assertRaises(ValueError):
            plan_reports(FakeModel([json.dumps(plan(OTHER))] * 2), payload('2'), review_intent=True)

    def test_real_route_http_recovers_after_repeated_menu(self):
        import http.client
        import threading
        from http.server import ThreadingHTTPServer
        from server import Handler
        class TestHandler(Handler):
            model = FakeModel([json.dumps(clarify()), json.dumps(plan()), json.dumps(review())])
            def log_message(self, *args):
                pass
        with ThreadingHTTPServer(('127.0.0.1', 0), TestHandler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            client = http.client.HTTPConnection(*server.server_address, timeout=5)
            try:
                client.request('POST', '/route', json.dumps(payload('١')).encode(),
                               headers={'Content-Type': 'application/json'})
                response = client.getresponse()
                self.assertEqual(response.status, 200)
                result = json.loads(response.read())
                self.assertEqual(result['reports'][0]['filters']['field'], FIELD)
                self.assertIsNone(result['clarification'])
            finally:
                client.close()
                server.shutdown()
                thread.join(timeout=3)


if __name__ == '__main__':
    unittest.main()
