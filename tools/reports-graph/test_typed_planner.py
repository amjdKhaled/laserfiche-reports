import json
import unittest
from jsonschema import Draft202012Validator
from server import planner_request, validate_plan_schema, planner_schema_for_catalog, plan_reports, canonicalize_property_names
from test_graph import FakeModel

CATALOG = {'fields': [{'name': 'أجل الحفظ', 'fieldType': 'Date'},
                       {'name': 'مدة النشاط', 'fieldType': 'Integer'},
                       {'name': 'حالة السجل', 'fieldType': 'String'}],
           'entryProperties': ['name', 'created', 'entryId', 'pageCount'], 'templates': []}

def plan(filters=None, **options):
    return {'reports': [{'operation': 'search', 'title': 'تقرير', 'selection':
        {'requiresFilter': True, 'filters': filters} if filters else {'requiresFilter': False}, **options}]}

class TypedPlannerTests(unittest.TestCase):
    def test_api_date_alias_is_canonicalized_without_a_second_model_call(self):
        output = plan({'field': 'creationTime', 'operator': 'less_than', 'value': '2037-01-01'}, countOnly=True)
        model = FakeModel([json.dumps(output)])
        result = plan_reports(model, {'question': 'كم وثيقة منشأة في 2036 وما أقل؟', 'catalog': CATALOG})
        self.assertEqual(len(model.calls), 1)
        self.assertEqual(result['reports'][0]['filters']['field'], 'created')
        self.assertEqual(result['reports'][0]['resultType'], 'count')

    def test_alias_never_overwrites_real_metadata_field(self):
        catalog = {**CATALOG, 'fields': CATALOG['fields'] + [{'name': 'creationTime', 'fieldType': 'Integer'}]}
        request = planner_request(json.dumps(plan({'field': 'creationTime', 'operator': 'equals', 'value': '2036'})), 'سؤال')
        canonicalize_property_names(request, catalog)
        validate_plan_schema(request, catalog)
        self.assertEqual(request.reports[0].filters.field, 'creationTime')

    def test_all_field_slots_and_templates_use_live_names(self):
        validator = Draft202012Validator(planner_schema_for_catalog(CATALOG).model_json_schema())
        for output in [plan(sortField='creationTime'), plan(field='حالة السجل'),
                       plan(sortField='غير موجود')]:
            with self.subTest(output=output): self.assertFalse(validator.is_valid(output))
        for slot in ['groupFields', 'metrics']:
            output = plan(operation='group', **{slot: [{'field': 'غير موجود', **({'function': 'count'} if slot == 'metrics' else {})}]})
            self.assertFalse(validator.is_valid(output))
        output = plan()
        output['reports'][0]['selection'] = {'requiresFilter': True, 'template': 'قالب مخترع'}
        self.assertFalse(validator.is_valid(output))
        self.assertTrue(validator.is_valid(plan(sort='creationTime desc', allResults=False, limit=1)))

    def test_expiry_count_is_one_output_and_preserves_the_named_metadata_field(self):
        output = plan({'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2037-01-01'}, countOnly=True)
        model = FakeModel([json.dumps(output)])
        result = plan_reports(model, {'question': 'كم وثيقة أجل الحفظ فيها لغاية 2036 وما أقل؟', 'catalog': CATALOG})
        actual = result['reports'][0]
        self.assertEqual(len(result['reports']), 1)
        self.assertTrue(actual['countOnly'])
        self.assertEqual(actual['filters']['field'], 'أجل الحفظ')
        self.assertIsNone(actual['sort'])
        self.assertIn('وما أقل', model.calls[0][0].content)

    def test_generation_grammar_uses_actual_field_names_and_types(self):
        validator = Draft202012Validator(planner_schema_for_catalog(CATALOG).model_json_schema())
        for condition in [
            {'field': 'مدة النشاط', 'operator': 'less_than', 'value': '2036-12-31'},
            {'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2036'},
            {'field': 'حالة السجل', 'operator': 'less_than', 'value': 'نشط'},
            {'field': 'غير موجود', 'operator': 'equals', 'value': 'نشط'},
            {'field': 'مدة النشاط', 'operator': 'less_than', 'relative': {'unit': 'year'}}]:
            with self.subTest(condition=condition): self.assertFalse(validator.is_valid(plan(condition)))
        self.assertTrue(validator.is_valid(plan({'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2037-01-01'}, countOnly=True)))

    def test_backend_equivalent_checks_reject_invalid_calendar_integer_and_bounds(self):
        for condition in [
            {'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2036-02-30'},
            {'field': 'مدة النشاط', 'operator': 'equals', 'value': '1.5'},
            {'field': 'مدة النشاط', 'operator': 'between', 'value': '10', 'upper': '1'},
            {'field': 'أجل الحفظ', 'operator': 'date_between', 'value': '2036-01-01'},
            {'field': 'مدة النشاط', 'operator': 'less_than', 'value': '1', 'upper': '2'}]:
            with self.subTest(condition=condition), self.assertRaises(ValueError):
                validate_plan_schema(planner_request(json.dumps(plan(condition)), 'سؤال'), CATALOG)

    def test_named_folder_count_has_one_selection_and_never_matches_document_name(self):
        output = {'reports': [{'operation': 'search', 'title': 'عدد وثائق المركز',
            'selection': {'requiresFilter': True, 'folderName': 'مركز الوثائق والمحفوظات'},
            'includeSubfolders': True, 'countOnly': True}]}
        model = FakeModel([json.dumps(output)])
        result = plan_reports(model, {'question': 'كم عدد الوثائق الموجودة في مركز الوثائق والمحفوظات؟', 'catalog': CATALOG})
        self.assertEqual(len(model.calls), 1)
        self.assertEqual(len(result['reports']), 1)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertEqual(result['reports'][0]['folderName'], 'مركز الوثائق والمحفوظات')
        self.assertIsNone(result['reports'][0]['name'])

    def test_invalid_typed_plan_is_repaired_without_running_repository_query(self):
        invalid = plan({'field': 'مدة النشاط', 'operator': 'less_than', 'value': '2036-12-31'}, countOnly=True)
        clarify = {'reports': [{'operation': 'clarify', 'title': 'توضيح معيار النشاط', 'selection': {'requiresFilter': False}}],
                   'clarification': 'هل تقصد تاريخ انتهاء الحفظ أم مدة النشاط؟'}
        model = FakeModel([json.dumps(invalid), json.dumps(clarify)])
        result = plan_reports(model, {'question': 'كم وثيقة نشطة لغاية 2036 وما أقل؟', 'catalog': CATALOG})
        self.assertEqual(result['reports'][0]['operation'], 'clarify')
        self.assertIn('Numeric fields cannot be compared', model.calls[1][-1].content)

    def test_operation_union_forbids_unrequested_aggregation_on_search(self):
        validator = Draft202012Validator(planner_schema_for_catalog(CATALOG).model_json_schema())
        for option in [{'metrics': [{'function': 'count'}]}, {'groupFields': [{'field': 'حالة السجل'}]},
                       {'rollup': 'sum'}, {'having': {'metric': 0, 'operator': 'greater_than', 'value': 1}}]:
            with self.subTest(option=option): self.assertFalse(validator.is_valid(plan(**option)))
        self.assertTrue(validator.is_valid(plan(allResults=True)))
        grouped = plan(); grouped['reports'][0].update(operation='group', groupFields=[{'field': 'حالة السجل'}], metrics=[{'function': 'count'}])
        self.assertTrue(validator.is_valid(grouped))

    def test_bounds_grammar_requires_range_and_exactly_one_upper_bound(self):
        validator = Draft202012Validator(planner_schema_for_catalog(CATALOG).model_json_schema())
        for condition in [
            {'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2037-01-01', 'upper': '2038-01-01'},
            {'field': 'أجل الحفظ', 'operator': 'between', 'value': '2036-01-01'},
            {'field': 'أجل الحفظ', 'operator': 'between', 'value': '2036-01-01', 'upper': '2037-01-01', 'upperRelative': {'unit': 'year'}},
            {'field': 'مدة النشاط', 'operator': 'between', 'value': '1', 'upper': '2036-01-01'}]:
            with self.subTest(condition=condition): self.assertFalse(validator.is_valid(plan(condition)))
        for condition in [
            {'field': 'أجل الحفظ', 'operator': 'less_than', 'value': '2037-01-01'},
            {'field': 'أجل الحفظ', 'operator': 'between', 'value': '2036-01-01', 'upper': '2037-01-01'},
            {'field': 'أجل الحفظ', 'operator': 'date_between', 'relative': {'unit': 'month'}, 'upperRelative': {'unit': 'month', 'boundary': 'end'}},
            {'field': 'مدة النشاط', 'operator': 'between', 'value': '1', 'upper': '10'}]:
            with self.subTest(condition=condition): self.assertTrue(validator.is_valid(plan(condition)))

    def test_inventory_draft_stays_listing_with_one_planning_call(self):
        model = FakeModel([json.dumps(plan(allResults=True))])
        result = plan_reports(model, {'question': 'اعطني تقرير عن كل الوثائق الموجودة في هذا المخزن', 'catalog': CATALOG})
        self.assertEqual(len(model.calls), 1)
        actual = result['reports'][0]
        self.assertEqual(actual['operation'], 'search')
        self.assertEqual(actual['resultType'], 'documents')
        self.assertTrue(actual['allResults'])
        self.assertEqual(actual['metrics'], [])
        self.assertIsNone(actual['rollup'])
