begin;

create or replace function public.match_document_chunks(
    query_embedding vector(1024),
    match_count integer default 8,
    minimum_similarity real default 0.30,
    repository_filter text default null
)
returns table (
    chunk_id bigint,
    document_id uuid,
    repository_id text,
    entry_id bigint,
    document_name text,
    full_path text,
    page_number integer,
    chunk_index integer,
    content text,
    similarity real
)
language sql
stable
as $$
    select
        c.id,
        d.id,
        d.repository_id,
        d.entry_id,
        d.name,
        d.full_path,
        c.page_number,
        c.chunk_index,
        c.content,
        (1 - (c.embedding <=> query_embedding))::real as similarity
    from public.document_chunks c
    join public.documents d on d.id = c.document_id
    where c.embedding is not null
      and (repository_filter is null or d.repository_id = repository_filter)
      and (1 - (c.embedding <=> query_embedding)) >= minimum_similarity
    order by c.embedding <=> query_embedding
    limit greatest(match_count, 1);
$$;

commit;
