-- ============================================================================
-- 0027_platform_provisioning.sql — creating a new client (منشأة)
-- ----------------------------------------------------------------------------
-- Part 4 of 5 (see multi-tenant-plan.md). Everything so far isolates the
-- businesses that exist; this is how a new one comes into being.
--
-- No self-serve signup, by decision: the operator onboards each client. This
-- migration provides the database half; the Edge Function `create-organization`
-- provides the auth half (it can mint auth.users, which SQL cannot).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. platform_admins — deliberately NOT a fourth app_role
-- ---------------------------------------------------------------------------
-- app_role (admin|manager|staff) describes a person's authority INSIDE one
-- business, and is_admin() is consulted by all 61 RLS policies plus RoleGate in
-- the UI. Adding a 'platform' value to that enum would silently widen every one
-- of those checks at once. A separate table keeps the two concepts apart:
-- app_role answers "what may you do in your business", platform_admins answers
-- "may you create businesses".
--
-- Note what this does NOT grant: membership here gives no read access to any
-- tenant's data. current_org() still governs every row, and a platform admin
-- has no org of their own. Support access into a client's books, if ever
-- needed, should be an explicit, time-boxed, audited impersonation — never
-- ambient. audit_log already exists to record it.
-- ---------------------------------------------------------------------------
create table platform_admins (
  user_id    uuid primary key references auth.users(id) on delete cascade,
  note       text,
  created_at timestamptz not null default now()
);

alter table platform_admins enable row level security;
-- No policy at all: RLS with zero policies denies every authenticated user.
-- Only the service role (which bypasses RLS) reads this table, from the Edge
-- Function. Tenants cannot even discover that the table has rows.

create or replace function is_platform_admin()
returns boolean
language sql stable security definer set search_path = public
as $$ select exists (select 1 from platform_admins where user_id = auth.uid()); $$;
revoke execute on function is_platform_admin() from public, anon;
grant execute on function is_platform_admin() to authenticated;

-- ---------------------------------------------------------------------------
-- 2. provision_organization — one transaction, no half-built businesses
-- ---------------------------------------------------------------------------
-- The auth account cannot share a transaction with this (auth is a separate
-- API), which is exactly why everything else is in here: either the business
-- exists complete, or it does not exist at all. The caller creates the auth
-- user first and deletes it if this function raises.
--
-- A new business needs all four pieces to be usable:
--   organization  — identity, slug, invoice header
--   profiles      — its first admin, or nobody can log in
--   sites         — at least one فرع; all stock and cash is site-scoped
--                   (rule 1), and the UI assumes sites[0] exists
--   doc_counters  — numbering starts at ١, not continuing another business's
--
-- Not seeded: contacts and items. A new client starts with an empty book.
-- Cash drawers need nothing — a drawer balance is the sum of an empty
-- append-only ledger, which is already zero.
-- ---------------------------------------------------------------------------
create or replace function provision_organization(
  p_slug          text,
  p_business_name text,
  p_owner_user_id uuid,
  p_owner_name    text,
  p_owner_username text,
  p_site_name     text default 'الفرع الرئيسي',
  p_address_line  text default null,
  p_phone_line    text default null
)
returns uuid
language plpgsql security definer set search_path = public as $$
declare
  v_org uuid;
begin
  if p_slug is null or p_business_name is null or p_owner_user_id is null then
    raise exception 'بيانات ناقصة لإنشاء المنشأة';
  end if;

  insert into organization (slug, business_name, address_line, phone_line)
  values (lower(trim(p_slug)), p_business_name, p_address_line, p_phone_line)
  returning id into v_org;

  -- org_id is passed explicitly: profiles deliberately has no current_org()
  -- default, because the platform admin running this belongs to no org.
  insert into profiles (user_id, org_id, full_name, username, role, active)
  values (p_owner_user_id, v_org, p_owner_name, lower(trim(p_owner_username)), 'admin', true);

  insert into sites (org_id, name_ar, active) values (v_org, p_site_name, true);

  insert into doc_counters (org_id, scope, year, last_value) values
    (v_org, 'po_seq', 0, 0),
    (v_org, 'so_seq', 0, 0);

  return v_org;
end $$;

-- Callable only by the service role (the Edge Function). Not by tenants, and
-- not by anon — creating a business is never a client-side action.
revoke execute on function provision_organization(text,text,uuid,text,text,text,text,text)
  from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 3. platform_orgs — the operator console's read model
-- ---------------------------------------------------------------------------
-- Metadata only: how many users and sites, when created, active or not. No
-- financial figures. Deliberately a SECURITY DEFINER function rather than a
-- view, so it can be granted to the service role alone.
-- ---------------------------------------------------------------------------
create or replace function platform_list_orgs()
returns table (
  id uuid, slug text, business_name text, active boolean,
  created_at timestamptz, user_count bigint, site_count bigint
)
language sql stable security definer set search_path = public as $$
  select o.id, o.slug, o.business_name, o.active, o.created_at,
         (select count(*) from profiles p where p.org_id = o.id),
         (select count(*) from sites s where s.org_id = o.id)
  from organization o
  order by o.created_at;
$$;
revoke execute on function platform_list_orgs() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 4. Bootstrapping
-- ---------------------------------------------------------------------------
-- The first platform admin must be inserted by hand with the service role,
-- since no UI exists to create one and no one is yet privileged to do it:
--
--   insert into platform_admins (user_id, note)
--   values ('<your auth.users id>', 'founder');
--
-- Deliberately not automated here: it would mean this migration picking a
-- human to hand the keys to.
-- ---------------------------------------------------------------------------
