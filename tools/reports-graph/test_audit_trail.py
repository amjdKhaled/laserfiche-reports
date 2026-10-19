import json
import unittest
from pydantic import ValidationError
from server import planner_request, direct_planner_schema, RoutePlan


class AuditTrailContractTests(unittest.TestCase):
    def plan(self, **extra):
        return planner_request(json.dumps({'reports': [dict(operation='audit', title='سجل العمليات',
            selection={'requiresFilter': False}, audit={}, **extra)]}), 'اعرض سجل العمليات').reports[0]

    def test_output_derived_for_events_counts_and_groups(self):
        self.assertEqual('details', self.plan().resultType)
        self.assertEqual('count', self.plan(countOnly=True).resultType)
        raw = {'reports': [{'operation': 'audit', 'title': 'نشاط المستخدمين', 'selection': {'requiresFilter': False},
                           'audit': {'groupBy': 'userName'}}]}
        self.assertEqual('statistics', planner_request(json.dumps(raw), 'نشاط المستخدمين').reports[0].resultType)

    def test_audit_does_not_accept_or_silently_drop_repository_conditions(self):
        for change in ({'name': 'ignored'}, {'content': True}, {'audit': None}, {'sort': 'id asc'}):
            data = dict(operation='audit', title='تدقيق', question='تدقيق', limit=50,
                        requiresFilter=False, resultType='details', audit={})
            data.update(change)
            with self.assertRaises(ValidationError): RoutePlan.model_validate(data)
        with self.assertRaises(ValidationError):
            RoutePlan.model_validate(dict(operation='search', title='بحث', question='بحث', limit=50,
                requiresFilter=False, resultType='documents', audit={}))

    def test_available_source_gets_separate_grammar_and_actual_event_values(self):
        catalog = {'fields': [], 'templates': [], 'auditTrail': {'status': 'available',
            'eventTypes': [{'value': 'EV123', 'label': 'Recycle Entry'}]}}
        contract = direct_planner_schema(catalog).model_json_schema()
        self.assertIn('AuditTrailPlan', contract['$defs'])
        self.assertEqual(['EV123'], contract['$defs']['AuditQuery']['properties']['eventTypes']['items']['enum'])
        disabled = direct_planner_schema({'fields': [], 'templates': []}).model_json_schema()
        self.assertNotIn('AuditTrailPlan', disabled['$defs'])
