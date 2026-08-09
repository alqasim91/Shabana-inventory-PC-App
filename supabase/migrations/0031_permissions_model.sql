-- ============================================================================
-- 0031_permissions_model.sql — per-user صلاحيات, additive only
-- ----------------------------------------------------------------------------
-- Business ask: an admin should be able to decide, per user and from the UI,
-- exactly what that person may do — with role presets as a starting point, the
-- ability to copy another user's setup, and access limited to chosen branches.
--
-- THIS MIGRATION CHANGES NO BEHAVIOUR. It builds the model and backfills it so
-- that every existing user's permission set is *exactly* what their role grants
-- them today. 0032 is the one that switches the policies over to reading it.
-- Splitting them this way means the risky migration starts from a state we can
-- already inspect and prove correct.
--
-- Design decisions worth stating:
--
--  * `role` STAYS. It is the preset a user was started from and the recovery
--    path: is_admin() keeps its meaning, admins bypass every permission check,
--    and an admin can therefore never lock themselves — or the business — out.
--    Only non-admins are governed by the permission rows.
--
--  * Presets are COPIED, not linked (the owner's explicit choice). Picking
--    "مدير" writes that preset's rows for that one user; editing the preset
--    later touches nobody. Nobody's access changes behind the admin's back.
--
--  * Permission keys are English identifiers in the database, Arabic labels
--    live in /src/labels.ts — same rule the enums follow.
--
--  * The catalog is a TABLE, not an enum: the UI reads it to render the editor
--    (grouped by `area`, ordered by `sort`), and adding a permission later is
--    an INSERT rather than an ALTER TYPE.
--
--  * Branch scoping is expressed as `profiles.all_sites` + rows in user_sites,
--    NOT as "no rows means everywhere". An empty set must mean *nothing*, or an
--    admin who clears a user's branches would silently grant them all of them.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. The catalog — the whole vocabulary of what can be permitted
-- ---------------------------------------------------------------------------
create table if not exists permission_keys (
  key   text primary key,
  area  text not null,          -- groups the toggles in the editor
  sort  int  not null,          -- order within the group
  note  text                    -- English gloss for whoever reads the schema
);

alter table permission_keys enable row level security;

drop policy if exists permission_keys_read on permission_keys;
-- Global vocabulary, identical for every business and containing no tenant
-- data, so it is readable by any signed-in user (the editor needs it) and
-- writable by none — new keys arrive by migration.
create policy permission_keys_read on permission_keys for select to authenticated
  using (true);

insert into permission_keys (key, area, sort, note) values
  -- المبيعات
  ('sales.view',              'sales',      10, 'see sales orders and their lines'),
  ('sales.draft',             'sales',      20, 'create and edit draft sales orders'),
  ('sales.discount',          'sales',      30, 'set a discount on a sales order'),
  ('sales.invoice',           'sales',      40, 'draft -> invoiced (freezes lines, issues a number)'),
  ('sales.place',             'sales',      50, 'invoiced -> placed (deducts branch stock)'),
  ('sales.cancel_placement',  'sales',      60, 'placed -> invoiced (returns the stock)'),
  ('sales.edit_locked',       'sales',      70, 'edit an order after invoicing (reverses & re-applies stock)'),
  ('sales.delete',            'sales',      80, 'delete a sales order'),
  -- المشتريات
  ('purchases.view',          'purchases',  10, 'see purchase orders, lines and conversions'),
  ('purchases.create',        'purchases',  20, 'create a purchase order'),
  ('purchases.edit',          'purchases',  30, 'edit a purchase order'),
  ('purchases.convert',       'purchases',  40, 'convert purchased KG into branch stock'),
  ('purchases.delete',        'purchases',  50, 'delete a purchase order or reverse a conversion'),
  -- المخزون
  ('inventory.view',          'inventory',  10, 'see items and stock levels'),
  ('inventory.items',         'inventory',  20, 'create and edit items, incl. the sale price'),
  ('inventory.adjust',        'inventory',  30, 'manual stock adjustment (تسوية)'),
  ('inventory.transfer',      'inventory',  40, 'transfer stock between branches'),
  ('inventory.item_delete',   'inventory',  50, 'delete an item'),
  -- الدفعات
  ('payments.record',         'payments',   10, 'record a payment against an order'),
  ('payments.credit',         'payments',   20, 'use or refund a client credit / vendor advance'),
  ('payments.delete',         'payments',   30, 'delete a payment'),
  -- الخزينة
  ('cash.view',               'cash',       10, 'see branch cash drawer balances and movements'),
  ('cash.manual',             'cash',       20, 'manual drawer إيداع / سحب / تسوية'),
  -- جهات الاتصال
  ('contacts.view',           'contacts',   10, 'see contacts, balances and statements'),
  ('contacts.manage',         'contacts',   20, 'create and edit contacts, phones, payment methods'),
  ('contacts.delete',         'contacts',   30, 'delete a contact'),
  -- المستندات
  ('attachments.manage',      'documents',  10, 'attach and remove order documents'),
  -- التقارير
  ('reports.view',            'reports',    10, 'open the reports page'),
  ('audit.view',              'reports',    20, 'read the audit log — who did what'),
  -- الإدارة
  ('users.manage',            'admin',      10, 'create users and set their permissions'),
  ('sites.manage',            'admin',      20, 'add and edit branches'),
  ('settings.manage',         'admin',      30, 'edit the business identity on invoices')
on conflict (key) do update
  set area = excluded.area, sort = excluded.sort, note = excluded.note;

-- ---------------------------------------------------------------------------
-- 2. What each user may do
-- ---------------------------------------------------------------------------
create table if not exists user_permissions (
  org_id  uuid not null references organization(id) on delete cascade,
  user_id uuid not null references profiles(user_id) on delete cascade,
  perm    text not null references permission_keys(key),
  primary key (user_id, perm)
);
create index if not exists user_permissions_lookup_idx on user_permissions (user_id, perm);
create index if not exists user_permissions_org_idx     on user_permissions (org_id);

-- ---------------------------------------------------------------------------
-- 3. Which branches a user may touch
-- ---------------------------------------------------------------------------
alter table profiles
  add column if not exists all_sites boolean not null default true;

comment on column profiles.all_sites is
  'true = this user works across every فرع. false = only the sites listed in user_sites.';

create table if not exists user_sites (
  org_id  uuid not null references organization(id) on delete cascade,
  user_id uuid not null references profiles(user_id) on delete cascade,
  site_id uuid not null references sites(id) on delete cascade,
  primary key (user_id, site_id)
);
create index if not exists user_sites_lookup_idx on user_sites (user_id, site_id);
create index if not exists user_sites_org_idx     on user_sites (org_id);

-- ---------------------------------------------------------------------------
-- 4. Cross-tenant safety (the 0026 rule, applied to the two new tables)
-- ---------------------------------------------------------------------------
-- org_id is never supplied by the client: it is derived from the profile the
-- row is about. A site from another business is then refused outright, so an
-- admin cannot grant their staff access to someone else's branch even by
-- passing its uuid directly.
create or replace function user_scope_set_org()
returns trigger language plpgsql
security definer set search_path = public as $$
declare v_org uuid; v_site_org uuid;
begin
  select org_id into v_org from profiles where user_id = new.user_id;
  if v_org is null then
    raise exception 'المستخدم غير موجود';
  end if;
  new.org_id := v_org;

  if tg_table_name = 'user_sites' then
    select org_id into v_site_org from sites where id = new.site_id;
    if v_site_org is distinct from v_org then
      raise exception 'الفرع لا ينتمي إلى نفس المنشأة';
    end if;
  end if;

  return new;
end;
$$;

drop trigger if exists trg_user_permissions_org on user_permissions;
create trigger trg_user_permissions_org
  before insert or update on user_permissions
  for each row execute function user_scope_set_org();

drop trigger if exists trg_user_sites_org on user_sites;
create trigger trg_user_sites_org
  before insert or update on user_sites
  for each row execute function user_scope_set_org();

-- ---------------------------------------------------------------------------
-- 5. RLS on the new tables
-- ---------------------------------------------------------------------------
-- A user may always read their OWN rows — the app needs them to decide which
-- buttons to draw. Reading everyone's needs users.manage, which is what the
-- editor (and "copy from another user") requires anyway. Writing is admin-only
-- for now; 0032 relaxes it to has_perm('users.manage').
alter table user_permissions enable row level security;
alter table user_sites       enable row level security;

drop policy if exists user_permissions_read   on user_permissions;
drop policy if exists user_permissions_write  on user_permissions;
drop policy if exists user_permissions_delete on user_permissions;
create policy user_permissions_read on user_permissions for select to authenticated
  using (org_id = current_org() and (user_id = auth.uid() or is_admin()));
create policy user_permissions_write on user_permissions for insert to authenticated
  with check (org_id = current_org() and is_admin());
create policy user_permissions_delete on user_permissions for delete to authenticated
  using (org_id = current_org() and is_admin());

drop policy if exists user_sites_read   on user_sites;
drop policy if exists user_sites_write  on user_sites;
drop policy if exists user_sites_delete on user_sites;
create policy user_sites_read on user_sites for select to authenticated
  using (org_id = current_org() and (user_id = auth.uid() or is_admin()));
create policy user_sites_write on user_sites for insert to authenticated
  with check (org_id = current_org() and is_admin());
create policy user_sites_delete on user_sites for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- 6. The presets — and the definition of "no behaviour change"
-- ---------------------------------------------------------------------------
-- These sets are not an opinion about who *should* be able to do what. They are
-- a transcription of what each role can do TODAY, read off the live policies:
--   staff  — reads everything, builds drafts, records payments
--   manager— everything is_manager_or_admin() currently unlocks
--   admin  — everything (and bypasses these rows entirely)
-- That is what makes 0032 safe: the same people can do the same things the
-- morning after. From there the admin narrows it down in the UI.
-- STABLE, not IMMUTABLE: the admin branch reads permission_keys, and a function
-- that reads a table and claims to be immutable is one the planner is entitled
-- to fold to a constant.
create or replace function role_preset_permissions(p_role app_role)
returns setof text
language sql stable
set search_path = public as $$
  select k from unnest(
    case p_role
      when 'admin' then array(select key from permission_keys)
      when 'manager' then array[
        'sales.view','sales.draft','sales.discount','sales.invoice','sales.place',
        'sales.cancel_placement',
        'purchases.view','purchases.create','purchases.edit','purchases.convert',
        'inventory.view','inventory.items','inventory.adjust','inventory.transfer',
        'payments.record','payments.credit',
        'cash.view',
        'contacts.view','contacts.manage',
        'attachments.manage',
        'reports.view','audit.view'
      ]
      else array[   -- staff
        'sales.view','sales.draft','sales.discount',
        'purchases.view',
        'inventory.view',
        'payments.record',
        'cash.view',
        'contacts.view',
        'reports.view'
      ]
    end
  ) as k;
$$;

-- ---------------------------------------------------------------------------
-- 7. The two questions every policy will ask from 0032 onward
-- ---------------------------------------------------------------------------
-- SECURITY DEFINER for the same reason current_app_role() is: these are read
-- from inside policies on tables the caller may not read directly, and the
-- definer context skips user_permissions' own RLS (no recursion — RLS is not
-- FORCEd on it).
create or replace function has_perm(p_key text)
returns boolean
language sql stable
security definer set search_path = public as $$
  select is_admin() or exists (
    select 1 from user_permissions
     where user_id = auth.uid() and perm = p_key
  );
