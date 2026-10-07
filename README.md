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
