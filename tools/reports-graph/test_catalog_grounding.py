import unittest

from catalog_grounding import ground_catalog_scope, mentioned_tags, mentioned_templates
from scope_grounding import folder_scope, ground_folder_scope

CATALOG = {
    "templates": ["القرار", "التعميم", "SASO (2)", "SASO (5)", "طلب ترشيح لدورة تدريبية", "طلب ترشيح لدورة تدريبية(اخضر)",
                  "المركز الوطني للوثائق و المحفوظات", "المركز الوطني للوثائق و المحفوظات (احمر)", "General", "Tracking",
                  "مجلد - سري", "مجلد - سري للغاية"],
    "tags": [{"name": "تم الإعتماد"}, {"name": "تم الرفض"}, {"name": "Test"}, {"name": "Test Water mark"}],
}
SEARCH = {"operation": "search", "requiresFilter": False, "template": None, "name": None, "filters": None}


class MentionedTemplateTests(unittest.TestCase):
    def test_loose_wording_names_the_live_template(self):
        # Seen 2026-10-29: these came back as the whole repository.
        cases = {
            "اعرض الوثائق التي قالبها القرار": ["القرار"],
            "هات الملفات اللي التمبلت بتاعها القرار": ["القرار"],
            "الملفات اللي تمبلت قرار": ["القرار"],
            "القالب اسمه \"القرار\"": ["القرار"],
            "كرت الفهرسة بتاعه التعميم": ["التعميم"],
            "بطاقة الفهرس بتاعتها القرار": ["القرار"],
            "الملفات اللي التيمبلت بتاعها القرار": ["القرار"],
            "الوثائق اللي كرت الأرشفة بتاعها القرار": ["القرار"],
            "وثائق كرت ارشيف التعميم": ["التعميم"],
            "كارت الأرشيف بتاعها القرار": ["القرار"],
            "documents whose archive card is القرار": ["القرار"],
            "كم وثيقة قالبها SASO (5)": ["SASO (5)"],
            "وثائق قالب طلب ترشيح لدورة تدريبية": ["طلب ترشيح لدورة تدريبية"],
            "وثائق قالب طلب ترشيح لدورة تدريبية (اخضر)": ["طلب ترشيح لدورة تدريبية(اخضر)"],
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, mentioned_templates(question, CATALOG))

    def test_no_template_without_a_cue_or_a_complete_name(self):
        for question in ("الوثائق اللي اسمها قرار", "قالب المركز الوطني", "كم عدد الوثائق حسب القالب؟", "القرارات المعتمدة"):
            with self.subTest(question=question):
                self.assertEqual([], mentioned_templates(question, CATALOG))


class MentionedTagTests(unittest.TestCase):
    def test_tags_and_negation(self):
        cases = {
            "اعرض الوثائق التي عليها وسم تم الإعتماد": [("تم الإعتماد", False)],
            "الوثائق اللي مش عليها وسم تم الاعتماد": [("تم الإعتماد", True)],
            "الوثائق بدون الوسم تم الرفض": [("تم الرفض", True)],
            "الوثائق اللي التاج بتاعها Test Water mark": [("Test Water mark", False)],
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, mentioned_tags(question, CATALOG))


