-- ============================================================================
-- 0026_org_referential_integrity.sql — close the SECURITY DEFINER hole
-- ----------------------------------------------------------------------------
-- Part 3 of 5 (see multi-tenant-plan.md). This is the security-critical one.
--
-- THE HOLE: 12 RPCs are SECURITY DEFINER, so they bypass RLS by design, and
-- every one takes caller-supplied UUIDs. A user in org B can pass org A's
-- po_id to po_overpay() and move org A's money — RLS never sees the read.
--
-- ----------------------------------------------------------------------------
-- DESIGN NOTE — why this differs from the plan
-- ----------------------------------------------------------------------------
-- The plan said: add an assert_org() call inside each of the 12 RPCs. This
-- migration enforces the same invariant at the DATA layer instead, as a
-- referential rule: a row may never point at a row belonging to a different
-- organization. Three reasons:
--
--   1. COVERAGE. Guards inside 12 functions protect exactly those 12 call
--      paths. A rule on the tables protects every path — the 12 RPCs, direct
--      writes, and every RPC written in the future by someone who forgets.
--   2. RISK. Those 12 functions carry the verified overpay/credit/cash-
--      direction arithmetic. Rewriting them wholesale to insert one guard line
--      risks regressions in money logic that is already proven correct. These
--      triggers add the check without touching a line of that arithmetic.
--   3. AUDITABILITY. One guard function plus a declarative table of 30 edges
--      beats 12 re-derived function bodies at review time.
--
-- SUFFICIENCY. Every one of the 12 RPCs writes (insert or update) — none is a
-- pure read. A blocked write aborts the whole transaction, so the RPC returns
-- nothing. Enforcing at write time therefore stops cross-org RPC abuse
-- completely; there is no path that leaks data before the write is attempted.
--
-- This is the multi-tenant analogue of a foreign key: FKs say "this row must
-- exist"; these say "and it must belong to your business".
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. assert_org — explicit guard, for use in future RPCs
-- ---------------------------------------------------------------------------
create or replace function assert_org(p_org_id uuid)
returns void language plpgsql stable security definer set search_path = public as $$
begin
  if p_org_id is distinct from current_org() then
    raise exception 'غير مصرح: بيانات خارج نطاق المنشأة' using errcode = '42501';
  end if;
end $$;
revoke execute on function assert_org(uuid) from public, anon;
grant execute on function assert_org(uuid) to authenticated;

-- ---------------------------------------------------------------------------
-- 2. Generic cross-reference guard
-- ---------------------------------------------------------------------------
-- tg_argv[0] = column on this row holding the reference
-- tg_argv[1] = table that column points at
-- A NULL reference is allowed (nullable FKs like payments.site_id).
-- ---------------------------------------------------------------------------
create or replace function assert_ref_same_org()
returns trigger language plpgsql security definer set search_path = public as $$
declare
  v_col     text  := tg_argv[0];
  v_tbl     text  := tg_argv[1];
  j         jsonb := to_jsonb(new);
  v_ref     uuid;
  v_my_org  uuid;
  v_ref_org uuid;
begin
  v_ref := nullif(j ->> v_col, '')::uuid;
  if v_ref is null then
    return new;
  end if;

  v_my_org := nullif(j ->> 'org_id', '')::uuid;
  execute format('select org_id from public.%I where id = $1', v_tbl)
    into v_ref_org using v_ref;

  if v_ref_org is distinct from v_my_org then
    raise exception 'غير مصرح: % يشير إلى بيانات خارج نطاق المنشأة (%)',
      tg_table_name, v_col using errcode = '42501';
  end if;
  return new;
