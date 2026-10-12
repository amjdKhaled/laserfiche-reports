import json
import unittest
from jsonschema import Draft202012Validator, ValidationError
from qa_contracts import case
from server import planner_request, validate_plan_schema, plan_reports, direct_planner_schema
from test_graph import FakeModel
from test_plan_intent_review import review


class ContractRegressionTests(unittest.TestCase):
    def test_generated_failure_families_are_rejected_before_execution(self):
        for index in (11, 12, 13, 14, 17, 18):
            family, catalog, draft, _, _ = case(index, 20261012)
            with self.subTest(family=family), self.assertRaises(ValueError):
                validate_plan_schema(planner_request(json.dumps(draft), 'طلب'), catalog)

    def test_aggregation_types_are_constrained_in_generation_grammar(self):
        for index in (10, 11, 12, 13):
            _, catalog, draft, valid, _ = case(index, 20261012)
            draft['contextMode'] = 'current'
            schema = direct_planner_schema(catalog).model_json_schema()
            Draft202012Validator.check_schema(schema)
            validator = Draft202012Validator(schema)
            if valid: validator.validate(draft)
            else:
                with self.assertRaises(ValidationError): validator.validate(draft)

    def test_bad_metric_is_replanned_instead_of_becoming_a_backend_error(self):
        _, catalog, bad, _, _ = case(11, 20261012)
        good = json.loads(json.dumps(bad))
        good['reports'][0]['metrics'] = [{'function': 'count'}]
        model = FakeModel([json.dumps(bad), json.dumps(good), json.dumps(review())])
        result = plan_reports(model, {'question': 'كم وثيقة حسب الإدارة؟', 'catalog': catalog}, review_intent=True)
        self.assertEqual(result['reports'][0]['metrics'][0]['function'], 'count')
        self.assertEqual(len(model.calls), 3)
        self.assertIn('numeric field', model.calls[1][-1].content)

    def test_nonfinite_having_cannot_reach_decimal_backend(self):
        _, catalog, draft, _, _ = case(10, 20261012)
        for number in (float('inf'), float('-inf'), float('nan')):
            draft['reports'][0]['having'] = {'metric': 0, 'operator': 'greater_than', 'value': number}
            with self.assertRaises(ValueError): planner_request(json.dumps(draft), 'طلب')


if __name__ == '__main__': unittest.main()