class GroundCatalogScopeTests(unittest.TestCase):
    def test_template_replaces_the_whole_repository(self):
        grounded = ground_catalog_scope(SEARCH, "اعرض الوثائق التي قالبها القرار", CATALOG)
        self.assertEqual("القرار", grounded["template"])
        self.assertTrue(grounded["requiresFilter"])

    def test_tag_is_added_to_existing_conditions_and_name_stand_in_is_dropped(self):
        plan = {**SEARCH, "requiresFilter": True, "filters": {"logic": "and", "conditions": [
            {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 7}},
            {"field": "name", "operator": "contains", "value": "تم الإعتماد"}]}}
        grounded = ground_catalog_scope(plan, "الوثائق اللي عليها وسم تم الإعتماد واتعملت في يوليو 2026", CATALOG)
        self.assertEqual({"logic": "and", "conditions": [
            {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 7}},
            {"tag": "تم الإعتماد", "operator": "has_tag"}]}, grounded["filters"])

    def test_plan_that_already_has_the_tag_is_unchanged(self):
        plan = {**SEARCH, "requiresFilter": True, "filters": {"tag": "تم الإعتماد", "operator": "has_tag"}}
        self.assertIs(plan, ground_catalog_scope(plan, "اعرض الوثائق التي عليها وسم تم الإعتماد", CATALOG))

    def test_unasked_field_condition_is_dropped_with_a_tag(self):
        # Seen 2026-10-29 in the hard questions: the model added Status conditions to tag questions.
        for invented in ({"field": "Status", "operator": "is_not_empty"}, {"field": "Status", "operator": "equals", "value": "Rejected"}):
            plan = {**SEARCH, "requiresFilter": True, "filters": {"logic": "and", "conditions": [invented, {"tag": "تم الرفض", "operator": "not_tag"}]}}
            with self.subTest(invented=invented):
                self.assertEqual({"tag": "تم الرفض", "operator": "not_tag"},
                                 ground_catalog_scope(plan, "الوثائق اللي مش عليها وسم تم الرفض", CATALOG)["filters"])

    def test_field_condition_the_question_states_is_kept(self):
        stated = {"field": "Approval Status", "operator": "equals", "value": "Rejected"}
        plan = {**SEARCH, "requiresFilter": True, "filters": stated}
        grounded = ground_catalog_scope(plan, "الوثائق اللي عليها وسم تم الرفض و Approval Status قيمتها Rejected", CATALOG)
        self.assertEqual({"logic": "and", "conditions": [stated, {"tag": "تم الرفض", "operator": "has_tag"}]}, grounded["filters"])

    def test_two_named_templates_are_either_template(self):
        for question in ("وثائق قالب القرار أو قالب التعميم", "documents with template القرار or template التعميم"):
            with self.subTest(question=question):
                grounded = ground_catalog_scope({**SEARCH, "template": "القرار"}, question, CATALOG)
                self.assertIsNone(grounded["template"])
                self.assertEqual({"logic": "or", "conditions": [{"field": "template", "operator": "equals", "value": "القرار"},
                                                                {"field": "template", "operator": "equals", "value": "التعميم"}]},
                                 grounded["filters"])

    def test_statistics_keep_their_operation(self):
        plan = {**SEARCH, "operation": "group"}
        self.assertEqual("القرار", ground_catalog_scope(plan, "كم وثيقة قالبها القرار في كل شهر", CATALOG)["template"])


class FolderContentsTests(unittest.TestCase):
    PLAN = {"operation": "search", "entryType": "document", "folderName": None, "folderId": None, "includeSubfolders": False,
            "template": None, "name": None, "filters": None}

    def test_folder_word_in_egyptian_and_english(self):
        self.assertEqual(("استخراج الحقول و النصوص بالعربي", False), folder_scope("ايه الملفات اللي جوا فولدر استخراج الحقول و النصوص بالعربي"))
        self.assertEqual(("Publications", False), folder_scope("show the folder Publications"))
        self.assertEqual(("الوارد", False), folder_scope("اعرض الوثائق في دوسيه الوارد"))
        self.assertEqual(("Publications", False), folder_scope("files in directory Publications"))

    def test_folder_name_is_not_turned_into_a_field_condition(self):
        plan = {**self.PLAN, "filters": {"field": "Status", "operator": "equals", "value": "Rejected"}}
        self.assertIsNone(ground_folder_scope(plan, "الوثائق في مجلد الطلبات المرفوضة")["filters"])

    def test_contents_include_subfolders_documents_stay_documents(self):
        cases = {"ايه الملفات اللي جوا فولدر استخراج الحقول و النصوص بالعربي": "all",
                 "محتويات مجلد طلبات قيد الاعتماد": "all",
                 "اعرض الوثائق في مجلد الوارد": "document",
                 "الوثائق داخل مجلد الوارد": "document"}
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, ground_folder_scope(self.PLAN, question)["entryType"])

    def test_folder_listing_of_contents_becomes_a_search_of_everything(self):
        grounded = ground_folder_scope({**self.PLAN, "operation": "folders", "entryType": "folder"}, "ايه اللي جوا فولدر Publications")
        self.assertEqual(("search", "all"), (grounded["operation"], grounded["entryType"]))


