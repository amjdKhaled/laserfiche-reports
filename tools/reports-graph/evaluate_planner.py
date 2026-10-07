#!/usr/bin/env python3
"""Run actual qwen2.5:7b against held-out questions. Fixtures are NOT in its prompt.

python evaluate_planner.py --output planner-evaluation.json
A failed case is a model/contract regression, never a reason to add a question handler.
"""
import argparse
import json
import time
import calendar
from datetime import date, timedelta
from pathlib import Path
from langchain_ollama import ChatOllama
from server import plan_reports, check_ollama, DEFAULT_CHAT_MODEL
from agent_cases import CASES


def includes(actual, expected):
    if isinstance(expected, dict):
        return isinstance(actual, dict) and all(key in actual and includes(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return isinstance(actual, list) and all(any(includes(item, wanted) for item in actual) for wanted in expected)
    return actual == expected


def normalize_plan(plan, today_text):
    """Compare actual conditions, allowing literal dates and equivalent tool aliases."""
    today = date.fromisoformat(today_text)
    def relative(value):
        unit, offset, boundary = value.get("unit", "day"), value.get("offset", 0), value.get("boundary", "start")
        start = today
        if unit == "week":
            if boundary != "rolling": start -= timedelta(days=(today.weekday() + 1) % 7)
            start += timedelta(days=offset * 7)
        elif unit == "day": start += timedelta(days=offset)
        else:
            if boundary != "rolling": start = date(today.year, today.month if unit == "month" else 1, 1)
            months = offset * (12 if unit == "year" else 1)
            year, month = divmod(start.year * 12 + start.month - 1 + months, 12)
            start = date(year, month + 1, min(start.day, calendar.monthrange(year, month + 1)[1]))
        if boundary == "end":
            if unit in ("day", "week"): start += timedelta(days=1 if unit == "day" else 7)
            else:
                year, month = divmod(start.year * 12 + start.month - 1 + (1 if unit == "month" else 12), 12)
                start = date(year, month + 1, 1)
        return start.isoformat()
    def node(value):
        if not isinstance(value, dict): return value
        output = {k: v for k, v in value.items() if v is not None}
        if "conditions" in output:
            output["conditions"] = [node(c) for c in output["conditions"]]
        for key, literal in (("relative", "value"), ("upperRelative", "upper")):
            if key in output: output[literal] = relative(output.pop(key))
        aliases = {"date_before": "less_than", "date_after": "greater_than", "date_between": "between"}
        if "operator" in output: output["operator"] = aliases.get(output["operator"], output["operator"])
        return output
    result = dict(plan)
    if result.get("operation") in ("latest_created", "latest_modified"):
        result["sort"] = "creationTime desc" if result["operation"] == "latest_created" else "lastModifiedTime desc"
        result["operation"] = "search"
    if result.get("field") and result.get("value") is not None:
        result["filters"] = {"field": result["field"], "operator": "equals", "value": result["value"]}
    if result.get("groupBy"):
        result["groupFields"] = [{"field": result["groupBy"]}]
    if result.get("filters"): result["filters"] = node(result["filters"])
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default="planner-evaluation.json")
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--model", default=DEFAULT_CHAT_MODEL)
    parser.add_argument("--limit", type=int, default=len(CASES))
    args = parser.parse_args()
    error = check_ollama("http://127.0.0.1:11434", args.model)
    if error:
        parser.exit(2, error + ": actual model evaluation did not run.\n")
    model = ChatOllama(model=args.model, temperature=0, num_ctx=16384, num_predict=4096,
                       keep_alive="30m", client_kwargs={"timeout": args.timeout, "trust_env": False})
    results = []
    for case in CASES[:args.limit]:
        started = time.monotonic()
        item = {"question": case["question"], "expected": case["expect"]}
        try:
            payload = {key: value for key, value in case.items() if key != "expect"}
            request = plan_reports(model, payload, budget_seconds=args.timeout)
            item.update(plan=request, passed=any(includes(normalize_plan(plan, case["today"]), normalize_plan(case["expect"], case["today"])) for plan in request["reports"]))
        except Exception as error:
            item.update(passed=False, error=type(error).__name__ + ": " + str(error)[:300])
        item["seconds"] = round(time.monotonic() - started, 3)
        results.append(item)
        Path(args.output).write_text(json.dumps({"model": args.model, "passed": sum(r["passed"] for r in results), "total": len(results), "cases": results}, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{len(results)}/{min(args.limit, len(CASES))}: {'PASS' if item['passed'] else 'FAIL'} ({item['seconds']}s) {case['question']}", flush=True)
    raise SystemExit(0 if all(r["passed"] for r in results) else 1)

if __name__ == "__main__":
    main()
