import contextlib
import io
import unittest
from unittest.mock import patch
from server import parse_options

class TimeoutOptionsTests(unittest.TestCase):
    def test_one_hour_is_the_default_and_explicitly_accepted(self):
        with patch.dict('os.environ', {}, clear=True):
            for argv in ([], ['--model-timeout-seconds', '3600', '--planner-timeout-seconds', '3600']):
                options = parse_options(argv)
                self.assertEqual(options.model_timeout_seconds, 3600)
                self.assertEqual(options.planner_timeout_seconds, 3600)
    def test_invalid_planning_budget_still_fails_before_model_loading(self):
        for value in ('-1', '14', '3601'):
            with self.subTest(value=value), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                parse_options(['--planner-timeout-seconds', value])
