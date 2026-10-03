#!/usr/bin/env python3
"""Run the synthetic report-quality corpus against the local Ollama model.

This is a separate quality gate from mock-based regression tests. It prints
per-case provenance/status checks; it does not certify general model accuracy.
"""
import argparse
import json
import os
from pathlib import Path
from urllib.parse import urlsplit

from server import ChatOllama, build_graph, validate_request


def check_case(result, expected):
    selection = result.get("selection", {})
    rows = selection.get("rows", [])
    return (result.get("verified") is True and selection.get("status") == expected["status"]
            and set(expected.get("requiredReferences", [])).issubset({row["reference"] for row in rows})
            and ("allowedReferences" not in expected or
                 {row["reference"] for row in rows}.issubset(set(expected["allowedReferences"])))
            and all(not any(value in row["quote"] for row in rows)
                    for value in expected.get("forbiddenQuotations", []))
            and all(any(value in row["quote"] for row in rows)
                    for value in expected.get("quotationContains", [])))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="qwen2.5:7b")
    parser.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("adversarial_cases.json"))
    args = parser.parse_args()
    endpoint = urlsplit(args.ollama_url)
    if endpoint.scheme != "http" or endpoint.hostname not in ("localhost", "127.0.0.1", "::1"):
        parser.error("Ollama must run on local HTTP.")
    os.environ["LANGCHAIN_TRACING_V2"] = "false"
    os.environ["LANGSMITH_TRACING"] = "false"
    graph = build_graph(ChatOllama(model=args.model, base_url=args.ollama_url,
                                  format="json", temperature=0, num_ctx=16384, num_predict=4096))
    cases = json.loads(args.cases.read_text(encoding="utf-8"))
    passed = 0
    for case in cases:
        try:
            result = graph.invoke(validate_request({key: case[key] for key in ("question", "evidence", "scope")}))
            success = check_case(result, case["expected"])
            actual = result.get("selection", {}).get("status", "unverified-fallback")
        except Exception as error:
            success, actual = False, type(error).__name__
        passed += int(success)
        print(json.dumps({"case": case["name"], "passed": success, "actual": actual,
                          "expected": case["expected"]["status"]}, ensure_ascii=False), flush=True)
    print(f"Quality cases passed: {passed}/{len(cases)}", flush=True)
    raise SystemExit(0 if passed == len(cases) else 1)


if __name__ == "__main__":
    main()
