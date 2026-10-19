import json
import unittest
from pydantic import ValidationError
from server import planner_request, direct_planner_schema, RoutePlan, plan_reports, planner_schema_for_catalog, QuestionIntent
from test_graph import FakeModel
from jsonschema import Draft202012Validator


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

    def test_audit_catalog_survives_compaction_and_routes_real_event_code(self):
        catalog = {'fields': [], 'templates': [], 'auditTrail': {'status': 'available',
            'eventTypes': [{'value': 'EV123', 'label': 'Recycle Entry'}]}}
        raw = {'contextMode': 'current', 'reports': [{'operation': 'audit', 'title': 'سجل الحذف',
            'selection': {'requiresFilter': False}, 'audit': {'eventTypes': ['EV123']}}]}
        contract = direct_planner_schema(catalog).model_json_schema()
        Draft202012Validator.check_schema(contract)
        Draft202012Validator(contract).validate(raw)
        model = FakeModel([json.dumps(raw)])
        result = plan_reports(model, {'question': 'من حذف الملفات؟', 'catalog': catalog})
        self.assertEqual('audit', result['reports'][0]['operation'])
        self.assertEqual(['EV123'], result['reports'][0]['audit']['eventTypes'])
        self.assertIn('Recycle Entry', model.calls[0][1].content)
        self.assertIn('EV123', model.calls[0][1].content)

    def test_independent_count_and_statistics_grammar_accept_audit_tool(self):
        catalog = {'fields': [], 'auditTrail': {'status': 'available', 'eventTypes': [{'value': 'EV123', 'label': 'Recycle Entry'}]}}
        for kind, audit, count in [('count', {'eventTypes': ['EV123']}, True),
                                   ('statistics', {'groupBy': 'userName'}, False)]:
            intent = QuestionIntent.model_validate({'outputs': [{'resultType': kind, 'meaning': 'أحداث التدقيق',
                'conditionShape': 'none', 'requestText': 'أحداث التدقيق'}]})
            schema = planner_schema_for_catalog(catalog, intent).model_json_schema()
            Draft202012Validator.check_schema(schema)
            raw = {'outputs': {'output0': {'operation': 'audit', 'title': 'تقرير التدقيق',
                'selection': {'requiresFilter': False}, 'audit': audit, 'countOnly': count}}}
            Draft202012Validator(schema).validate(raw)
