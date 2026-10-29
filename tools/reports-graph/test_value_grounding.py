import unittest

from value_grounding import ground_field_values, mentioned_value

ACTION = ["تم فهرسة الوثيقة", "تم رفض فهرسة الوثيقة", "تحت الاجراء"]
CATALOG = {
    "templates": ["القرار", "المركز الوطني للوثائق و المحفوظات"],
    "tags": [{"name": "تم القبول"}],
    "fields": [
        {"name": "إ جراء الوثيقة", "fieldType": "List", "listValues": ACTION},
        {"name": "إجراء   الوثيقة", "fieldType": "List", "listValues": ACTION},
        {"name": "هل تريد ارشفة الوثيقة؟", "fieldType": "List", "listValues": ["تحت الإجراء", "نعم", "لا"]},
        {"name": "حالة المعاملة", "fieldType": "List", "listValues": ["تحت الإجراء / In Progress", "قبول / Approved"]},
        {"name": "حالة الطلب", "fieldType": "String"},
        {"name": "التصنيف", "fieldType": "List", "listValues": ["الواردة", "قبول"]},
    ],
}
GLOSSARY = [{"words": ["مؤرشفة", "مؤرشف", "archived"],
             "condition": {"field": "هل تريد ارشفة الوثيقة؟", "operator": "equals", "value": "نعم"}}]
UNDER_ACTION = {"logic": "or", "conditions": [
    {"field": "إ جراء الوثيقة", "operator": "equals", "value": "تحت الاجراء"},
    {"field": "إجراء   الوثيقة", "operator": "equals", "value": "تحت الاجراء"},
    {"field": "هل تريد ارشفة الوثيقة؟", "operator": "equals", "value": "تحت الإجراء"},
    {"field": "حالة المعاملة", "operator": "equals", "value": "تحت الإجراء / In Progress"},
]}
ARCHIVED = {"field": "هل تريد ارشفة الوثيقة؟", "operator": "equals", "value": "نعم"}


def plan(filters=None, **extra):
    return {"operation": "search", "folderName": None, "requiresFilter": False, "filters": filters, **extra}


def ground(question, planned=None):
    return ground_field_values(planned or plan(), question, CATALOG, GLOSSARY)


class ListChoiceTests(unittest.TestCase):
    def test_value_selects_every_list_field_offering_it(self):
        self.assertEqual(UNDER_ACTION, ground("ما هي الوثائق التي حالتها تحت الاجراء")["filters"])

    def test_spelling_and_attached_letters_do_not_matter(self):
        self.assertEqual(UNDER_ACTION, ground("كام وثيقة لسه تحت الإجراء؟")["filters"])
        self.assertEqual(UNDER_ACTION, ground("ماهي الوثائق اللتي لاتزال تحت الاجراء")["filters"])

    def test_english_part_of_a_bilingual_choice(self):
        self.assertEqual({"field": "حالة المعاملة", "operator": "equals", "value": "تحت الإجراء / In Progress"},
                         ground("documents in progress")["filters"])

    def test_replaces_the_models_guess_and_keeps_its_date(self):
        month = {"field": "created", "operator": "in_period", "value": "2026-10"}
        guessed = {"logic": "and", "conditions": [
            {"field": "إجراء   الوثيقة", "operator": "equals", "value": "تحت الاجراء"},
            {"field": "حالة الطلب", "operator": "contains", "value": "تحت الاجراء"}, month]}
        grounded = ground("الوثائق اللي تحت الاجراء في أكتوبر 2026", plan(guessed))
        self.assertEqual({"logic": "and", "conditions": [month, UNDER_ACTION]}, grounded["filters"])
        self.assertTrue(grounded["requiresFilter"])

    def test_negated_value_stays_with_the_planner(self):
        planned = plan()
        self.assertIs(planned, ground_field_values(planned, "الوثائق اللي مش تحت الاجراء", CATALOG, GLOSSARY))
        self.assertIsNone(mentioned_value("documents that are not in progress", CATALOG))

    def test_folder_and_tag_names_are_not_values(self):
        planned = plan(folderName="الواردة")
        self.assertIs(planned, ground_field_values(planned, "الوثائق في مجلد الواردة", CATALOG, GLOSSARY))
        planned = plan({"tag": "تم القبول", "operator": "has_tag"})
        self.assertIs(planned, ground_field_values(planned, "الوثائق اللي عليها وسم تم القبول", CATALOG, GLOSSARY))

    def test_name_after_a_folder_or_template_word_is_no_value(self):
        self.assertIsNone(mentioned_value("اعطيني تقرير عن الوثائق الموجودة في مجلد الوارد", CATALOG))
        self.assertIsNone(mentioned_value("الوثائق اللي قالبها قبول", CATALOG))

    def test_feminine_ending_matches_the_choice(self):
        catalog = {"fields": [{"name": "حالة الوثيقة", "listValues": ["جاهز للاتلاف"]}]}
        self.assertEqual(("جاهز للاتلاف", [("حالة الوثيقة", "جاهز للاتلاف")]), mentioned_value("كم وثيقة جاهزة للإتلاف؟", catalog))
        guessed = plan({"field": "اجراء الوثيقة", "operator": "equals", "value": "إتلاف"})
        self.assertEqual({"field": "حالة الوثيقة", "operator": "equals", "value": "جاهز للاتلاف"},
                         ground_field_values(guessed, "كم وثيقة جاهزة للإتلاف؟", catalog, [])["filters"])

    def test_generic_yes_is_no_value(self):
        self.assertIsNone(mentioned_value("هل يوجد وثائق نعم", CATALOG))

    def test_two_different_values_stay_with_the_planner(self):
        self.assertIsNone(mentioned_value("الوثائق تحت الاجراء او قبول", CATALOG))

    def test_only_document_searches(self):
        planned = plan(operation="folders")
        self.assertIs(planned, ground_field_values(planned, "مجلدات تحت الاجراء", CATALOG, GLOSSARY))


