-- ============================================================================
-- 0034_pc_local_storage.sql — order attachments, without a storage service
-- ----------------------------------------------------------------------------
-- PC-ONLY. Never applied to the cloud database.
--
-- The cloud keeps attachment BYTES in Supabase Storage (the storage-api
-- service, backed by S3). The PC edition ships no such service, so
-- pc-prelude.sql provides only a SCHEMA shim for storage.objects/buckets -
-- enough for migrations 0022/0028 to apply, but nothing that can hold a file.
-- Attachments were therefore non-functional on PC.
--
-- WHY BYTES IN POSTGRES, RATHER THAN ANOTHER SERVICE
-- Shipping storage-api would mean another binary, another port, another
-- Windows service, another restart loop to debug - the exact machinery that
-- accounts for most of the failures this project has had. Postgres is already
-- running, already reachable through PostgREST, and already enforces the
-- org-scoped RLS these files need. Three concrete wins:
--
--   * Backups already cover it. pg_dump captures the attachments with the
--     books, so one backup file is genuinely the whole shop. A separate file
--     store would need its own backup, its own restore, and its own way to go
--     silently out of sync with the database.
--   * Isolation is stronger. Access is checked against the order_attachments
--     row itself rather than against a path prefix, so a guessed path proves
--     nothing.
--   * Nothing new can fail to start.
--
-- The cost is database size. Attachments are receipts and delivery notes that
-- the client already downscales before upload, and the ceiling below is 10 MB
-- (the same limit 0022 set on the cloud bucket), so a busy shop adds a few
-- hundred MB a year. That is a fine trade on a single PC; it would not be at
-- cloud scale, which is exactly why the two editions differ here.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 1. The bytes
-- ---------------------------------------------------------------------------
-- Keyed by the SAME storage_path that order_attachments already records, so
-- the existing table stays the single source of truth for metadata and this
-- table holds nothing but content. No new concepts in the app's data model.
create table if not exists pc_file_bytes (
  path       text primary key,
  org_id     uuid not null references organization (id) on delete cascade,
  mime_type  text,
  byte_size  integer not null,
  bytes      bytea  not null,
  created_at timestamptz not null default now(),
  created_by uuid
);

create index if not exists pc_file_bytes_org_idx on pc_file_bytes (org_id);

-- RLS on, and deliberately NO policies: every path in and out of this table
-- goes through the SECURITY DEFINER functions below, which check authority
-- explicitly. A table nobody can select from directly cannot leak another
-- org's file through a policy someone forgot to think about.
alter table pc_file_bytes enable row level security;
revoke all on pc_file_bytes from anon, authenticated;


-- ---------------------------------------------------------------------------
-- 2. pc_file_put — upload
-- ---------------------------------------------------------------------------
-- Mirrors the authority the cloud's storage INSERT policy enforced (0028):
-- manager or admin, and the path must sit inside the caller's own org folder.
-- Content arrives base64-encoded because it travels as JSON through PostgREST.
create or replace function pc_file_put(
  p_path text,
  p_mime text,
  p_b64  text
) returns jsonb
language plpgsql security definer set search_path = public, extensions
as $$
declare
  v_org   uuid := current_org();
  v_bytes bytea;
  v_size  integer;
begin
  if auth.uid() is null then
    return jsonb_build_object('ok', false, 'code', 'unauthorized');
  end if;
  if v_org is null then
    return jsonb_build_object('ok', false, 'code', 'no_org');
  end if;
  if not is_manager_or_admin() then
    return jsonb_build_object('ok', false, 'code', 'forbidden');
  end if;

  -- The org id must be the first path segment, exactly as the cloud policy
  -- required. Belt and braces: org_id is also stored on the row, and reads are
  -- checked against the attachment row, so the prefix is not load-bearing on
  -- its own - but keeping the same shape means paths stay portable between the
  -- two editions.
  if p_path is null or p_path !~ ('^' || v_org::text || '/') then
    return jsonb_build_object('ok', false, 'code', 'bad_path');
  end if;

  begin
    v_bytes := decode(coalesce(p_b64, ''), 'base64');
  exception when others then
    return jsonb_build_object('ok', false, 'code', 'bad_encoding');
  end;

  v_size := length(v_bytes);
  if v_size = 0 then
    return jsonb_build_object('ok', false, 'code', 'empty_file');
  end if;
  -- Same 10 MB ceiling migration 0022 set on the cloud bucket.
  if v_size > 10485760 then
    return jsonb_build_object('ok', false, 'code', 'too_large');
  end if;

  insert into pc_file_bytes (path, org_id, mime_type, byte_size, bytes, created_by)
  values (p_path, v_org, p_mime, v_size, v_bytes, auth.uid());

  return jsonb_build_object('ok', true, 'path', p_path, 'byte_size', v_size);
exception
  when unique_violation then
    -- upsert:false in the client; a repeat means a genuine collision.
    return jsonb_build_object('ok', false, 'code', 'already_exists');
end $$;


-- ---------------------------------------------------------------------------
-- 3. pc_file_get — download
-- ---------------------------------------------------------------------------
-- Returns base64, or null when the caller may not see the file. Authority is
-- the attachment ROW, mirroring 0028's read policy: you can read a file only
-- if your organization has an order_attachments row pointing at it. Guessing a
-- path gets you nothing.
create or replace function pc_file_get(p_path text)
returns text
language sql stable security definer set search_path = public, extensions
as $$
  select encode(f.bytes, 'base64')
    from pc_file_bytes f
   where f.path = p_path
     and auth.uid() is not null
     and exists (
       select 1 from order_attachments a
        where a.storage_path = f.path
          and a.org_id = current_org()
     );
$$;


-- ---------------------------------------------------------------------------
-- 4. pc_file_delete — remove
-- ---------------------------------------------------------------------------
-- Manager+, and the file must belong to your org - the authority the cloud's
-- storage DELETE policy carried. Called BEFORE the attachment row is deleted
-- (see the client's ordering note), so the row is still present to authorize
-- it; org_id on this table is what makes that check work either way.
create or replace function pc_file_delete(p_path text)
returns jsonb
language plpgsql security definer set search_path = public, extensions
as $$
declare
  v_org uuid := current_org();
begin
  if auth.uid() is null then
    return jsonb_build_object('ok', false, 'code', 'unauthorized');
  end if;
  if not is_manager_or_admin() then
    return jsonb_build_object('ok', false, 'code', 'forbidden');
  end if;

  delete from pc_file_bytes where path = p_path and org_id = v_org;
  -- Deleting something already gone is success, not an error: the client
  -- removes the bytes first and then the row, so a retry after a partial
  -- failure must be able to complete rather than dead-end.
  return jsonb_build_object('ok', true);
end $$;


revoke execute on function pc_file_put(text, text, text)  from public, anon;
revoke execute on function pc_file_get(text)               from public, anon;
revoke execute on function pc_file_delete(text)            from public, anon;
grant  execute on function pc_file_put(text, text, text)  to authenticated;
grant  execute on function pc_file_get(text)               to authenticated;
grant  execute on function pc_file_delete(text)            to authenticated;
