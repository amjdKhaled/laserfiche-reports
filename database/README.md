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

- `documents`: Laserfiche identity and indexing state. It does not store the original file.
- `document_metadata`: searchable Laserfiche fields and values.
- `document_chunks`: extracted text chunks and local embeddings.
- `match_document_chunks`: cosine-similarity search with repository filtering.

The vector dimension is 1024 for the local multilingual `bge-m3` embedding
model. A model change that uses another dimension requires a database migration.
