-- ============================================================================
-- 0007_created_by_defaults.sql — auto-populate created_by from the JWT user
-- ----------------------------------------------------------------------------
-- Client inserts previously left created_by NULL (the app didn't send it), so
-- the "بواسطة {name}" attribution on PO conversions was always blank and the
-- audit trail on directly-inserted rows was lost. Defaulting the column to
-- auth.uid() populates it for every authenticated client insert.
--
-- This also improves the trigger-derived ledger rows: the SECURITY DEFINER
-- triggers copy the parent row's created_by onto the stock/cash movements they
-- write (e.g. po_conversion_apply → stock_movements.created_by := new.created_by),
-- so a populated parent now flows an accurate actor id all the way through.
-- Triggers that set created_by explicitly are unaffected (an explicit value
-- always wins over a column default); inside a SECURITY DEFINER function
-- auth.uid() is NULL anyway, so nothing regresses there.
-- ============================================================================

alter table purchase_orders  alter column created_by set default auth.uid();
alter table po_conversions   alter column created_by set default auth.uid();
alter table sales_orders     alter column created_by set default auth.uid();
alter table payments         alter column created_by set default auth.uid();
alter table stock_transfers  alter column created_by set default auth.uid();
alter table stock_movements  alter column created_by set default auth.uid();
alter table cash_movements   alter column created_by set default auth.uid();
