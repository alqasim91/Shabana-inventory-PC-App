-- ============================================================================
-- 0033_pc_local_auth.sql — single-tenant auth for the PC edition
-- ----------------------------------------------------------------------------
-- PC-ONLY. This migration exists only in Shabana-inventory-PC-App and is never
-- applied to the cloud database. It provides the local, no-edge-runtime
-- equivalents of two things the cloud app gets from Supabase Edge Functions
-- (Deno), which a PC install has no way to run and no safe place to keep a
-- service_role key for:
--
--   • create-organization  → pc_first_run_bootstrap()   (the first-run screen)
--   • admin-create-user     → pc_create_user()           (ongoing user creation)
--
-- SQL cannot open the auth HTTP API, but a SECURITY DEFINER function owned by a
-- superuser CAN write auth.users directly — which is exactly what these do,
-- with the same server-side authority checks the Edge Functions enforced.
--
-- SECURITY-CRITICAL. This is the "reshaping auth for local admin creation"
-- work flagged in BUILD_PLAN.md / OPUS-HANDOFF.md for a careful pass. Written
-- and reviewed on an Opus model, syntax-tested against a local Postgres, but
-- NOT yet run against the real GoTrue-migrated auth schema on Windows.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 0. Fix auth.uid()/auth.role()/auth.email() for THIS stack's PostgREST.
-- ---------------------------------------------------------------------------
-- THE most important few lines in this repo. platform-bootstrap.sql vendors
-- Supabase's 2018-era definitions, which read
-- current_setting('request.jwt.claim.sub') — a per-claim GUC that PostgREST
-- REMOVED in v9.0 (2021). Our pinned PostgREST 16 exposes ONLY the single JSON
-- GUC request.jwt.claims. Left unfixed, auth.uid() returns NULL on every
-- request, so is_admin()/current_org()/has_perm() all return NULL/false and
-- EVERY RLS policy fails closed — a logged-in user sees none of their own data
-- and the app looks completely broken with no error.
--
-- These are Supabase's own current canonical definitions: coalesce the (legacy,
-- now-absent) per-claim GUC with the JSON claims object, so they're correct
-- whether the stack sets one form or the other.
create or replace function auth.uid() returns uuid
language sql stable
as $$
  select coalesce(
    nullif(current_setting('request.jwt.claim.sub', true), ''),
    (nullif(current_setting('request.jwt.claims', true), '')::jsonb ->> 'sub')
  )::uuid
$$;

create or replace function auth.role() returns text
language sql stable
as $$
  select coalesce(
    nullif(current_setting('request.jwt.claim.role', true), ''),
    (nullif(current_setting('request.jwt.claims', true), '')::jsonb ->> 'role')
  )::text
$$;

create or replace function auth.email() returns text
language sql stable
as $$
  select coalesce(
    nullif(current_setting('request.jwt.claim.email', true), ''),
    (nullif(current_setting('request.jwt.claims', true), '')::jsonb ->> 'email')
  )::text
$$;


-- ---------------------------------------------------------------------------
-- 1. pc_needs_setup() — is this a fresh, unclaimed install?
-- ---------------------------------------------------------------------------
-- Anon-callable, and deliberately reveals only a single boolean: whether this
-- install has been claimed yet. The first-run screen calls it to decide whether
-- to show setup or redirect to login. It leaks nothing an attacker could use —
-- a fresh install is a fresh install whether or not anyone asks.
--
-- The test is "no PROFILE exists", not "no organization exists": migrations
-- 0009/0024 seed a default organization row (slug 'shabana') as part of the
-- cloud's single-tenant → multi-tenant conversion, so a freshly migrated PC
-- database ALWAYS has exactly one organization and zero users. Testing for the
-- organization therefore reported "already set up" on every fresh install, the
-- setup screen never appeared, and — with no account ever created — the app
-- could not be logged into at all. A profile is the thing a human actually
-- creates, so its absence is the true definition of "unclaimed".
create or replace function pc_needs_setup() returns boolean
language sql stable security definer set search_path = public
as $$ select not exists (select 1 from profiles); $$;

revoke execute on function pc_needs_setup() from public;
grant execute on function pc_needs_setup() to anon, authenticated;


