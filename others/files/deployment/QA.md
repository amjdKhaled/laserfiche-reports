# Reports QA — 12 October 2026

## What was actually verified

The generated contract run executed **100,000 distinct inputs**, distributed
across 20 parameterized families (5,000 each), using production parsing,
schema validation and explicit-choice validation. Seed: `20261012`.
It made **zero model calls** and **zero Laserfiche requests**. It is not a
100,000-question language-accuracy test.

Before the validation fixes: 72,486 passed and 27,514 failed their expected
accept/reject checks. Failures belonged to six families:

| Defect | Failed cases |
|---|---:|
| Arithmetic aggregation of text fields accepted | 5,000 |
| Calendar grouping of text fields accepted | 5,000 |
| Non-count metric without its field accepted | 5,000 |
| Having references a nonexistent metric | 5,000 |
| Logical group carries an empty leaf field | 5,000 |
| Long/Short field accepts a fractional value | 2,514 |

After fixes: **100,000 passed, 0 failed**. Results are stored in
[`qa/2026-10-12`](../qa/2026-10-12/). Reproduce from `tools/reports-graph`:

```powershell
python qa_contracts.py --count 100000 --output qa-contracts.json
```

Python and C# now agree on these execution constraints. Aggregation generation
grammar also limits time buckets to dates and arithmetic metrics to numbers,
so the model can repair rejected plans before executing a repository request.
Finite aggregate thresholds are required.

## Clarification and conversation fixes

An explicit displayed option, selected by number (`1`, `١`, `۱`) or exact name,
is resolved against the current catalog. The model receives the original full
question and the separate verbatim reply. Valid plans must reference the selected
catalog item. A repeated answered menu triggers repair; exhausted repair returns
an error rather than another identical menu. Clarifications after a resolved
catalog choice receive semantic review. Genuine missing criteria can still be
clarified, and independent new questions retain their own scope.

New regressions exercise the supplied screenshot's repeated menu, preservation
of year bounds/count intent, text-vs-date fields, template-vs-field choices,
additional filters, multiple reports, independent questions, reviewer rejection,
and the actual graph HTTP handler. Scripted model responses in these regression
tests are explicitly **not** Qwen semantic accuracy evidence.

Other verification: the web project built and served Arabic HTML with HTTP 200.
The C# test assembly ran 130 passing cases through a reflection runner because
VSTest cannot start in this execution environment; one PostgreSQL fixture was
skipped. UI tests: 23 passed. Python discovery: 157 tests, one optional native
grammar converter test skipped. Windows installation itself was not executed here.

## Real Qwen language evaluation

`qa_language.py` calls the running **actual Qwen graph** at `/route`; it never
falls back to a simulated model. Its 100,000 generated cases are compositional
variants of ten authored intent families, with formal Arabic, Saudi phrasing,
year bounds, numeric bounds, count/list distinction, negation, AND/OR, relative
modification dates, numbered clarification replies, follow-up counts and grouping.
The corpus uses a synthetic catalog and a fixed date of 2026-10-12. It checks
plan meaning and scope, not whether a plan merely returns something. Zero results
can be correct; a nonempty result for the wrong filter would still be wrong.

The attempt in this environment was **blocked**: no running Qwen graph was
available. **Zero real-model scenarios were executed.** The recorded status
is in `qa/2026-10-12/language-status.json`. No real repository totals or full
on-prem end-to-end accuracy have been verified.

After updating the installed application, run from the project folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\windows\qa.ps1 -Count 100000
```

The test sends planning-only requests through the graph's existing single-request
queue. It performs no document writes or repository searches. Large runs can take
a long time and share that queue with normal questions. Stop with Ctrl+C and resume:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\windows\qa.ps1 -Count 100000 -Resume
```

Results: `%LOCALAPPDATA%\LaserficheReports\logs\qa-language.jsonl` and
`qa-language.summary.json`. Each record includes the exact question/history,
expected filter, actual plan and failure reason. A changed model/runtime/corpus
requires a new output path. One graph request may use multiple model calls;
the summary counts graph requests rather than claiming a model-call count.

The synthetic corpus and its oracle cover a finite subset of valid query shapes.
An oracle mismatch needs inspection: it can indicate a planner defect or a valid
equivalent plan the oracle does not yet recognize. Hand-authored unseen prompts
and live repository results remain separate acceptance work.

Confirm the installed planner version:

```powershell
(Invoke-RestMethod http://127.0.0.1:5050/health).plannerVersion
```

Expected: `live-plan-v7.5`.

## Follow-up v7.6

Resolved menu replies now retain the original question in the executable plan
and reviewer payload as well as the planner input. Previously the plan's question
could still be `1`. A regression checks both payloads. Review instructions now
explicitly distinguish count from grouping and field-name words from folders.
These instruction changes have not been benchmarked on the user's actual model.

Installed startup now records local planning traces in logs/planner-trace.jsonl,
rotating at 10 MB into one previous file. Traces contain questions, schema and
model replies; they remain local. Collect current health and recent logs with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\deployment\windows\diagnostics.ps1
```

The language evaluator accepts any ready, identified local graph model, so 7B
and 14B can be evaluated with the same corpus. Use distinct output paths per
model; resume still checks model/runtime identity. The uploaded October 9 v7.3
logs do not establish the cause of the newer screenshot's failure.
