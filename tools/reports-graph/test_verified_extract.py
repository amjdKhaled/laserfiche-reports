import unittest
from verified_extract import validate_matches, extract


class Message:
    def __init__(self, content):
        self.content = content


class VerifiedExtractionTests(unittest.TestCase):
    def test_all_73_documents_are_checked_in_bounded_batches(self):
        evidence = [{"text": f"محتوى وثيقة {i}"} for i in range(73)]
        selected = []
        for start in range(0, len(evidence), 2):
            batch = evidence[start:start + 2]
            result = {"matches": [
                {"sourceIndex": index, "relevant": True, "quotes": [item["text"]]}
                for index, item in enumerate(batch)
            ]}
            selected.extend(match["quotes"][0] for match in validate_matches(result, batch))
        self.assertEqual(selected, [item["text"] for item in evidence])

    def test_missing_passage_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "every passage"):
            validate_matches({"matches": []}, [{"text": "مصدر"}])

    def test_hallucinated_quote_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "not supported"):
            validate_matches({"matches": [
                {"sourceIndex": 0, "relevant": True, "quotes": ["ID 100158"]}
            ]}, [{"text": "ID 598"}])

    def test_duplicate_source_is_rejected(self):
        item = {"sourceIndex": 0, "relevant": False, "quotes": []}
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            validate_matches({"matches": [item, item]}, [{"text": "a"}, {"text": "b"}])

    def test_invalid_model_output_is_retried_then_fails_without_partial_results(self):
        class Model:
            calls = 0
            def invoke(self, messages):
                self.calls += 1
                return type("Reply", (), {"content": '{"matches":[]}'})()
        model = Model()
        with self.assertRaisesRegex(ValueError, "incomplete"):
            extract(model, "سؤال", [{"text": "مصدر"}], Message, Message)
        self.assertEqual(model.calls, 2)


if __name__ == "__main__":
    unittest.main()