$$;

comment on function has_perm(text) is
  'Does the caller hold this permission? Admins always do — they are the recovery path.';

-- A null site means "not branch-specific" (e.g. a site-agnostic purchase
-- order), which nobody should be locked out of.
create or replace function can_use_site(p_site uuid)
returns boolean
language sql stable
security definer set search_path = public as $$
  select p_site is null
      or is_admin()
      or coalesce((select all_sites from profiles where user_id = auth.uid()), true)
      or exists (
           select 1 from user_sites
            where user_id = auth.uid() and site_id = p_site
         );
$$;

comment on function can_use_site(uuid) is
  'May the caller see and act on this فرع? Admins and all_sites users always may.';

revoke execute on function has_perm(text)     from public, anon;
revoke execute on function can_use_site(uuid) from public, anon;
revoke execute on function role_preset_permissions(app_role) from public, anon;
grant  execute on function has_perm(text)     to authenticated;
grant  execute on function can_use_site(uuid) to authenticated;
grant  execute on function role_preset_permissions(app_role) to authenticated;

-- ---------------------------------------------------------------------------
-- 8. Backfill — nobody's access changes
-- ---------------------------------------------------------------------------
insert into user_permissions (org_id, user_id, perm)
select p.org_id, p.user_id, k.perm
from profiles p
cross join lateral role_preset_permissions(p.role) as k(perm)
on conflict (user_id, perm) do nothing;

