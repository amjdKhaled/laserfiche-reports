#!/usr/bin/env python3
"""Replay captured planning calls locally; no Laserfiche session is required.

Example: python replay_planner.py --trace logs/planner-trace.jsonl --stage QuestionIntent
The original schema and messages are retained. Defaults have no response timeout.
Outputs may differ after a model/runtime change; this is not an accuracy score.
"""
import argparse
import json
from pathlib import Path
from urllib.parse import urlsplit

from langchain_core.messages import HumanMessage, SystemMessage, AIMessage
from langchain_ollama import ChatOllama
from report_reasoning import invoke_structured


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--trace', required=True)
    parser.add_argument('--stage', default='QuestionIntent')
    parser.add_argument('--model', default='qwen2.5:7b')
    parser.add_argument('--ollama-url', default='http://127.0.0.1:11434')
    parser.add_argument('--output', default='logs/planner-replay.jsonl')
    args = parser.parse_args()
    url = urlsplit(args.ollama_url)
    if url.scheme != 'http' or url.hostname not in ('localhost', '127.0.0.1', '::1'):
        parser.error('Use local HTTP for Ollama.')
    records = [json.loads(line) for line in Path(args.trace).read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    calls = [record for record in records if record.get('stage') == args.stage and 'messages' in record and 'schema' in record]
    if not calls:
        parser.error('No matching captured calls.')
    model = ChatOllama(model=args.model, base_url=args.ollama_url, temperature=0, keep_alive='30m',
                       client_kwargs={'timeout': None, 'trust_env': False})
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    for index, call in enumerate(calls, 1):
        class CapturedSchema:
            @staticmethod
            def model_json_schema():
                return call['schema']
        messages = [{'system': SystemMessage, 'human': HumanMessage, 'ai': AIMessage}[item['role']](content=item['content'])
                    for item in call['messages']]
        options = call['options']
        try:
            response = invoke_structured(model, messages, CapturedSchema, max_tokens=options['num_predict'],
                                         num_ctx=options['num_ctx'], embed_schema=False, stream=False)
            result = {'requestId': call.get('requestId'), 'stage': args.stage, 'model': args.model,
                      'originalResponse': call.get('response'), 'replayedResponse': response}
        except Exception as error:
            result = {'requestId': call.get('requestId'), 'stage': args.stage, 'errorType': type(error).__name__}
        with target.open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(result, ensure_ascii=False) + '\n')
        print(f'{index}/{len(calls)} saved to {target}', flush=True)


if __name__ == '__main__':
    main()
