import json
import unittest

from jsonschema import Draft202012Validator, ValidationError
from server import QuestionIntent, planner_schema_for_catalog, plan_reports, validate_question_intent, planner_request
from test_graph import FakeModel
from test_plan_intent_review import review

CATALOG = {'fields': [{'name': 'موعد انتهاء السريان - ميلادي', 'fieldType': 'Date'}]}


def intent(kind='count', shape='upper_bound'):
    return {'outputs': [{'resultType': kind, 'meaning': 'عدد السجلات التي تنتهي في السنة المحددة أو قبلها', 'conditionShape': shape}]}


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


class IndependentIntentTests(unittest.TestCase):
    def test_one_sided_count_grammar_excludes_grouping_and_invented_range(self):
        schema = planner_schema_for_catalog(CATALOG, QuestionIntent.model_validate(intent())).model_json_schema()
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema).validate(query())
        for invalid in [query('group'), query(operator='between'), query(operator='greater_than')]:
            with self.subTest(invalid=invalid), self.assertRaises(ValidationError):
                Draft202012Validator(schema).validate(invalid)
        listing = query(); listing['reports'][0]['countOnly'] = False
        with self.assertRaises(ValidationError):
            Draft202012Validator(schema).validate(listing)

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
        self.assertEqual(json.loads(model.calls[3][1].content)['questionIntent'], intent())

    def test_legitimate_aggregation_and_explicit_ranges_remain_available(self):
        schema = planner_schema_for_catalog(CATALOG, QuestionIntent.model_validate(intent('statistics', 'range'))).model_json_schema()
        grouped = query('group', 'between')
        Draft202012Validator(schema).validate(grouped)
        validate_question_intent(planner_request(json.dumps(grouped), 'وزع حسب التاريخ خلال فترة'),
                                 QuestionIntent.model_validate(intent('statistics', 'range')))

    def test_unresolved_field_can_still_clarify(self):
        schema = planner_schema_for_catalog({}, QuestionIntent.model_validate(intent())).model_json_schema()
        clarification = {'reports': [{'operation': 'clarify', 'title': 'توضيح', 'selection': {'requiresFilter': False}}],
                         'clarification': 'ما المقصود بمعيار السريان؟'}
        Draft202012Validator(schema).validate(clarification)
        validate_question_intent(planner_request(json.dumps(clarification), 'سؤال'), QuestionIntent.model_validate(intent()))
