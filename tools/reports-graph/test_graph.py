import unittest
import io
import json

from server import NO_EVIDENCE, build_graph, validate_request
from sync import discover_all


class FakeModel:
    def __init__(self):
        self.calls = []

    def invoke(self, messages):
        self.calls.append(messages)
        return type("Reply", (), {"content": "ورد ذلك في الوثيقة [1]."})()


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
        self.assertIn("الوثيقة: لائحة؛ ID 618", model.calls[0][1].content)

    def test_laserfiche_field_source_is_identified_separately(self):
        model = FakeModel()
        build_graph(model).invoke({"question": "ما تصنيف الوثيقة 618؟", "evidence": [
            {"entryId": 618, "textSource": "laserfiche-metadata",
             "text": "التصنيف الرئيسي للوثيقة: وثائق التشغيل والصيانة"}
        ]})
        self.assertIn("Laserfiche metadata", model.calls[0][1].content)

    def test_rejects_excessive_evidence(self):
        with self.assertRaises(ValueError):
            validate_request({"question": "سؤال", "evidence": [{"entryId": 1, "text": "x"}] * 9})

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