end $$;
revoke execute on function assert_ref_same_org() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 3. Wire it to all 28 tenant-to-tenant foreign keys
-- ---------------------------------------------------------------------------
-- Read as a table: (child, column, parent). Enumerated from pg_constraint, not
-- from memory, so nothing is missed.
-- ---------------------------------------------------------------------------
do $$
declare
  e text[];
  edges text[][] := array[
    ['cash_movements',          'site_id',                   'sites'],
    ['client_credits',          'contact_id',                'contacts'],
    ['client_credits',          'site_id',                   'sites'],
    ['contact_payment_methods', 'contact_id',                'contacts'],
    ['contact_phones',          'contact_id',                'contacts'],
    ['payments',                'contact_payment_method_id', 'contact_payment_methods'],
    ['payments',                'site_id',                   'sites'],
    ['po_conversions',          'item_id',                   'items'],
    ['po_conversions',          'po_id',                     'purchase_orders'],
    ['po_conversions',          'site_id',                   'sites'],
    ['po_line_conversions',     'item_id',                   'items'],
    ['po_line_conversions',     'po_line_id',                'po_lines'],
    ['po_line_conversions',     'site_id',                   'sites'],
    ['po_lines',                'item_id',                   'items'],
    ['po_lines',                'po_id',                     'purchase_orders'],
    ['po_lines',                'site_id',                   'sites'],
    ['purchase_orders',         'vendor_id',                 'contacts'],
    ['sales_order_lines',       'item_id',                   'items'],
    ['sales_order_lines',       'so_id',                     'sales_orders'],
    ['sales_orders',            'client_id',                 'contacts'],
    ['sales_orders',            'site_id',                   'sites'],
    ['stock_movements',         'item_id',                   'items'],
    ['stock_movements',         'site_id',                   'sites'],
    ['stock_transfers',         'from_site',                 'sites'],
    ['stock_transfers',         'item_id',                   'items'],
    ['stock_transfers',         'to_site',                   'sites'],
    ['vendor_credits',          'contact_id',                'contacts'],
    ['vendor_credits',          'site_id',                   'sites']
  ];
  i int;
begin
  for i in 1 .. array_length(edges, 1) loop
    e := edges[i:i][1:3];
    execute format(
      'create trigger %I before insert or update on %I
         for each row execute function assert_ref_same_org(%L, %L)',
      'trg_org_ref_' || edges[i][1] || '_' || edges[i][2],
      edges[i][1], edges[i][2], edges[i][3]);
  end loop;
end $$;

-- ---------------------------------------------------------------------------
-- 4. The two polymorphic references (no FK exists, so no generic wiring)
-- ---------------------------------------------------------------------------
create or replace function assert_payment_parent_org()
returns trigger language plpgsql security definer set search_path = public as $$
declare v_org uuid;
begin
  if new.parent_type = 'po'::payment_parent then
    select org_id into v_org from purchase_orders where id = new.parent_id;
  else
    select org_id into v_org from sales_orders   where id = new.parent_id;
  end if;
  if v_org is distinct from new.org_id then
    raise exception 'غير مصرح: الدفعة تشير إلى أمر خارج نطاق المنشأة'
      using errcode = '42501';
  end if;
  return new;
end $$;
revoke execute on function assert_payment_parent_org() from public, anon, authenticated;

create trigger trg_org_ref_payments_parent before insert or update on payments
  for each row execute function assert_payment_parent_org();

create or replace function assert_attachment_parent_org()
returns trigger language plpgsql security definer set search_path = public as $$
declare v_org uuid;
begin
  if new.order_type = 'purchase'::order_doc_type then
    select org_id into v_org from purchase_orders where id = new.order_id;
  else
    select org_id into v_org from sales_orders   where id = new.order_id;
  end if;
  if v_org is distinct from new.org_id then
    raise exception 'غير مصرح: المرفق يشير إلى أمر خارج نطاق المنشأة'
      using errcode = '42501';
  end if;
  return new;
end $$;
revoke execute on function assert_attachment_parent_org() from public, anon, authenticated;

create trigger trg_org_ref_attachments_parent before insert or update on order_attachments
  for each row execute function assert_attachment_parent_org();

-- ---------------------------------------------------------------------------
-- NOTE: per-organization document numbering (next_doc_number, po_assign_code,
-- so_status_transition, order_seq) lives in 0024, NOT here. It has to: 0024
-- changes doc_counters' primary key to (org_id, scope, year), which instantly
-- breaks the old two-argument next_doc_number and therefore every PO creation
-- and every invoicing action. A migration must not leave the database in a
-- broken state, so the writer moved next to the constraint it depends on.
-- Caught by the 0026 dry run.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- Post-flight: every tenant-to-tenant FK must carry a guard trigger.
-- ---------------------------------------------------------------------------
do $$
declare n_edges int; n_trig int;
begin
  select count(*) into n_trig from pg_trigger
   where tgname like 'trg_org_ref_%' and not tgisinternal;
  select count(*) into n_edges from pg_constraint c
   where c.contype='f' and c.connamespace='public'::regnamespace
     and c.confrelid::regclass::text in ('sites','contacts','items','purchase_orders',
         'sales_orders','po_lines','contact_payment_methods');
  if n_trig < n_edges + 2 then
    raise exception 'ABORT: % guard triggers for % FK edges (+2 polymorphic)', n_trig, n_edges;
  end if;
end $$;

-- ============================================================================
-- (end of 0026)
-- ============================================================================
