"""Regression and pipeline contract tests; not a real-LLM accuracy benchmark."""
import copy
import json
from pathlib import Path
import unittest
from calendar_grounding import relative_period, add_missing_period, period_field
from catalog_grounding import ground_catalog_scope, mentioned_templates
from semantic_expansion import language_evidence, contextual_folder_question, deletion_reading
from scope_grounding import folder_scope
from server import plan_reports, ground_expanded_recycle_bin, planner_request
from test_graph import FakeModel

CATALOG = {'fields': [{'name': 'حالة الطلب', 'fieldType': 'List', 'listValues': ['Accepted', 'Pending', 'Rejected']},
                      {'name': 'هل تريد ارشفة الوثيقة؟', 'fieldType': 'List', 'listValues': ['نعم', 'لا']}],
           'templates': ['القرار', 'طلب التدريب'], 'recycleBin': {'status': 'available'}}


def draft(**selection):
    return {'reports': [{'operation': 'search', 'title': 'نتائج',
                         'selection': {'requiresFilter': bool(selection), **selection}}]}


class AdditiveLanguageTests(unittest.TestCase):
    def test_saudi_dates_cover_real_calendar_boundaries(self):
        for question, period in [('البارح', ('day', -1)), ('قبل أمس', ('day', -2)), ('أمس الأول', ('day', -2)),
                ('هالشهر', ('month', 0)), ('الشهر ذا', ('month', 0)), ('الشهر اللي راح', ('month', -1)),
                ('هالأسبوع', ('week', 0)), ('الأسبوع ذا', ('week', 0)), ('هالسنة', ('year', 0)),
                ('السنة ذي', ('year', 0)), ('السنة اللي راحت', ('year', -1))]:
            with self.subTest(question=question):
                self.assertEqual(relative_period(question), period)
        self.assertEqual(add_missing_period(None, 'وش الملفات اللي انعملت البارح', '2026-01-01')['period'],
                         {'year': 2025, 'month': 12, 'day': 31})

    def test_creation_and_modification_are_not_interchanged(self):
        for word in ('انعملت', 'انضافت', 'اتنشأت'):
            self.assertEqual(period_field('الملفات اللي ' + word), 'created')
        for word in ('اتحدثت', 'انعدل عليها', 'تعدلت', 'تغيرت'):
            self.assertEqual(period_field('الملفات اللي ' + word), 'modified')
        self.assertIsNone(period_field('انعملت أمس واتعدلت اليوم'))

    def test_negated_verbs_do_not_acquire_positive_periods(self):
        for word in ('ما', 'مو', 'مب', 'موب', 'مش', 'لم', 'لا'):
            for verb in ('انعملت', 'اتعدلت'):
                with self.subTest(word=word, verb=verb):
                    self.assertIsNone(add_missing_period(None, f'الملفات اللي {word} {verb} اليوم', '2026-11-02'))
        # Interrogative «ما» is not a negation.
        self.assertEqual(add_missing_period(None, 'ما الوثائق التي أنشئت اليوم', '2026-11-02')['field'], 'created')

    def test_vague_or_multiple_dates_remain_unresolved(self):
        for text in ('توها انضافت', 'قبل فترة', 'من زمان', 'آخر كم يوم', 'أمس أو اليوم', 'من بداية هالشهر'):
            self.assertIsNone(relative_period(text))

    def test_extended_template_aliases_ground_live_names_only(self):
        for word in ('تيمبليت', 'تيكبليت', 'تمبلييت', 'كرت فهرستها', 'قالب الفهرسة', 'metadata template'):
            # قالب الفهرسة is a compound cue; it must never invent a template named الفهرسة.
            question = f'الوثائق اللي {word} القرار'
            with self.subTest(word=word):
                self.assertEqual(mentioned_templates(question, CATALOG), ['القرار'])
        self.assertEqual(mentioned_templates('وثائق تيمبليت غير موجود', CATALOG), [])
        self.assertEqual(mentioned_templates('وثائق قالب الفهرسة', {'templates': ['الفهرسة']}), ['الفهرسة'])

    def test_gulf_template_association_and_negation(self):
        self.assertEqual(mentioned_templates('documents اللي template حقها القرار', CATALOG), ['القرار'])
        plan = {'operation': 'search', 'template': None, 'filters': None, 'name': None}
        for word in ('مو', 'مب', 'موب'):
            result = ground_catalog_scope(plan, f'الوثائق اللي قالبها {word} القرار', CATALOG)
            self.assertEqual(result['filters']['operator'], 'not_equals')

    def test_file_container_is_contextual(self):
        for question in ('ابي اللي جوه ملف الوارد', 'وش اللي داخل ملف الفهرسة؟', 'طلع مستندات ملف التدريب'):
            view = contextual_folder_question(question)
            self.assertIsNotNone(folder_scope(view))
        for question in ('افتح ملف العقد', 'افتح ملف PDF', 'اعرض ملف اسمه الوارد', 'الوثائق اللي مو داخل ملف الوارد'):
            self.assertEqual(contextual_folder_question(question), question)
        question = 'وثائق قالب ملف العقود اللي داخل ملف الوارد'
        self.assertEqual(contextual_folder_question(question, ['ملف العقود']), question)

    def test_archive_location_does_not_replace_old_business_definition(self):
        self.assertEqual(folder_scope('الوثائق الموجودة داخل مركز الوثائق والمحفوظات'), ('مركز الوثائق والمحفوظات', False))
        self.assertIsNone(folder_scope('ما هي الوثائق المؤرشفة'))
        self.assertIsNone(folder_scope('الوثائق اللي مو داخل مركز الوثائق والمحفوظات'))
        self.assertIsNone(folder_scope('متى تم نقل الوثيقة إلى مركز الوثائق والمحفوظات'))

    def test_scan_history_negation_and_purge_are_not_current_bin(self):
        for question in ('وثائق المسح الضوئي', 'الوثائق اللي انمسحت سكانر', 'مين حذف الوثيقة 619',
                         'الملفات اللي انحذفت نهائيا', 'الوثائق اللي تم استعادتها', 'الملفات اللي مو محذوفة',
                         'الوثائق المحذوفة من الفهرس'):
            self.assertIsNone(deletion_reading(question), question)
        self.assertEqual(deletion_reading('اعرض الملفات اللي انحذفت'), 'current_recycle_bin')

    def test_twenty_colloquial_requests_preserve_text_and_catalog(self):
        # Tests evidence extraction is non-destructive, not that a fake model understands these.
        questions = ('وش الملفات اللي انعملت اليوم؟', 'عطني الوثايق اللي اتعدلت أمس', 'طلعلي اللي انمسحت من السلة',
            'وش عندنا بالمخزن؟', 'ابي اللي جوه فولدر الوارد', 'الملفات اللي ما عليها كرت', 'كم طلب تدريب انقبل؟',
            'الطلبات اللي للحين ما اعتمدوها', 'الملفات اللي اتارشفت هالشهر', 'وش اللي داخل ملف الفهرسة؟',
            'طيب واللي اتعدلت منها؟', 'خلها بس حق الشهر اللي فات', 'أبي الملفات اللي لا انقبلت ولا انرفضت',
            'وش التيمبليتات الموجودة عندنا؟', 'الملفات اللي status حقها pending', 'وش صار على الوثيقة 619؟',
            'كم ملف انحذف البارح؟', 'الملفات اللي توها انضافت', 'وريني آخر 5 ملفات اتحدثت', 'لا، قصدي المجلد مو الوثيقة')
        before = copy.deepcopy(CATALOG)
        for question in questions:
            self.assertIsInstance(language_evidence(question, CATALOG)['concepts'], dict)
        self.assertEqual(before, CATALOG)


