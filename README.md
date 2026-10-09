# Laserfiche Reports

Fully local, on-premise AI reporting and chat for Laserfiche.

## Phase 1

- Reuse the proven Laserfiche Repository API integration from `amjdKhaled/Asset-Manager-1zip`.
- Keep Laserfiche as the source of truth.
- Run the web application, LangGraph, Supabase/PostgreSQL with pgvector, and Ollama on the same machine.
- Never send documents, metadata, prompts, embeddings, or credentials to a cloud service.

## Planned local flow

1. Read documents and metadata from Laserfiche Repository API.
2. Use existing Laserfiche text when available; otherwise run local OCR.
3. Split extracted text into chunks.
4. Create embeddings locally and store them in Supabase PostgreSQL/pgvector.
5. Retrieve evidence for a user's question.
6. Generate an Arabic/English answer through local Ollama with LangChain and LangGraph.
7. Show the answer and its Laserfiche document evidence in one chat interface.

## Repository layout

- `src/LaserficheReports.Domain` — Laserfiche entities and domain errors.
- `src/LaserficheReports.Application` — service interfaces and application DTOs.
- `src/LaserficheReports.Infrastructure` — Repository API authentication, HTTP clients, search, entries, metadata, documents, and repository discovery.
- `src/LaserficheReports.Web` — local ASP.NET Core host and API endpoints.
- `src/LaserficheReports.Infrastructure.Tests` — regression tests carried over for API versions, authentication, repository parsing, paging, URL construction, document preview, and traversal.
- `database/migrations` — local Supabase/PostgreSQL schema and pgvector search function.
- `scripts/apply-database.ps1` — applies and verifies the local database schema.
- `docs` — architecture and phased implementation notes.

## Local configuration

Copy `src/LaserficheReports.Web/appsettings.Local.example.json` to
`appsettings.Local.json`. The local file is excluded from Git so credentials are
never committed.

## Development order

1. Laserfiche connection and document retrieval.
2. Local Supabase schema and one-document ingestion.
3. Local Arabic OCR fallback through non-generative PP-OCRv5. (implemented)
4. Chunking and local embeddings. (implemented)
5. Vector retrieval and local LLM answering. (implemented on the feature branch)
6. Single-chat UI. (implemented on the feature branch)
7. Local LangGraph orchestration and complete repository discovery. (implemented on the feature branch)

## Run the chat interface (Windows)

This branch adds a local chat and document interface at
`http://127.0.0.1:5187/`. The existing `public.documents` table is reused.
The web host accepts loopback requests only while it uses local Laserfiche
credentials. LangGraph and Ollama also bind to loopback.
OCR is disabled by default for repository-wide indexing. A private
`appsettings.Local.json` or environment override can explicitly enable it later.
Ingestion still embeds the document name, path,
template, dates and populated Laserfiche metadata fields for RAG. Reindexing
an existing document in this mode updates its metadata evidence without
deleting its existing OCR page chunks.

1. Run local Supabase and Ollama. Ensure `nomic-embed-text-v2-moe` and
   `qwen2.5:7b` are installed in Ollama.
2. Copy `src/LaserficheReports.Web/appsettings.Local.example.json` to
   `src/LaserficheReports.Web/appsettings.Local.json` and enter your existing
   Laserfiche and local PostgreSQL details. Do not commit the private file.
3. Apply the database migrations if they have not already been applied:
   `powershell -ExecutionPolicy Bypass -File .\scripts\apply-database.ps1`.
4. Set up the small Python graph environment once:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\setup.ps1
```

5. In a dedicated PowerShell window start the answer graph:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\start.ps1 -Model qwen2.5:7b
```

6. In another PowerShell window start .NET:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5187"
dotnet run --project .\src\LaserficheReports.Web
```

Open `http://127.0.0.1:5187/` and sign in with your Laserfiche account.
For a short demo, leave OCR disabled and index Entry `618` from **Documents & system**.
Ask “ما تصنيف الوثيقة 618؟” or “ما موعد تسليم الوثيقة 618؟”; expand a source
to see the exact Laserfiche field text used in the answer. A page source links
to its original image. Content analysis requires the local LangGraph and Ollama services.
Live metadata equality and inventory reports use Laserfiche directly.
To index every document accessible to the current Laserfiche account, use
**فهرسة المستودع بالكامل** in the same tab. The browser walks the repository
folder tree, indexes documents one at a time, checkpoints after every item,
and shows failures for retry. Keep that browser tab open while it runs; if it
closes, sign in again and resume. A new scan restarts discovery. Indexing a
large repository can take hours, especially when OCR is enabled. Documents
that cannot be read are excluded from the index; a failed folder is reported
explicitly rather than silently counted as complete. The indexed document
list supports search and paging beyond the first 100 rows.
The **Documents & system** tab shows
database, repository, graph, and optional OCR status. Its ingestion form
indexes a chosen Entry ID. Verify the services from another PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-local.ps1
```

To process every accessible document from a command line LangGraph batch:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\sync.ps1 -All
```

