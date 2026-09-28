import unittest

from server import NO_EVIDENCE, build_graph, validate_request


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


if __name__ == "__main__":
    unittest.main()
