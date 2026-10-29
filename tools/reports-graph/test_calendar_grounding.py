import unittest
from datetime import date

from calendar_grounding import (add_missing_period, ground_report_filters, normalize_arabic, period_bounds,
                                period_field, relative_period, whole_periods, ground_named_month, named_month)


class RelativePeriodTests(unittest.TestCase):
    def test_fixed_phrases_resolve_to_one_period(self):
        cases = {
            "اعرض الوثائق التي أنشئت هذا الشهر": ("month", 0),
            "ما هي الوثائق اللتي تم انشائها بالامس": ("day", -1),
            "الوثائق التي أنشئت أول أمس": ("day", -2),
            "ماهي الوثائق اللتي تم انشائها اليوم؟": ("day", 0),
            "الوثائق المنشأة الشهر الماضي": ("month", -1),
            "الوثائق خلال الشهر الماضي": ("month", -1),
            "الوثائق التي لم تراجع بعد وأنشئت هذا الأسبوع": ("week", 0),
            "وثائق الأسبوع الماضي": ("week", -1),
            "وثائق هذه السنة": ("year", 0),
            "وثائق العام الماضي": ("year", -1),
            "الوثائق اللي اتعملت امبارح": ("day", -1),
            "الوثائق اللي اتعملت النهارده": ("day", 0),
        }
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, relative_period(question))

    def test_open_ranges_and_mixed_periods_stay_with_the_planner(self):
        for question in ("الوثائق منذ بداية هذا الشهر", "الوثائق قبل هذا الشهر", "حتى اليوم",
                         "وثائق من بداية السنة", "الوثائق التي أنشئت اليوم وأمس", "كم عدد الوثائق في المستودع"):
            with self.subTest(question=question):
                self.assertIsNone(relative_period(question))

    def test_bounds_follow_the_backend_calendar(self):
        today = date(2026, 10, 15)  # Thursday
        self.assertEqual((date(2026, 10, 1), date(2026, 11, 1)), period_bounds("month", 0, today))
        self.assertEqual((date(2026, 9, 1), date(2026, 10, 1)), period_bounds("month", -1, today))
        self.assertEqual((date(2025, 12, 1), date(2026, 1, 1)), period_bounds("month", -10, today))
        self.assertEqual((date(2026, 10, 11), date(2026, 10, 18)), period_bounds("week", 0, today))  # Sunday start
        self.assertEqual((date(2026, 10, 14), date(2026, 10, 15)), period_bounds("day", -1, today))

    def test_arabic_spelling_is_normalized(self):
        self.assertEqual(normalize_arabic("أُسبوع الإدارة"), normalize_arabic("اسبوع الاداره"))
        self.assertEqual(normalize_arabic("مستشفى ١٢"), "مستشفي 12")


class GroundFiltersTests(unittest.TestCase):
    def test_this_month_planned_as_today_becomes_the_whole_month(self):
        planned = {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 14}}
        self.assertEqual({"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10}},
                         ground_report_filters(planned, "اعرض الوثائق التي أنشئت هذا الشهر", "2026-10-14"))

    def test_yesterday_between_two_days_becomes_one_day_inside_other_conditions(self):
        planned = {"logic": "and", "conditions": [
            {"field": "created", "operator": "between", "value": "2026-10-11", "upper": "2026-10-12"},
            {"field": "Status", "operator": "equals", "value": "تم الإعتماد"}]}
        grounded = ground_report_filters(planned, "الوثائق المعتمدة التي أنشئت بالأمس", "2026-10-12")
        self.assertEqual({"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 11}},
                         grounded["conditions"][0])
        self.assertEqual(planned["conditions"][1], grounded["conditions"][1])

    def test_week_becomes_a_sunday_to_saturday_range(self):
        grounded = ground_report_filters({"field": "modified", "operator": "equals", "value": "2026-10-15"},
                                         "الوثائق المعدلة هذا الأسبوع", "2026-10-15")
        self.assertEqual({"logic": "and", "conditions": [
            {"field": "modified", "operator": "from_period", "period": {"year": 2026, "month": 10, "day": 11}},
            {"field": "modified", "operator": "before_period", "period": {"year": 2026, "month": 10, "day": 18}}]}, grounded)

    def test_custom_date_fields_are_grounded_only_when_declared(self):
        planned = {"field": "تاريخ القرار", "operator": "equals", "value": "2026-10-15"}
        self.assertIs(planned, ground_report_filters(planned, "قرارات هذا الشهر", "2026-10-15"))
        self.assertEqual("in_period", ground_report_filters(planned, "قرارات هذا الشهر", "2026-10-15",
                                                            date_fields=("created", "تاريخ القرار"))["operator"])

    def test_uncertain_plans_are_left_unchanged(self):
        two_dates = {"logic": "and", "conditions": [
            {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 1}},
            {"field": "modified", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 1}}]}
        open_range = {"field": "created", "operator": "before_period", "period": {"year": 2026, "month": 10}}
        for planned, question in ((two_dates, "وثائق هذا الشهر"), (open_range, "الوثائق قبل هذا الشهر"),
                                  (open_range, "وثائق هذا الشهر"), (None, "وثائق هذا الشهر")):
            with self.subTest(question=question):
                self.assertIs(planned, ground_report_filters(planned, question, "2026-10-15"))


