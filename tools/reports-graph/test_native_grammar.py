"""Optional integration test against llama.cpp's actual schema converter.

Set LLAMA_SCHEMA_CONVERTER to examples/json_schema_to_grammar.py from
ggml-org/llama.cpp commit 00681df. No model, network or repository data needed.
The always-on structural regressions are in test_live_planner.py.
"""
import importlib.util
import os
import unittest

from report_reasoning import compact_schema
from server import direct_planner_schema


@unittest.skipUnless(os.environ.get('LLAMA_SCHEMA_CONVERTER'), 'External llama.cpp converter not configured')
class NativeGrammarTests(unittest.TestCase):
    def test_full_live_contract_compiles_with_typed_filter_rules(self):
        spec = importlib.util.spec_from_file_location('llama_schema_converter', os.environ['LLAMA_SCHEMA_CONVERTER'])
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        catalog = {'fields': [{'name': 'Expiry', 'fieldType': 'Date'},
                              {'name': 'Cost', 'fieldType': 'Number'},
                              {'name': 'Status', 'fieldType': 'String'}],
                   'templates': ['Records'], 'tags': [{'name': 'Review'}], 'tagStatus': 'complete',
                   'entryProperties': ['created', 'modified', 'entryId', 'name']}
        contract = compact_schema(direct_planner_schema(catalog).model_json_schema())
        converter = module.SchemaConverter(prop_order={}, allow_fetch=False, dotall=False, raw_pattern=False)
        converter.visit(converter.resolve_refs(contract, 'stdin'), '')
        grammar = converter.format_grammar()
        rules = dict(line.split(' ::= ', 1) for line in grammar.splitlines())
        self.assertIn('RepositoryFilter', rules)
        self.assertIn('Selection-1-filters-kv', rules['Selection-1'])
        self.assertIn('RepositoryFilter', rules['Selection-1-filters'])
        self.assertIn('CalendarPeriod', rules)
        self.assertNotEqual(rules['RepositoryFilter'], 'value')


if __name__ == '__main__':
    unittest.main()
