-- ============================================================================
-- 0005_rls.sql — Row Level Security (rule 9)
-- ============================================================================
-- Tiers (via helpers in 0003):
--   is_admin()             → admin only
--   is_manager_or_admin()  → manager + admin  (the "can mutate business data" tier)
--   authenticated          → any signed-in user (all three roles can READ everything)
--
-- Ledger tables (stock_movements, cash_movements) are written almost entirely
-- by SECURITY DEFINER triggers, which bypass RLS. The insert policies below only
-- govern the few DIRECT writes the app makes: manual stock adjustments
-- (manager+) and manual cash drawer moves (admin). UPDATE/DELETE are additionally
-- blocked by the append-only triggers in 0004.
--
-- Everything is denied by default once RLS is enabled; each policy re-grants.
-- ============================================================================

alter table sites                   enable row level security;
alter table profiles                enable row level security;
alter table contacts                enable row level security;
alter table contact_phones          enable row level security;
alter table contact_payment_methods enable row level security;
alter table items                   enable row level security;
alter table purchase_orders         enable row level security;
alter table po_conversions          enable row level security;
alter table sales_orders            enable row level security;
alter table sales_order_lines       enable row level security;
alter table payments                enable row level security;
alter table stock_movements         enable row level security;
alter table stock_transfers         enable row level security;
alter table cash_movements          enable row level security;
alter table doc_counters            enable row level security;

-- ---------------------------------------------------------------------------
-- sites — everyone reads; only admin manages (rule 9: sites are admin-only).
-- ---------------------------------------------------------------------------
create policy sites_read   on sites for select to authenticated using (true);
create policy sites_admin  on sites for all    to authenticated
  using (is_admin()) with check (is_admin());

-- ---------------------------------------------------------------------------
-- profiles — everyone reads (to show names/roles); only admin manages users.
-- A user may update their OWN profile's non-role fields is out of scope here;
-- keep it simple: admin manages all, users read all.
-- ---------------------------------------------------------------------------
create policy profiles_read  on profiles for select to authenticated using (true);
create policy profiles_admin on profiles for all    to authenticated
  using (is_admin()) with check (is_admin());

-- ---------------------------------------------------------------------------
-- contacts (+ phones + payment methods) — read all; manager+ create/edit;
-- admin deletes.
-- ---------------------------------------------------------------------------
create policy contacts_read   on contacts for select to authenticated using (true);
create policy contacts_write  on contacts for insert to authenticated with check (is_manager_or_admin());
create policy contacts_update on contacts for update to authenticated
  using (is_manager_or_admin()) with check (is_manager_or_admin());
create policy contacts_delete on contacts for delete to authenticated using (is_admin());

create policy phones_read   on contact_phones for select to authenticated using (true);
create policy phones_write  on contact_phones for insert to authenticated with check (is_manager_or_admin());
create policy phones_update on contact_phones for update to authenticated
  using (is_manager_or_admin()) with check (is_manager_or_admin());
create policy phones_delete on contact_phones for delete to authenticated using (is_manager_or_admin());

create policy pm_read   on contact_payment_methods for select to authenticated using (true);
create policy pm_write  on contact_payment_methods for insert to authenticated with check (is_manager_or_admin());
create policy pm_update on contact_payment_methods for update to authenticated
  using (is_manager_or_admin()) with check (is_manager_or_admin());
create policy pm_delete on contact_payment_methods for delete to authenticated using (is_manager_or_admin());

-- ---------------------------------------------------------------------------
-- items — read all; manager+ create/edit (pricing & thresholds); admin deletes.
-- ---------------------------------------------------------------------------
create policy items_read   on items for select to authenticated using (true);
create policy items_write  on items for insert to authenticated with check (is_manager_or_admin());
create policy items_update on items for update to authenticated
  using (is_manager_or_admin()) with check (is_manager_or_admin());
create policy items_delete on items for delete to authenticated using (is_admin());

-- ---------------------------------------------------------------------------
-- purchase_orders + conversions — read all; manager+ create/convert; admin
-- deletes (reversals). PO editing guarded further by triggers in 0004.
-- ---------------------------------------------------------------------------
create policy po_read   on purchase_orders for select to authenticated using (true);
create policy po_write  on purchase_orders for insert to authenticated with check (is_manager_or_admin());
create policy po_update on purchase_orders for update to authenticated
  using (is_manager_or_admin()) with check (is_manager_or_admin());
