import json
import unittest

from server import build_graph, format_context, MAX_CONTEXT_CHARACTERS
from report_reasoning import validate_draft, apply_review, numeric_literals
from context_windows import focused_window
from evaluate import check_case


class ScriptedModel:
    def __init__(self, replies):
        self.replies = iter(replies)
        self.schemas = []
        self.calls = []

    def bind(self, **kwargs):
        self.schemas.append(kwargs["format"])
        return self

    def invoke(self, messages):
        self.calls.append(messages)
        value = next(self.replies)
        if isinstance(value, Exception):
            raise value
        return type("Reply", (), {"content": json.dumps(value, ensure_ascii=False)})()


class ReasoningTests(unittest.TestCase):
    def selection(self, quote="لم تتم الموافقة على الطلب."):
        return {"status": "answered", "rows": [{"topic": "decision", "reference": 1, "quote": quote}]}

    def review(self, faithful=True, supported=True):
        return {"coverage": "sufficient" if faithful else "insufficient",
                "rows": [{"rowId": 1, "relevant": True, "faithful": faithful}],
                "findings": [{"findingId": 1, "supported": supported}], "issues": []}

    def request(self):
        return {"question": "هل تمت الموافقة؟", "evidence": [{"entryId": 1, "text": "لم تتم الموافقة على الطلب."}],
                "scope": {"exhaustive": False}}

    def test_reviewed_synthesis_is_cited_and_uses_a_schema_at_each_stage(self):
        model = ScriptedModel([self.selection(), {"findings": [{"text": "الطلب لم يحصل على الموافقة.", "rowIds": [1]}]}, self.review()])
        result = build_graph(model).invoke(self.request())
        self.assertIn("الطلب لم يحصل على الموافقة. [1]", result["answer"])
        self.assertEqual(result["quality"]["semanticReview"], "completed")
        self.assertEqual(result["quality"]["modelCalls"], 3)
        self.assertEqual(len(model.schemas), 3)
        self.assertTrue(all(s["type"] == "object" for s in model.schemas))
        self.assertTrue(all("JSON Schema:" in call[0].content for call in model.calls))

    def test_composer_gets_authoritative_source_identity_and_type(self):
        request = self.request()
        request["evidence"][0].update(documentName="قرار اللجنة", pageNumber=4, textSource="ocr")
        model = ScriptedModel([self.selection(), {"findings": []},
            {"coverage": "sufficient", "rows": self.review()["rows"], "findings": [], "issues": []}])
        build_graph(model).invoke(request)
        source = json.loads(model.calls[1][1].content)["quotations"][0]["source"]
        self.assertEqual(source, {"documentName": "قرار اللجنة", "pageNumber": 4, "sourceType": "OCR page"})

    def test_verbatim_but_misleading_negation_fragment_is_removed_by_review(self):
        model = ScriptedModel([self.selection("تمت الموافقة"),
            {"findings": [{"text": "تمت الموافقة على الطلب.", "rowIds": [1]}]}, self.review(False, False)])
        result = build_graph(model).invoke(self.request())
        self.assertEqual(result["selection"]["rows"], [])
        self.assertEqual(result["findings"], [])
        self.assertEqual(result["quality"]["status"], "insufficient")
        self.assertNotIn("| تمت الموافقة", result["answer"])

    def test_invalid_or_unavailable_review_never_releases_generated_claim(self):
        for bad_review in ({}, OSError("local model stopped")):
            model = ScriptedModel([self.selection(), {"findings": [{"text": "معلومة غير مؤكدة", "rowIds": [1]}]}, bad_review])
            result = build_graph(model).invoke(self.request())
            self.assertNotIn("معلومة غير مؤكدة", result["answer"])
            self.assertEqual(result["quality"]["status"], "source_only")
            self.assertIn("لم تتم الموافقة", result["answer"])

    def test_draft_rejects_hallucinated_number_and_invalid_row_reference(self):
        rows = [{"reference": 1, "quote": "القيمة: ١٢٬٥٠٠ ريال"}]
        for finding in ({"text": "القيمة 13000 ريال", "rowIds": [1]},
                        {"text": "القيمة صحيحة", "rowIds": [2]},
                        {"text": "القيمة صحيحة [1]", "rowIds": [1]},
                        {"text": "القيمة صحيحة", "rowIds": [True]}):
            with self.subTest(finding=finding), self.assertRaises(ValueError):
                validate_draft(json.dumps({"findings": [finding]}), rows)
        validate_draft(json.dumps({"findings": [{"text": "القيمة 12,500 ريال", "rowIds": [1]}]}), rows)

    def test_review_cannot_upgrade_insufficient_or_omit_and_duplicate_reviews(self):
        selected = self.selection()
        selected["status"] = "insufficient"
        draft = {"findings": [{"text": "لم تتم الموافقة", "rowIds": [1]}]}
        self.assertEqual(apply_review(json.dumps(self.review()), selected, draft)["selection"]["status"], "insufficient")
        for reviews in ([], [self.review()["rows"][0]] * 2):
            review = self.review()
            review["rows"] = reviews
            with self.assertRaises(ValueError):
                apply_review(json.dumps(review), selected, draft)

    def test_rejected_claim_does_not_remove_valid_source_quotation(self):
        result = apply_review(json.dumps(self.review(supported=False)), self.selection(),
                              {"findings": [{"text": "تمت الموافقة", "rowIds": [1]}]})
        self.assertEqual(result["findings"], [])
        self.assertEqual(len(result["selection"]["rows"]), 1)
        self.assertEqual(result["selection"]["status"], "insufficient")
        self.assertIn("unsupported_claim", result["issues"])

    def test_numeric_guard_preserves_sign_percent_and_decimal_list_boundaries(self):
        cases = [("الرصيد −٥٠٠ ريال", "الرصيد 500 ريال"),
                 ("النسبة ٥ ٪", "النسبة 5"),
                 ("القيم 12,50", "القيمة 1250"),
                 ("القيم 12, 500", "القيمة 12500"),
                 ("القيمة ١٢٫٥", "القيمة 125"),
                 ("الفترة 2025-2026", "السنة 2026")]
        for quote, finding in cases:
            with self.subTest(quote=quote), self.assertRaises(ValueError):
                validate_draft(json.dumps({"findings": [{"text": finding, "rowIds": [1]}]}),
                               [{"quote": quote}])
        self.assertEqual(numeric_literals("الرصيد −٥٠٠، والنسبة ٥ ٪"), {"-500", "5%"})
        self.assertEqual(numeric_literals("12,500.50 و١٢٬٥٠٠٫٥٠"), {"12500.50"})

    def test_focused_window_preserves_late_answer_and_original_characters(self):
        source = "مقدمة عامة. " * 160 + "\nموعد التسليم: ١٤٤٨/٠٣/٢٧ بعد موافقة المدير.\n" + "تفاصيل أخرى. " * 50
        excerpt, start = focused_window(source, "ما موعد التسليم؟", 350)
        self.assertIn("موعد التسليم: ١٤٤٨/٠٣/٢٧", excerpt)
        self.assertEqual(excerpt, source[start:start + len(excerpt)])

    def test_context_budget_includes_headers_and_json_escape_overhead(self):
        evidence = [{"entryId": i + 1, "documentName": "س" * 300, "text": '"\\\n' * 2000} for i in range(32)]
        context = format_context({"question": "سؤال", "evidence": evidence})["context"]
        self.assertLessEqual(len(context), MAX_CONTEXT_CHARACTERS)
        self.assertEqual(len(json.loads(context)), 32)

    def test_quality_gate_requires_answer_rows_and_requested_synthesis(self):
        result = {"verified": True, "reviewed": True,
                  "selection": {"status": "answered", "rows": []}, "findings": []}
        self.assertFalse(check_case(result, {"status": "answered"}))
        result["selection"]["rows"] = self.selection()["rows"]
        self.assertTrue(check_case(result, {"status": "answered"}))
        self.assertFalse(check_case(result, {"status": "answered", "minimumFindings": 1}))


if __name__ == "__main__":
    unittest.main()
