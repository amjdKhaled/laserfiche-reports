import unittest
import io
import json
from pathlib import Path

from evaluate import check_case

from server import NO_EVIDENCE, build_graph, validate_request, format_context, parse_grounded_rows
from sync import discover_all


class FakeModel:
    def __init__(self, answers=None):
        self.calls = []
        self.answers = iter(answers or [json.dumps({"status": "answered", "rows": [{"topic": "decision", "reference": 1, "quote": "قرار مجلس الإدارة"}]}, ensure_ascii=False)] * 2)

    def invoke(self, messages):
        self.calls.append(messages)
        return type("Reply", (), {"content": next(self.answers)})()


class GraphTests(unittest.TestCase):
    def test_no_evidence_does_not_call_model(self):
        model = FakeModel()
        result = build_graph(model).invoke({"question": "ما القرار؟", "evidence": []})
        self.assertEqual(result["answer"], NO_EVIDENCE)
        self.assertEqual(model.calls, [])

    def test_grounded_context_is_passed_to_model(self):
        model = FakeModel()
        request = validate_request({"question": "ما القرار؟", "evidence": [
            {"entryId": 618, "pageNumber": 1, "text": "قرار مجلس الإدارة", "documentName": "لائحة"}
        ]})
        result = build_graph(model).invoke(request)
        self.assertIn("[1]", result["answer"])
        self.assertIn("قرار مجلس الإدارة", model.calls[0][1].content)

    def test_laserfiche_field_source_is_identified_separately(self):
        model = FakeModel()
        build_graph(model).invoke({"question": "ما تصنيف الوثيقة 618؟", "evidence": [
            {"entryId": 618, "textSource": "laserfiche-metadata",
             "text": "التصنيف الرئيسي للوثيقة: وثائق التشغيل والصيانة"}
        ]})
        self.assertIn("Laserfiche metadata", model.calls[0][1].content)

    def test_rejects_excessive_evidence(self):
        with self.assertRaises(ValueError):
            validate_request({"question": "سؤال", "evidence": [{"entryId": 1, "text": "x"}] * 33})

    def test_invalid_model_response_falls_back_to_actual_sources(self):
        model = FakeModel(["ادعاء مخترع [99]", "ادعاء مخترع [99]"])
        result = build_graph(model).invoke({"question": "سؤال", "evidence": [
            {"entryId": 618, "documentName": "لائحة", "text": "المعلومة الأصلية"}]})
        self.assertEqual(len(model.calls), 2)
        self.assertIn("المعلومة الأصلية", result["answer"])
        self.assertNotIn("ادعاء مخترع", result["answer"])
        self.assertIn("| المرجع |", result["answer"])

    def test_scope_and_context_truncation_are_explicit(self):
        evidence = [{"entryId": i + 1, "text": "x" * 8000} for i in range(32)]
        context = json.loads(format_context({"evidence": evidence})["context"])
        self.assertEqual(len(context), 32)
        self.assertTrue(all(item["excerptTruncated"] for item in context))
        self.assertLess(sum(len(item["text"]) for item in context), 28000)
        model = FakeModel()
        build_graph(model).invoke({"question": "سؤال", "evidence": evidence[:1],
                                  "scope": {"exhaustive": False, "mode": "selected-documents"}})
        self.assertIn('"exhaustive": false', model.calls[0][1].content)
        self.assertIn('selected-documents', model.calls[0][1].content)

    def test_rejects_boolean_and_negative_entry_ids(self):
        for entry_id in (True, -1, 0):
            with self.assertRaises(ValueError):
                validate_request({"question": "سؤال", "evidence": [{"entryId": entry_id, "text": "x"}]})

    def test_model_quotes_are_checked_against_the_cited_excerpt(self):
        context = format_context({"evidence": [{"entryId": 618, "text": "التاريخ: ١٤٤٨/٠٣/٢٧"}]})["context"]
        valid = {"status": "answered", "rows": [{"topic": "date", "reference": 1, "quote": "١٤٤٨/٠٣/٢٧"}]}
        self.assertEqual(parse_grounded_rows(json.dumps(valid), context)["rows"][0]["quote"], "١٤٤٨/٠٣/٢٧")
        for quote in ("1448/03/27", "١٤٤٨/٠٣/٢٨", "التاريخ ... ٢٧", "تمت الموافقة"):
            valid["rows"][0]["quote"] = quote
            with self.assertRaises(ValueError):
                parse_grounded_rows(json.dumps(valid), context)

    def test_quote_from_another_document_cannot_be_misattributed(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "موعد قديم"}, {"entryId": 2, "text": "موعد حديث"}]})["context"]
        with self.assertRaises(ValueError):
            parse_grounded_rows(json.dumps({"status": "answered", "rows": [{"topic": "date", "reference": 1, "quote": "موعد حديث"}]}), context)

    def test_model_cannot_supply_document_names_counts_or_narrative(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "إجراء الوثيقة: تحت الاجراء"}]})["context"]
        for extra in ("documentName", "entryId", "answer", "count", "summary"):
            row = {"topic": "status", "reference": 1, "quote": "تحت الاجراء", extra: "مخترع"}
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                parse_grounded_rows(json.dumps({"status": "answered", "rows": [row]}), context)

    def test_empty_answer_and_boolean_reference_are_rejected(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "النص الأصلي"}]})["context"]
        for payload in ({"status": "answered", "rows": []}, {"status": "answered", "rows": [{"topic": "other", "reference": True, "quote": "النص الأصلي"}]}):
            with self.assertRaises(ValueError):
                parse_grounded_rows(json.dumps(payload), context)
        self.assertEqual(parse_grounded_rows('{"status":"insufficient","rows":[]}', context)["rows"], [])

    def test_model_cannot_quote_text_outside_the_visible_context(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "أ" * 2000 + "معلومة مخفية"}]})["context"]
        with self.assertRaises(ValueError):
            parse_grounded_rows(json.dumps({"status": "answered", "rows": [{"topic": "other", "reference": 1, "quote": "معلومة مخفية"}]}), context)

    def test_backend_owns_identity_and_reports_missing_comparison_evidence(self):
        model = FakeModel([json.dumps({"status": "answered", "rows": [{"topic": "date", "reference": 1, "quote": "1448/03/27"}]})])
        result = build_graph(model).invoke({"question": "قارن الوثائق 618 و609", "evidence": [{"entryId": 618, "documentName": "الاسم الأصلي", "text": "1448/03/27"}], "scope": {"requestedEntryIds": [618,609], "exhaustive": False}})
        self.assertIn("618", result["answer"])
        self.assertIn("الاسم الأصلي", result["answer"])
        self.assertIn("لم تتوفر أدلة مفهرسة متاحة للوثائق المحددة: 609", result["answer"])

    def test_conflict_keeps_both_original_values_without_resolving(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "موعد التسليم: 2026-10-03"}, {"entryId": 2, "text": "موعد التسليم: 2026-10-04"}]})["context"]
        payload = {"status": "conflicting", "rows": [{"topic": "date", "reference": 1, "quote": "2026-10-03"}, {"topic": "date", "reference": 2, "quote": "2026-10-04"}]}
        self.assertEqual(len(parse_grounded_rows(json.dumps(payload), context)["rows"]), 2)
        payload["rows"] = payload["rows"][:1]
        with self.assertRaises(ValueError):
            parse_grounded_rows(json.dumps(payload), context)

    def test_english_missing_information_report_does_not_claim_document_absence(self):
        model = FakeModel(['{"status":"insufficient","rows":[]}'])
        result = build_graph(model).invoke({"question": "What is the deadline?", "evidence": [{"entryId": 1, "text": "A document"}]})
        self.assertIn("does not prove its absence", result["answer"])
        self.assertIn("No verified answer rows", result["answer"])

    def test_synthetic_quality_corpus_is_valid_and_separate_from_mock_tests(self):
        cases = json.loads(Path(__file__).with_name("adversarial_cases.json").read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(cases), 40)
        for case in cases:
            with self.subTest(case=case["name"]):
                validate_request({key: case[key] for key in ("question", "evidence", "scope")})
                self.assertIn(case["expected"]["status"], ("answered", "insufficient", "conflicting"))
        self.assertFalse(check_case({"verified": False}, {"status": "insufficient"}))
        self.assertFalse(check_case({"verified": True, "selection": {"status": "answered", "rows": []}}, {"status": "insufficient"}))

    def test_quote_budget_prevents_unbounded_model_output(self):
        evidence = [{"entryId": i, "text": str(i) * 1200} for i in range(1, 5)]
        context = format_context({"evidence": evidence})["context"]
        payload = {"status": "answered", "rows": [{"topic": "other", "reference": i, "quote": str(i) * 1200} for i in range(1, 5)]}
        with self.assertRaises(ValueError):
            parse_grounded_rows(json.dumps(payload), context)

    def test_invalid_scope_page_and_empty_passages_are_rejected(self):
        for payload in (
            {"question": "سؤال", "evidence": [{"entryId": 1, "text": " "}]},
            {"question": "سؤال", "evidence": [{"entryId": 1, "text": "النص", "pageNumber": True}]},
            {"question": "سؤال", "evidence": [], "scope": {"exhaustive": "true"}},
            {"question": "سؤال", "evidence": [], "scope": {"requestedEntryIds": [False]}}):
            with self.assertRaises(ValueError):
                validate_request(payload)

    def test_conflicting_values_in_one_page_are_allowed_but_duplicate_is_not(self):
        context = format_context({"evidence": [{"entryId": 1, "text": "التسليم: ٣ أكتوبر. التسليم: ٤ أكتوبر."}]})["context"]
        payload = {"status": "conflicting", "rows": [
            {"topic": "date", "reference": 1, "quote": "التسليم: ٣ أكتوبر"},
            {"topic": "date", "reference": 1, "quote": "التسليم: ٤ أكتوبر"}]}
        self.assertEqual(len(parse_grounded_rows(json.dumps(payload), context)["rows"]), 2)
        payload["rows"][1] = payload["rows"][0]
        with self.assertRaises(ValueError):
            parse_grounded_rows(json.dumps(payload), context)

    def test_comparison_requires_selected_evidence_from_each_requested_document(self):
        answer = json.dumps({"status": "answered", "rows": [{"topic": "date", "reference": 1, "quote": "موعد أول"}]})
        result = build_graph(FakeModel([answer])).invoke({"question": "قارن الوثيقتين 1 و2", "scope": {"requestedEntryIds": [1, 2]},
            "evidence": [{"entryId": 1, "text": "موعد أول"}, {"entryId": 2, "text": "موعد ثان"}]})
        self.assertEqual(result["selection"]["status"], "insufficient")

    def test_quote_outside_requested_scope_is_rejected(self):
        answer = json.dumps({"status": "answered", "rows": [{"topic": "other", "reference": 2, "quote": "معلومة أخرى"}]})
        result = build_graph(FakeModel([answer, answer])).invoke({"question": "الوثيقة 1", "scope": {"requestedEntryIds": [1]},
            "evidence": [{"entryId": 1, "text": "معلومة أولى"}, {"entryId": 2, "text": "معلومة أخرى"}]})
        self.assertFalse(result["verified"])

    def test_discovery_walks_all_folders_and_deduplicates_documents(self):
        class Opener:
            def open(self, url, timeout):
                folder = int(url.rsplit("/", 2)[-2])
                data = {
                    0: {"repositoryId": "TestEmployee", "folders": [{"id": 10}, {"id": 20}], "documents": [{"id": 618}]},
                    10: {"repositoryId": "testemployee", "folders": [{"id": 20}], "documents": [{"id": 608}]},
                    20: {"repositoryId": "TestEmployee", "folders": [], "documents": [{"id": 618}, {"id": 609}]},
                }[folder]
                return io.BytesIO(json.dumps(data).encode())

        self.assertEqual(discover_all("http://127.0.0.1:5187", Opener()), [608, 609, 618])

    def test_discovery_rejects_incomplete_folder_listing(self):
        class Opener:
            def open(self, url, timeout):
                return io.BytesIO(b'{"repositoryId":"TestEmployee","folders":[]}')

        with self.assertRaisesRegex(ValueError, "Incomplete folder"):
            discover_all("http://127.0.0.1:5187", Opener())


if __name__ == "__main__":
    unittest.main()
