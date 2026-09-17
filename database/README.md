# Local Supabase database

This database is an application-owned, rebuildable search index. Laserfiche
remains the source of truth and the application never writes to the Laserfiche
Repository SQL database.

## Apply the schema on Windows

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\apply-database.ps1
```

The script expects the standard self-hosted Supabase database container name
`supabase-db`. Supply `-ContainerName` when your local container has a different
name.

## Stored data

- `lf_reports_documents`: Laserfiche identity and indexing state. It does not store the original file.
- `lf_reports_document_metadata`: searchable Laserfiche fields and values.
- `lf_reports_document_chunks`: extracted text chunks and local embeddings.
- `match_lf_reports_chunks`: cosine-similarity search with repository filtering.

The project reuses the existing local `nomic-embed-text-v2-moe` model and its
768-dimensional pgvector type under the `extensions` schema. A later model
change that uses another dimension requires a database migration.