-- ---------------------------------------------------------------------------
-- 2. pc_first_run_bootstrap() — claim a fresh install
-- ---------------------------------------------------------------------------
-- Creates the one organization, its first admin auth account, its first site,
-- and doc counters — the local stand-in for the create-organization Edge
-- Function. Anon-callable BY NECESSITY (nobody is logged in on a fresh
-- install), but safe because it is strictly one-shot: the instant any
-- organization exists it becomes a permanent no-op error, so it can neither
-- create a second tenant nor overwrite an existing one. The advisory lock
-- closes the (single-PC-unlikely but real) double-submit race.
--
-- The slug is fixed to 'shabana' — the same DEFAULT_ORG_SLUG the app's
-- username→email mapping already falls back to — so the owner logs in with a
-- bare username and no "business code" field. On a single-tenant machine the
-- slug is invisible plumbing; it only has to be internally consistent, and
-- 'shabana' makes the login page's existing default Just Work.
create or replace function pc_first_run_bootstrap(
  p_business_name  text,
  p_admin_fullname text,
  p_admin_username text,
  p_admin_password text,
  p_site_name      text default 'الفرع الرئيسي'
) returns jsonb
language plpgsql security definer set search_path = public, extensions
as $$
declare
  v_slug     constant text := 'shabana';
  v_username text := lower(trim(coalesce(p_admin_username, '')));
  v_email    text;
  v_user_id  uuid;
  v_org      uuid;
begin
  perform pg_advisory_xact_lock(hashtext('pc_first_run_bootstrap'));

  -- "Claimed" means a user exists, not that an organization row exists — see
  -- pc_needs_setup() above for why the two are not the same on a fresh
  -- database. This keeps the one-shot guarantee intact: the instant the first
  -- profile is created, this function is a permanent no-op.
  if exists (select 1 from profiles) then
    return jsonb_build_object('ok', false, 'code', 'already_setup');
  end if;

  if trim(coalesce(p_business_name, '')) = ''
     or trim(coalesce(p_admin_fullname, '')) = ''
     or v_username !~ '^[a-z0-9._-]+$'
     or length(coalesce(p_admin_password, '')) < 6 then
    return jsonb_build_object('ok', false, 'code', 'invalid_input');
  end if;

  v_email := v_username || '@' || v_slug || '.local';

  -- Dynamic SQL: email_confirmed_at / is_sso_user / is_anonymous are added to
  -- auth.users by GoTrue's OWN migrations, which run when the GoTrue service
  -- first starts — AFTER this function is created during provisioning. EXECUTE
  -- defers name resolution to call time (first run, services already up), when
  -- those columns exist. A static INSERT would fail at CREATE FUNCTION time.
  -- crypt()/gen_salt() are schema-qualified because this function's search_path
  -- can't be trusted to include extensions inside dynamic SQL.
  execute $ins$
    insert into auth.users (
      instance_id, id, aud, role, email,
      encrypted_password, email_confirmed_at,
      raw_app_meta_data, raw_user_meta_data,
      created_at, updated_at
    ) values (
      '00000000-0000-0000-0000-000000000000', gen_random_uuid(),
      'authenticated', 'authenticated', $1,
      extensions.crypt($2, extensions.gen_salt('bf', 10)), now(),
      '{"provider":"email","providers":["email"]}'::jsonb, '{}'::jsonb,
      now(), now()
    ) returning id
  $ins$ into v_user_id using v_email, p_admin_password;

  -- Clear the placeholder organization the migrations seeded (see
  -- pc_needs_setup) so provision_organization can create the real one under the
  -- name the owner just typed, rather than colliding on the unique slug. This
  -- is safe precisely here and nowhere else: we only reach this line when no
  -- profile exists, and the guards below additionally refuse to delete an org
  -- that anything at all is attached to. On a claimed install this deletes
  -- nothing.
  delete from organization o
   where o.slug = v_slug
     and not exists (select 1 from profiles p where p.org_id = o.id)
     and not exists (select 1 from sites    s where s.org_id = o.id);

  -- provision_organization is owned by the same superuser and REVOKEd from
  -- anon/authenticated, but this SECURITY DEFINER function runs as its owner,
  -- for whom the revoke doesn't apply.
  v_org := provision_organization(
    lower(v_slug),
    trim(p_business_name),
    v_user_id,
    trim(p_admin_fullname),
    v_username,
    coalesce(nullif(trim(p_site_name), ''), 'الفرع الرئيسي')
  );

  return jsonb_build_object('ok', true, 'org_id', v_org, 'username', v_username);