class GlossaryTests(unittest.TestCase):
    def test_archived_means_the_archive_field_is_yes(self):
        self.assertEqual(ARCHIVED, ground("الوثائق المؤرشفة")["filters"])
        self.assertEqual(ARCHIVED, ground("how many archived documents are there?")["filters"])

    def test_archived_keeps_the_template(self):
        template = {"field": "template", "operator": "equals", "value": "القرار"}
        guessed = {"logic": "and", "conditions": [template, {"field": "حالة الطلب", "operator": "equals", "value": "مؤرشفة"}]}
        self.assertEqual({"logic": "and", "conditions": [template, ARCHIVED]},
                         ground("القرارات المؤرشفة", plan(guessed))["filters"])

    def test_unasked_model_condition_goes(self):
        guessed = {"logic": "and", "conditions": [{"field": "Status", "operator": "equals", "value": "تم الارشفة"}]}
        self.assertEqual(ARCHIVED, ground("الوثائق المؤرشفة", plan(guessed))["filters"])

    def test_plural_noun_names_its_template(self):
        glossary = GLOSSARY + [{"words": ["القرارات"], "condition": {"field": "template", "operator": "equals", "value": "القرار"}}]
        template = {"field": "template", "operator": "equals", "value": "القرار"}
        grounded = ground_field_values(plan(ARCHIVED), "ارسلي لي قائمة في القرارات المؤرشفة", CATALOG, glossary)
        self.assertEqual({"logic": "and", "conditions": [ARCHIVED, template]}, grounded["filters"])
        grounded = ground_field_values(plan(template=None), "كم عدد القرارات؟", {**CATALOG, "templates": ["التعميم"]}, glossary)
        self.assertIsNone(grounded["filters"])

    def test_negated_term_stays_with_the_planner(self):
        planned = plan()
        self.assertIs(planned, ground_field_values(planned, "الوثائق اللي مش مؤرشفة", CATALOG, GLOSSARY))

    def test_term_needs_its_field(self):
        catalog = {**CATALOG, "fields": [f for f in CATALOG["fields"] if f["name"] != "هل تريد ارشفة الوثيقة؟"]}
        planned = plan()
        self.assertIs(planned, ground_field_values(planned, "الوثائق المؤرشفة", catalog, GLOSSARY))


if __name__ == "__main__":
    unittest.main()
