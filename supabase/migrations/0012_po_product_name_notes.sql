-- 0012 — Purchase-order product name + notes
-- Two optional free-text columns captured on the new-PO form. Purely descriptive:
-- they don't affect conversions, stock, or pricing.

alter table purchase_orders
  add column product_name text,
  add column notes text;

comment on column purchase_orders.product_name is 'Free-text name/description of the purchased material (e.g. "ألومنيوم خام"). Optional.';
comment on column purchase_orders.notes is 'Free-text notes on the purchase order. Optional.';