The command recursively discovers the root and all child folders, rejects
incomplete folder listings, de-duplicates document IDs, and reports ingestion
failures. It needs the web application running and uses a separate authenticated
local session. To process several known documents instead:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\sync.ps1 -EntryIds 618,609
```

The sync command can run from Windows Task Scheduler for a full rescan or
selected IDs. Change-only synchronization, enterprise SSO, and production-wide
deployment still need design and validation. Browser login uses session-specific Laserfiche credentials, and
retrieved entries are checked against the live repository before display.
The web service accepts loopback requests only. Chat history stays in each
browser's local storage; no new history table is created.

The detailed implementation sequence is documented in `docs/ROADMAP.md`.

## Prepare the local Supabase database

With the local Supabase Docker stack running:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\apply-database.ps1
```

This validates and reuses the existing local `documents` table, then adds the
`match_laserfiche_reports_documents` vector-search function. Every row created
for this project is labelled in metadata, while original documents remain in
Laserfiche and are not copied into PostgreSQL.

## Test one-document ingestion

Set the local repository and PostgreSQL connection in
`src/LaserficheReports.Web/appsettings.Local.json` (do not commit this file):

```json
{
  "Laserfiche": {
    "ServerUrl": "https://localhost",
    "RepositoryId": "testemployee"
  },
  "Supabase": {
    "PostgresConnectionString": "Host=localhost;Port=5432;Database=postgres;Username=postgres.YOUR_POOLER_TENANT_ID;Password=YOUR_LOCAL_PASSWORD;SSL Mode=Disable"
  },
  "Ocr": {
    "Enabled": false,
    "BaseUrl": "http://127.0.0.1:8765",
    "TimeoutSeconds": 1800,
    "MinimumTextLength": 3,
    "MaxImageSizeMegabytes": 50,
    "MaxFallbackPages": 100
  },
  "LocalAI": {
    "Provider": "Ollama",
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text-v2-moe",
    "EmbeddingDimensions": 768,
    "ChunkSize": 1200,
    "OcrCorrectionEnabled": true,
    "OcrCorrectionModel": "qwen2.5:7b",
    "TimeoutSeconds": 600,
    "ChunkOverlap": 80
  }
}
```

Install the local PaddleOCR worker once from a normal PowerShell window (no
Administrator privileges are required):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\paddleocr-vl\setup.ps1
```

The setup creates an isolated Python environment inside `tools/paddleocr-vl`.
Start the worker in its own PowerShell window before the .NET application:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\paddleocr-vl\start.ps1 `
  -TextDetectionMaxSideLength 4000 `
  -PreprocessingProfile quality
```

The first start downloads the PaddleOCR detection/orientation models and the Arabic PP-OCRv5 recognition model
and can take several minutes. When the console says `PaddleOCR Arabic worker is
ready`, verify it with:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:8765/health"
```

The setup also installs headless OpenCV and CAMeL Tools' Modern Standard Arabic
morphology database. Only CAMeL's morphology runtime dependencies are installed
in this dedicated worker environment; its unrelated PyTorch/Transformer NLP
components are intentionally omitted. The default `quality` profile runs the
exact original image and an independent OpenCV CLAHE contrast variant. CAMeL
morphology, Paddle's recognition score, and conservative noise checks rank the
candidates. CAMeL is never used to rewrite, spell-correct, or invent recognized
text. A variant must beat the original by a safety margin and retain at least
60% of its tokens before it can be selected.

`quality` performs two full OCR passes and therefore takes roughly twice as long
as `-PreprocessingProfile original` on CPU. Use `thorough` only for a measured
experiment; it adds adaptive thresholding as a third full pass. Submit one
ingestion request at a time; concurrent OCR requests are rejected with
`ocr_busy` instead of waiting in a long queue.

After the .NET application starts, verify the complete application-to-worker
connection with:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:5187/api/ocr/status"
```

OCR is used only when Laserfiche has no searchable page text. Page images are
sent only over the machine's loopback interface; the .NET service rejects any
non-loopback OCR URL. The worker temporarily writes one page to the operating
system temp directory for model inference and deletes it immediately afterward.
No page image is stored in PostgreSQL or sent to an external OCR service. If the
worker is unavailable, ingestion returns HTTP 503 and preserves any existing
indexed content and chunks. Electronic documents that report `pageCount=0` are
probed through the V2 Export endpoint so OCR is still invoked; probing stops at
the first unavailable page and is capped by `MaxFallbackPages`. The worker uses
the non-generative `PaddleOCR` text pipeline with the
`arabic_PP-OCRv5_mobile_rec` recognition model. Text-only Ollama rewriting is disabled by default; even when an older local
configuration enables it, changed text is rejected until image-backed verification
is available. This protects names as well as digit shapes and numeric order.
Entry `618` is the Arabic reference document used for testing, not a language restriction.
Each OCR response includes the SHA-256 hash of the received page; the .NET service
checks that hash before storing text. Low-confidence text is excluded from the indexed result. The worker exposes
`reviewLines` and `needsReview` when Paddle provides rejected line scores.
`--minimum-score` defaults to `0.35`. These engine scores are not calibrated
accuracy percentages. Review metadata is logged by .NET, not persisted in the
existing database schema. The worker response also reports `selectedVariant` and
per-candidate confidence/morphology diagnostics. `/health` reports `opencv`,
`camelTools`, and `preprocessingProfile`; verify `camelTools : True` before the
Entry 618 accuracy test.