class SemanticPipelineTests(unittest.TestCase):
    def test_live_choices_original_question_and_business_policy_reach_both_models(self):
        from test_plan_intent_review import review
        question = 'كم طلب تدريب انقبل؟'
        model = FakeModel([json.dumps(draft(filters={'field': 'حالة الطلب', 'operator': 'equals', 'value': 'Accepted'})), json.dumps(review())])
        plan_reports(model, {'question': question, 'catalog': CATALOG}, review_intent=True)
        for call in model.calls:
            sent = json.loads(call[1].content)
            self.assertEqual(sent['originalQuestion'], question)
            self.assertEqual(sent['catalog']['listChoices']['حالة الطلب'], ['Accepted', 'Pending', 'Rejected'])
            self.assertTrue(any(t['condition']['field'] == 'هل تريد ارشفة الوثيقة؟' for t in sent['businessGlossary']))

    def test_missing_colloquial_date_is_grounded_without_extra_model_call(self):
        model = FakeModel([json.dumps(draft())])
        result = plan_reports(model, {'question': 'وش الملفات اللي انعملت هالشهر؟', 'today': '2026-11-02', 'catalog': CATALOG})
        self.assertEqual(result['reports'][0]['filters']['field'], 'created')
        self.assertEqual(result['reports'][0]['filters']['period'], {'year': 2026, 'month': 11, 'day': None})
        self.assertEqual(len(model.calls), 1)

    def test_dated_deletion_never_returns_all_bin_or_filters_creation(self):
        for operation in ('search', 'recycle_bin'):
            request = planner_request(json.dumps({'reports': [{'operation': operation, 'title': 'نتائج', 'selection': {'requiresFilter': False}}]}), 'الملفات المحذوفة')
            result = ground_expanded_recycle_bin(request, 'الملفات اللي انحذفت اليوم', CATALOG)
            self.assertEqual(result.reports[0].operation, 'clarify')
            self.assertIn('بتاريخ الحذف', result.clarification)

    def test_plain_deleted_alias_respects_source_availability(self):
        for status, expected in [('available', 'recycle_bin'), ('unavailable', 'clarify')]:
            result = ground_expanded_recycle_bin(planner_request(json.dumps(draft()), 'الملفات المحذوفة'), 'الملفات اللي انحذفت',
                                                {'recycleBin': {'status': status}})
            self.assertEqual(result.reports[0].operation, expected)

    def test_original_question_survives_spelling_correction(self):
        question = 'وثائق قلاب القرار'
        model = FakeModel([json.dumps(draft(template='القرار'))])
        plan_reports(model, {'question': question, 'catalog': CATALOG})
        sent = json.loads(model.calls[0][1].content)
        self.assertEqual(sent['originalQuestion'], question)
        self.assertEqual(sent['question'], 'وثائق قالب القرار')

    def test_glossary_is_in_all_deployment_paths(self):
        root = Path(__file__).parents[2]
        for path in ('deployment/windows/install.ps1', 'deployment/windows/build-package.ps1',
                     'deployment/docker/Dockerfile', 'build/publish.ps1'):
            self.assertIn('glossary.json', (root / path).read_text())


class BinConstraintTests(unittest.TestCase):
    def test_template_filter_is_not_discarded_to_return_whole_bin(self):
        request = planner_request(json.dumps(draft(template='القرار')), 'المحذوفة بقالب القرار')
        result = ground_expanded_recycle_bin(request, 'المحذوفة بقالب القرار', CATALOG)
        self.assertEqual(result.reports[0].operation, 'clarify')

    def test_history_question_cannot_become_current_bin_inventory(self):
        request = planner_request(json.dumps(draft()), 'مين حذف الوثائق من سلة المحذوفات')
        result = ground_expanded_recycle_bin(request, 'مين حذف الوثائق من سلة المحذوفات', CATALOG)
        self.assertEqual(result.reports[0].operation, 'clarify')
        self.assertIn('سجل أحداث', result.clarification)
