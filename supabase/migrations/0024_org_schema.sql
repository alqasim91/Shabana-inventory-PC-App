-- ============================================================================
-- 0024_org_schema.sql — multi-tenant foundation: org_id on every table
-- ----------------------------------------------------------------------------
-- Part 1 of 5 (see multi-tenant-plan.md).
--
-- This migration is DELIBERATELY behaviour-preserving. With exactly one
-- organization, every row backfills to it and current_org() returns it for
-- every existing user, so the app behaves identically before and after. The
-- RLS policies still read `using (true)` — 0025 tightens them. Nothing here
-- isolates anything yet; it only makes isolation possible.
--
-- DEPLOY COUPLING: 0024–0028 must ship as ONE release together with the
-- updated admin-create-user Edge Function. profiles.org_id becomes NOT NULL,
-- and that function inserts profiles with the service role (where auth.uid()
-- is null, so the current_org() default cannot fire) — it must pass org_id
-- explicitly or user creation breaks.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. organization: singleton → real table
-- ---------------------------------------------------------------------------
-- The old table keyed on `id boolean primary key default true` with a
-- singleton CHECK. Rebuilt rather than altered because the PK type changes.
-- `slug` is constrained to a valid DNS label: it becomes both the URL segment
-- (/shabana/login) and the login email domain (ahmed@shabana.local). Choosing
-- 'shabana' for the existing business makes the derived emails byte-identical
-- to today's, so no auth.users rewrite and no password resets.
-- ---------------------------------------------------------------------------
alter table organization rename to organization_legacy;

create table organization (
  id            uuid        primary key default gen_random_uuid(),
  slug          text        not null unique,
  business_name text        not null,
  address_line  text,
  phone_line    text,
  active        boolean     not null default true,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  constraint org_slug_format check (slug ~ '^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$')
);

insert into organization (slug, business_name, address_line, phone_line)
select 'shabana', business_name, address_line, phone_line from organization_legacy;

-- cascade drops the legacy table's own policies and touch trigger.
drop table organization_legacy cascade;

alter table organization enable row level security;

-- Read stays open here; 0025 scopes it to the caller's own org.
create policy org_read on organization for select to authenticated using (true);
create policy org_update on organization for update to authenticated
  using (is_admin()) with check (is_admin());
-- NOTE: no INSERT policy. Creating an organization is a provisioning action
-- (0027), never something a tenant admin can do. This intentionally drops the
-- old org_insert policy, which existed only to seed the singleton row.

create trigger trg_organization_touch before update on organization
  for each row execute function organization_touch();

-- ---------------------------------------------------------------------------
-- 2. org_id on all 21 remaining tables: add → backfill → NOT NULL → index
-- ---------------------------------------------------------------------------
-- Driven by an explicit array so all 21 tables get byte-identical treatment.
--
-- Triggers are disabled per-table around the backfill for two reasons:
--   1. block_mutation() raises unconditionally on UPDATE and guards four
--      append-only ledgers (stock_movements, cash_movements, client_credits,
--      vendor_credits) — a plain UPDATE would abort this whole migration.
--   2. audit_row() would otherwise write one audit_log row per backfilled
--      row, burying the real business history under a schema change.
--
-- org_id is denormalized onto child tables (po_lines, payments, …) rather than
-- joined through the parent so every RLS policy is a single indexed equality
-- test, and so all 66 policies read identically at review time.
-- ---------------------------------------------------------------------------
do $$
declare
  t     text;
  v_org uuid := (select id from organization);
  tbls  text[] := array[
    'sites', 'profiles', 'contacts', 'contact_phones', 'contact_payment_methods',
    'items', 'purchase_orders', 'po_conversions', 'sales_orders',
    'sales_order_lines', 'payments', 'stock_movements', 'stock_transfers',
    'cash_movements', 'doc_counters', 'audit_log', 'client_credits',
    'po_lines', 'po_line_conversions', 'order_attachments', 'vendor_credits'
  ];
begin
  if v_org is null then
    raise exception 'no organization row to backfill to — aborting';
  end if;

  foreach t in array tbls loop
    execute format('alter table %I add column org_id uuid references organization(id)', t);

    execute format('alter table %I disable trigger user', t);
    execute format('update %I set org_id = %L', t, v_org);
    execute format('alter table %I enable trigger user', t);

    execute format('alter table %I alter column org_id set not null', t);
    execute format('create index %I on %I (org_id)', t || '_org_idx', t);
  end loop;
end $$;

-- ---------------------------------------------------------------------------
-- 3. current_org() — the resolver every policy will call
-- ---------------------------------------------------------------------------
-- Same shape as the existing is_admin()/current_app_role(): SECURITY DEFINER
-- with a pinned search_path so policies can call it without recursing through
-- profiles' own RLS.
--
-- If this ever returns NULL, `org_id = current_org()` evaluates to NULL →
-- false → zero rows, i.e. a total lockout for that user. profiles.org_id is
-- NOT NULL as of step 2, which is what prevents that.
-- ---------------------------------------------------------------------------
create or replace function current_org()
returns uuid
language sql stable security definer set search_path = public
as $$ select org_id from profiles where user_id = auth.uid(); $$;