exception
  when unique_violation then
    -- Someone won the race between the guard and the insert.
    return jsonb_build_object('ok', false, 'code', 'already_setup');
end $$;

revoke execute on function pc_first_run_bootstrap(text,text,text,text,text) from public;
grant execute on function pc_first_run_bootstrap(text,text,text,text,text) to anon, authenticated;


-- ---------------------------------------------------------------------------
-- 3. pc_create_user() — ongoing user creation by a tenant admin
-- ---------------------------------------------------------------------------
-- The local stand-in for admin-create-user. Enforces the same authority the
-- Edge Function did, SERVER-SIDE: the caller must be an admin of an org, and
-- the new account is bound to the caller's own org with an email derived from
-- the caller's org slug — none of which the client can influence. The
-- profile_seed_permissions trigger (0031) seeds the new user's permissions
-- from the role preset on insert, exactly as in the cloud.
create or replace function pc_create_user(
  p_username  text,
  p_password  text,
  p_full_name text,
  p_role      text
) returns jsonb
language plpgsql security definer set search_path = public, extensions
as $$
declare
  v_caller      uuid := auth.uid();
  v_caller_role text;
  v_org         uuid;
  v_slug        text;
  v_username    text := lower(trim(coalesce(p_username, '')));
  v_email       text;
  v_user_id     uuid;
begin
  if v_caller is null then
    return jsonb_build_object('ok', false, 'code', 'unauthorized');
  end if;

  select role, org_id into v_caller_role, v_org
  from profiles where user_id = v_caller;

  if v_caller_role is distinct from 'admin' then
    return jsonb_build_object('ok', false, 'code', 'forbidden');
  end if;
  if v_org is null then
    return jsonb_build_object('ok', false, 'code', 'no_org');
  end if;

  if v_username !~ '^[a-z0-9._-]+$'
     or trim(coalesce(p_full_name, '')) = ''
     or coalesce(p_role, '') not in ('admin', 'manager', 'staff')
     or length(coalesce(p_password, '')) < 6 then
    return jsonb_build_object('ok', false, 'code', 'invalid_input');
  end if;

  select slug into v_slug from organization where id = v_org;
  if v_slug is null then
    return jsonb_build_object('ok', false, 'code', 'no_org');
  end if;
  v_email := v_username || '@' || v_slug || '.local';

  begin
    execute $ins$
      insert into auth.users (
        instance_id, id, aud, role, email,
        encrypted_password, email_confirmed_at,
        raw_app_meta_data, raw_user_meta_data,
        created_at, updated_at
      ) values (
        '00000000-0000-0000-0000-000000000000', gen_random_uuid(),
        'authenticated', 'authenticated', $1,
        extensions.crypt($2, extensions.gen_salt('bf', 10)), now(),
        '{"provider":"email","providers":["email"]}'::jsonb, '{}'::jsonb,
        now(), now()
      ) returning id
    $ins$ into v_user_id using v_email, p_password;
  exception when unique_violation then
    return jsonb_build_object('ok', false, 'code', 'email_exists');
  end;

  begin
    insert into profiles (user_id, org_id, full_name, username, role, active)
    values (v_user_id, v_org, trim(p_full_name), v_username, p_role, true);
  exception when unique_violation then
    -- Orphaned auth user (username taken on profiles) would be able to log in
    -- with no profile — delete it so the failure leaves no trace.
    delete from auth.users where id = v_user_id;
    return jsonb_build_object('ok', false, 'code', 'email_exists');
  end;

  return jsonb_build_object('ok', true, 'user_id', v_user_id);
end $$;

revoke execute on function pc_create_user(text,text,text,text) from public, anon;
grant execute on function pc_create_user(text,text,text,text) to authenticated;
