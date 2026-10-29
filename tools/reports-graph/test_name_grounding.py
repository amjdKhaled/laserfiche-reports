import unittest

from name_grounding import ground_entry_name, mentioned_name

CATALOG = {"templates": ["القرار", "وثيقة شراء"], "tags": [{"name": "تم الرفض"}]}


def search(filters=None, **extra):
    return {"operation": "search", "name": None, "template": None, "requiresFilter": False, "filters": filters, **extra}


class MentionedNameTests(unittest.TestCase):
    def test_the_named_file(self):
        cases = {
            "الملف اللي اسمه قرار 1-4 موجود؟": "قرار 1-4",
            "طب الملف اللي اسمه «عقد التوريد» موجود ولا لا": "عقد التوريد",
            "فين ملف قرار 1-4؟": "قرار 1-4",
            "هو فين ملف عقد التوريد؟": "عقد التوريد",
            "دور على وثيقة محضر الاجتماع الأول": "محضر الاجتماع الأول",
            "is there a file named قرار 1-4?": "قرار 1-4",
            "where is the document called Annual Report 2025?": "Annual Report 2025",
            "هل يوجد ملف بعنوان خطة العمل في مجلد الوارد": "خطة العمل",
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, mentioned_name(question, CATALOG))

    def test_no_name(self):
        for question in ("هاتلي الملفات اللي اتعملت النهارده", "فين الملف اللي اتعمل امبارح", "فين ملف قالبه القرار",
                         "أبي اللي اسمها يبدأ بمحضر", "documents named like contract",
                         "كم عدد الوثائق في المستودع؟", "الوثائق اللي قالبها القرار", "فين ملف القرار",
                         "how many documents are in the repository?", "is there a file that was created today?"):
            with self.subTest(question=question):
                self.assertIsNone(mentioned_name(question, CATALOG))


class GroundEntryNameTests(unittest.TestCase):
    def test_replaces_the_models_guess(self):
        guessed = {"logic": "and", "conditions": [{"field": "Request Number", "operator": "equals", "value": "1-4"}]}
        grounded = ground_entry_name(search(guessed), "فين ملف قرار 1-4؟", CATALOG)
        self.assertEqual({"field": "name", "operator": "contains", "value": "قرار 1-4"}, grounded["filters"])
        self.assertTrue(grounded["requiresFilter"])

    def test_a_whole_repository_count_gets_the_name(self):
        grounded = ground_entry_name(search(countOnly=True), "is there a file named قرار 1-4?", CATALOG)
        self.assertEqual({"field": "name", "operator": "contains", "value": "قرار 1-4"}, grounded["filters"])
        self.assertTrue(grounded["countOnly"])

    def test_keeps_a_date(self):
        day = {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 28}}
        grounded = ground_entry_name(search({"logic": "and", "conditions": [day, {"field": "Status", "operator": "equals", "value": "x"}]}),
                                     "الملف اللي اسمه عقد التوريد اللي اتعمل امبارح", CATALOG)
        self.assertEqual({"logic": "and", "conditions": [day, {"field": "name", "operator": "contains", "value": "عقد التوريد"}]},
                         grounded["filters"])

    def test_leaves_other_plans(self):
        planned = search(name="قرار 1-4")
        self.assertIs(planned, ground_entry_name(planned, "الملف اللي اسمه قرار 1-4", CATALOG))
        planned = {"operation": "folders", "filters": None}
        self.assertIs(planned, ground_entry_name(planned, "الملف اللي اسمه قرار 1-4", CATALOG))
        planned = search()
        self.assertIs(planned, ground_entry_name(planned, "هاتلي الملفات اللي اتعملت النهارده", CATALOG))


if __name__ == "__main__":
    unittest.main()