revoke execute on function current_org() from public, anon;
grant execute on function current_org() to authenticated;

-- ---------------------------------------------------------------------------
-- 4. Default org_id from the caller, so the app never sets it
-- ---------------------------------------------------------------------------
-- Every insert the frontend makes today omits org_id; this default fills it
-- in. It also works inside the SECURITY DEFINER RPCs, because auth.uid() reads
-- the request JWT rather than the effective role.
--
-- profiles is excluded on purpose: its org_id is always passed explicitly by
-- provisioning (0027). A platform admin creating org B's first user has no
-- org of their own, so a current_org() default would resolve to NULL — and
-- failing loudly on NOT NULL beats silently filing the user under the wrong
-- business.
-- ---------------------------------------------------------------------------
-- audit_log is excluded too — its org_id comes from the audited ROW, not the
-- actor. See step 4b.
do $$
declare
  t    text;
  tbls text[] := array[
    'sites', 'contacts', 'contact_phones', 'contact_payment_methods',
    'items', 'purchase_orders', 'po_conversions', 'sales_orders',
    'sales_order_lines', 'payments', 'stock_movements', 'stock_transfers',
    'cash_movements', 'doc_counters', 'client_credits',
    'po_lines', 'po_line_conversions', 'order_attachments', 'vendor_credits'
  ];
begin
  foreach t in array tbls loop
    execute format('alter table %I alter column org_id set default current_org()', t);
  end loop;
end $$;

-- ---------------------------------------------------------------------------
-- 4b. audit_row(): stamp org_id from the audited row, never from the actor
-- ---------------------------------------------------------------------------
-- Without this the audit insert inherits a current_org() default, which is
-- NULL whenever there is no JWT — so EVERY service-role write to an audited
-- table (the admin-create-user Edge Function, dashboard SQL, backfills) would
-- die on a NOT NULL violation. Caught by the 0024 dry run.
--
-- Sourcing org_id from the row is also the semantically correct choice: an
-- audit entry belongs to the business whose data changed, not to whoever
-- changed it. `organization` is the one audited table with no org_id column —
-- it IS the org, so its own id is used.
-- ---------------------------------------------------------------------------
create or replace function audit_row()
  returns trigger language plpgsql security definer set search_path = public as $$
declare
  rec   jsonb;
  eid   text;
  v_org uuid;
begin
  if (tg_op = 'DELETE') then rec := to_jsonb(old); else rec := to_jsonb(new); end if;
  eid := coalesce(rec->>'id', rec->>'user_id');

  v_org := coalesce(
    nullif(rec->>'org_id', '')::uuid,
    case when tg_table_name = 'organization' then nullif(rec->>'id', '')::uuid end,
    current_org()
  );

  insert into audit_log(actor, action, entity, entity_id, data, org_id)
  values (auth.uid(), lower(tg_op), tg_table_name, eid, rec, v_org);

  if (tg_op = 'DELETE') then return old; else return new; end if;
end $$;
revoke execute on function audit_row() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 5. Uniqueness becomes per-organization
-- ---------------------------------------------------------------------------
-- Every one of these is globally unique today and would reject org B's FIRST
-- order the moment a second business exists: B's PO-2026-0001 would collide
-- with A's, and B's order_seq 1 with A's.
--
-- NOT changed:
--   profiles.username            — it IS the login identity; stays global
--   order_attachments.storage_path — stays global; paths become org-prefixed
--   contact_phones_one_primary   — already org-scoped via contact_id
-- ---------------------------------------------------------------------------
alter table doc_counters drop constraint doc_counters_pkey;
alter table doc_counters add primary key (org_id, scope, year);

alter table purchase_orders drop constraint purchase_orders_order_code_key;
alter table purchase_orders add constraint po_order_code_org_key unique (org_id, order_code);

alter table sales_orders drop constraint sales_orders_invoice_number_key;
alter table sales_orders add constraint so_invoice_number_org_key unique (org_id, invoice_number);

alter table purchase_orders drop constraint po_order_seq_key;
alter table purchase_orders add constraint po_order_seq_org_key unique (org_id, order_seq);

alter table sales_orders drop constraint so_order_seq_key;
alter table sales_orders add constraint so_order_seq_org_key unique (org_id, order_seq);

