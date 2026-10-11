import json
import unittest
from clarification_options import suggestions, clarification
from server import plan_reports
from test_graph import FakeModel

CATALOG = {'fields': [{'name': 'تاريخ انتهاء العقد', 'fieldType': 'Date'},
                      {'name': 'تاريخ استلام العقد', 'fieldType': 'Date'},
                      {'name': 'الميزانية', 'fieldType': 'Number'}]}

class ClarificationOptionsTests(unittest.TestCase):
    def test_suggests_only_real_labels_and_handles_typo(self):
        items = suggestions('تاريخ انتها العقد', CATALOG)
        self.assertEqual(items[0], ('حقل', 'تاريخ انتهاء العقد'))
        self.assertNotIn(('حقل', 'الميزانية'), items)
        self.assertLessEqual(len(items), 3)

    def test_no_match_asks_for_kind_without_inventing_labels(self):
        self.assertIn('اسم الوثيقة', clarification('xyz', CATALOG))
        self.assertNotIn('تاريخ انتهاء العقد', clarification('xyz', CATALOG))

    def test_two_invalid_plans_return_clarification_never_execute(self):
        bad = json.dumps({'reports': [{'operation': 'search', 'title': 'تقرير',
            'selection': {'requiresFilter': True, 'filters': {'field': 'invented', 'operator': 'equals', 'value': 'x'}}}]})
        result = plan_reports(FakeModel([bad, bad]), {'question': 'تاريخ انتهاء العقد', 'catalog': CATALOG})
        self.assertEqual(result['reports'][0]['operation'], 'clarify')
        self.assertIn('تاريخ انتهاء العقد', result['clarification'])
        self.assertEqual(result['clarificationQuestion'], 'تاريخ انتهاء العقد')

    def test_dependency_failure_stays_an_error(self):
        class Offline:
            def invoke(self, *_): raise TimeoutError('offline')
        with self.assertRaises(TimeoutError):
            plan_reports(Offline(), {'question': 'العقد', 'catalog': CATALOG})

    def test_menu_choice_reaches_planner_with_original_request(self):
        model = FakeModel([json.dumps({'contextMode': 'clarification_reply', 'reports': [
            {'operation': 'search', 'countOnly': True, 'title': 'عدد الوثائق', 'selection': {'requiresFilter': True,
             'filters': {'field': 'تاريخ استلام العقد', 'operator': 'through_period', 'period': {'year': 2036}}}}]})])
        result = plan_reports(model, {'question': '٢', 'catalog': CATALOG, 'history': [
            {'role': 'user', 'text': 'كم وثيقة حتى 2036؟'},
            {'role': 'assistant', 'kind': 'clarification', 'clarificationQuestion': 'كم وثيقة حتى 2036؟',
             'text': '1. تاريخ انتهاء العقد\n2. تاريخ استلام العقد'}]})
        context = json.loads(model.calls[0][1].content)['clarificationContext']
        self.assertEqual(context['selectedOption'], 'تاريخ استلام العقد')
        self.assertEqual(context['question'], 'كم وثيقة حتى 2036؟')
        self.assertTrue(result['reports'][0]['countOnly'])
        self.assertEqual(result['reports'][0]['filters']['period']['year'], 2036)
