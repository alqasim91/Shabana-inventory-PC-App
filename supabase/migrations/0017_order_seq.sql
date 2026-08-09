-- ============================================================================
-- 0017_order_seq.sql — persistent sequential order numbers
-- ----------------------------------------------------------------------------
-- The human-facing order identifier is now a plain running number per type:
--   أمر شراء ١، أمر شراء ٢ …   /   أمر بيع ١، أمر بيع ٢ …
-- Backed by a dedicated sequence each, assigned at INSERT (so a sales order
-- gets its number the moment it is created — draft or not, invoiced or not),
-- and never changes afterwards. The old order_code / invoice_number columns
-- stay as harmless internal keys.
-- ============================================================================

create sequence if not exists purchase_order_seq;
create sequence if not exists sales_order_seq;

alter table purchase_orders add column if not exists order_seq int;
alter table sales_orders    add column if not exists order_seq int;

-- Backfill existing rows in creation order (oldest = 1).
update purchase_orders p set order_seq = o.rn
  from (select id, row_number() over (order by created_at, id) as rn from purchase_orders) o
  where o.id = p.id;
update sales_orders s set order_seq = o.rn
  from (select id, row_number() over (order by created_at, id) as rn from sales_orders) o
  where o.id = s.id;

-- Continue each sequence past the highest backfilled value.
select setval('purchase_order_seq', coalesce((select max(order_seq) from purchase_orders), 0), true);
select setval('sales_order_seq',    coalesce((select max(order_seq) from sales_orders), 0), true);

-- New orders get their number automatically at insert.
alter table purchase_orders alter column order_seq set default nextval('purchase_order_seq');
alter table sales_orders    alter column order_seq set default nextval('sales_order_seq');

alter table purchase_orders alter column order_seq set not null;
alter table sales_orders    alter column order_seq set not null;

alter table purchase_orders add constraint po_order_seq_key unique (order_seq);
alter table sales_orders    add constraint so_order_seq_key unique (order_seq);

-- Own the sequences so they drop with the column/table.
alter sequence purchase_order_seq owned by purchase_orders.order_seq;
alter sequence sales_order_seq    owned by sales_orders.order_seq;