-- ---------------------------------------------------------------------------
-- 6. Document numbering follows the constraints changed above
-- ---------------------------------------------------------------------------
-- This CANNOT be deferred to a later migration. Step 5 just repointed
-- doc_counters' primary key at (org_id, scope, year), which instantly breaks
-- the old two-argument next_doc_number() — its `on conflict (scope, year)` no
-- longer matches any constraint. Every purchase-order creation and every
-- invoicing action would fail from the moment 0024 landed until the fix
-- arrived. A migration must leave the database in a working state, so the
-- writer moves next to the constraint it depends on. (Caught by dry run.)
-- ---------------------------------------------------------------------------
drop function if exists next_doc_number(text, int);

create or replace function next_doc_number(p_org uuid, p_scope text, p_year int)
returns int language plpgsql security definer set search_path = public as $$
declare v int;
begin
  insert into doc_counters(org_id, scope, year, last_value)
  values (p_org, p_scope, p_year, 1)
  on conflict (org_id, scope, year)
    do update set last_value = doc_counters.last_value + 1
  returning last_value into v;
  return v;
end $$;
revoke execute on function next_doc_number(uuid, text, int) from public, anon, authenticated;

create or replace function po_assign_code()
returns trigger language plpgsql security definer set search_path = public as $$
declare y int := extract(year from coalesce(new.order_date, current_date));
begin
  if new.order_code is null then
    new.order_code := 'PO-' || y || '-' ||
      lpad(next_doc_number(new.org_id, 'po', y)::text, 4, '0');
  end if;
  return new;
end $$;

create or replace function so_status_transition()
returns trigger language plpgsql security definer set search_path = public as $$
declare y int := extract(year from coalesce(new.order_date, current_date));
begin
  if new.status = old.status then
    return new;
  end if;

  if coalesce(current_setting('app.editing', true), '') <> 'on' then
    if not (
         (old.status = 'draft'    and new.status = 'invoiced')
      or (old.status = 'invoiced' and new.status = 'placed')
      or (old.status = 'placed'   and new.status = 'invoiced')
      or (old.status = 'placed'   and new.status = 'closed')
      or (old.status = 'closed'   and new.status = 'placed')
    ) then
      raise exception 'انتقال حالة غير مسموح به لأمر البيع (% ← %)', old.status, new.status;
    end if;
  end if;

  if old.status = 'draft' and new.status = 'invoiced' then
    if not exists (select 1 from sales_order_lines where so_id = new.id) then
      raise exception 'لا يمكن فوترة أمر بيع بدون بنود';
    end if;
    if new.invoice_number is null then
      new.invoice_number := 'SO-' || y || '-' ||
        lpad(next_doc_number(new.org_id, 'so', y)::text, 4, '0');
    end if;
  end if;

  return new;
end $$;

-- ---------------------------------------------------------------------------
-- 6b. order_seq becomes per-organization
-- ---------------------------------------------------------------------------
-- Same reasoning: step 5 made (org_id, order_seq) the unique key, but the
-- column still defaulted to the GLOBAL purchase_order_seq — so org B's first
-- purchase order would read أمر شراء ١٤ instead of ١. Every business counts
-- its own orders from ١. The running number is not year-scoped, so it lives in
-- doc_counters under year 0.
-- ---------------------------------------------------------------------------
create or replace function next_org_seq(p_org uuid, p_scope text)
returns int language plpgsql security definer set search_path = public as $$
declare v int;
begin
  insert into doc_counters(org_id, scope, year, last_value)
  values (p_org, p_scope, 0, 1)
  on conflict (org_id, scope, year)
    do update set last_value = doc_counters.last_value + 1
  returning last_value into v;
  return v;
end $$;
revoke execute on function next_org_seq(uuid, text) from public, anon, authenticated;

create or replace function assign_order_seq()
returns trigger language plpgsql security definer set search_path = public as $$
begin
  if new.order_seq is null then
    new.order_seq := next_org_seq(new.org_id, tg_argv[0]);
  end if;
  return new;
end $$;
revoke execute on function assign_order_seq() from public, anon, authenticated;

-- Seed each existing org's counter at its current maximum so numbering
-- continues unbroken rather than restarting and colliding.
insert into doc_counters(org_id, scope, year, last_value)
select org_id, 'po_seq', 0, max(order_seq) from purchase_orders group by org_id
on conflict (org_id, scope, year) do update set last_value = excluded.last_value;

insert into doc_counters(org_id, scope, year, last_value)
select org_id, 'so_seq', 0, max(order_seq) from sales_orders group by org_id
on conflict (org_id, scope, year) do update set last_value = excluded.last_value;

-- Retire the global sequences in favour of the per-org counters.
alter table purchase_orders alter column order_seq drop default;
alter table sales_orders    alter column order_seq drop default;
drop sequence if exists purchase_order_seq;
drop sequence if exists sales_order_seq;

create trigger trg_po_assign_seq before insert on purchase_orders
  for each row execute function assign_order_seq('po_seq');
create trigger trg_so_assign_seq before insert on sales_orders
  for each row execute function assign_order_seq('so_seq');
