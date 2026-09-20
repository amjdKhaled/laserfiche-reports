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
3. Local OCR fallback through Tesseract. (implemented)
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
    "ExecutablePath": "C:\\Program Files\\Tesseract-OCR\\tesseract.exe",
    "Languages": "ara+eng",
    "PageSegmentationMode": 4,
    "FallbackPageSegmentationModes": [6],
    "ImageScaleFactor": 2,
    "Dpi": 300
  },
  "LocalAI": {
    "Provider": "Ollama",
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text-v2-moe",
    "EmbeddingDimensions": 768,
    "ChunkSize": 1200,
    "ChunkOverlap": 200
  }
}
```

Install Tesseract on the same Windows machine and include both the Arabic and
English language data files (`ara.traineddata` and `eng.traineddata`). OCR is
used only when Laserfiche has no searchable text for a page. Images are streamed
from Laserfiche into the local OCR process and are not saved in PostgreSQL or
sent to an external service. If Tesseract is unavailable, ingestion continues
with metadata and reports `metadata-only` rather than failing the document.
Receipt and table pages are enlarged in memory and evaluated with table-aware
and uniform-block page segmentation. Bidirectional formatting markers emitted
by mixed Arabic/English OCR are removed before indexing. Prefer the official
`tessdata_best` Arabic model when accuracy matters more than OCR speed.

Start the API with local Laserfiche credentials, then ingest Entry `608`:

```powershell
$env:LF_USERNAME = "YOUR_LASERFICHE_USERNAME"
$env:LF_PASSWORD = "YOUR_LASERFICHE_PASSWORD"
dotnet run --project .\src\LaserficheReports.Web
```

In a second PowerShell window, use the HTTPS address printed by `dotnet run`:

```powershell
Invoke-RestMethod -Method Post -Uri "https://localhost:PORT/api/ingestion/laserfiche/608" -SkipCertificateCheck
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
  -Uri "http://127.0.0.1:5187/api/laserfiche/documents/608/pages/1/image" `
  -OutFile ".\laserfiche-608-page-1.bin"
```

The response is streamed from Laserfiche and is not stored on the application
server. The temporary output above exists only because the test caller requests
an output file.
