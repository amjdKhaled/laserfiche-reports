import json
import unittest
from unittest.mock import patch

from server import plan_reports
from test_graph import FakeModel
from test_typed_planner import CATALOG, plan


def review(**changes):
    return {"outputMatches": True, "scopeMatches": True, "conditionsMatch": True,
            "fieldsMatch": True, "datesMatch": True, "issues": [], "clarification": None, **changes}


class PlanIntentReviewTests(unittest.TestCase):
    def test_schema_valid_semantic_errors_require_repair_and_reaudit(self):
        cases = [
            ("كم وثيقة غير مرفوضة؟", "outputMatches", "A count was replaced by a list."),
            ("اعرضها في نفس المجلد", "scopeMatches", "Previous folder selection was dropped."),
            ("مقبول أو تحت الإجراء والقسم مالية", "conditionsMatch", "OR alternatives became AND."),
            ("اعرض غير المرفوضة", "conditionsMatch", "Negation was lost."),
            ("عدد الوثائق المنتهي أجل حفظها", "fieldsMatch", "Creation date substituted for expiry."),
            ("اعرض وثائق هذا الشهر", "datesMatch", "Exclusive end incorrectly became inclusive."),
            ("عددها حتى 2036 وما أقل", "outputMatches", "Upper bound became an oldest-document output."),
        ]
        for question, check, issue in cases:
            with self.subTest(question=question):
                corrected = plan(countOnly=True)
                model = FakeModel([json.dumps(plan()), json.dumps(review(**{check: False, "issues": [issue]})),
                                   json.dumps(corrected), json.dumps(review())])
                result = plan_reports(model, {"question": question, "catalog": CATALOG}, review_intent=True)
                self.assertEqual(len(model.calls), 4)
                self.assertTrue(result["reports"][0]["countOnly"])
                self.assertIn("Intent review rejected", model.calls[2][-1].content)
                self.assertIn("proposedPlan", model.calls[1][1].content)

    def test_valid_nested_filters_and_followup_context_reach_independent_reviewer(self):
        condition = {"logic": "and", "conditions": [
            {"logic": "or", "conditions": [
                {"field": "حالة السجل", "operator": "equals", "value": "مقبول"},
                {"field": "حالة السجل", "operator": "equals", "value": "تحت الإجراء"}]},
            {"field": "مدة النشاط", "operator": "less_or_equal", "value": "10"}]}
        output = plan(condition, countOnly=True)
        history = [{"role": "user", "text": "في مجلد الوثائق"}]
        model = FakeModel([json.dumps(output), json.dumps(review())])
        result = plan_reports(model, {"question": "كم منها مدة النشاط فيها 10 أو أقل؟", "catalog": CATALOG,
                                    "history": history, "today": "2026-10-07"}, review_intent=True)
        self.assertEqual(len(model.calls), 2)
        self.assertEqual(result["reports"][0]["filters"]["logic"], "and")
        sent = json.loads(model.calls[1][1].content)
        self.assertEqual(sent["history"], history)
        self.assertEqual(sent["today"], "2026-10-07")

    def test_repeated_semantic_rejection_never_returns_an_executable_plan(self):
        rejected = review(fieldsMatch=False, issues=["Unknown meaning of active duration"],
                          clarification="هل تقصد مدة النشاط أم تاريخ انتهاء الحفظ؟")
        model = FakeModel([json.dumps(plan()), json.dumps(rejected)] * 2)
        result = plan_reports(model, {"question": "كم وثيقة نشطة؟", "catalog": CATALOG}, review_intent=True)
        self.assertEqual(result["reports"][0]["operation"], "clarify")
        self.assertIsNone(result["reports"][0]["filters"])
        self.assertIn("مدة النشاط", result["clarification"])

    def test_repeated_error_without_genuine_ambiguity_fails_closed(self):
        model = FakeModel([json.dumps(plan()), json.dumps(review(conditionsMatch=False, issues=["Lost condition"]))] * 2)
        with self.assertRaises(ValueError):
            plan_reports(model, {"question": "سؤال", "catalog": CATALOG}, review_intent=True)

    def test_inconsistent_approval_and_malformed_review_are_not_accepted(self):
        for response in [review(issues=["Missing restriction"]), {"outputMatches": True}]:
            with self.subTest(response=response):
                model = FakeModel([json.dumps(plan()), json.dumps(response)] * 2)
                with self.assertRaises(ValueError):
                    plan_reports(model, {"question": "سؤال", "catalog": CATALOG}, review_intent=True)

    def test_reviewer_shares_original_planning_deadline(self):
        model = FakeModel([json.dumps(plan())])
        # Initial start, first model deadline, then expired review deadline.
        with patch("server.time.monotonic", side_effect=[0, 0, 0, 11, 11]):
            with self.assertRaises(TimeoutError):
                plan_reports(model, {"question": "سؤال", "catalog": CATALOG}, budget_seconds=10, review_intent=True)
        self.assertEqual(len(model.calls), 1)
