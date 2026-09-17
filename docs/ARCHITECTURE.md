# Local architecture

Every runtime component is hosted on the same Windows machine and communicates
through localhost or the local network only.

| Component | Responsibility |
|---|---|
| ASP.NET Core | Chat UI, local API, security boundary, orchestration |
| Laserfiche Repository API | Source documents, metadata, text, and repository structure |
| Supabase/PostgreSQL + pgvector | Document index, chunks, metadata mirror, embeddings |
| Local OCR | Extract text only when Laserfiche has no usable text |
| Ollama or LM Studio | Local embeddings and answer generation |
| n8n | Scheduled/incremental ingestion after the core pipeline works |

Laserfiche remains the authoritative source. Supabase contains a rebuildable
search index and must not be treated as the document system of record.

