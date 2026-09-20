# Build roadmap

## 1. Laserfiche foundation — complete

- Repository API connection, authentication, repository context, search,
  documents, folders, metadata, templates, paging, and API version handling.

## 2. Local document index — complete

- Reuse the existing Supabase/PostgreSQL `documents` table with source-labelled
  Laserfiche chunks.
- Read one known Laserfiche document (Entry 608).
- Upsert its identity and metadata without storing the original file.
- Save its available Laserfiche text and mark its ingestion state.

## 3. Local OCR fallback — complete

- Prefer text already available from Laserfiche. (complete)
- Run local Tesseract OCR only when usable text is missing. (complete)
- Record whether text came from Laserfiche, OCR, both, or neither. (complete)
- Stream page images to OCR without persisting document files outside Laserfiche. (complete)

## 4. Chunking and embeddings — current

- Split text with page and offset references.
- Generate 768-dimensional embeddings locally with the existing `nomic-embed-text-v2-moe` model.
- Store chunks and embeddings in pgvector.

## 5. Retrieval

- Embed the user's question locally.
- Retrieve the best chunks with repository and permission filters.
- Return document name, Entry ID, path, page, and similarity as evidence.

## 6. Local answer generation

- Send only the retrieved evidence to Ollama or LM Studio on localhost.
- Require grounded answers and an explicit "not found" response when evidence
  is insufficient.

## 7. Single-chat interface

- Login, Chat, History, and Admin only; no dashboard.
- Arabic and English support.
- Answers show clickable Laserfiche evidence.

## 8. n8n automation

- Use local n8n for scheduled and incremental ingestion.
- n8n does not sit in the path of every chat question.
- Retry failed ingestion jobs and refresh documents changed in Laserfiche.

## 9. Security and portability

- Carry the current Laserfiche user/repository context into Reports.
- Apply the user's Laserfiche permissions before retrieval.
- Keep tokens and credentials out of URLs and Git.
- Package the .NET 8 app and local services for another Windows machine.
