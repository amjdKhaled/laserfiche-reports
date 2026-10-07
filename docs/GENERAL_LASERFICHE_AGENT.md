# Schema-aware Laserfiche agent

## Execution contract

The existing UI is preserved. `app.js` changes only the chat request body: up to eight recent turns from the active conversation (user text up to 2,000 characters, assistant context up to 1,000). Context is neither a data source nor a cache of live results.

Every question reads the selected repository's FieldDefinitions and TemplateDefinitions. `GetRepositorySchema` supplies all field names/types, required/multi-value flags, templates and built-in entry properties to the local **qwen2.5:7b** planner. This snapshot is reused only within the HTTP request. No field names or question examples are embedded in the system prompt. Old exact-question/keyword routing helpers have been removed.

The planner produces one structured request containing up to six independent reports. A normal request makes one planning call; one bounded repair is allowed for invalid JSON/schema references. Python validates names against the supplied schema; C# independently validates field existence, types, operators, paging, sorting, bounds and identifiers before converting conditions to Laserfiche search syntax. Only numbers mentioned in the question/conversation may become direct entry/folder identifiers; Qwen decides their meaning.

The generic operations map to SearchEntries, AggregateEntries, GetEntry/GetEntryMetadata, GetFolderContents, GetFolderInformation, GetTemplates, GetRepositorySchema/GetAvailableFields/GetFieldDefinition and GetOcrContent. Schema is discovered eagerly, so it does not cost a separate LLM round trip. Filters/selection and OCR within one report form a dependent live-search → IDs → OCR pipeline. Multiple report items execute in order; arbitrary tool-to-tool output references outside this pipeline are not supported.

## Supported structured operations

- Nested AND/OR (up to five levels), equals, not_equals, contains, starts_with, numeric/date comparisons, inclusive between, is_empty and is_not_empty.
- Date expressions resolved in Backend using Asia/Riyadh: day/week/month/year, calendar start/end and rolling offsets. Calendar weeks start Sunday. Calendar periods use `>= start` and `< end`; the end is exclusive. Explicit dates use ISO `yyyy-MM-dd`.
- Listing with page/limit and created/modified/name/ID sorting. Metadata sorting reads the bounded full selection then sorts numerically, by date, or by text and pages the sorted result.
- Grouping by up to four metadata fields or entry properties, with day/week/month/year buckets for dates. Up to four metrics: count, sum, average, min, max, distinct_count. Generic `having` conditions filter computed metrics; `rollup` calculates the first metric across all groups before paging (including average document counts per month). Numeric metrics require numeric fields. Rollup averages include observed groups; missing time buckets are not automatically filled with zeros. Groups are sorted/paged **after** complete calculation; null metric values are not zero.
- Exact count-only responses use Laserfiche TotalCount without downloading entry details. A document report uses the original document table, without a path column; it is not implicitly an aggregation. The planner defaults `allResults=true` for ordinary listings, so the backend follows all search pages and verifies row count against TotalCount. Explicit limited/recent/page requests retain pagination via `allResults=false`. Complete listings, like aggregation, retain the 10,000-result safety bound and fail explicitly rather than silently omitting documents. Rows are rendered by the backend without sending them to Qwen.
- Document metadata by ID; name lookup must resolve uniquely before reading a particular document. Duplicate names produce a short request to choose an ID, with candidates.
- OCR summaries read only content chunks (never indexed metadata). `contentMode=search` uses scoped lexical/vector retrieval for topic matching; `summary` reads selected document passages. Hybrid retrieval is restricted to IDs from live Laserfiche. Current names and permissions come from live search or live entry checks.

Simple live facts/tables/counts bypass `/present`. OCR normally uses one final combined extraction/analysis call, with mandatory verbatim provenance, citation ownership and numeric checks. It does **not** claim independent semantic review. Optional `server.py --review-content` retains the additional independent review when desired. Failed verification never becomes invented facts. Model, context size and timeouts have not been increased.

## Retrieval performance

Report links revalidate IDs with live search batches of 100, rather than sequential GET /Entries/{id}. A 151-ID test needs two searches. Search projections use repeated `fields=` parameters (up to ten), so grouping usually needs no per-entry field requests. Truncated/omitted projections use bounded parallel live field retrieval only when necessary. FieldDefinitionId joins recover names where the entry fields API omits them.

Listing uses search page counts/names/paths already returned. No path calls are made solely to render a table or link. Missing displayed page counts use up to four parallel details calls, and unavailable values remain `—`. Explicit metadata detail and OCR authorization retrieval also have bounded concurrency. Hybrid OCR reuses live selected identities instead of fetching each ID again.

The API's field projection format and search syntax follow official documentation:

- https://developer.laserfiche.com/docs/guides/documents-and-folders/guide_get-folder-listing/
- https://doc.laserfiche.com/laserfiche.documentation/11/userguide/en-us/Subsystems/client_wa/Content/Search/Advanced/Template_Field.htm
- https://doc.laserfiche.com/laserfiche.documentation/11/userguide/en-us/Subsystems/client_wa/Content/Search/Advanced/Operators.htm

## Validation performed here

- Python contract/grounding/HTTP tests: 62 passed (fake models; **not proof of Qwen language comprehension**).
- Web Backend tests: 92 passed; 1 external PostgreSQL integration test skipped.
- Infrastructure tests: 251 passed, including repeated field parameters and ascending sorting.
- Existing UI/export tests: 17 passed.
- Actual ASP.NET process with HTTP fixtures: login, live search filter compilation, 50 displayed rows/TotalCount=50 across three search pages, original document columns without path, count-only=50, grouping=25+25, and link validation succeeded. Zero entry GETs and zero `/present` calls in that fixture. The fixture uses predetermined tool plans, not real Qwen. Its ~0.15s listing time is **not a real Ollama/Laserfiche performance measurement**.