class WithoutTemplateTests(unittest.TestCase):
    def test_documents_without_a_template(self):
        # The user, 2026-10-29: «كرت الفهرسة» is the template; «لا تحتوي على كرت فهرسة» means no template at all.
        for question in ("ارسل لي قائمة بالمعاملات التي لا تحتوي على كرت فهرسة", "الوثائق اللي مالهاش قالب",
                         "الوثائق اللي مالهاش كرت أرشفة",
                         "الملفات اللي من غير تمبلت", "documents without a template", "show documents that have no template",
                         "documents that don't have a template"):
            with self.subTest(question=question):
                grounded = ground_catalog_scope({**SEARCH, "template": "General"}, question, CATALOG)
                self.assertIsNone(grounded["template"])
                self.assertEqual({"field": "template", "operator": "is_empty"}, grounded["filters"])

    def test_invented_template_conditions_are_replaced(self):
        plan = {**SEARCH, "filters": {"logic": "and", "conditions": [{"field": "template", "operator": "equals", "value": "General"},
                                                                     {"field": "Document", "operator": "is_not_empty"}]}}
        self.assertEqual({"field": "template", "operator": "is_empty"},
                         ground_catalog_scope(plan, "ارسل لي قائمة بالمعاملات التي لا تحتوي على كرت فهرسة", CATALOG)["filters"])

    def test_negated_template_is_excluded_not_selected(self):
        for question, name in (("الوثائق اللي مش قالبها القرار", "القرار"), ("الوثائق اللي قالبها مش القرار", "القرار"),
                               ("documents whose template is not General", "General"),
                               ("documents not using the Tracking template", "Tracking")):
            with self.subTest(question=question):
                grounded = ground_catalog_scope({**SEARCH, "template": name}, question, CATALOG)
                self.assertIsNone(grounded["template"])
                self.assertEqual({"field": "template", "operator": "not_equals", "value": name}, grounded["filters"])

    def test_a_named_template_is_not_a_missing_template(self):
        self.assertEqual("القرار", ground_catalog_scope(SEARCH, "الوثائق اللي قالبها القرار", CATALOG)["template"])
        self.assertIs(SEARCH, ground_catalog_scope(SEARCH, "كم عدد الوثائق حسب القالب؟", CATALOG))


class EnglishTests(unittest.TestCase):
    def test_templates_and_tags_in_english(self):
        self.assertEqual("General", ground_catalog_scope(SEARCH, "documents with the General template", CATALOG)["template"])
        self.assertEqual("General", ground_catalog_scope(SEARCH, "show documents using template General", CATALOG)["template"])
        self.assertEqual({"tag": "Test", "operator": "has_tag"}, ground_catalog_scope(SEARCH, "documents tagged with Test", CATALOG)["filters"])
        self.assertEqual({"tag": "Test", "operator": "not_tag"}, ground_catalog_scope(SEARCH, "documents without the Test tag", CATALOG)["filters"])

    def test_folders_in_english(self):
        cases = {"show documents in the Publications folder": ("Publications", False),
                 "list all documents in folder Publications and its subfolders": ("Publications", True),
                 "documents in folder Publications created this month": ("Publications", False),
                 "documents in folder \"Publications\"": ("Publications", False)}
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, folder_scope(question))

    def test_contents_and_subfolders_in_english(self):
        plan = FolderContentsTests.PLAN
        self.assertEqual("all", ground_folder_scope(plan, "what's inside the Publications folder")["entryType"])
        self.assertEqual("folders", ground_folder_scope(plan, "list the subfolders of folder Publications")["operation"])
        self.assertEqual("document", ground_folder_scope(plan, "documents in the Publications folder")["entryType"])


class TemplateNamedLikeAFolderTests(unittest.TestCase):
    def test_template_named_with_a_folder_word_is_not_a_folder(self):
        # The live catalog has templates «مجلد - سري» and «مجلد - سري للغاية» (2026-10-29).
        templates = CATALOG["templates"]
        self.assertIsNone(folder_scope("الوثائق اللي قالبها مجلد - سري", templates))
        self.assertEqual(("الوارد", False), folder_scope("وثائق قالب مجلد - سري للغاية في مجلد الوارد", templates))
        self.assertEqual("مجلد - سري للغاية", ground_catalog_scope(SEARCH, "وثائق قالب مجلد - سري للغاية", CATALOG)["template"])


if __name__ == "__main__":
    unittest.main()
