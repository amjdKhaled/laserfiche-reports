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
7. Local LangGraph orchestration and explicit batch synchronization. (implemented on the feature branch)

## Run the chat interface (Windows)

This branch adds a local chat and document interface at
`http://127.0.0.1:5187/`. The existing `public.documents` table is reused.
The web host accepts loopback requests only while it uses local Laserfiche
credentials. LangGraph and Ollama also bind to loopback.
OCR can be deferred: set `Ocr:Enabled` to `false` in your private
`appsettings.Local.json`. Ingestion still embeds the document name, path,
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
to its original image. Answers require the local LangGraph and Ollama services.
The **Documents & system** tab shows
database, repository, graph, and optional OCR status. Its ingestion form
indexes a chosen Entry ID. Verify the services from another PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-local.ps1
```

To process several known documents using LangGraph:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\sync.ps1 -EntryIds 618,609
```

The sync command can run from Windows Task Scheduler if periodic refresh of
those explicit IDs is needed. Discovery of all changed Laserfiche entries,
enterprise SSO, and production-wide deployment still need design and
validation. Browser login uses session-specific Laserfiche credentials, and
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
    "Enabled": true,
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