## Required real-model acceptance

`tools/reports-graph/agent_cases.py` contains **56 held-out Arabic questions**, formal/colloquial paraphrases, three different due-date schemas, counts, relative dates, compound conditions, negation, aggregation, sorting, follow-up, document/folder/template questions, OCR and hybrid intent. It is never imported by the production planner. Expected plans are not sent to Qwen. Unit tests exercise the contract only.

Run after fetching this branch and restarting the graph and Web application:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\start.ps1 -Model qwen2.5:7b -TimeoutSeconds 600
```

In another terminal, run actual model evaluation:

```powershell
.\tools\reports-graph\.venv\Scripts\python.exe .\tools\reports-graph\evaluate_planner.py --output planner-evaluation.json
```

The evaluator compares structured intent/conditions against a held-out schema and accepts equivalent literal/relative dates and latest-tool aliases. It records the real plans and durations and exits nonzero for failures. This is a planner acceptance test, not a live Laserfiche result test. Then repeat metadata, aggregation and hybrid queries against the actual selected repository, edit a field, and re-query to verify freshness and server date interpretation. Never add question-specific handlers to fix failed cases.

**This environment has no running Ollama/qwen2.5:7b, real Laserfiche or Supabase OCR connection. Real-model and live-service acceptance could not be executed. The full Definition of Done is therefore pending, and this change must not be described as proven to understand all Arabic questions.**

## Deliberate limits

Complete aggregation/metadata sorting is bounded by the existing 10,000-result safety limit and fails rather than estimating. Multi-value grouping/sorting needs a defined attribution rule and requests clarification instead of counting a document ambiguously. Blank-field filters mean an assigned empty field; they do not prove an absent field is assigned. Relative calendar expressions do not support Hijri conversion. Arbitrary Workflow state, historical values, content-wide counts and arbitrary cross-report joins are not inferred when tools cannot establish them. OCR summaries remain selected-passage analyses, not complete-document guarantees. The model may still produce an incorrect *valid* intent; only real-model acceptance can measure that risk.

## Planner intent contract (intent-v5)

Real user logs showed a new listing question still produced group(Subject, Date/month) over all 76 entries after fetching the previous fix. Laserfiche execution was about 3 seconds; model planning took about 600 seconds cold and 84 seconds later. These logs establish that planning dominates, but do not distinguish model loading, prompt evaluation, generation or hardware pressure.

Each generated plan now begins with required resultType and requiresFilter decisions. Python and Backend reject a documents intent paired with aggregation/count/content, and reject a declared selection restriction with no corresponding condition. This is semantic model output validation, not question-specific keyword routing; an internally consistent wrong intent remains possible and must be tested on real Qwen. No extra review call is added: one planning call, at most one repair of an invalid plan.

The planner uses a shorter generic prompt and a compact JSON schema (annotations removed, validation/property names retained). Output budget is 2048 instead of 4096; ordinary input uses 8192 context instead of 16384, with 16384 retained for larger complete catalogs. All discovered field names remain available. Ollama load/prompt/generation timings, token counts and validated operation/filter-presence are logged without document content. The schema-agent-v5 health handshake prevents a new Web backend accepting the previous graph process. Startup prints planner=intent-v5. No timeout increase, model change or UI edits.

62 Python tests, 92 Web tests (1 external integration skipped), and 17 UI/export tests pass. Actual ASP.NET HTTP-fixture smoke still displays all 50 document rows across three pages, count=50 and requested aggregation=25+25. Fixtures prove execution/validation, not real Qwen comprehension or speed. Actual Ollama/Laserfiche acceptance remains unavailable in this workspace.

## Validation diagnostics and clarification repair (intent-v5.1)

The next real log failed with ValidationError twice; the former handler logged only the exception class, so the exact failed field/condition is not recoverable from that attachment. Measured prompt evaluation was 404.6s then 244.5s; generation 78.7s then 96.1s; model load only ~32ms then ~24ms. No claim of a hardware cause or successful real-model repair can be made from these logs.

Fixed a reproducible contract bug: clarification can retain the user's intended result type (documents/count/content) and requiresFilter while executing no query. Previously such a clarification was rejected as a documents/operation contradiction. Clarifications carrying executable conditions still fail; document/group contradictions and field validation remain enforced. Rejected plans now log up to eight error paths/types/messages, excluding raw input/context. Repair receives its rejected draft and specific errors instead of an exception cut at 250 characters.

The full JSON schema remains in Ollama's format parameter and output is still validated independently. For planning only, do not duplicate the full schema in the prompt. Repository fields are sent as compact [name,type,multi-value] tuples without dropping any name; Backend validation retains the authoritative named catalog. The original UI remains; only the chat history row background and delete button alignment/focus styling change. Startup prints planner=intent-v5.1 with the compatible schema-agent-v5 protocol.

Validation: 65 Python tests, including a real ChatOllama HTTP transport against a mocked Ollama endpoint; 93 Web tests (1 external PostgreSQL integration skipped); 17 UI/export tests. Real qwen2.5:7b inference and live Laserfiche acceptance remain unavailable here. Prior HTTP-fixture smoke was not rerun for this increment.
