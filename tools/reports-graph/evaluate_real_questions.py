#!/usr/bin/env python3
"""Plan the real questions in real_questions.json with the production model and settings, then
compare each plan with its expected plan.

python evaluate_real_questions.py --catalog <repository catalog JSON> --output real-questions-result.json

The catalog is a GetRepositorySchema snapshot (the "catalog" of a route_input planner trace); it
holds repository field names and stays outside Git. A failed case is a planning error to fix in
the planner or its grounding, never a reason to add a handler for that question.
"""
import argparse
import json
import sys
import time
from datetime import date, timedelta
from pathlib import Path

from langchain_ollama import ChatOllama

from calendar_grounding import normalize_arabic
from server import DEFAULT_CHAT_MODEL, check_ollama, plan_reports


def key(value):
    """Spelling- and spacing-insensitive name, as the backend matches names."""
    return normalize_arabic(value).replace(" ", "").replace(" ", "") if isinstance(value, str) else value


def canonical_filter(node):
    """Express a single-day date as in_period, and drop unset members."""
    if not isinstance(node, dict):
        return node
    node = {k: v for k, v in node.items() if v is not None}
    if isinstance(node.get("period"), dict):
        node["period"] = {k: v for k, v in node["period"].items() if v is not None}
    if "conditions" in node:
        conditions = [canonical_filter(child) for child in node["conditions"]]
        if len(conditions) == 1:
            return conditions[0]
        # «قالب و تاريخ» and «تاريخ و قالب» select the same documents.
        node["conditions"] = sorted(conditions, key=lambda c: json.dumps(c, ensure_ascii=False, sort_keys=True))
        return node
    day = None
    if node.get("operator") == "equals" and node.get("field") in ("created", "modified"):
        day = date.fromisoformat(str(node["value"])[:10])
    elif node.get("operator") in ("between", "date_between") and node.get("field") in ("created", "modified"):
        low, high = date.fromisoformat(str(node["value"])[:10]), date.fromisoformat(str(node["upper"])[:10])
        day = low if high in (low, low + timedelta(days=1)) else None
    if day is not None:
        return {"field": node["field"], "operator": "in_period", "period": {"year": day.year, "month": day.month, "day": day.day}}
    for name in ("field", "value", "tag"):
        if name in node:
            node[name] = key(node[name])
    return node


def leaves(node):
    """Every condition of a filter tree, canonical."""
    node = canonical_filter(node)
    if not isinstance(node, dict):
        return []
    return [leaf for child in node["conditions"] for leaf in leaves(child)] if "conditions" in node else [node]


def mismatches(plan, expect):
    problems = []
    for name, wanted in expect.items():
        if name == "absent":
            problems += [f"{field} should be empty: {plan.get(field)!r}" for field in wanted if plan.get(field)]
        elif name == "includes":
            # A condition that must appear anywhere, whatever else the planner added (a template, a date).
            planned = leaves(plan.get("filters"))
            problems += [f"missing {json.dumps(leaf, ensure_ascii=False)}" for leaf in wanted if canonical_filter(leaf) not in planned]
        elif name == "absent_value":
            # A negated value: no condition may select it (not_equals is the planner's own reading).
            planned = [leaf for leaf in leaves(plan.get("filters"))
                       if leaf.get("operator") in ("equals", "contains", "starts_with") and key(wanted) in str(leaf.get("value", ""))]
            problems += [f"unexpected {json.dumps(leaf, ensure_ascii=False)}" for leaf in planned]
        elif name == "filters":
            if canonical_filter(plan.get("filters")) != canonical_filter(wanted):
                problems.append(f"filters {json.dumps(plan.get('filters'), ensure_ascii=False)}")
        elif name == "template":
            # The backend runs plan.template and a "template equals" filter the same way.
            condition = canonical_filter(plan.get("filters"))
            as_filter = (condition or {}).get("field") == "template" and condition.get("operator") == "equals" and condition.get("value") == key(wanted)
            # With "includes", the template may stand beside the other conditions: «template = X and archived».
            if "includes" in expect and (condition or {}).get("logic") == "and":
                as_filter = as_filter or {"field": "template", "operator": "equals", "value": key(wanted)} in condition["conditions"]
            if key(plan.get(name)) != key(wanted) and not as_filter:
                problems.append(f"template {plan.get(name)!r} filters {json.dumps(plan.get('filters'), ensure_ascii=False)[:160]}")
        elif name == "folderName":
            if key(plan.get(name)) != key(wanted):
                problems.append(f"{name} {plan.get(name)!r}")
        elif plan.get(name) != wanted:
            problems.append(f"{name} {plan.get(name)!r}")
    return problems


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", default=str(Path(__file__).with_name("real_questions.json")))
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--output", default="real-questions-result.json")
    parser.add_argument("--model", default=DEFAULT_CHAT_MODEL)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--only", nargs="*", help="Case ids to run")
    args = parser.parse_args()
    error = check_ollama("http://127.0.0.1:11434", args.model)
    if error:
        parser.exit(2, error + ": the model evaluation did not run.\n")
    suite = json.loads(Path(args.cases).read_text(encoding="utf-8"))
    catalog = json.loads(Path(args.catalog).read_text(encoding="utf-8"))
    # Same model settings and planning options as the installed service.
    model = ChatOllama(model=args.model, temperature=0, num_ctx=16384, num_predict=4096, keep_alive="30m",
                       client_kwargs={"timeout": args.timeout, "trust_env": False})
    results = []
    cases = [case for case in suite["cases"] if not args.only or case["id"] in args.only]
    for case in cases:
        started = time.monotonic()
        item = {"id": case["id"], "question": case["question"], "expect": case["expect"],
                "review": case.get("review", False), "note": case.get("note")}
        try:
            payload = {"question": case["question"], "catalog": catalog, "today": suite["today"],
                       "timezone": "Asia/Riyadh", "history": []}
            response = plan_reports(model, payload, budget_seconds=args.timeout, max_tokens=1536,
                                    review_intent=True, interpret_intent=False)
            plans = response["reports"]
            problems = [mismatches(plan, case["expect"]) for plan in plans]
            item.update(plans=plans, clarification=response.get("clarification"),
                        passed=any(not found for found in problems), problems=min(problems, key=len) if problems else ["no plan"])
        except Exception as failure:
            item.update(passed=False, problems=[type(failure).__name__ + ": " + str(failure)[:300]])
        item["seconds"] = round(time.monotonic() - started, 1)
        results.append(item)
        Path(args.output).write_text(json.dumps({"model": args.model, "today": suite["today"],
            "passed": sum(r["passed"] for r in results), "total": len(results), "cases": results},
            ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{len(results)}/{len(cases)} {'PASS' if item['passed'] else 'FAIL'} {item['seconds']:>5}s "
              f"{case['id']}: {case['question']}" + ("" if item["passed"] else "\n      " + "; ".join(item["problems"])), flush=True)
    passed = sum(r["passed"] for r in results)
    print(f"\n{passed}/{len(results)} passed")
    raise SystemExit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
