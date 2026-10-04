#!/usr/bin/env python3
"""Run the synthetic report-quality corpus against the local Ollama model.

This is a separate quality gate from mock-based regression tests. It prints
per-case provenance/status checks; it does not certify general model accuracy.
"""
import argparse
import json
import os
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

from server import ChatOllama, build_graph, validate_request
from report_reasoning import PROMPT_VERSION


def check_case(result, expected):
    selection = result.get("selection", {})
    rows = selection.get("rows", [])
    return (result.get("verified") is True and selection.get("status") == expected["status"]
            and (selection.get("status") == "insufficient" or bool(rows))
            and (not rows or result.get("reviewed") is True)
            and len(result.get("findings", [])) >= expected.get("minimumFindings", 0)
            and set(expected.get("requiredReferences", [])).issubset({row["reference"] for row in rows})
            and ("allowedReferences" not in expected or
                 {row["reference"] for row in rows}.issubset(set(expected["allowedReferences"])))
            and all(not any(value in row["quote"] for row in rows)
                    for value in expected.get("forbiddenQuotations", []))
            and all(any(value in row["quote"] for row in rows)
                    for value in expected.get("quotationContains", []))
            and all(any(value in item["text"] for item in result.get("findings", []))
                    for value in expected.get("findingContains", []))
            and all(not any(value in item["text"] for item in result.get("findings", []))
                    for value in expected.get("forbiddenFindings", [])))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="qwen2.5:7b")
    parser.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("adversarial_cases.json"))
    parser.add_argument("--output", type=Path, help="Optional JSON quality report; excludes document text.")
    args = parser.parse_args()
    endpoint = urlsplit(args.ollama_url)
    if endpoint.scheme != "http" or endpoint.hostname not in ("localhost", "127.0.0.1", "::1"):
        parser.error("Ollama must run on local HTTP.")
    os.environ["LANGCHAIN_TRACING_V2"] = "false"
    os.environ["LANGSMITH_TRACING"] = "false"
    graph = build_graph(ChatOllama(model=args.model, base_url=args.ollama_url,
                                  temperature=0, num_ctx=16384, num_predict=4096))
    cases = json.loads(args.cases.read_text(encoding="utf-8"))
    passed = 0
    results = []
    for case in cases:
        started = time.monotonic()
        result = {}
        try:
            result = graph.invoke(validate_request({key: case[key] for key in ("question", "evidence", "scope")}))
            success = check_case(result, case["expected"])
            actual = result.get("selection", {}).get("status", "unverified-fallback")
        except Exception as error:
            success, actual = False, type(error).__name__
        passed += int(success)
        item = {"case": case["name"], "passed": success, "actual": actual,
                "expected": case["expected"]["status"], "quality": result.get("quality"),
                "elapsedSeconds": round(time.monotonic() - started, 2)}
        results.append(item)
        print(json.dumps(item, ensure_ascii=False), flush=True)
    print(f"Quality cases passed: {passed}/{len(cases)}", flush=True)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps({"promptVersion": PROMPT_VERSION, "model": args.model,
            "generatedAt": datetime.now(timezone.utc).isoformat(), "passed": passed,
            "total": len(cases), "cases": results}, ensure_ascii=False, indent=2), encoding="utf-8")
    raise SystemExit(0 if passed == len(cases) else 1)


if __name__ == "__main__":
    main()