class AddMissingPeriodTests(unittest.TestCase):
    def test_today_the_model_dropped_is_added_on_creation(self):
        # The question that searched the whole repository on 2026-10-26.
        self.assertEqual({"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 26}},
                         add_missing_period(None, "ما الوثائق اللتي تم انشاها بتاريخ اليوم", "2026-10-26"))

    def test_period_joins_other_conditions_with_and(self):
        status = {"field": "Status", "operator": "equals", "value": "تم الإعتماد"}
        self.assertEqual({"logic": "and", "conditions": [
            status, {"field": "created", "operator": "in_period", "period": {"year": 2026, "month": 10, "day": 28}}]},
            add_missing_period(status, "الوثائق المعتمدة التي أنشئت اليوم", "2026-10-28"))

    def test_modification_question_uses_the_modified_date(self):
        self.assertEqual({"field": "modified", "operator": "in_period", "period": {"year": 2026, "month": 10}},
                         add_missing_period(None, "الوثائق المعدلة هذا الشهر", "2026-10-28"))

    def test_creation_or_modification_word_decides_the_field(self):
        cases = {"الوثائق اللي اتعملت النهارده": "created", "المستندات المنشأة أمس": "created",
                 "الوثائق التي تم إنشاؤها هذا الأسبوع": "created", "الملفات التي أضيفت اليوم": "created",
                 "الوثائق التي عدلت اليوم": "modified", "وثائق وزارة العدل التي أنشئت اليوم": "created",
                 "وثائق هذا الشهر": None, "الوثائق التي أنشئت أو عدلت اليوم": None, "معدل الوثائق اليوم": None}
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, period_field(question))

    def test_plans_that_already_have_a_date_or_an_unclear_question_are_left_unchanged(self):
        before = {"field": "created", "operator": "before_period", "period": {"year": 2026, "month": 10}}
        relative = {"field": "created", "operator": "date_after", "relative": {"unit": "day", "offset": 0}}
        for planned, question in ((before, "الوثائق التي أنشئت هذا الشهر"), (relative, "الوثائق التي أنشئت اليوم"),
                                  (None, "وثائق هذا الشهر"), (None, "الوثائق التي أنشئت منذ بداية هذا الشهر"),
                                  (None, "كم عدد الوثائق في المستودع")):
            with self.subTest(question=question):
                self.assertIs(planned, add_missing_period(planned, question, "2026-10-28"))


class EnglishPeriodTests(unittest.TestCase):
    def test_english_relative_periods_and_fields(self):
        cases = {"documents created yesterday": (("day", -1), "created"), "documents modified last month": (("month", -1), "modified"),
                 "documents added this week": (("week", 0), "created"), "documents updated today": (("day", 0), "modified"),
                 "documents created this year": (("year", 0), "created")}
        for question, (period, field) in cases.items():
            with self.subTest(question=question):
                self.assertEqual(period, relative_period(question))
                self.assertEqual(field, period_field(question))

    def test_english_open_ranges_stay_with_the_planner(self):
        for question in ("documents created since the start of this month", "documents modified before last month"):
            with self.subTest(question=question):
                self.assertIsNone(relative_period(question))


class WholePeriodTests(unittest.TestCase):
    def test_a_range_of_one_whole_month_or_year_becomes_that_period(self):
        cases = [(("2026-08-01", "2026-08-31"), {"year": 2026, "month": 8}),
                 (("2026-02-01", "2026-03-01"), {"year": 2026, "month": 2}),
                 (("2026-01-01", "2026-12-31"), {"year": 2026})]
        for (low, high), period in cases:
            with self.subTest(low=low, high=high):
                self.assertEqual({"field": "created", "operator": "in_period", "period": period},
                                 whole_periods({"field": "created", "operator": "between", "value": low, "upper": high}))

    def test_other_ranges_are_unchanged(self):
        for low, high in (("2026-08-01", "2026-08-15"), ("2026-08-02", "2026-08-31"), ("2026-08-01T00:00:00", "2026-08-31")):
            planned = {"field": "created", "operator": "between", "value": low, "upper": high}
            with self.subTest(low=low, high=high):
                self.assertIs(planned, whole_periods(planned))


class NamedMonthTests(unittest.TestCase):
    def test_named_month_and_year(self):
        cases = {"اتعملت في أكتوبر 2026": (2026, 10), "الوثائق المنشأة في شهر اغسطس سنة 2026": (2026, 8),
                 "created in October 2026": (2026, 10), "في 2026": None, "من سبتمبر 2026 الى اكتوبر 2026": None}
        for question, expected in cases.items():
            with self.subTest(question=question):
                self.assertEqual(expected, named_month(question))

    def test_lost_month_is_restored(self):
        planned = {"logic": "and", "conditions": [{"field": "created", "operator": "in_period", "period": {"year": 2026}},
                                                  {"tag": "تم الرفض", "operator": "has_tag"}]}
        grounded = ground_named_month(planned, "الوثائق اللي عليها وسم تم الرفض واتعملت في أكتوبر 2026")
        self.assertEqual({"year": 2026, "month": 10}, grounded["conditions"][0]["period"])

    def test_other_years_and_days_are_unchanged(self):
        for period in ({"year": 2025}, {"year": 2026, "month": 10, "day": 3}, {"year": 2026, "month": 10}):
            planned = {"field": "created", "operator": "in_period", "period": period}
            with self.subTest(period=period):
                self.assertIs(planned, ground_named_month(planned, "الوثائق المنشأة في أكتوبر 2026"))


if __name__ == "__main__":
    unittest.main()
