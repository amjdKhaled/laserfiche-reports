import json
import unittest
from server import build_graph, RoutePlan
from test_graph import FakeModel

class FastAnswerTests(unittest.TestCase):
    def test_two_calls_keep_grounded_analysis_and_independent_review(self):
        draft = {"status": "answered", "rows": [{"topic": "status", "reference": 1, "quote": "لم تتم الموافقة على الطلب"}],
                 "findings": [{"text": "لم تتم الموافقة على الطلب.", "rowIds": [1]}]}
        review = {"coverage": "sufficient", "rows": [{"rowId": 1, "relevant": True, "faithful": True}],
                  "findings": [{"findingId": 1, "supported": True}], "issues": []}
        model = FakeModel([json.dumps(draft), json.dumps(review)])
        result = build_graph(model, fast=True).invoke({"question": "هل تمت الموافقة؟",
            "evidence": [{"entryId": 618, "documentName": "حالي", "text": "لم تتم الموافقة على الطلب", "textSource": "ocr"}]})
        self.assertEqual(len(model.calls), 2)
        self.assertTrue(result["reviewed"])
        self.assertIn("لم تتم الموافقة", result["answer"])
        self.assertEqual(result["quality"]["modelCalls"], 2)

    def test_fast_path_rejects_invented_quotes_without_extra_retry(self):
        model = FakeModel([json.dumps({"status": "answered", "rows": [{"topic": "status", "reference": 1, "quote": "تمت الموافقة نهائيا"}], "findings": []})])
        result = build_graph(model, fast=True).invoke({"question": "هل تمت الموافقة؟", "evidence": [{"entryId": 618, "text": "لم تتم الموافقة"}]})
        self.assertFalse(result["verified"])
        self.assertEqual(len(model.calls), 1)

    def test_router_schema_rejects_arbitrary_tools_and_http(self):
        for value in ({"operation": "http", "url": "https://external"}, {"operation": "search", "limit": 10000},
                      {"operation": "search", "sql": "drop table documents"}):
            with self.assertRaises(ValueError):
                RoutePlan.model_validate(value)
