"""Protocol regressions. These do not measure Qwen's semantic accuracy."""
import json
import unittest
from jsonschema import Draft202012Validator, ValidationError
from server import direct_planner_schema, planner_schema_for_catalog, plan_reports
from test_graph import FakeModel
from test_plan_intent_review import review

CATALOG = {'fields': [{'name': 'انتهاء صلاحية السجل', 'fieldType': 'Date'},
                      {'name': 'التكلفة', 'fieldType': 'Number'}]}

class LivePlannerTests(unittest.TestCase):
    def test_shared_contract_preserves_live_types_and_required_selection(self):
        schema = direct_planner_schema(CATALOG).model_json_schema()
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        plan = {'reports': [{'operation': 'search', 'title': 'العدد', 'countOnly': True,
                'selection': {'requiresFilter': True, 'filters': {
                    'field': 'انتهاء صلاحية السجل', 'operator': 'less_than', 'value': '2042-01-01'}}}]}
        validator.validate(plan)
        for field, value in [('حقل غير موجود', '2042-01-01'), ('التكلفة', '2042-01-01')]:
            invalid = json.loads(json.dumps(plan))
            invalid['reports'][0]['selection']['filters'].update(field=field, value=value)
            with self.assertRaises(ValidationError): validator.validate(invalid)
        invalid = json.loads(json.dumps(plan)); invalid['reports'][0]['selection'] = {'requiresFilter': True}
        with self.assertRaises(ValidationError): validator.validate(invalid)
        self.assertLess(len(json.dumps(schema)), len(json.dumps(planner_schema_for_catalog(CATALOG).model_json_schema())))
        self.assertNotIn('RoutePlan', schema['$defs'])

    def test_clarification_is_delivered_without_a_second_model_veto(self):
        question = 'اعرض المنتهية حتى السنة المذكورة'
        response = {'reports': [{'operation': 'clarify', 'title': 'توضيح',
                    'selection': {'requiresFilter': False}}], 'clarification': 'أي سنة تقصد؟'}
        model = FakeModel([json.dumps(response)])
        result = plan_reports(model, {'question': question, 'catalog': CATALOG}, review_intent=True)
        self.assertEqual(len(model.calls), 1)
        self.assertEqual(result['clarificationQuestion'], question)
        self.assertEqual(result['clarification'], response['clarification'])

    def test_reply_uses_original_context_without_quote_extraction_gate(self):
        original = 'كم سجل انتهت صلاحيته حتى 2041؟'
        history = [{'role': 'user', 'text': original}, {'role': 'assistant', 'text': 'أي تقويم؟',
                   'kind': 'clarification', 'clarificationQuestion': original}]
        draft = {'reports': [{'operation': 'search', 'title': 'العدد', 'countOnly': True,
                'selection': {'requiresFilter': True, 'filters': {'field': 'انتهاء صلاحية السجل',
                'operator': 'less_than', 'value': '2042-01-01'}}}]}
        model = FakeModel([json.dumps(draft), json.dumps(review())])
        result = plan_reports(model, {'question': 'ميلادي', 'history': history, 'catalog': CATALOG}, review_intent=True)
        sent = json.loads(model.calls[0][1].content)
        self.assertEqual(sent['clarificationContext']['question'], original)
        self.assertNotIn('questionIntent', sent)
        self.assertIn('JSON Schema:', model.calls[0][0].content)
        self.assertEqual(len(model.calls), 2)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertEqual(result['reports'][0]['filters']['value'], '2042-01-01')
