import json
import unittest
from pydantic import ValidationError
from jsonschema import Draft202012Validator
from server import (planner_request, direct_planner_schema, planner_schema_for_catalog,
                    QuestionIntent, plan_reports, validate_plan_schema, validate_clarification_text)
from test_graph import FakeModel

class RecycleBinTests(unittest.TestCase):
    catalog = {'fields': [], 'templates': [], 'recycleBin': {'status': 'available', 'scope': 'current_user_visible'}}
    def raw(self, count=False):
        return {'contextMode': 'current', 'reports': [{'operation': 'recycle_bin', 'title': 'وثائق السلة',
            'selection': {'requiresFilter': False}, 'entryType': 'document', 'countOnly': count}]}
    def test_listing_and_count_grammar_and_catalog_survive_compaction(self):
        for count in [False, True]:
            raw = self.raw(count)
            schema = direct_planner_schema(self.catalog).model_json_schema()
            Draft202012Validator.check_schema(schema)
            Draft202012Validator(schema).validate(raw)
            model = FakeModel([json.dumps(raw)])
            result = plan_reports(model, {'question': 'اعرض الموجود في السلة', 'catalog': self.catalog})
            self.assertEqual('count' if count else 'documents', result['reports'][0]['resultType'])
            self.assertFalse(result['reports'][0]['allResults'])
            self.assertEqual('available', json.loads(model.calls[0][1].content)['catalog']['recycleBin']['status'])
    def test_independent_intent_supports_current_bin_without_audit(self):
        for kind in ['documents', 'count']:
            intent = QuestionIntent.model_validate({'outputs': [{'resultType': kind, 'meaning': 'سلة المحذوفات',
                'conditionShape': 'none', 'requestText': 'سلة المحذوفات'}]})
            schema = planner_schema_for_catalog(self.catalog, intent).model_json_schema()
            raw = {'outputs': {'output0': self.raw(kind == 'count')['reports'][0]}}
            Draft202012Validator(schema).validate(raw)
    def test_unavailable_and_unsupported_constraints_never_become_search_or_zero(self):
        self.assertNotIn('RecycleBinPlan', direct_planner_schema({}).model_json_schema()['$defs'])
        request = planner_request(json.dumps(self.raw()), 'السلة')
        with self.assertRaises(ValueError): validate_plan_schema(request, {})
        for change in [{'name': 'secret'}, {'content': True}, {'audit': {}}, {'sort': 'id desc'}, {'requiresFilter': True}]:
            raw = request.model_dump(by_alias=True)
            raw['reports'][0].update(change)
            with self.assertRaises(ValidationError): planner_request(json.dumps(raw))
    def test_repetitive_clarification_is_repaired_once(self):
        broken = {'contextMode': 'current', 'reports': [{'operation': 'clarify', 'title': 'توضيح', 'selection': {'requiresFilter': False}}],
                  'clarification': 'هل تريد عرض السلة؟ هل تريد عرض السلة؟'}
        model = FakeModel([json.dumps(broken), json.dumps(self.raw())])
        result = plan_reports(model, {'question': 'اعرض السلة', 'catalog': self.catalog})
        self.assertEqual('recycle_bin', result['reports'][0]['operation'])
        self.assertEqual(2, len(model.calls))
    def test_short_clarification_and_repeated_long_phrase(self):
        validate_clarification_text('ما اسم المستودع المطلوب؟')
        with self.assertRaises(ValueError): validate_clarification_text('سؤال؟ آخر؟')
        with self.assertRaises(ValueError): validate_clarification_text(('هذه جملة طويلة تتكون من عشر كلمات لإعادة نفس العرض مرارا ' * 3))
