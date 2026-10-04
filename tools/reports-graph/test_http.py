import http.client
import json
import threading
import unittest
from http.server import ThreadingHTTPServer

from server import Handler


class HttpTests(unittest.TestCase):
    def setUp(self):
        class Graph:
            def invoke(self, payload):
                return {"answer": payload["question"], "selection": {"rows":
                    [{"reference": 1}] if payload["evidence"] else []}}

        class TestHandler(Handler):
            graph = Graph()

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), TestHandler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.client = http.client.HTTPConnection(*self.server.server_address, timeout=3)

    def tearDown(self):
        self.client.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)

    def test_utf8_json_over_both_http_framings(self):
        body = json.dumps({"question": "ما القرار؟", "evidence": []}, ensure_ascii=False).encode()
        for chunked in (False, True):
            with self.subTest(chunked=chunked):
                self.client.request("POST", "/answer", [body[:7], body[7:]] if chunked else body,
                                    headers={"Content-Type": "application/json"}, encode_chunked=chunked)
                response = self.client.getresponse()
                self.assertEqual(response.status, 200)
                self.assertEqual(json.loads(response.read())["answer"], "ما القرار؟")

    def test_invalid_json_is_a_bad_request(self):
        self.client.request("POST", "/answer", b'{"question":')
        response = self.client.getresponse()
        self.assertEqual(response.status, 400)
        response.read()

    def test_related_documents_use_selected_evidence_not_all_candidates(self):
        self.client.request("POST", "/answer", json.dumps({"question": "سؤال", "evidence": [
            {"entryId": 618, "text": "دليل مختار"}, {"entryId": 42, "text": "مرشح غير مستخدم"}]}).encode())
        response = self.client.getresponse()
        self.assertEqual(response.status, 200)
        self.assertEqual(json.loads(response.read())["relatedEntryIds"], [618])
