begin;

create table if not exists public.lf_reports_documents (
    id uuid primary key default gen_random_uuid(),
    repository_id text not null,
    entry_id bigint not null,
    name text not null,
    full_path text,
    mime_type text,
    file_extension text,
    page_count integer,
    source_created_at timestamptz,
    source_modified_at timestamptz,
    source_version integer,
    content_hash text,
    text_source text not null default 'pending'
        check (text_source in ('pending', 'laserfiche', 'ocr', 'none')),
    ingestion_status text not null default 'pending'
        check (ingestion_status in ('pending', 'processing', 'indexed', 'failed')),
    ingestion_error text,
    indexed_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (repository_id, entry_id)
);

create table if not exists public.lf_reports_document_metadata (
    id bigint generated always as identity primary key,
    document_id uuid not null references public.lf_reports_documents(id) on delete cascade,
    field_name text not null,
    field_type text,
    value_text text,
    value_json jsonb,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (document_id, field_name)
);

-- Reuse the already installed local Ollama embedding model rather than create
-- a second vector dimension beside the user's existing n8n RAG environment.
create table if not exists public.lf_reports_document_chunks (
    id bigint generated always as identity primary key,
    document_id uuid not null references public.lf_reports_documents(id) on delete cascade,
    chunk_index integer not null check (chunk_index >= 0),
    page_number integer check (page_number is null or page_number > 0),
    content text not null,
    token_count integer check (token_count is null or token_count >= 0),
    start_offset integer,
    end_offset integer,
    embedding extensions.vector(768),
    embedding_model text,
    created_at timestamptz not null default now(),
    unique (document_id, chunk_index)
);

create index if not exists ix_documents_repository_entry
    on public.lf_reports_documents (repository_id, entry_id);

create index if not exists ix_documents_ingestion_status
    on public.lf_reports_documents (ingestion_status);

create index if not exists ix_documents_source_modified
    on public.lf_reports_documents (source_modified_at desc);

create index if not exists ix_document_metadata_document
    on public.lf_reports_document_metadata (document_id);

create index if not exists ix_document_metadata_field_value
    on public.lf_reports_document_metadata (field_name, value_text);

create index if not exists ix_document_chunks_document
    on public.lf_reports_document_chunks (document_id, chunk_index);

create index if not exists ix_document_chunks_embedding_hnsw
    on public.lf_reports_document_chunks
    using hnsw (embedding extensions.vector_cosine_ops)
    where embedding is not null;

create or replace function public.set_lf_reports_updated_at()
returns trigger
language plpgsql
as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists lf_reports_documents_set_updated_at on public.lf_reports_documents;
create trigger lf_reports_documents_set_updated_at
before update on public.lf_reports_documents
for each row execute function public.set_lf_reports_updated_at();

drop trigger if exists lf_reports_document_metadata_set_updated_at on public.lf_reports_document_metadata;
create trigger lf_reports_document_metadata_set_updated_at
before update on public.lf_reports_document_metadata
for each row execute function public.set_lf_reports_updated_at();

commit;
