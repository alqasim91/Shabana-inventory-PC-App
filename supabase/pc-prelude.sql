-- ============================================================================
-- pc-prelude.sql - PC-edition compatibility layer
-- ----------------------------------------------------------------------------
-- Applied by provision.ps1 AFTER platform-bootstrap.sql and BEFORE the
-- application migrations 0001..0033.
--
-- WHY THIS FILE EXISTS
-- The migrations in supabase/migrations/ are vendored verbatim from the cloud
-- app and must stay that way - editing them here would fork the two products
-- and silently diverge every time we re-sync. This file instead prepares the
-- database so that those unmodified migrations apply cleanly on a machine that
-- has no Supabase cloud services behind it.
--
-- It fixes exactly two things, both of which HARD-ABORT provisioning
-- (provision.ps1 runs psql with ON_ERROR_STOP=1) on a fresh install:
--
--   1. Sequence minvalue. 0017_order_seq.sql ends with
--        select setval('purchase_order_seq', coalesce(max(order_seq), 0), true);
--      On the cloud this ran against tables that already had rows, so the value
--      was >= 1. On a FRESH install both tables are empty, so it evaluates to
--      setval(..., 0) - and a default sequence has minvalue 1, so PostgreSQL
--      rejects it: "value 0 is out of bounds for sequence". Pre-creating the
--      sequences with minvalue 0 makes 0017's `create sequence if not exists`
--      a no-op and its setval legal. nextval() still returns 1 first, so order
--      numbers are unchanged. This is a latent bug in the cloud migration too;
--      it is simply unreachable there because those tables were never empty.
--
--   2. The storage schema. 0022 and 0028 create policies on storage.objects
--      and insert a bucket row into storage.buckets. Those tables are created
--      by Supabase's storage-api service, which the PC edition does not ship -
--      and platform-bootstrap.sql does not create them either. Without them
--      both migrations error out and provisioning dies before it ever registers
--      a single service, which is why localhost never came up.
--
-- SCOPE LIMIT - READ THIS BEFORE TRUSTING ATTACHMENTS
-- The storage tables below are a SCHEMA shim, not an implementation. They make
-- the migrations apply and keep the org-isolation policies enforceable, but
-- nothing in the PC edition currently uploads or serves file bytes. Order
-- attachments are therefore NOT functional on PC yet; that needs a real local
-- file store (see BUILD_PLAN.md). The shim deliberately mirrors Supabase's own
-- storage schema so that a later implementation can drop in behind it.
-- ============================================================================

-- --- 1. Order-number sequences with a legal floor of 0 ----------------------
create sequence if not exists purchase_order_seq minvalue 0 start 0;
create sequence if not exists sales_order_seq    minvalue 0 start 0;

-- --- 2. Minimal storage schema ---------------------------------------------
create schema if not exists storage;

create table if not exists storage.buckets (
  id                 text primary key,
  name               text not null,
  owner              uuid,
  public             boolean default false,
  file_size_limit    bigint,
  allowed_mime_types text[],
  created_at         timestamptz default now(),
  updated_at         timestamptz default now()
);

create table if not exists storage.objects (
  -- Unqualified: gen_random_uuid() has been in pg_catalog since PostgreSQL 13,
  -- so this does not depend on which schema pgcrypto was installed into.
  id               uuid primary key default gen_random_uuid(),
  bucket_id        text references storage.buckets (id),
  name             text,
  owner            uuid,
  created_at       timestamptz default now(),
  updated_at       timestamptz default now(),
  last_accessed_at timestamptz default now(),
  metadata         jsonb,
  path_tokens      text[] generated always as (string_to_array(name, '/')) stored
);

create unique index if not exists objects_bucket_name_idx
  on storage.objects (bucket_id, name);

-- RLS must be ON for the policies 0022/0028 create to mean anything. Enabling
-- it here (rather than relying on those migrations) keeps the table closed by
-- default in the window before 0022 runs.
alter table storage.objects enable row level security;
alter table storage.buckets enable row level security;

-- Supabase's own helper, used by 0028's insert policy to check that an upload
-- lands inside the caller's org folder. Same semantics as upstream: split the
-- key on '/' and drop the filename, leaving the folder segments.
create or replace function storage.foldername(name text)
returns text[]
language plpgsql
immutable
as $$
declare
  parts text[];
begin
  parts := string_to_array(name, '/');
  return parts[1 : array_length(parts, 1) - 1];
end
$$;

grant usage on schema storage to anon, authenticated, service_role;
grant all on storage.objects to authenticated, service_role;
grant all on storage.buckets to authenticated, service_role;
grant select on storage.buckets to anon;
grant execute on function storage.foldername(text) to anon, authenticated, service_role;

-- --- 3. Let GoTrue migrate the auth schema ---------------------------------
-- GoTrue runs its own migrations, as supabase_auth_admin, the first time the
-- service starts. Its 00_init_auth_schema does
--   create or replace function auth.uid() ...
-- but platform-bootstrap.sql has already created those functions, owned by the
-- superuser that ran it. CREATE OR REPLACE requires ownership, so GoTrue dies
-- with "must be owner of function uid (SQLSTATE 42501)" and NSSM restart-loops
-- it forever - no login, and an auth service that never comes up.
--
-- Handing supabase_auth_admin ownership lets its migrations complete. It does
-- NOT leave GoTrue's (broken, per-claim-GUC) auth.uid() in place: provisioning
-- applies 0033 AFTER GoTrue has migrated, and the superuser can replace a
-- function regardless of who owns it. See provision.ps1, "why 0033 comes last".
do $$
declare r record;
begin
  for r in
    select p.oid::regprocedure as sig
      from pg_proc p
      join pg_namespace n on n.oid = p.pronamespace
     where n.nspname = 'auth'
  loop
    execute format('alter function %s owner to supabase_auth_admin', r.sig);
  end loop;
end $$;

grant create, usage on schema auth to supabase_auth_admin;
