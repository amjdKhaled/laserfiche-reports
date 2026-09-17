select extname
from pg_extension
where extname in ('pgcrypto', 'vector')
order by extname;

select table_name
from information_schema.tables
where table_schema = 'public'
  and table_name in ('lf_reports_documents', 'lf_reports_document_metadata', 'lf_reports_document_chunks')
order by table_name;

select routine_name
from information_schema.routines
where routine_schema = 'public'
  and routine_name = 'match_lf_reports_chunks';
