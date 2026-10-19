import unittest
import json
from server import planner_request, RoutePlan, direct_planner_schema, plan_reports
from test_graph import FakeModel
from jsonschema import Draft202012Validator

class AvailabilityTests(unittest.TestCase):
    def test_generic_tool_routes_explicit_ids_to_details(self):
        raw = {'contextMode': 'current', 'reports': [{'operation': 'availability', 'title': 'فحص الإتاحة',
            'selection': {'requiresFilter': True, 'entryIds': [619, 620]}}]}
        Draft202012Validator(direct_planner_schema({'fields': []}).model_json_schema()).validate(raw)
        plan = planner_request(json.dumps(raw), 'افحص إتاحة 619 و620').reports[0]
        self.assertEqual(plan.resultType, 'details')
        self.assertEqual(plan.entryIds, [619, 620])

    def test_unavailable_sources_reach_model_without_becoming_a_normal_search(self):
        model = FakeModel([json.dumps({'reports': [{'operation': 'clarify', 'title': 'المحذوفات',
            'selection': {'requiresFilter': False}}], 'clarification': 'يلزم مصدر سلة المحذوفات أو التدقيق.'})])
        result = plan_reports(model, {'question': 'أريد الملفات المحذوفة', 'catalog': {
            'fields': [], 'capabilities': {'deletedEntries': 'unavailable_requires_recycle_bin_or_audit_source'}}})
        self.assertEqual(result['reports'][0]['operation'], 'clarify')
        self.assertIn('unavailable_requires_recycle_bin_or_audit_source', model.calls[0][1].content)

    def test_missing_ids_or_filters_do_not_become_a_repository_search(self):
        for extra in ({}, {'entryIds': [619], 'name': 'other'}, {'entryIds': [619], 'countOnly': True}):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                RoutePlan.model_validate(dict(operation='availability', title='فحص الإتاحة',
                    question='افحص إتاحة الإدخال', resultType='details', requiresFilter=True, limit=50, **extra))

if __name__ == '__main__':
    unittest.main()
