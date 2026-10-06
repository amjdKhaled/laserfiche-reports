import json
import unittest
from server import ReportRequest, present_reports
from test_graph import FakeModel

class PlanningTests(unittest.TestCase):
    def test_multiple_reports_keep_their_own_scope_and_latest_semantics(self):
        request = ReportRequest.model_validate({'reports': [
            {'operation': 'latest_modified', 'title': 'آخر تعديل', 'question': 'الوثيقة الأخيرة المعدلة', 'limit': 1},
            {'operation': 'latest_created', 'title': 'آخر إنشاء', 'question': 'الوثيقة الأخيرة المنشأة', 'limit': 1},
            {'operation': 'metadata', 'title': 'وثيقة محددة', 'question': 'حقول الوثيقة 42', 'entryIds': [42], 'limit': 1}]})
        self.assertEqual([p.entryIds for p in request.reports], [[], [], [42]])
        self.assertEqual([p.operation for p in request.reports[:2]], ['latest_modified', 'latest_created'])
        self.assertEqual([p.limit for p in request.reports[:2]], [1, 1])

    def test_presentation_is_independently_reviewed_and_rejects_invented_facts(self):
        payload = {'question': 'آخر إنشاء وآخر تعديل', 'reports': [
            {'index': 0, 'facts': 'آخر إنشاء: الوثيقة 619. تاريخ الإنشاء 2026-10-06.'},
            {'index': 1, 'facts': 'آخر تعديل: الوثيقة 42. تاريخ التعديل 2026-10-06.'}]}
        draft = {'reports': [
            {'index': 0, 'summary': 'آخر وثيقة منشأة هي 619.', 'quotes': ['الوثيقة 619']},
            {'index': 1, 'summary': 'آخر وثيقة معدلة هي 42.', 'quotes': ['الوثيقة 42']}]}
        model = FakeModel([json.dumps(draft), json.dumps({'reports': [
            {'index': 0, 'supported': True}, {'index': 1, 'supported': False}]})])
        result = present_reports(model, payload)
        self.assertEqual(len(model.calls), 2)
        self.assertEqual(result['reports'], [{'index': 0, 'summary': 'آخر وثيقة منشأة هي 619.'}])
        for change in ({'summary': 'آخر وثيقة هي 999.'}, {'quotes': ['الوثيقة 999']}):
            bad = json.loads(json.dumps(draft)); bad['reports'][0].update(change)
            with self.assertRaises(ValueError):
                present_reports(FakeModel([json.dumps(bad)]), payload)

    def test_presentation_cannot_silently_drop_or_duplicate_a_report(self):
        payload = {'question': 'تقريران', 'reports': [{'index': 0, 'facts': 'وثيقة 1'}, {'index': 1, 'facts': 'وثيقة 2'}]}
        with self.assertRaises(ValueError):
            present_reports(FakeModel([json.dumps({'reports': [{'index': 0, 'summary': 'وثيقة 1', 'quotes': ['وثيقة 1']}]})]), payload)

    def test_invalid_plan_is_reanalyzed_without_a_keyword_answer(self):
        from server import plan_reports
        payload = {'question': 'آخر تعديل وآخر إنشاء', 'catalog': {'fields': [{'name': 'إجراء الوثيقة'}]}}
        plans = {'reports': [
            {'operation': 'latest_modified', 'title': 'آخر تعديل', 'question': 'آخر وثيقة معدلة', 'limit': 1},
            {'operation': 'latest_created', 'title': 'آخر إنشاء', 'question': 'آخر وثيقة منشأة', 'limit': 1}]}
        wrong = json.loads(json.dumps(plans)); wrong['reports'][0]['limit'] = 10
        model = FakeModel([json.dumps(wrong), json.dumps(plans)])
        self.assertEqual(len(plan_reports(model, payload)['reports']), 2)
        self.assertEqual(len(model.calls), 2)
        self.assertIn('إجراء الوثيقة', model.calls[1][1].content)
        with self.assertRaises(ValueError):
            plan_reports(FakeModel(['bad JSON', 'bad JSON']), payload)

    def test_dependency_failures_remain_distinct(self):
        from server import dependency_error
        from httpx import ReadTimeout
        self.assertEqual(dependency_error(ReadTimeout('slow CPU')), 'local_model_timeout')
        self.assertEqual(dependency_error(ConnectionError('refused')), 'ollama_unavailable')
        self.assertEqual(dependency_error(ValueError('invalid plan')), 'local_model_invalid_output')
        missing = type('MissingModel', (Exception,), {'status_code': 404})()
        self.assertEqual(dependency_error(missing), 'model_not_found')
