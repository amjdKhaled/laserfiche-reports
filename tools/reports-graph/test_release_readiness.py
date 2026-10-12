import json
import unittest
from pathlib import Path
from langchain_core.messages import SystemMessage
from langchain_ollama import ChatOllama
from clarification_options import suggestions
from report_reasoning import invoke_structured, Draft
from release_check import CASES, CATALOG, verify
from server import plan_reports
from test_graph import FakeModel


class ReleaseReadinessTests(unittest.TestCase):
    def test_oss_request_has_reasoning_budget_and_native_think_level(self):
        model = ChatOllama(model='gpt-oss:20b', client_kwargs={'trust_env': False})
        calls = []
        class Binding:
            model = 'gpt-oss:20b'
            def bind(self, **kwargs):
                calls.append(kwargs)
                return self
            def invoke(self, messages):
                return type('Reply', (), {'content': '{"findings":[]}'})()
        invoke_structured(Binding(), [SystemMessage(content='JSON')], Draft, max_tokens=768)
        self.assertEqual(calls[0]['options']['num_predict'], 8192)
        self.assertEqual(calls[0]['reasoning'], 'low')
        params = model._chat_params([SystemMessage(content='JSON')], **calls[0])
        self.assertEqual(params['think'], 'low')
        self.assertEqual(params['options']['num_predict'], 8192)

    def test_near_identical_menu_names_do_not_hide_other_alternatives(self):
        options = suggestions('اجراء الوثيقة', {'fields': [{'name': n} for n in [
            'إجراء الوثيقة', 'اجراء الوثيقة', 'إجراء  الوثيقة', 'إجراء   الوثيقة', 'إ جراء الوثيقة']]})
        self.assertEqual(len(options), 1)

    def test_release_suite_contains_every_advertised_question(self):
        script = (Path(__file__).parents[2] / 'src/LaserficheReports.Web/wwwroot/app.js').read_text()
        for question, *_ in CASES[:3]:
            self.assertIn(question, script)

    def test_suggested_and_reported_failure_plans_are_executable(self):
        filters = [
            {'field': 'created', 'operator': 'in_period', 'period': {'year': 2026, 'month': 10}},
            None, None,
            {'field': 'إجراء الوثيقة', 'operator': 'is_not_empty'},
            {'field': 'نشط لغاية في المركز - م', 'operator': 'through_period', 'period': {'year': 2036}},
            {'field': 'آخر موعد للرد', 'operator': 'less_than', 'relative': {'unit': 'day', 'offset': 0}}]
        for (question, kind, field, condition), predicate in zip(CASES, filters):
            with self.subTest(question=question):
                plan = {'operation': 'schema' if kind == 'schema' else 'search', 'title': 'تقرير',
                        'selection': {'requiresFilter': False} if predicate is None else {'requiresFilter': True, 'filters': predicate}}
                if kind == 'count':
                    plan['countOnly'] = True
                result = plan_reports(FakeModel([json.dumps({'contextMode': 'current', 'reports': [plan]})]),
                                      {'question': question, 'catalog': CATALOG})
                self.assertTrue(verify(result, kind, field, condition))
                self.assertFalse(result.get('clarification'))


if __name__ == '__main__':
    unittest.main()