See [Arabic OCR accuracy evaluation](docs/ARABIC_OCR_ACCURACY.md) for paired
local experiments and the outstanding real-document validation gate.

Start the API with local Laserfiche credentials, then ingest the Arabic reference Entry `618`:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
dotnet run --project .\src\LaserficheReports.Web
```

Open the browser interface, sign in to Laserfiche, and use the
**Documents & system** tab to ingest Entry `618`. The API requires that
browser's authenticated session.

The ingestion request saves the document identity, metadata, and searchable
page text. It prefers text already available in Laserfiche and runs local OCR
only for missing pages. It splits metadata and page text into distinct chunks
and uses the local Ollama `nomic-embed-text-v2-moe` model to create 768-dimensional
embeddings. The document row keeps `embedding = NULL`; its `document-chunk`
rows contain the vectors used by retrieval. Running with OCR enabled replaces
this document's project-owned chunks. Running with OCR disabled and no native
page text refreshes only metadata chunks, preserving existing page evidence.

To verify that the application can retrieve document content as well as
metadata, stream page 1 to a local file:

```powershell
Invoke-WebRequest `
  -Uri "http://127.0.0.1:5187/api/laserfiche/documents/618/pages/1/image" `
  -OutFile ".\laserfiche-618-page-1.bin"
```

The response is streamed from Laserfiche and is not stored on the application
server. The temporary output above exists only because the test caller requests
an output file.

## Structured reports and repository scope

Answers render as reports with a summary, results table, notes and numbered
sources. Click a reference to inspect its source. **تحميل التقرير** downloads a
standalone UTF-8 HTML report or Markdown file with the question, original answer
time, scope and original evidence text. HTML requires no network or scripts;
open it in a browser to print/save as PDF. **نسخ التقرير** copies Markdown;
**طباعة التقرير** prints the selected report. Reports with zero matches can also
be downloaded. Downloaded files contain the selected evidence, so treat them
like the source documents. Old history without a timestamp is labeled explicitly.

- `ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء` checks the
  current metadata of every accessible document, including nested folders and all
  API continuation pages. It does not use vector top-k results or model-generated
  counts. Whitespace, Arabic hamza and diacritics are normalized for matching;
  original values are preserved in the report. Declared multi-value fields match
  individual values exactly. Use one equality condition and the full field name.
- `اعرض جميع الوثائق في المستودع` produces a live document inventory.
- `لخص الوثيقة 618` or `قارن الوثائق 618، 609` restrict content retrieval to the
  specified IDs. Other questions search the repository index without an implicit
  document filter. General content answers use selected evidence, not an exhaustive
  scan of every page. The report displays this distinction explicitly.
- Content answering considers up to 240 candidates and selects up to 24 distinct
  passages across documents by default. The local model receives bounded excerpts;
  the expandable source cards contain the original selected indexed passages.
- The local model returns structured JSON selections rather than free-form factual
  prose. Each row must contain a continuous verbatim quotation from the exact
  referenced excerpt shown to the model. The server rejects invented quotes, changed
  numbers, quotes from other documents, unknown references, and model-written names,
  IDs, totals or narrative fields. It builds the report and document identities itself.
  One failed validation triggers a retry, then an explicitly labeled source-excerpt
  fallback. Empty/insufficient answers and potential conflicts have distinct summaries.
- These checks establish quotation provenance, not source truth, semantic relevance,
  completeness, or OCR accuracy. Content reports deliberately prioritize original
  source wording over unconstrained paraphrase. Existing indexed text is not repaired.
  Missing evidence for a requested comparison forces an insufficient-answer label.
- Natural inventory requests include «ماهي الوثائق الموجود في هذا المخزن» and the
  screenshot spelling «ماهي الوثائق الموجود في هذا ال repasetory». These read the
  live repository even when the vector database is unavailable. Unsupported compound
  equality/range filters prompt clarification instead of silently returning a census.
- Reports respect current account permissions. Outages abort the report; skipped
  inaccessible entries or reaching `Reports:MaxLiveDocuments` (default 10000) label
  it as partial. A live traversal is not a transactional snapshot; concurrent changes
  may affect its results. Unknown fields are reported explicitly rather than claiming
  zero matches. Compound filters and range comparisons are not live metadata queries.

Configuration defaults are in `Reports`: `CandidateLimit=240`, `EvidenceLimit=24`
(maximum 32), and `MaxLiveDocuments=10000`. No schema migration is needed.
After updating, restart both the web application and `tools/reports-graph/start.ps1`
and refresh the browser to load the report renderer.

Validation:

```powershell
dotnet test
.\tools\reports-graph\.venv\Scripts\python.exe -m pip install -r tools/reports-graph/requirements-test.txt
.\tools\reports-graph\.venv\Scripts\python.exe -m unittest discover -s tools/reports-graph -p "test_*.py"
```

## Supabase tenant connection error

`no tenant identifier provided (ENOIDENTIFIER)` comes from the connection pooler,
not the LLM. For self-hosted Supavisor, the database username must include the
actual `POOLER_TENANT_ID` from the Supabase `.env`, e.g. `postgres.<actual-tenant>`.
Use the password from that installation. The default session-mode host port is
5432, but check your actual Docker port mapping; port 5432 alone does not establish
whether a connection is direct or pooled. Direct PostgreSQL connections use the
actual PostgreSQL role without a Supavisor tenant suffix.

The chat and database status endpoint now return the same actionable diagnostic.
The application cannot infer your tenant ID or fix a private local configuration.
An interactive helper updates only the Supabase connection in your existing local
JSON file and preserves the other settings; it never changes the database schema:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\configure-database.ps1
```

