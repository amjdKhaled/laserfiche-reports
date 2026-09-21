# Laserfiche Reports

Fully local, on-premise AI reporting and chat for Laserfiche.

## Phase 1

- Reuse the proven Laserfiche Repository API integration from `amjdKhaled/Asset-Manager-1zip`.
- Keep Laserfiche as the source of truth.
- Run the web application, n8n, Supabase/PostgreSQL with pgvector, OCR, and the LLM on the same machine.
- Never send documents, metadata, prompts, embeddings, or credentials to a cloud service.

## Planned local flow

1. Read documents and metadata from Laserfiche Repository API.
2. Use existing Laserfiche text when available; otherwise run local OCR.
3. Split extracted text into chunks.
4. Create embeddings locally and store them in Supabase PostgreSQL/pgvector.
5. Retrieve evidence for a user's question.
6. Generate an Arabic/English answer through a local Ollama or LM Studio model.
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
5. Vector retrieval and local LLM answering.
6. Single-chat UI.
7. n8n automation and incremental synchronization.

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
powershell -ExecutionPolicy Bypass -File .\tools\paddleocr-vl\start.ps1 -TextDetectionMaxSideLength 3000
```

The first start downloads the PP-StructureV3 layout/table models and the Arabic PP-OCRv5 recognition model
and can take several minutes. When the console says `PP-StructureV3 Arabic worker is
ready`, verify it with:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:8765/health"
```

The default 3000-pixel detection limit reduces CPU processing time while
preserving enough detail for Arabic legal documents. Submit one ingestion
request at a time; concurrent OCR requests are rejected with `ocr_busy`
instead of waiting in a long queue.

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
the non-generative `PP-StructureV3` layout pipeline with the
`arabic_PP-OCRv5_mobile_rec` recognition model. Arabic OCR output is then
proofread locally in Arabic or English by the configured Ollama model. Entry
`618` is the Arabic reference document used for testing, not a language restriction.
A correction is rejected if
it changes any number/date or changes the text length materially. Each OCR
response includes the SHA-256 hash of the received page; the .NET service checks
that hash before storing text so stale or mismatched results cannot enter the
index. OCR confidence below `0.35` is discarded by default.

Start the API with local Laserfiche credentials, then ingest the Arabic reference Entry `618`:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
dotnet run --project .\src\LaserficheReports.Web
```

In a second PowerShell window, use the address printed by `dotnet run`:

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5187/api/ingestion/laserfiche/618" -TimeoutSec 1800
```

The ingestion request saves the document identity, metadata, and searchable
page text. It prefers text already available in Laserfiche and runs local OCR
only for missing pages. It then splits every page into overlapping chunks and
uses the local Ollama `nomic-embed-text-v2-moe` model to create 768-dimensional
embeddings. The document row keeps `embedding = NULL`; its `document-chunk`
rows contain the vectors used by retrieval. Running the request again refreshes
the same document row and replaces only that document's project-owned chunks.

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