create policy po_delete on purchase_orders for delete to authenticated using (is_admin());

create policy conv_read   on po_conversions for select to authenticated using (true);
create policy conv_write  on po_conversions for insert to authenticated with check (is_manager_or_admin());
create policy conv_delete on po_conversions for delete to authenticated using (is_admin());
-- (no update policy: a conversion is corrected by delete + re-add, keeping the
--  stock ledger honest)

-- ---------------------------------------------------------------------------
-- sales_orders — read all. STAFF may create drafts and edit them; advancing the
-- lifecycle (invoice/place/cancel) is manager+ (rule 9). Delete is admin only.
-- ---------------------------------------------------------------------------
create policy so_read on sales_orders for select to authenticated using (true);
-- insert: managers freely; staff only as a draft
create policy so_insert on sales_orders for insert to authenticated
  with check (is_manager_or_admin() or status = 'draft');
-- update: managers freely; staff only while it is (and stays) a draft
create policy so_update on sales_orders for update to authenticated
  using (is_manager_or_admin() or status = 'draft')
  with check (is_manager_or_admin() or status = 'draft');
create policy so_delete on sales_orders for delete to authenticated using (is_admin());

-- sales_order_lines — mirror the parent: staff can build a draft's lines.
create policy sol_read on sales_order_lines for select to authenticated using (true);
create policy sol_write on sales_order_lines for insert to authenticated
  with check (is_manager_or_admin() or exists (
    select 1 from sales_orders so where so.id = so_id and so.status = 'draft'));
create policy sol_update on sales_order_lines for update to authenticated
  using (is_manager_or_admin() or exists (
    select 1 from sales_orders so where so.id = so_id and so.status = 'draft'))
  with check (is_manager_or_admin() or exists (
    select 1 from sales_orders so where so.id = so_id and so.status = 'draft'));
create policy sol_delete on sales_order_lines for delete to authenticated
  using (is_manager_or_admin() or exists (
    select 1 from sales_orders so where so.id = so_id and so.status = 'draft'));

-- ---------------------------------------------------------------------------
-- payments — read all; ANY role may record a payment (rule 9: staff record
-- payments). Overpayment/site rules enforced by triggers. Only admin may
-- delete/adjust a recorded payment.
-- ---------------------------------------------------------------------------
create policy payments_read   on payments for select to authenticated using (true);
create policy payments_write  on payments for insert to authenticated with check (true);
create policy payments_delete on payments for delete to authenticated using (is_admin());
-- (no update policy: payments are corrected by admin delete + re-add)

-- ---------------------------------------------------------------------------
-- stock_movements — read all. Direct inserts are manual adjustments (manager+);
-- everything else comes through SECURITY DEFINER triggers. No update/delete
-- (append-only trigger also enforces this).
-- ---------------------------------------------------------------------------
create policy sm_read  on stock_movements for select to authenticated using (true);
create policy sm_write on stock_movements for insert to authenticated
  with check (is_manager_or_admin() and source_type = 'adjustment');

-- stock_transfers — read all; manager+ create (rule 3). No edit/delete.
create policy transfer_read  on stock_transfers for select to authenticated using (true);
create policy transfer_write on stock_transfers for insert to authenticated
  with check (is_manager_or_admin());

-- ---------------------------------------------------------------------------
-- cash_movements — read all. Direct inserts are MANUAL drawer moves and are
-- admin-only (rule 9: cash adjustments). Payment-driven rows arrive via the
-- SECURITY DEFINER payment trigger. No update/delete.
-- ---------------------------------------------------------------------------
create policy cm_read  on cash_movements for select to authenticated using (true);
create policy cm_write on cash_movements for insert to authenticated
  with check (is_admin() and source_type = 'manual');

-- ---------------------------------------------------------------------------
-- doc_counters — internal; touched only by SECURITY DEFINER numbering function.
-- No policies → no direct client access (numbering can't be tampered with).
-- ---------------------------------------------------------------------------