It asks for the actual tenant ID and a hidden password. Use `-Port`/`-HostName` for
your real mapping, or `-DirectConnection` only for a directly exposed local
PostgreSQL server using the `postgres` role. The helper assumes local unencrypted
loopback access. Environment variables such as `Supabase__PostgresConnectionString`
override the file; update or remove stale overrides in the application's terminal.
Restart .NET and verify `http://127.0.0.1:5187/api/database/status` returns `ready`.

Reference: [Supabase self-hosted Postgres connections](https://supabase.com/docs/guides/self-hosting/accessing-postgres).

## Report quality and adversarial evaluation

`tools/reports-graph/adversarial_cases.json` contains 46 synthetic questions for
missing dates, missing comparison documents, repository counts/percentages from a
sample, instructions injected into questions/documents, OCR name guessing, Arabic
digit fidelity, conflicting dates, metadata vs OCR, Hijri conversion, compliance
claims, English answers and ambiguous ranking. These are deliberately separate
from mock-based regression tests: only running the local model measures its behavior.
The additional cases cover negated approvals, conditional payments, drafts versus
final decisions, conflicting values on one page, unrelated project dates, missing
table headings, currency/units, workflow execution, filenames versus page content,
incomplete multipart answers and comparisons across both requested documents.

```powershell
.\tools\reports-graph\.venv\Scripts\python.exe .\tools\reports-graph\evaluate.py --model qwen2.5:7b
```

The evaluator verifies expected status, required/allowed references and quotation contents;
it exits nonzero for an unverified fallback or failed case. Even passing this
synthetic corpus does not certify production answers. Review real documents and
question relevance, especially when OCR is degraded.

The v2 pipeline adds schema-constrained answer composition, a separate semantic
review, query-focused source windows, and hybrid lexical/vector retrieval with RRF.
Read [AI reports architecture](docs/AI_REPORTS_ARCHITECTURE.md) for implementation,
primary research sources, limits and local evaluation instructions. Restart both
the web application and LangGraph after updating; `/health` on port 8766 reports
`promptVersion: reports-grounded-v2.1`. Semantic review is a model check, not a guarantee.

The extraction prompt preserves negation, exceptions, conditions and units, and
does not treat indexed fields as a live read. The validator also requires evidence
from every explicitly requested document before marking a comparison answered.
HTTP requests support both fixed-length UTF-8 and bounded chunked framing, avoiding
a false `413` when .NET sends a streamed request. Unsupported negative field filters
require clarification rather than silently becoming positive equality filters.
If browser history storage is full, the report remains visible and downloadable;
a message asks you to download it before closing the page.

UI/HTML export regression tests (development only; no production dependency):

```powershell
npm ci --prefix tools/reports-ui
npm test --prefix tools/reports-ui
```

### Repository selection, Office exports and Dashboard identity

Login now includes a repository selector/discovery action; the header identifies
the active repository. Chats and ingestion checkpoints are separated by server,
repository and account, with stale-tab checks on every browser API request.

Each report offers Word (`.docx`), Excel (`.xlsx`), HTML and Markdown downloads,
and PDF saving through the browser print dialog. Its Laserfiche button opens the
explicit related document IDs using Web Client search URLs after rechecking access.
The blue/white Dashboard identity and ISB logo are reused, with fixed-layout
wrapping tables and horizontal scrolling inside the table on small screens.

See [repository reports setup and limits](docs/REPOSITORY_REPORTS.md), including
`Laserfiche:WebClientBaseUrl` for a custom Web Client directory.


## Planner selection contract (intent-v5.5)

The model-facing schema requires `selection` on every report. Unrestricted discovery
uses `{ "requiresFilter": false }`. Restricted requests must include a nonempty
structured filter, entry IDs, folder ID, name, or template in `selection` alongside
`requiresFilter: true`. Recursive filters require either a value, a relative date,
an empty-value operator, or an AND/OR group of conditions. The server translates this
into the existing Backend contract; repository field validation and the unfiltered
query guard remain in place. Legacy flat plans remain accepted for existing clients.
No question-specific handlers or UI changes are included.

Schema definition GETs retry once after HTTP 502/503/504, respecting cancellation.
Persistent failures and authentication errors remain errors; old schema is never
substituted. Model/planner timeout defaults remain zero (unlimited).

Restart both services after updating. The graph startup line should show
`planner=intent-v5.5`. Regression tests use HTTP fixtures, not a live Qwen/Laserfiche
installation; semantic output from the actual model still requires live acceptance.


## Report references and Web Client links

Reports show the first three evidence references in compact rows, with the rest
under “عرض المزيد”. Clicking a bracketed reference opens its exact evidence row.
References retain the document ID, name, OCR page when available, original excerpt,
and whether the evidence came from live metadata or indexed content. Arabic cells
align right; dates and IDs use isolated LTR values inside RTL cells.

Web Client links prefer `Laserfiche:WebClientBaseUrl`, then the URL advertised by the
current repository, then the API host plus `/laserfiche`. Set the override to the
same canonical Web Client host used in your browser if discovery is unavailable.
Individual documents use `DocView.aspx?db=...&id=...`; folder links and grouped
searches retain their corresponding Browse URLs. Access is validated live in batches.
Repository API authentication does not create a Web Client browser session; an
expired or absent Web Client session still requires its normal sign-in.

Safe entry, metadata and search-result GETs retry one transient 502/503/504, never a
search submission. Errors distinguish authentication, permissions, missing entries
and server/gateway failure without exposing response bodies. No stale repository
facts substitute for failed live reads. Planner version is `intent-v5.6`; model and
unlimited timeout defaults are unchanged.


## HTTP 429 / Laserfiche error 9030

9030 from `/Token` indicates session capacity or user license allocation, not an
invalid AI query. Token acquisition no longer retries this code as temporary rate
throttling. A one-minute account/repository cooldown shares the typed failure among
parallel reads, preserving its diagnostic ID. Ordinary 429 throttling still uses
bounded retries. No active Laserfiche sessions are terminated automatically and no
licensing settings are changed. Sign out of unused clients and verify the affected
account's session limit and Named User license in Laserfiche Administration Console;
Background reads share the cooldown; an explicit sign-in sends one fresh attempt so recovery does not wait for a cached 9030. The upstream message alone does not identify which of
those licensing constraints is responsible. Planner/model/UI remain unchanged.


## Reports session renewal and failed sign-in

Access-token cache misses renew with the existing refresh token on the v2 Token
endpoint, under the same repository/session scope and single-flight lock. Renewal
never changes account. Rejected refresh credentials require explicit sign-in;
transient renewal failures preserve the refresh token and surface the live error.
Empty/expired renewal responses are rejected rather than cached.

A failed account or repository switch restores the previous selection, credentials,
report generation and token scope. A successful switch commits a new isolated
scope. Signing in again with the same existing credentials reuses or renews the
current token instead of opening another password-grant session. UI, table,
planner, qwen2.5:7b and unlimited AI timeout defaults are unchanged.

Regression coverage includes actual loopback HTTP login requests with browser
cookies, failed/successful account switches, concurrent refresh, invalid renewal
responses and immediate explicit recovery after 9030. These fixtures do not
replace acceptance against the deployed Laserfiche and Ollama services.


## Typed semantic plans and named repository locations

Planner `intent-v5.7` constrains generated filters to the live field names and type
families before validating calendar dates, numeric literals, operators and bounds.
Compact field descriptions provide semantic context without excluding any field
names. The prompt distinguishes one count with several criteria from independent
reports, inclusive year bounds from individual years, numeric retention durations
from dates, and document names from repository locations. Ambiguous status or
retention meaning requires clarification rather than invented field/value mappings.

`folderName` is resolved live through a folder-name search before applying a folder
scope. Missing/non-unique names ask for clarification rather than returning a zero
document count. `includeSubfolders` selects descendants explicitly. Count answers
state their interpreted criteria so the user can check the scope. Document tables,
UI, qwen2.5:7b and unlimited timeouts remain unchanged. Advertised Web Client URLs
such as `/laserfiche?repo=TestEmployee` are normalized before composing entry links.

The regression tests use schema/HTTP fixtures; they do not claim actual Qwen
understanding on the deployed server. `tools/reports-graph/evaluate_planner.py`
contains held-out natural Arabic acceptance questions, including folder counts,
inclusive future-year bounds and ambiguous numeric retention fields. Its fixtures
are never imported by runtime routing. Live acceptance must run against local
Ollama/Laserfiche after updating both the graph and .NET application.


## Operation-specific planning grammar

Planner `intent-v5.8` uses separate JSON grammar branches for repository searches,
aggregation and clarification. Search cannot generate metrics, grouping, having or
rollup properties. Range operators require exactly one upper bound; ordinary
comparisons cannot generate an upper bound. Validation stays strict: the backend
never changes search to aggregation just to accept an invalid draft. Inventory
requests remain complete document listings. No wording-specific handlers are used.

One-column Markdown tables also render in the existing table component. Document
columns, styling, model and unlimited timeouts remain unchanged. Tests exercise
invalid grammar combinations, valid ranges, complete 73-document chat execution
with live-service fixtures and one-column rendering. They do not replace acceptance
against the deployed Qwen and Laserfiche services.

### Planner v5.9: live field namespace and year bounds

The planner now constrains sorting, grouping, metric fields and template names
to the selected repository catalog, in addition to filter fields. API property
aliases (`creationTime`, `lastModifiedTime`, `id`) are translated to their query
properties (`created`, `modified`, `entryId`) before validation, only when the
alias is not itself an actual metadata field. Sort expressions keep API names.
The incomplete legacy `field` shortcut is no longer offered in the generation
schema; structured filters carry both the field and its predicate.

Arabic upper-bound phrases such as `2036 وما أقل` are explained to the model as
one selection condition, not a request for a second oldest-document report.
A single total uses `countOnly`; an inclusive Gregorian year bound on a date
field uses `<2037-01-01`. Retention duration, retention year and creation time
must not be substituted for one another. Ambiguous field meaning still needs
clarification. These are planning instructions, not a guarantee of model accuracy.

`PLANNER_VALIDATED` now logs the chosen field/operator tree and sort choices
without logging filter values. Regression tests use simulated model responses;
acceptance testing must also compare answers from the installed local model
against the actual repository, especially expiry/retention questions.

After updating, restart the graph process and confirm `planner=intent-v5.9`.

### Planner v6.0: general semantic intent audit

Normal `/route` requests now include an independent local-model intent audit
after schema/type validation and before any Laserfiche query is returned. The
audit checks output type, scope, all conditions and logical nesting, actual field
meanings/types, and calendar/date boundaries against the original question,
live catalog and conversation history. A rejected plan is repaired and audited
again under the same overall planning deadline. Repeated semantic rejection
never returns an executable plan; genuine ambiguity can return a precise
clarification. Invalid model output and dependency failures remain explicit errors.

The instructions cover general AND/OR conditions, negation, empty values,
numeric and date ranges, units, follow-ups, counts versus lists, sorting and
content analysis. They do not implement a hard-coded router for particular
Arabic questions. Unsupported calculations/calendars/criteria must be clarified,
not silently approximated. A general question uses the entire selected repository
unless explicitly restricted; content evidence still has retrieval coverage limits.

Confirm `planner=live-plan-v7.3; planIntentReview=True` on restart, and
`planIntentReview: true` in `/health`. `PLAN_INTENT_REVIEW` logs the checks. The
additional audit normally adds one model call, and failed plans can require
two additional calls. On slow CPU-only installations this increases latency.
The `--skip-plan-review` server flag explicitly opts out of this safeguard.

The reviewer uses the same local model in a separate call; it is fallible. Unit
and HTTP tests simulate responses to verify rejection, repair, safe clarification,
context preservation and shared deadlines. They do not prove the installed
model understands every future question or validate counts against a real
Laserfiche repository. Live acceptance testing remains necessary.

The planner and reviewer also receive a bounded live metadata sample (up to
eight documents, three distinct values per field, 80 characters per value).
It helps distinguish full field names, units and calendar variants without
repository-specific field mappings or canned questions. Missing sample values
never prove absence. Sampling has a 15-second budget; an unavailable sample
does not prevent planning against the authoritative field definitions.
No OCR or document-content samples are added to the catalog.

Count answers retain the document table, source references and live-document
links, alongside the exact server total when available. The displayed rows are
bounded by the requested page size and are labelled as partial when more exist;
the total is never inferred from those rows. No stylesheet or table component
changes are required.

Planner v6.2 generates an exclusive folder locator (`selection.folder.id` or
`selection.folder.name`); the Python boundary translates it to the existing
backend contract. The generation grammar cannot include both. Existing API
clients with a single flat folder selector remain compatible. A generated
clarification must contain one non-executable report and a non-empty specific
question, rather than an incomplete query or a mixed report.

The reviewer receives explicit repository/presentation defaults and complete
relative date anchors. It must not invent a folder, ordering, grouping or state
field absent from the original request/catalog. Instructions are shorter and
contain no repository-specific field mapping. `PLANNER_DRAFT` logs operations
and filter shapes before validation without field values, document contents or
raw questions. Repository API v2 metadata `id` is preserved as the definition
ID, alongside the older `fieldId`/`fieldDefinitionId` shapes.

Planner v6.3 first interprets the requested outputs and condition shape in a
small independent call, without catalog fields or a proposed plan. It then
constrains the catalog-backed generation grammar: a simple count cannot become
grouping, and a single one-sided bound cannot become a two-sided range. The
same invariants are checked after parsing, even if the model bypasses the
generation grammar. Explicit grouping, ranges, multiple reports and genuine
clarifications remain supported. No question-specific field/year mappings
are used. `QUESTION_INTENT` logs output types and condition shapes.

Retries use the original question, catalog and independent interpretation,
without replaying the rejected draft. The semantic reviewer still checks the
actual selected field, calendar and dates against the original request. This
adds one small interpretation call; it does not make the local model infallible.
`evaluate_planner.py` exercises the same interpretation and review path as HTTP.
The tests use scripted model responses and do not validate the user's live
Qwen model or repository totals. The document-table UI is unchanged.

Planner v6.4 corrects two shortcomings of v6.3: an interpretation could invent
multiple outputs from one current request, and the multi-output grammar pooled
comparison choices while the validator enforced them by report position.
The interpreter now quotes distinct request evidence and explicit bound evidence
from the current question. Invented, overlapping range bounds and duplicated
requests are rejected and reinterpreted before catalog-backed planning. Exact
repeated attempts and their responses are removed from planning history; other
follow-up context remains. This is evidence validation, not a question-keyword
router. Arabic instructions cover intent decomposition without repository field
names, example years or question-specific handlers.

Each output has its own indexed object/selection/filter schema, so the second
output cannot decode the first output's comparison constraints. The transport
normalizes indexed outputs into the unchanged backend reports array. If semantic
review rejects the result, the interpreter can reconsider its initial reading
instead of requiring the planner to preserve an incorrect intent forever.

Timeout defaults remain zero/unlimited, including CPU-only use. To capture a
real Qwen failure locally, add `-PlannerTracePath .\logs\planner-trace.jsonl`
to `tools/reports-graph/start.ps1`. The opt-in file contains the original
question/history/catalog, exact prompt messages, decoding schemas and model
replies; no authentication headers or OCR are captured. Keep it local and review
its metadata values before sharing. It resides under the ignored logs directory.
`tools/reports-graph/replay_planner.py --trace logs/planner-trace.jsonl` can
replay captured interpretation calls locally with no response timeout and no
Laserfiche credentials. `--stage RepositoryPlannerSchema` replays planning calls.
The trace allows diagnosing the actual model rather than inferring its reasoning
from operation summaries. Tests still use scripted replies and cannot establish
live model accuracy or repository totals. Table UI/formatting remain unchanged.

Planner v6.5 fixes clarification replies being validated as independent questions.
The web response marks a clarification and carries its original user question;
the browser preserves that metadata in repository-scoped conversation history.
The interpreter chooses current, followup or clarification_reply context. Evidence
for a clarification reply can quote the original request and current answer, so a
field/calendar choice need not repeat the count and year. An independent new
question still grounds evidence in its own text. Legacy unmarked history can use
followup context. Subsequent clarifications retain the original request.

Failed exchanges are kept visible but excluded from planning history. Retrying
an answer therefore preserves the earlier clarification rather than incorporating
failure messages. The send control is restored when outstanding operations finish.
HTTP 409 session_busy remains retryable in the current login; session_scope_changed
and HTTP 401 require authentication. No table, stylesheet, export or document-link
layout changes are made, and model/planner deadlines remain unlimited by default.

This update includes C# and browser changes: stop both processes, pull the branch,
restart the graph (confirm intent-v6.5), run the web project with a build, and refresh
the browser with Ctrl+F5. Do not use --no-build until the new web assembly is built.
The opt-in planner trace still records the original inputs and individual calls.
Regression tests cover clarification replies, retry after failure in the same
conversation/session, metadata round trips and repository-change authentication.
They simulate model replies and do not certify the installed Qwen's semantics.


### Live planner v7.0

The HTTP route now plans directly from the current question, conversation and live
catalog. The older independent intent experiment remains available to unit tests
but no longer fixes report count/bound grammar before the planner has seen fields.
This removes the exact-quotation extraction gate that produced the reported 503s.
A valid clarification is delivered directly; it is not audited as a query missing
filters. Executable plans still undergo live-name/type/date validation and semantic
review; rejected queries are never executed. No repository field or year is mapped
in code. The reduced, reachable tool schema is supplied both to Ollama's `format`
and in the system prompt, with a context window sized for both plus output space.
Model/planner response timeouts remain zero/unlimited by default. Tables are unchanged.

Metadata/count reports already search the live repository on each request. Content
reports now discover their current document scope from Laserfiche and await ingestion
before index retrieval, including new documents. A refresh failure aborts retrieval;
it does not return old indexed passages. Unrestricted content requests refresh every
accessible document returned by the full live search; an incomplete search requires a
narrower scope. This can be expensive, especially with local OCR enabled: freshness
means waiting for extraction/embedding, not instantaneous OCR. Results are not a
transactional snapshot during concurrent repository edits. This does not make sampled
content retrieval an exact repository-wide count.

Restart both services after pulling and confirm `planner=live-plan-v7.3`. Run the web
project with a build (do not use `--no-build`). Keep `-PlannerTracePath` enabled for
acceptance with the real model. `replay_planner.py` now defaults to `LivePlannerSchema`
and preserves schema embedding when replaying new traces; old stages can be selected
with `--stage QuestionIntent` or `--stage RepositoryPlannerSchema`.
The automated tests exercise contracts, mock model failures and refresh ordering;
they do not prove semantic correctness of Qwen or actual repository counts. Validate
natural-language questions against known live entries and expected counts, and test
adding/modifying a document then repeating the same report.

### Live planner v7.1: current tag definitions and clarification ownership

The catalog now reads every page of the active repository's `TagDefinitions`
on each request, alongside live fields and templates. The typed filter grammar
supports `has_tag` and `not_tag`, including AND/OR combinations with field filters.
Tag names are validated against current definitions and compiled by the backend;
the model cannot submit raw search syntax. No tag, field or question is mapped in
code. A denied or unsupported tag-definition endpoint is marked `unavailable`,
not represented as a complete empty catalog. Authentication failures still fail.

The same planning call identifies whether the current message starts a question,
continues a report or answers a pending clarification. A new question's clarification
no longer inherits an unrelated old question. This does not add another model call.
Table rendering and default unlimited AI deadlines are unchanged.

Laserfiche's search UI includes capabilities not yet represented by these tools,
including version-specific conditions, records-management disposition, digital
signatures and business-process predicates. The planner is instructed to explain
missing capabilities instead of substituting an unrelated metadata condition.
Supporting tags does not establish correctness of date-field interpretation by
the real model; automated tests use controlled catalogs and model responses.

To capture a real failed request, restart **both** the web application (with a build)
and the graph, reproduce the question, and collect the local planning diagnostic:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\start.ps1 -Model qwen2.5:7b -TimeoutSeconds 0 -PlannerTimeoutSeconds 0 -PlannerTracePath .\logs\planner-trace.jsonl
# After reproducing the question, run in another terminal at the repository root:
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\diagnose.ps1
```

`logs/planner-diagnostics.json` contains the latest request's question, conversation,
live catalog, individual model calls and graph version. It does not collect API
credentials, cookies or OCR. Its repository metadata and question text may be
sensitive; share only with the person diagnosing the report. The authenticated
read-only `/api/reports/planning-catalog` endpoint also exposes the current catalog
from the same selected repository and session as chat.

### Reports session recovery and Search Syntax

Live repository queries already execute through `SearchAsync` using Laserfiche
Search Syntax generated by `StructuredRepositoryQuery`. The planner chooses
validated predicates, names and types; the backend emits the actual syntax,
including field comparisons, tag predicates and AND/OR parentheses. Reference:
https://doc.laserfiche.com/laserfiche.documentation/12/userguide/en-us/content/search-syntax.htm
Sending unconstrained syntax from the model would not fix semantic mistakes;
the current compiler makes name, type and operator validation possible before
executing. The web log's `Stage=COMPILED_SEARCH` is available at Debug level.

An expired reports-session token first uses refresh-token renewal. If renewal is
rejected, or no refresh token exists, the service may restore the **same** user's
selected repository using that session's encrypted stored credentials under the
existing single-flight lock. It never uses the configured disk/service account
for this recovery. Rejected credentials stop repeated automatic password attempts;
temporary dependency failures allow retry. LFDS and External Share behavior is
unchanged. A final authentication failure now returns `401 session_expired`,
which opens the existing login dialog instead of an opaque `chat_failed` error.
This restores sessions during long planning requests without keeping a live
Laserfiche search running throughout model generation. It does not fix a rejected
AI plan: planning and repository authentication are separate stages.

### Live planner v7.2: backend calendar periods and compatible-service handshake

Gregorian Date/DateTime predicates can use `period={year:Y, month:M?, day:D?}`.
`in_period` compiles to `>=start AND <next-period-start`; `through_period` to
`<next-period-start`; `before_period` to `<start`; `from_period` to `>=start`;
`after_period` to `>=next-period-start`. The backend handles year/month rollover
and leap days. Invalid calendar dates, numeric/text fields and mixed period/literal
bounds are rejected. These work within the same AND/OR tree as other conditions.
The model selects the requested period; it no longer needs to invent both dates
for a year-only question. Numeric years remain numeric comparisons, and Hijri
interpretation/conversion is not silently added. No repository field or year is
configured in code. Existing literal and relative date filters remain supported.

The planner instructions explain the built-in entry properties, so repository
creation dates do not depend on a custom field literally named like the question.
The authenticated web client now requires `/health` to advertise
`planningProtocol=live-periods-v1`. Earlier intent-v6 servers used the same routing
version and could pass the old handshake after a source pull. They are now rejected
before sending the question, with an instruction to restart the graph. Restart
**both services** and verify `planner=live-plan-v7.3` before testing this release.

Regression tests cover the screenshot's year-only question through the graph HTTP
contract, C# routing and live-search compilation to the existing document table,
using controlled model/API responses. They cover compound period/tag/number counts,
period boundaries and stale-service detection. They do not measure the installed
Qwen model's interpretation or establish actual repository totals. Capture a
failed real question with `-PlannerTracePath`, then run `diagnose.ps1` for acceptance.
Direct-plan repairs also restart from the original question/catalog plus validation
feedback, without replaying the invalid assistant draft as conversation history.


### Native planner grammar compatibility (v7.3)

The uploaded failing traces contained array filters and year-only date values
outside the declared schema. The direct planner had combined `properties` and
`anyOf` required-only alternatives in one selection object. llama.cpp does not
support that intersection. Keep complete object alternatives and share property
schemas through direct references instead. Numeric patterns now use ordinary
capture groups, supported by the converter, rather than non-capturing groups.
These changes do not encode field names, question words or years in production.

Validation: the old captured contract failed llama.cpp's Python converter at
`required: [filters]`; removing that defect exposed its unsupported numeric
pattern. The corrected contract compiled using the captured live catalog.
`test_live_planner.py` guards the schema structure and captured failure shapes.
For the optional native conversion test, set `LLAMA_SCHEMA_CONVERTER` to
`examples/json_schema_to_grammar.py` from ggml-org/llama.cpp commit `00681df`, then
run `python -m unittest discover -s tools/reports-graph -p test_native_grammar.py`.
The external converter is a test tool only, not a runtime dependency.

This verifies schema compatibility, not Qwen semantic accuracy or the totals in
an on-prem repository. Semantic review and live backend execution remain in
place. Opt-in traces now include `plan_rejected` validation diagnostics as well
as model responses. Restart the graph after pulling and check `live-plan-v7.3`.
