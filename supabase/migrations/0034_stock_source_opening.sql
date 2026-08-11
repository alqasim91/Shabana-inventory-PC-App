-- ============================================================================
-- 0034_stock_source_opening.sql — a fifth reason a stock movement exists
-- ----------------------------------------------------------------------------
-- A business adopting this app already owns stock. Until now the only way to
-- get that stock into the ledger was a تسوية per item per فرع, with the
-- operator doing the arithmetic — and the resulting history reads as if the
-- shop had corrected its books on day one rather than simply opened them.
--
-- 'opening' is that starting count, kept distinct from 'adjustment' so the
-- item ledger can say رصيد افتتاحي and so a migration is never mistaken for
-- a correction when someone reads the history a year from now.
--
-- ALONE IN ITS OWN FILE ON PURPOSE. Postgres will not let a new enum value be
-- *used* in the same transaction that adds it, and 0035 needs the literal
-- 'opening' in a trigger's WHEN clause. Run this one first, on its own.
-- ============================================================================

alter type stock_source add value if not exists 'opening';
