# Local Supabase database

Laserfiche Reports reuses the existing local `public.documents` RAG table.
Laserfiche remains the source of truth and the application never writes to the
Laserfiche Repository SQL database.

## Apply the schema on Windows

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\apply-database.ps1
```

The script expects the standard self-hosted Supabase database container name
`supabase-db`. Supply `-ContainerName` when your local container has a different
name.

## Stored data

- Each Laserfiche text chunk is one row in `public.documents`.
- `content` contains the extracted chunk text.
- `embedding` contains its local Ollama embedding.
- `metadata` stores `source`, `repository_id`, `entry_id`, document name, path,
  page number, chunk index, and text source.
- Laserfiche Reports always sets `metadata.source` to `laserfiche-reports`, so
  it does not mix with old n8n/RAG rows.
- `match_laserfiche_reports_documents` searches only those project rows.

The script verifies that the existing embeddings use the local
`nomic-embed-text-v2-moe` 768-dimensional vector type in the `extensions`
schema before it creates the project search function.
