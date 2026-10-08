"""Protocol regressions. These do not measure Qwen's semantic accuracy."""
import json
import unittest
from jsonschema import Draft202012Validator, ValidationError
from server import direct_planner_schema, planner_schema_for_catalog, plan_reports, planner_request, validate_plan_schema
from test_graph import FakeModel
from test_plan_intent_review import review

CATALOG = {'fields': [{'name': 'انتهاء صلاحية السجل', 'fieldType': 'Date'},
                      {'name': 'التكلفة', 'fieldType': 'Number'}]}

class LivePlannerTests(unittest.TestCase):
    def test_direct_repair_does_not_replay_an_invalid_assistant_draft(self):
        invalid = {'reports': [{'operation': 'search', 'title': 'خطة مرفوضة',
            'selection': {'requiresFilter': True, 'filters': {'field': 'حقل مخترع', 'operator': 'equals', 'value': 'خطأ'}}}]}
        corrected = {'reports': [{'operation': 'search', 'title': 'الوثائق',
            'selection': {'requiresFilter': True, 'filters': {'field': 'created', 'operator': 'in_period', 'period': {'year': 2026}}}}]}
        model = FakeModel([json.dumps(invalid), json.dumps(corrected), json.dumps(review())])
        result = plan_reports(model, {'question': 'وثائق أنشئت خلال 2026', 'catalog': {'fields': [], 'entryProperties': ['created']}}, review_intent=True)
        self.assertEqual(result['reports'][0]['filters']['operator'], 'in_period')
        self.assertFalse(any(message.type == 'ai' for message in model.calls[1]))
        self.assertEqual(json.loads(model.calls[1][1].content)['question'], 'وثائق أنشئت خلال 2026')
        self.assertIn('Unknown repository field', model.calls[1][-1].content)

    def test_year_period_is_a_live_date_tool_without_model_invented_endpoints(self):
        catalog = {**CATALOG, 'entryProperties': ['created', 'modified', 'name']}
        draft = {'contextMode': 'current', 'reports': [{'operation': 'search', 'title': 'الوثائق',
            'selection': {'requiresFilter': True, 'filters': {
                'field': 'created', 'operator': 'in_period', 'period': {'year': 2026}}}}]}
        validator = Draft202012Validator(direct_planner_schema(catalog).model_json_schema())
        validator.validate(draft)
        result = plan_reports(FakeModel([json.dumps(draft), json.dumps(review())]),
            {'question': 'ماهي الوثائق التي انشأت بتاريخ 2026', 'catalog': catalog}, review_intent=True)
        self.assertEqual(result['reports'][0]['resultType'], 'documents')
        self.assertEqual(result['reports'][0]['filters']['period']['year'], 2026)
        self.assertIsNone(result['reports'][0]['filters']['value'])
        for field, period in [('name', {'year': 2026}), ('التكلفة', {'year': 2026}), ('created', {'year': 2023, 'month': 2, 'day': 29}),
                              ('created', {'year': 2026, 'day': 1}), ('created', {'year': True})]:
            invalid = json.loads(json.dumps(draft))
            invalid['reports'][0]['selection']['filters'].update(field=field, period=period)
            with self.assertRaises(ValueError): validate_plan_schema(planner_request(json.dumps(invalid), 'سؤال'), catalog)
        invalid = json.loads(json.dumps(draft))
        invalid['reports'][0]['selection']['filters']['value'] = '2026-01-01'
        with self.assertRaises(ValidationError): validator.validate(invalid)
        with self.assertRaises(ValueError): validate_plan_schema(planner_request(json.dumps(invalid), 'سؤال'), catalog)

    def test_calendar_period_can_combine_with_tag_and_numeric_conditions_for_one_count(self):
        catalog = {**CATALOG, 'tags': [{'name': 'قيد المتابعة'}], 'tagStatus': 'complete'}
        draft = {'contextMode': 'current', 'reports': [{'operation': 'search', 'title': 'العدد', 'countOnly': True,
            'selection': {'requiresFilter': True, 'filters': {'logic': 'and', 'conditions': [
                {'field': 'انتهاء صلاحية السجل', 'operator': 'through_period', 'period': {'year': 2042}},
                {'tag': 'قيد المتابعة', 'operator': 'has_tag'},
                {'field': 'التكلفة', 'operator': 'less_or_equal', 'value': '500'}]}}}]}
        Draft202012Validator(direct_planner_schema(catalog).model_json_schema()).validate(draft)
        result = plan_reports(FakeModel([json.dumps(draft), json.dumps(review())]),
            {'question': 'كم سجل ينتهي حتى 2042 وعليه وسم قيد المتابعة وتكلفته 500 أو أقل؟', 'catalog': catalog}, review_intent=True)
        self.assertEqual(len(result['reports']), 1)
        self.assertEqual(result['reports'][0]['resultType'], 'count')
        self.assertEqual(len(result['reports'][0]['filters']['conditions']), 3)

    def test_shared_contract_preserves_live_types_and_required_selection(self):
        schema = direct_planner_schema(CATALOG).model_json_schema()
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        plan = {'contextMode': 'current', 'reports': [{'operation': 'search', 'title': 'العدد', 'countOnly': True,
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


class LiveTagTests(unittest.TestCase):
    def test_tag_namespace_is_dynamic_and_distinct_from_metadata(self):
        catalog = {**CATALOG, 'tags': [{'name': 'بحاجة لمراجعة'}, {'name': 'تم اعتماده'}], 'tagStatus': 'complete'}
        schema = direct_planner_schema(catalog).model_json_schema()
        validator = Draft202012Validator(schema)
        draft = {'contextMode': 'current', 'reports': [{'operation': 'search', 'title': 'النتائج',
            'countOnly': True, 'selection': {'requiresFilter': True, 'filters': {'logic': 'and', 'conditions': [
                {'tag': 'بحاجة لمراجعة', 'operator': 'has_tag'},
                {'tag': 'تم اعتماده', 'operator': 'not_tag'},
                {'field': 'انتهاء صلاحية السجل', 'operator': 'less_than', 'value': '2042-01-01'}]}}}]}
        validator.validate(draft)
        parsed = planner_request(json.dumps(draft), 'عدد المطابقة')
        validate_plan_schema(parsed, catalog)
        self.assertEqual(parsed.reports[0].filters.conditions[0].tag, 'بحاجة لمراجعة')
        unknown = json.loads(json.dumps(draft))
        unknown['reports'][0]['selection']['filters']['conditions'][0]['tag'] = 'غير معرف'
        with self.assertRaises(ValidationError): validator.validate(unknown)
        with self.assertRaises(ValueError): validate_plan_schema(planner_request(json.dumps(unknown), 'عدد المطابقة'), catalog)
        metadata = json.loads(json.dumps(draft))
        metadata['reports'][0]['selection']['filters']['conditions'][0] = {
            'field': 'بحاجة لمراجعة', 'operator': 'equals', 'value': 'نعم'}
        with self.assertRaises(ValidationError): validator.validate(metadata)
        with self.assertRaises(ValueError): validate_plan_schema(planner_request(json.dumps(metadata), 'عدد المطابقة'), catalog)
        without_tags = direct_planner_schema(CATALOG).model_json_schema()
        with self.assertRaises(ValidationError): Draft202012Validator(without_tags).validate(draft)

    def test_catalog_and_predicate_reach_reviewer_without_fixed_tag_names(self):
        catalog = {'fields': [], 'tags': [{'name': 'قيد الفحص', 'description': 'وسم عمل محلي'}], 'tagStatus': 'complete'}
        draft = {'contextMode': 'current', 'reports': [{'operation': 'search', 'title': 'العدد', 'countOnly': True,
            'selection': {'requiresFilter': True, 'filters': {'tag': 'قيد الفحص', 'operator': 'has_tag'}}}]}
        model = FakeModel([json.dumps(draft), json.dumps(review())])
        result = plan_reports(model, {'question': 'كم منها قيد الفحص؟', 'catalog': catalog}, review_intent=True)
        self.assertEqual(result['reports'][0]['filters']['tag'], 'قيد الفحص')
        for call in model.calls:
            sent = json.loads(call[1].content)
            self.assertEqual(sent['catalog']['tagStatus'], 'complete')
            self.assertEqual(sent['catalog']['tags'], catalog['tags'])
        reviewed = json.loads(model.calls[1][1].content)
        self.assertEqual(reviewed['contextMode'], 'current')
        self.assertEqual(reviewed['proposedPlan']['reports'][0]['filters']['tag'], 'قيد الفحص')

    def test_invalid_mixed_tag_and_field_predicate_never_executes(self):
        draft = {'reports': [{'operation': 'search', 'title': 'العدد', 'selection': {'requiresFilter': True,
            'filters': {'tag': 'وسم', 'field': 'التكلفة', 'operator': 'has_tag', 'value': '1'}}}]}
        with self.assertRaises(ValueError):
            validate_plan_schema(planner_request(json.dumps(draft), 'طلب'), {**CATALOG, 'tags': [{'name': 'وسم'}]})

    def test_new_question_does_not_inherit_pending_clarification_root(self):
        previous = 'كم وثيقة قبل السنة المحددة؟'
        history = [{'role': 'user', 'text': previous}, {'role': 'assistant', 'kind': 'clarification',
            'text': 'أي سنة؟', 'clarificationQuestion': previous}]
        for mode, expected in [('current', 'أعطني تقريرًا آخر'), ('clarification_reply', previous)]:
            draft = {'contextMode': mode, 'reports': [{'operation': 'clarify', 'title': 'توضيح',
                'selection': {'requiresFilter': False}}], 'clarification': 'أي تاريخ تريد؟'}
            result = plan_reports(FakeModel([json.dumps(draft)]), {'question': 'أعطني تقريرًا آخر',
                'history': history, 'catalog': CATALOG}, review_intent=True)
            self.assertEqual(result['clarificationQuestion'], expected)
