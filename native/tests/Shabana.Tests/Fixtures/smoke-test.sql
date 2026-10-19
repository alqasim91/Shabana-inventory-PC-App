-- ============================================================================
-- smoke-test.sql — proves the three core invariants on a throw-away database.
--   cd native-conversion/sqlite && sqlite3 :memory: < smoke-test.sql
-- (loads schema.sql itself; exit status is 1 BECAUSE of the expected rejections).
-- Lines printed as "Runtime error ... (19)" are the
-- EXPECTED rejections; the PASS/FAIL table at the end is the verdict.
-- ============================================================================
.read schema.sql
.bail off
.headers on
.mode box

CREATE TEMP TABLE smoke (name TEXT, ok INTEGER);   -- TEMP: touches nothing in the schema

-- Fixtures: one branch, one item, one client+vendor, a 100 kg PO converted to 50 units.
INSERT INTO sites (id, name_ar)  VALUES ('site-1', 'الفرع الرئيسي');
INSERT INTO items (id, name_ar, unit_type, sale_price_piasters) VALUES ('item-1', 'ماسورة', 'unit', 1250);
INSERT INTO contacts (id, type, name) VALUES ('c-1', 'both', 'عميل ومورد');
INSERT INTO purchase_orders (id, vendor_id, po_type, total_kg_milli, price_per_kg_piasters, total_amount_piasters)
VALUES ('po-1', 'c-1', 'general', 100000, 1000, 100000);                      -- 100 kg @ 10.00 = 1000.00 EGP
INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit)
VALUES ('po-1', 'site-1', 'item-1', 100000, 50000, 'unit');                  -- 50 units into site-1
-- 0) PO invariant converted_kg <= total_kg: one more milli-kg is refused.
.print >>> expecting 1 error: PO_CONVERSION_EXCEEDS_REMAINING
INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit)
VALUES ('po-1', 'site-1', 'item-1', 1, 1, 'unit');
INSERT INTO smoke VALUES ('PO converted_kg <= total_kg: over-conversion refused',
  (SELECT remaining_kg_milli FROM v_po_kg_progress WHERE po_id = 'po-1') = 0
  AND (SELECT COUNT(*) FROM po_conversions) = 1);

-- 1) APPEND-ONLY: UPDATE and DELETE on the ledger are refused.
.print >>> expecting 2 errors: LEDGER_READONLY
UPDATE stock_movements SET qty_delta_milli = 999999;
DELETE FROM stock_movements;
INSERT INTO smoke VALUES ('append-only: stock_movements UPDATE/DELETE blocked',
  (SELECT COUNT(*) = 1 AND SUM(qty_delta_milli) = 50000 FROM stock_movements));

-- A sales order: 10 units x 12.50 = 125.00 EGP, invoiced then placed (stock 50 -> 40).
INSERT INTO sales_orders (id, site_id, client_id) VALUES ('so-1', 'site-1', 'c-1');
INSERT INTO sales_order_lines (so_id, item_id, qty_milli, unit_price_piasters) VALUES ('so-1', 'item-1', 10000, 1250);
UPDATE sales_orders SET status = 'invoiced' WHERE id = 'so-1';
UPDATE sales_orders SET status = 'placed'   WHERE id = 'so-1';
INSERT INTO smoke VALUES ('stock view: 50 - 10 = 40 units',
  (SELECT qty_milli FROM v_stock_balance WHERE site_id = 'site-1' AND item_id = 'item-1') = 40000);

-- 2) OVERPAYMENT BLOCK: pay 100.00 cash, then try 25.01 (only 25.00 is left).
INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'so-1', 10000, 'cash');
.print >>> expecting 1 error: PAYMENT_OVERPAY
INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'so-1', 2501, 'cash');
INSERT INTO smoke VALUES ('overpayment blocked (still exactly 1 payment)', (SELECT COUNT(*) FROM payments) = 1);

-- 3) BALANCE VIEWS return the expected sums.
INSERT INTO smoke VALUES ('v_so_balance = 25.00 EGP outstanding',
  (SELECT balance_piasters FROM v_so_balance WHERE so_id = 'so-1') = 2500);
INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'so-1', 2500, 'cash');
INSERT INTO smoke VALUES ('SO auto-closed when collected == total',
  (SELECT status FROM sales_orders WHERE id = 'so-1') = 'closed');
INSERT INTO smoke VALUES ('v_cash_drawer_balance = 125.00 EGP',
  (SELECT balance_piasters FROM v_cash_drawer_balance WHERE site_id = 'site-1') = 12500);
INSERT INTO smoke VALUES ('v_contact_balance = -1000.00 (we owe vendor)',
  (SELECT balance_piasters FROM v_contact_balance WHERE contact_id = 'c-1') = -100000);
INSERT INTO smoke VALUES ('audit_log captured the payments',
  (SELECT COUNT(*) FROM audit_log WHERE entity = 'payments' AND action = 'insert') = 2);

-- 4) password_hash format: argon2id accepted, garbage rejected.
INSERT INTO users (id, username, full_name, role, password_hash, must_change_password)
VALUES ('u-ar', 'argon.user', 'argon', 'staff', '$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHQ$aGFzaGhhc2g', 0);
.print >>> expecting 1 error: CHECK constraint (garbage password_hash)
INSERT INTO users (id, username, full_name, role, password_hash, must_change_password)
VALUES ('u-bad', 'bad.user', 'bad', 'staff', 'plaintext-garbage', 0);
INSERT INTO smoke VALUES ('password_hash: argon2id accepted, garbage rejected',
  (SELECT COUNT(*) FROM users WHERE id = 'u-ar') = 1
  AND (SELECT COUNT(*) FROM users WHERE id = 'u-bad') = 0);

-- 4b) L-22: purchase-order editing is admin-only; the manager preset lacks purchases.edit.
INSERT INTO smoke VALUES ('manager preset lacks purchases.edit; admin has it',
  (SELECT COUNT(*) FROM role_presets WHERE role = 'manager' AND perm = 'purchases.edit') = 0
  AND (SELECT COUNT(*) FROM role_presets WHERE role = 'admin' AND perm = 'purchases.edit') = 1);

-- 5) AMENDMENT S-2: a back-dated cash payment's drawer row is dated by paid_at.
INSERT INTO payments (id, parent_type, parent_id, amount_piasters, method, site_id, paid_at)
VALUES ('pay-back', 'po', 'po-1', 500, 'cash', 'site-1', '2026-01-05T00:00:00.000Z');
INSERT INTO smoke VALUES ('S-2: back-dated payment cash row created_at = paid_at',
  (SELECT created_at FROM cash_movements WHERE source_type = 'payment' AND source_id = 'pay-back') = '2026-01-05T00:00:00.000Z');

.print
.print ===== SMOKE RESULT =====
SELECT name, CASE ok WHEN 1 THEN 'PASS' ELSE 'FAIL' END AS result FROM smoke;
SELECT CASE WHEN COUNT(*) = 0 THEN 'ALL CHECKS PASSED' ELSE COUNT(*) || ' CHECK(S) FAILED' END AS verdict
  FROM smoke WHERE ok <> 1;
