begin;

-- Reuse the existing n8n/Supabase RAG table. Do not create, alter, delete,
-- or migrate its prior data. Laserfiche Reports identifies its own chunk rows
-- by metadata.source = "laserfiche-reports".
do $$
begin
    if not exists (
        select 1 from information_schema.tables
        where table_schema = 'public' and table_name = 'documents'
    ) then
        raise exception 'Expected existing table public.documents was not found.';
    end if;

    if not exists (
        select 1
        from information_schema.columns
        where table_schema = 'public' and table_name = 'documents'
          and column_name in ('id', 'content', 'metadata', 'embedding')
        group by table_schema, table_name
        having count(*) = 4
    ) then
        raise exception 'public.documents must contain id, content, metadata, and embedding columns.';
    end if;
end;
$$;

-- Fail rather than silently mix incompatible embedding dimensions.
do $$
begin
    if exists (
        select 1 from public.documents
        where embedding is not null
          and extensions.vector_dims(embedding) <> 768
    ) then
        raise exception 'Existing public.documents embeddings are not 768-dimensional.';
    end if;
end;
$$;

create index if not exists ix_documents_lf_reports_source
    on public.documents ((metadata ->> 'source'))
    where metadata ->> 'source' = 'laserfiche-reports';

create index if not exists ix_documents_lf_reports_embedding_hnsw
    on public.documents
    using hnsw (embedding extensions.vector_cosine_ops)
    where embedding is not null
      and metadata ->> 'source' = 'laserfiche-reports';

commit;