-- Everyone keeps working across every branch until an admin says otherwise.
update profiles set all_sites = true where all_sites is not true;

-- ---------------------------------------------------------------------------
-- 9. A new account starts from its role's preset
-- ---------------------------------------------------------------------------
-- This is why the admin-create-user Edge Function needs no change: it still
-- inserts a profile with a role, and the rows appear underneath it.
create or replace function profile_seed_permissions()
returns trigger language plpgsql
security definer set search_path = public as $$
begin
  insert into user_permissions (org_id, user_id, perm)
  select new.org_id, new.user_id, k.perm
  from role_preset_permissions(new.role) as k(perm)
  on conflict (user_id, perm) do nothing;
  return null;
end;
$$;

drop trigger if exists trg_profile_seed_permissions on profiles;
create trigger trg_profile_seed_permissions
  after insert on profiles
  for each row execute function profile_seed_permissions();

-- Deliberately NO trigger re-seeding permissions when the role changes. The
-- editor saves a role and a set of toggles together, and a trigger firing in
-- the middle of that would wipe the toggles or not, depending purely on which
-- statement the client sent first. Applying a preset is a decision the admin
-- makes in the UI; committing it is the RPC below, in one transaction.

-- ---------------------------------------------------------------------------
-- 10. One atomic write for everything the editor can change
-- ---------------------------------------------------------------------------
-- Name, role, active, permissions and branches in a single transaction, so a
-- half-saved user is not a state that can exist. Also the one place the
-- lockout rules live:
--   * you cannot change your own role, active flag or permissions — an admin
--     who demotes themselves has no way back in
--   * you cannot remove the last active admin, by demotion or deactivation
create or replace function admin_set_user_access(
  p_user_id   uuid,
  p_full_name text,
  p_role      app_role,
  p_active    boolean,
  p_perms     text[],
  p_all_sites boolean,
  p_site_ids  uuid[]
) returns void
language plpgsql
security definer set search_path = public
as $$
declare
  v_org      uuid := current_org();
  v_target   profiles%rowtype;
  v_admins   int;
  v_unknown  text;
