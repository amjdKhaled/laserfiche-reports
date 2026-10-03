import unittest
import io
import json

from server import NO_EVIDENCE, build_graph, validate_request, valid_report, format_context
from sync import discover_all


class FakeModel:
    def __init__(self, answers=None):
        self.calls = []
        self.answers = iter(answers or ["# تقرير\n\n| البند | النتيجة | المرجع |\n| --- | --- | --- |\n| القرار | قرار مجلس الإدارة | [1] |"] * 2)

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

    def test_report_requires_references_on_every_row(self):
        self.assertFalse(valid_report("# تقرير\n| البند | المصدر |\n| --- | --- |\n| ادعاء | [1] |\n| ادعاء آخر | بلا مرجع |", 1))
        self.assertFalse(valid_report("# تقرير\n| البند | المصدر |\n| --- | --- |\n| ادعاء | [8] |", 1))

    def test_invalid_model_response_falls_back_to_actual_sources(self):
        model = FakeModel(["ادعاء مخترع [99]", "ادعاء مخترع [99]"])
        result = build_graph(model).invoke({"question": "سؤال", "evidence": [
            {"entryId": 618, "documentName": "لائحة", "text": "المعلومة الأصلية"}]})
        self.assertEqual(len(model.calls), 2)
        self.assertIn("المعلومة الأصلية", result["answer"])
        self.assertNotIn("ادعاء مخترع", result["answer"])
        self.assertTrue(valid_report(result["answer"], 1))

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
