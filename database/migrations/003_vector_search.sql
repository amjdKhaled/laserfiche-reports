begin;

create or replace function public.match_laserfiche_reports_documents(
    query_embedding extensions.vector(768),
    match_count integer default 8,
    minimum_similarity real default 0.30,
    repository_filter text default null
)
returns table (
    id bigint,
    content text,
    metadata jsonb,
    similarity real
)
language sql
stable
set search_path = public, extensions
as $$
    select
        d.id,
        d.content,
        d.metadata,
        (1 - (d.embedding OPERATOR(extensions.<=>) query_embedding))::real as similarity
    from public.documents d
    where d.embedding is not null
      and d.metadata ->> 'source' = 'laserfiche-reports'
      and (repository_filter is null or d.metadata ->> 'repository_id' = repository_filter)
      and (1 - (d.embedding OPERATOR(extensions.<=>) query_embedding)) >= minimum_similarity
    order by d.embedding OPERATOR(extensions.<=>) query_embedding
    limit greatest(match_count, 1);
$$;

commit;