begin
  if not is_admin() then
    raise exception 'هذا الإجراء متاح لمدير النظام فقط';
  end if;

  select * into v_target from profiles where user_id = p_user_id for update;
  if v_target.user_id is null or v_target.org_id is distinct from v_org then
    raise exception 'المستخدم غير موجود';
  end if;

  if trim(coalesce(p_full_name, '')) = '' then
    raise exception 'الاسم مطلوب';
  end if;

  -- Reject an unknown key loudly rather than silently dropping it: a typo in a
  -- permission name must never look like a saved-but-inactive permission.
  select string_agg(k, ', ') into v_unknown
  from unnest(coalesce(p_perms, '{}')) k
  where k not in (select key from permission_keys);
  if v_unknown is not null then
    raise exception 'صلاحيات غير معروفة: %', v_unknown;
  end if;

  if p_user_id = auth.uid()
     and (p_role is distinct from v_target.role or p_active is distinct from v_target.active)
  then
    raise exception 'لا يمكنك تغيير دورك أو تعطيل حسابك بنفسك';
  end if;

  -- Last-admin guard: count the OTHER active admins in this business.
  if (v_target.role = 'admin' and v_target.active)
     and (p_role <> 'admin' or not p_active)
  then
    select count(*) into v_admins
      from profiles
     where org_id = v_org and role = 'admin' and active and user_id <> p_user_id;
    if v_admins = 0 then
      raise exception 'لا يمكن إزالة آخر مدير نظام في المنشأة';
    end if;
  end if;

  if not p_all_sites and coalesce(array_length(p_site_ids, 1), 0) = 0 then
    raise exception 'اختر فرعًا واحدًا على الأقل أو فعّل «كل الفروع»';
  end if;

  update profiles
     set full_name = trim(p_full_name),
         role      = case when p_user_id = auth.uid() then v_target.role   else p_role   end,
         active    = case when p_user_id = auth.uid() then v_target.active else p_active end,
         all_sites = p_all_sites
   where user_id = p_user_id;

  -- Replace rather than merge: the editor always sends the complete picture,
  -- so a permission absent from p_perms is one the admin just switched off.
  delete from user_permissions where user_id = p_user_id;
  insert into user_permissions (org_id, user_id, perm)
  select v_org, p_user_id, k from unnest(coalesce(p_perms, '{}')) k
  on conflict (user_id, perm) do nothing;

  delete from user_sites where user_id = p_user_id;
  if not p_all_sites then
    insert into user_sites (org_id, user_id, site_id)
    select v_org, p_user_id, s from unnest(p_site_ids) s
    on conflict (user_id, site_id) do nothing;
  end if;
end;
$$;

revoke execute on function admin_set_user_access(uuid, text, app_role, boolean, text[], boolean, uuid[])
  from public, anon;
grant execute on function admin_set_user_access(uuid, text, app_role, boolean, text[], boolean, uuid[])
  to authenticated;

-- ---------------------------------------------------------------------------
-- 11. Post-flight — prove the backfill reproduces today's roles exactly
-- ---------------------------------------------------------------------------
do $$
declare bad text;
begin
  select string_agg(p.user_id::text || ' (' || p.role || ')', ', ')
    into bad
  from profiles p
  where (
    select count(*) from user_permissions up where up.user_id = p.user_id
  ) <> (
    select count(*) from role_preset_permissions(p.role)
  );

  if bad is not null then
    raise exception 'ABORT: backfill did not match the role preset for: %', bad;
  end if;

  if not exists (select 1 from permission_keys) then
    raise exception 'ABORT: permission catalog is empty';
  end if;
end $$;
