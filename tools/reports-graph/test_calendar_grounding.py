import unittest
from datetime import date

from calendar_grounding import ground_report_filters, normalize_arabic, period_bounds, relative_period


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


if __name__ == "__main__":
    unittest.main()
