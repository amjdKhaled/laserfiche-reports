import unittest

from catalog_grounding import mentioned_templates
from spelling import correct_spelling

CATALOG = {
    "templates": ["القرار", "التعميم", "وثيقة شراء", "SASO (4)", "SASO (5)", "المركز الوطني للوثائق و المحفوظات",
                  "المركز الوطني للوثائق و المحفوظات (احمر)", "Test_Templete"],
    "tags": [{"name": "تم الرفض"}, {"name": "تم الإعتماد"}, {"name": "تم رفع الطلب"}],
    "fields": [{"name": "حالة الوثيقة", "listValues": ["تحت الاجراء", "جاهز للاتلاف", "نعم", "لا"]}],
}
GLOSSARY = [{"words": ["مؤرشفة", "مؤرشف", "archived"], "condition": {}}]


def fixed(question):
    return correct_spelling(question, CATALOG, GLOSSARY)[0]


class PlanningWordTests(unittest.TestCase):
    def test_slips_in_planning_words(self):
        cases = {
            "الوثايق اللي كرت ارشفيف بتاعها القرار": "الوثائق اللي كرت أرشيف بتاعها القرار",
            "وثائق قلاب القرار": "وثائق قالب القرار",
            "الوثائق اللي اتعملت في اغسطوس 2026": "الوثائق اللي اتعملت في أغسطس 2026",
            "كم وثيقه مأرشفة": "كم وثيقه مؤرشفه",
            "documnets in the tempalte القرار": "documents in the template القرار",
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, fixed(question))

    def test_english_words_in_arabic_letters(self):
        self.assertEqual("اعرض الوثائق اللي في الفولدر", fixed("اعرض الدوكيومنتس اللي في الفولدرز"))
        for question in ("الملفات اللي التيمبلاتة بتاعها القرار", "الملفات اللي التمبلييت بتاعها القرار"):
            with self.subTest(question=question):
                self.assertEqual(["القرار"], mentioned_templates(fixed(question), CATALOG))

    def test_real_words_stay(self):
        for question in ("قرارات مجلس الإدارة", "show me older documents", "documents that match the name X",
                         "الوثائق اللي اتعملت في يونيه 2026", "الوثائق اللي اسمها قرار 1-4", "وثائق قالب الفواتير",
                         "كم عدد الوثائق في المستودع؟", "1"):
            with self.subTest(question=question):
                self.assertEqual((question, []), correct_spelling(question, CATALOG, GLOSSARY))

    def test_catalog_words_are_known(self):
        self.assertEqual("وثائق Test_Templete", fixed("وثائق Test_Templete"))


class CatalogNameTests(unittest.TestCase):
    def test_template_name_after_its_cue(self):
        self.assertEqual("وثائق قالب التعميم", fixed("وثائق قالب التعمم"))
        self.assertEqual("الوثائق اللي التمبلت بتاعها القرار", fixed("الوثائق اللي التمبلت بتاعها القرر"))
        self.assertEqual("وثائق قالب وثيقة شراء", fixed("وثائق قالب وثيقه شرا"))

    def test_single_word_name_needs_its_cue(self):
        self.assertEqual("اقرار الموظف", fixed("اقرار الموظف"))

    def test_digits_never_change(self):
        self.assertEqual("وثائق قالب SASO (3)", fixed("وثائق قالب SASO (3)"))

    def test_wrong_letter_far_on_the_keyboard_is_another_word(self):
        # «الرفع» is no slip of «الرفض»: ع and ض are far apart and sound different.
        self.assertEqual("الوثائق اللي عليها وسم تم الرفع", fixed("الوثائق اللي عليها وسم تم الرفع"))
        self.assertEqual("الوثائق اللي عليها وسم تم الإعتماد", fixed("الوثائق اللي عليها وسم تم الاعتمد"))

    def test_long_name_and_list_choice_anywhere(self):
        self.assertEqual("وثائق قالب المركز الوطني للوثائق و المحفوظات (احمر)",
                         fixed("وثائق قالب مركز الوطني للوثائق والمحفوظات (احمر)"))
        self.assertEqual("الوثائق اللي حالتها تحت الاجراء", fixed("الوثائق اللي حالتها تحت الاجرا"))
        self.assertEqual("كم وثيقة جاهز للاتلاف؟", fixed("كم وثيقة جاهزة للإتلاف؟"))

    def test_corrections_are_reported(self):
        self.assertEqual([("قلاب", "قالب"), ("التعمم", "التعميم")],
                         correct_spelling("وثائق قلاب التعمم", CATALOG, GLOSSARY)[1])


if __name__ == "__main__":
    unittest.main()
