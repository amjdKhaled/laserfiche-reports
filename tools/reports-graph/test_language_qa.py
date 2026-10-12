import unittest
from qa_language import scenario, verify, normalized_filter, leaf


class LanguageOracleTests(unittest.TestCase):
    def test_accepts_expected_filters_and_rejects_dropped_negation_or_scope(self):
        for i in range(10):
            expected = scenario(i)['expected']
            result = {'reports': [{'operation': expected['operation'], 'countOnly': expected['count'],
                                  'filters': expected['filter'], 'allResults': True}]}
            if expected.get('group'):
                result['reports'][0].update(groupFields=[{'field': expected['group'], 'bucket': None}], metrics=[])
            self.assertIsNone(verify(result, expected))
            result['reports'][0]['filters'] = None
            self.assertEqual(verify(result, expected), 'wrong_filter')

    def test_a_nonempty_but_wrong_plan_never_counts_as_success(self):
        expected = scenario(5)['expected']
        result = {'reports': [{'operation': 'search', 'filters': leaf('إجراء الوثيقة', 'equals', 'مرفوض')}]}
        self.assertEqual(verify(result, expected), 'wrong_filter')

    def test_equivalent_calendar_bounds_and_condition_order_are_accepted(self):
        self.assertEqual(normalized_filter({'field': 'created', 'operator': 'after_period', 'period': {'year': 2036}}),
                         normalized_filter(leaf('created', 'greater_or_equal', '2037-01-01')))
        a, b = leaf('الإدارة', 'equals', 'أ'), leaf('الحالة', 'not_equals', 'مرفوض')
        self.assertEqual(normalized_filter({'logic': 'and', 'conditions': [a, b]}),
                         normalized_filter({'logic': 'and', 'conditions': [b, a]}))

    def test_generator_preserves_original_followup_and_menu_question(self):
        for i in (7, 8, 207, 208, 99997, 99998):
            item = scenario(i)
            self.assertEqual(len(item['payload']['history']), 2)
            self.assertTrue(item['payload']['history'][0]['text'])
            self.assertTrue(item['expected']['filter'])

    def test_runner_counts_wrong_plan_and_resume_does_not_resubmit(self):
        # This tests the QA runner with a fixture HTTP service, not Qwen.
        import json
        import sys
        import tempfile
        import threading
        from pathlib import Path
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from unittest.mock import patch
        from qa_language import main
        class Fixture(BaseHTTPRequestHandler):
            calls = 0
            def send(self, payload):
                data = json.dumps(payload).encode()
                self.send_response(200); self.send_header('Content-Length', str(len(data)))
                self.end_headers(); self.wfile.write(data)
            def do_GET(self): self.send({'status': 'ready', 'model': 'qwen2.5:7b'})
            def do_POST(self):
                self.rfile.read(int(self.headers['Content-Length']))
                index = Fixture.calls; Fixture.calls += 1
                expected = scenario(index)['expected']
                self.send({'reports': [{'operation': 'search', 'countOnly': False,
                    'filters': expected['filter'] if index == 0 else None}]})
            def log_message(self, *args): pass
        with tempfile.TemporaryDirectory() as folder, ThreadingHTTPServer(('127.0.0.1', 0), Fixture) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            path = Path(folder) / 'results.jsonl'
            args = ['qa_language.py', '--count', '2', '--url', f'http://127.0.0.1:{server.server_port}', '--output', str(path)]
            try:
                with patch.object(sys, 'argv', args): self.assertEqual(main(), 1)
                with patch.object(sys, 'argv', args + ['--resume']): self.assertEqual(main(), 1)
                summary = json.loads(path.with_suffix('.summary.json').read_text())
                self.assertEqual((summary['executed'], summary['passed'], summary['failed']), (2, 1, 1))
                self.assertEqual(Fixture.calls, 2)
            finally:
                server.shutdown(); thread.join(timeout=3)


if __name__ == '__main__': unittest.main()
