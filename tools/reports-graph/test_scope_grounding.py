import unittest

from scope_grounding import folder_scope, ground_folder_scope


class FolderScopeTests(unittest.TestCase):
    def test_explicit_folder_names_and_subfolder_phrases(self):
        cases = {
            "اعطيني تقرير عن جميع الوثائق الموجودة في مجلد الوارد": ("الوارد", False),
            "الوثائق في المجلد الوارد التي أنشئت هذا الشهر": ("الوارد", False),
            "كم وثيقة في مجلد الأرشيف؟": ("الأرشيف", False),
            "اعطيني تقرير عن الوثائق الموجودة في مجلد طلب ترشيح لدورة تدريبية و ما تحته": ("طلب ترشيح لدورة تدريبية", True),
            "اعطيني تقرير عن جميع الوثائق الموجودة في مجلد طلب ترشيح لدورة تدريبية و ما يندرج تحته المجلد": ("طلب ترشيح لدورة تدريبية", True),
            "الوثائق في مجلد الوارد والمجلدات الفرعية": ("الوارد", True),
            "عد لي مستندات مجلد العقود مع المجلدات اللي تحته": ("العقود", True),
            "الوثائق في مجلد الوارد اللي فيها عقود": ("الوارد", False),
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, folder_scope(question))

    def test_no_single_explicit_folder(self):
        for question in ("اعرض المجلدات", "الوثائق في مجلد الوارد وفي مجلد الصادر", "عطني محتويات المجلد رقم 42",
                         "اعطيني تقرير عن الوثائق الموجودة في ملف طلب ترشيح لدورة تدريبية", "كم عدد الوثائق في المستودع"):
            with self.subTest(question=question):
                self.assertIsNone(folder_scope(question))


class GroundFolderScopeTests(unittest.TestCase):
    def test_invented_template_is_replaced_by_the_folder(self):
        planned = {"operation": "search", "template": None, "folderName": None,
                   "filters": {"logic": "and", "conditions": [{"field": "template", "operator": "equals", "value": "Invoices"}]}}
        grounded = ground_folder_scope(planned, "اعطيني تقرير عن جميع الوثائق الموجودة في مجلد الوارد")
        self.assertEqual("الوارد", grounded["folderName"])
        self.assertFalse(grounded["includeSubfolders"])
        self.assertIsNone(grounded["filters"])

    def test_template_with_the_folder_name_is_the_folder(self):
        grounded = ground_folder_scope({"operation": "search", "template": "طلب ترشيح لدورة تدريبية", "filters": None},
                                       "اعطيني تقرير عن الوثائق الموجودة في مجلد طلب ترشيح لدورة تدريبية")
        self.assertEqual("طلب ترشيح لدورة تدريبية", grounded["folderName"])
        self.assertIsNone(grounded["template"])

    def test_stated_conditions_are_kept(self):
        planned = {"operation": "search", "template": "القرار", "filters": {"logic": "and", "conditions": [
            {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10}},
            {"field": "name", "operator": "equals", "value": "الوارد"}]}}
        grounded = ground_folder_scope(planned, "وثائق قالب القرار في مجلد الوارد التي أنشئت هذا الشهر")
        self.assertEqual("القرار", grounded["template"])
        self.assertEqual({"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10}}, grounded["filters"])

    def test_subfolders_of_a_named_folder_are_a_scoped_folder_listing(self):
        # Before 2026-10-29 the folder listing ignored «مجلد الوارد» and listed every folder.
        grounded = ground_folder_scope({"operation": "folders", "folderName": None}, "اعرض المجلدات داخل مجلد الوارد")
        self.assertEqual(("folders", "folder", "الوارد"), (grounded["operation"], grounded["entryType"], grounded["folderName"]))

    def test_other_operations_and_questions_are_unchanged(self):
        planned = {"operation": "metadata", "folderName": None}
        self.assertIs(planned, ground_folder_scope(planned, "بيانات مجلد الوارد"))
        planned = {"operation": "search", "template": "القرار"}
        self.assertIs(planned, ground_folder_scope(planned, "الوثائق التي قالبها القرار"))


if __name__ == "__main__":
    unittest.main()
