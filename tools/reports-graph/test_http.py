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

    def test_route_over_http_repairs_a_plan_and_preserves_two_reports(self):
        from test_graph import FakeModel
        from server import ReportRequest
        plans = {'reports': [
            {'resultType': 'documents', 'requiresFilter': False, 'operation': 'latest_modified', 'title': 'آخر تعديل', 'question': 'آخر وثيقة معدلة', 'limit': 1},
            {'resultType': 'documents', 'requiresFilter': False, 'operation': 'latest_created', 'title': 'آخر إنشاء', 'question': 'آخر وثيقة منشأة', 'limit': 1}]}
        invalid = json.loads(json.dumps(plans)); invalid['reports'][0]['limit'] = 10
        self.server.RequestHandlerClass.model = FakeModel([json.dumps(invalid), json.dumps(plans)])
        body = json.dumps({'question': 'آخر تعديل وآخر إنشاء', 'catalog': {'fields': [{'name': 'إجراء الوثيقة'}]}}).encode()
        self.client.request('POST', '/route', body)
        response = self.client.getresponse()
        self.assertEqual(response.status, 200)
        self.assertEqual(json.loads(response.read()), ReportRequest.model_validate(plans).model_dump(by_alias=True))

    def test_route_http_preserves_conversation_context(self):
        from test_graph import FakeModel
        plan = {'reports': [{'resultType': 'documents', 'requiresFilter': False, 'operation': 'search', 'title': 'تقرير', 'question': 'رتبها', 'limit': 50, 'sort': 'creationTime asc'}]}
        model = FakeModel([json.dumps(plan)])
        self.server.RequestHandlerClass.model = model
        history = [{'role': 'user', 'text': 'اعرض وثائق المحاسبة'}]
        self.client.request('POST', '/route', json.dumps({'question': 'رتبها بالأقدم', 'history': history}).encode())
        response = self.client.getresponse()
        self.assertEqual(response.status, 200)
        response.read()
        self.assertEqual(json.loads(model.calls[0][1].content)['history'], history)

    def test_health_checks_the_actual_local_model_registry(self):
        from unittest.mock import patch
        handler = self.server.RequestHandlerClass
        handler.ollama_url = 'http://127.0.0.1:11434'
        handler.model_name = 'qwen2.5:7b'
        for code in ('ollama_unavailable', 'model_not_found', None):
            with self.subTest(code=code), patch('server.check_ollama', return_value=code):
                self.client.request('GET', '/health')
                response = self.client.getresponse()
                payload = json.loads(response.read())
                self.assertEqual(response.status, 503 if code else 200)
                self.assertEqual(payload.get('error') if code else payload['routingVersion'], code or 'schema-agent-v5')

    def test_model_timeout_is_not_mislabeled_as_connection_failure(self):
        from httpx import ReadTimeout
        class SlowModel:
            def invoke(self, messages):
                raise ReadTimeout('slow CPU')
        self.server.RequestHandlerClass.model = SlowModel()
        self.client.request('POST', '/route', json.dumps({'question': 'عدد الوثائق'}).encode())
        response = self.client.getresponse()
        self.assertEqual(response.status, 503)
        self.assertEqual(json.loads(response.read())['error'], 'local_model_timeout')
