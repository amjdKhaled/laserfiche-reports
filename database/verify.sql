select extname
from pg_extension
where extname in ('pgcrypto', 'vector')
order by extname;

select column_name, data_type, udt_schema, udt_name
from information_schema.columns
where table_schema = 'public'
  and table_name = 'documents'
  and column_name in ('id', 'content', 'metadata', 'embedding')
order by column_name;

select extensions.vector_dims(embedding) as embedding_dimensions
from public.documents
where embedding is not null
limit 1;

select routine_name
from information_schema.routines
where routine_schema = 'public'
  and routine_name = 'match_laserfiche_reports_documents';
