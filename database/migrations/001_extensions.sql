begin;

create extension if not exists pgcrypto;

-- The user's existing self-hosted Supabase installation keeps pgvector in the
-- `extensions` schema. Keep using that schema instead of creating a second
-- incompatible vector type in `public`.
create schema if not exists extensions;
create extension if not exists vector with schema extensions;

commit;
