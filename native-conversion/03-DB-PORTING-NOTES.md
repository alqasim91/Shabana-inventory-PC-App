# 03-DB-PORTING-NOTES.md — PostgreSQL/Supabase → SQLite (native Windows edition)

Deliverables of this step (documentation + SQL only; nothing else in the repo was touched, nothing committed):

| File | What it is |
|---|---|
| `sqlite/schema.sql` | Complete SQLite schema = final state of web migrations **0001–0044** (+ the data-bearing parts of PC-only **0100/0101**): 36 tables, 13 views, 108 triggers, 61 indexes. Loads with `sqlite3 :memory: < schema.sql`. |
| `sqlite/smoke-test.sql` | Throw-away proof: append-only blocks UPDATE/DELETE, overpayment blocked, balance views return the expected sums. |
| `sqlite/seed.sql` | Minimal first-run data: default branch, business identity, one placeholder admin **without a password**, default expense categories. |
| `03-DB-PORTING-NOTES.md` | This file. |

## 0. What was verified, and how (and what was not)

* **Source of truth.** All 43 web migrations were read in full (there is no `0038`; numbering jumps 0037→0039). Then `platform-bootstrap.sql` + `pc-prelude.sql` + migrations **0001–0044** (using the PC variant of `0032`, whose only change is the empty-database guard) were **applied to a real PostgreSQL 16.15** instance. The live catalog was used to cross-check this port: 273 columns, 82 live policies, 104 triggers, 84 `public` functions (+9 PC-only) — all accounted for below.
* **Column parity** (script): every PG column maps to a SQLite column (ignoring the unit suffix) except the intentional differences (`org_id`, `slug`, `profiles.user_id→users.id`, new auth columns).
* **Behavioural parity** (script, 65 assertions, run with `recursive_triggers` OFF and ON): PO conversion limits, stock/cash floors, lifecycle, discount rounding, overpayment, auto-close/re-open, transfers, credits, expenses, itemized POs, cascade deletes, audit contents, STRICT/CHECK rejections. `PRAGMA integrity_check` and `foreign_key_check` clean. The recurring-expense query in §10 was compared row-for-row against `expense_due_list()` on PostgreSQL.
* **Randomised stress test** (script): 4 seeds × 6,000 random operations (PO/conversion/SO/line/discount/invoice/place/cancel/pay/delete-payment/transfer/adjust/manual-cash; ~850 of each 6,000 are rejected by design; credit wallets and expenses are covered by the scenario script instead) followed by 14 ledger invariants — no negative stock or drawer, no overpaid order, converted ≤ total, closed ⇒ fully paid, `total = subtotal − discount`, `subtotal = Σ lines`, cash ledger = cash payments, net sale movements = lines of placed/closed orders, numbering assigned, FK check. **0 violations.**
* **Not verified:** nothing was run on Windows; Argon2id/PBKDF2 hashing is the app's job (the DB only validates the encoded prefix); performance with years of data (indexes mirror the web ones — run `PRAGMA optimize` on close).
* **PC repo is behind the web repo.** `Shabana-inventory-PC-App/supabase/migrations/` stops at **0035** (+0100/0101). Migrations **0036–0044** (delete_organization, `accountant` role, opening balances, financial summary, expenses…) exist only in `Shabana-Inventory`. This port follows **web 0044**, so the native app gets those features; a PC database still on 0035 needs those tables empty-created (they are, in `schema.sql`) and has no data for them to migrate.
* **Three defects in the web migrations were found while doing this** (§7). They are *fixed* in the SQLite port; they still exist in the PostgreSQL migrations.

## 1. Decisions at a glance

| Topic | Decision | Why |
|---|---|---|
| Tenancy | **Single business per DB file.** `org_id`, `organization.slug`, `platform_admins`, `provision_organization`, `delete_organization`, `current_org()`, the 34 `assert_ref_same_org` edges and all `org_id = current_org()` policy clauses are dropped. `organization` → one-row `business_settings`. | `PC-DIVERGENCE.md` ("a freshly migrated PC database always has exactly one organization") and `multi-tenant-plan.md` principle 4 ("inert until org #2"). A desktop file has one owner; isolation is the OS file. |
| Money | `INTEGER` **piasters** (EGP×100), suffix `_piasters`. Never REAL. | Exact; `SUM` is exact; fits `NUMERIC(12,2)` (≤ 999,999,999,999). |
| Quantities / KG | `INTEGER` **milli-units**, suffix `_milli`. | `NUMERIC(12,3)` → exact thousandths (gram precision on KG). |
| Discount value | `discount_value_x100` (piasters or basis points). | One scaling rule covers both `amount` and `percent`. |
| IDs | `TEXT` UUID v4 (36 chars), DB-side default expression. PG UUIDs import unchanged. | Keeps every FK and `source_id` valid; triggers need to mint ids. |
| Time | `TEXT` `YYYY-MM-DDTHH:MM:SS.sssZ` (UTC) with a round-trip CHECK on every column; dates `YYYY-MM-DD` local. | String order = time order; any other shape is rejected. |
| Enums | `TEXT` + `CHECK (… IN (…))` (12 PG enum types + 4 text-check lists). | SQLite has no enums. Adding a value = table rebuild (see §6). |
| Append-only | `BEFORE UPDATE`/`BEFORE DELETE` triggers → `RAISE(ABORT)`; 9 tables (6 from web + `audit_log`, `auth_events`, `stock_transfers`). | Requirement; mirrors `block_mutation()`. |
| Authorization | **App layer.** Role/permission/branch rules are not in the DB; data invariants are. | SQLite has no RLS/roles/JWT. §5 lists every rule. |
| Who is acting | One-row `app_context(actor_user_id, editing)` written **inside** each write transaction. | Replaces `auth.uid()` and `set_config('app.editing')`; rolls back with the tx; writers are serialised by `BEGIN IMMEDIATE`. |
| Error reporting | `RAISE(ABORT, 'CODE\|Arabic text')`. | SQLite cannot interpolate numbers into messages; the service maps `CODE` → `labels.ts` and re-queries figures. Code list in §9. |
| Auth | `users` table (merges `auth.users`+`profiles`): Argon2id (PBKDF2-SHA256 fallback) PHC-style string, `must_change_password`, `failed_attempts`, `locked_until`; `user_sessions`; `auth_events`. `user_permissions`, `user_sites`, `permission_keys` kept; `role_presets` replaces `role_preset_permissions()`. | Requirement; same permission model as web 0031–0042. |
| Attachments | Bytes in `attachment_blobs` (BLOB, same single-file backup, like PC 0101). Files-on-disk is an acceptable alternative (drop the table). | One-file backup/restore, transactional with the metadata. |
| Audit | `audit_log` kept (38 generated triggers, `json_object` snapshots, actor from `app_context`). Secrets never snapshotted. | Web has it (0011+). |
| Calendar day | Business-local, converted to a UTC `[start,end)` pair by the service. | Web cut days at UTC midnight (session timezone); a Cairo shop closes at local midnight. |

Connection rules: `PRAGMA foreign_keys=ON` on **every** connection; `journal_mode=WAL`; `synchronous=NORMAL` (or `FULL`); `busy_timeout`; use `BEGIN IMMEDIATE` for writes; keep the file on a **local** disk (WAL does not work over network shares). `recursive_triggers` may stay OFF — every nested UPDATE in the schema is guarded by `UPDATE OF <col>` / `WHEN`.

## 2. Units, rounding, ranges

* `1234.56 EGP` ⇄ `123456`; `2.5 kg` ⇄ `2500`. Convert only at the UI edge (parse decimal text, never a float).
* **Line total** = `(qty_milli * unit_price_piasters + 500) / 1000` (round-half-up in integers) — equals PG `round(qty*unit_price, 2)` for non-negative values.
* **Percent discount** = `(subtotal_piasters * bp + 5000) / 10000` (equals `round(subtotal*pct/100, 2)`); `amount` is taken as-is; clamped to the subtotal.
* **Tolerances disappear.** Web used `+0.005` / `+0.0005` slack because of NUMERIC rounding; integers need none (`>` means `>`).
* **Overflow guard.** SQLite silently turns an overflowing integer product into REAL. Therefore line `qty_milli ≤ 1,000,000,000` (1,000,000.000) and `unit_price ≤ 1,000,000,000` piasters (10,000,000.00 EGP): product ≤ 1e18 < 9.22e18. Stored money totals ≤ 999,999,999,999 (= `NUMERIC(12,2)` max). These are tighter than PG on lines only; the app should validate before insert. When valuing stock (`qty × cost`) round **per row** (see C8) — never `SUM(q) * cost`.
* **Floats:** the only REAL in the project is the ordering ratio in C7. STRICT tables reject a fractional value in an INTEGER column (verified).
* **Dates vs instants.** `order_date`, `conversion_date`, `occurred_on`, `as_of`, `expense_date`, `due_date` are *dates* (local calendar). `created_at`, `paid_at`, `voided_at` are *instants* (UTC). "Collections on day D" = `paid_at` within the UTC bounds of local day D (C4); the web used `paid_at::date` in UTC.

## 3. Table-by-table mapping (32 PG tables → 36 SQLite tables)

| PostgreSQL table | SQLite table | Column / constraint changes |
|---|---|---|
| `sites` | `sites` | `id` uuid→TEXT (v4 default expr); `active` boolean→INTEGER 0/1 CHECK; `created_at` timestamptz→TEXT UTC; `org_id` dropped. NEW: `CHECK(trim(name_ar)<>'')`. |
| `profiles` (+ `auth.users`) | `users` | PK `user_id`(→auth.users) becomes `id`; role enum `app_role` (4 values incl. `accountant`)→CHECK; `username` UNIQUE + format CHECK (`^[a-z0-9._-]+$`, same as `pc_create_user`); `active`,`all_sites`→INTEGER. NEW columns replacing auth.users: `email`(optional), `password_hash` (Argon2id / PBKDF2 / verify-only bcrypt, S-1), `password_changed_at`, `must_change_password`, `failed_attempts`, `locked_until`, `last_login_at`, `updated_at`. `org_id` dropped. |
| `contacts` | `contacts` | `type` enum→CHECK; `created_by`→FK `users`; `org_id` dropped. NEW: name non-blank CHECK. |
| `contact_phones` | `contact_phones` | `is_primary` boolean→INTEGER; partial unique index `contact_phones_one_primary` kept (`WHERE is_primary=1`). "Primary is mandatory" is an app rule in the web schema too. |
| `contact_payment_methods` | `contact_payment_methods` | `method` enum→CHECK of the 3 values that can satisfy `contact_pm_shape` (the enum's 4th value `credit` could never pass that constraint). Shape CHECK kept verbatim. |
| `items` | `items` | `low_stock_threshold` numeric(12,3)→`low_stock_threshold_milli`; `sale_price`/`cost_price` numeric(12,2)→`sale_price_piasters`/`cost_price_piasters` (≤ 1e9 piasters, see range note §2); `unit_type` enum→CHECK. |
| `purchase_orders` | `purchase_orders` | `total_kg`→`total_kg_milli`, `price_per_kg`→`price_per_kg_piasters`, `total_amount`→`total_amount_piasters`; `po_type`,`status` enums→CHECK; `order_date` date→TEXT; `order_seq` NOT NULL + `UNIQUE(org,seq)` → `INTEGER NOT NULL DEFAULT 0` + partial unique index `WHERE order_seq>0` (trigger assigns; re-SELECT after insert); `order_code UNIQUE(org,code)`→`UNIQUE`. `po_type_fields_chk` and the `>0`/`>=0` checks kept. `org_id` dropped. |
| `po_conversions` | `po_conversions` | `kg_consumed`,`output_qty`→`…_milli`; `output_unit` enum→CHECK; ON DELETE CASCADE from PO kept (SQLite fires the reversal trigger on cascade too). UPDATE now blocked (hardening). |
| `po_lines` | `po_lines` | `qty`→`qty_milli`; `unit_price`→`unit_price_piasters`; `line_total` (plain stored, **nullable** in PG after 0020 `drop expression`)→`line_total_piasters NOT NULL`; legacy `site_id` kept nullable/unused. |
| `po_line_conversions` | `po_line_conversions` | `qty`→`qty_milli`; denormalised `item_id`/`po_id` kept nullable, `po_id` still has no FK (0021 reason: must survive cascades); filled by `trg_plc_apply` instead of a BEFORE-INSERT assignment. |
| `sales_orders` | `sales_orders` | `subtotal`,`discount_amount`,`total_amount`→`…_piasters`; `discount_value`→`discount_value_x100` (piasters for `amount`, basis points for `percent`); **`discount_amount_piasters` and `total_amount_piasters` are now GENERATED STORED** (PG: BEFORE trigger); `status`,`discount_type` enums→CHECK; `order_seq`/`invoice_number` as for POs; `so_discount_*` CHECKs kept (+ `discount_type<>'none' OR value=0`, replacing the trigger's silent zeroing). |
| `sales_order_lines` | `sales_order_lines` | `qty`→`qty_milli`, `unit_price`→`unit_price_piasters`; `line_total` GENERATED `round(qty*price,2)` → GENERATED `(qty_milli*price+500)/1000`. |
| `payments` | `payments` | `amount`→`amount_piasters`; `parent_type`,`method` enums→CHECK (`method` keeps `credit`); `paid_at` timestamptz→TEXT; `parent_id` polymorphic, no FK (as web); `contact_payment_method_id` FK ON DELETE SET NULL kept. Rows immutable (hardening). |
| `stock_movements` | `stock_movements` | `qty_delta`→`qty_delta_milli`; `source_type` enum (5 values incl. `opening`)→CHECK; `source_id` no FK (as web). Append-only trigger. |
| `stock_transfers` | `stock_transfers` | `qty`→`qty_milli`; `transfer_distinct_sites` kept. Append-only (hardening). |
| `cash_movements` | `cash_movements` | `amount_delta`→`amount_delta_piasters`; `source_type` enum (`payment\|manual\|expense`)→CHECK; `reason` NOT NULL + NEW non-blank CHECK (rule 5: mandatory note). Append-only trigger. |
| `doc_counters` | `doc_counters` | PK `(org_id,scope,year)`→`(scope,year)`, `WITHOUT ROWID`. Also holds `po_seq`/`so_seq` (year 0) which replace the dropped sequences `purchase_order_seq`/`sales_order_seq`. |
| `organization` | `business_settings` | uuid PK→singleton `id=1 CHECK(id=1)`; `slug`,`active`,`created_at` dropped; `currency` CHECK list kept; `timezone` loses the `pg_timezone_names` check (validate in the app). |
| `audit_log` | `audit_log` | `data` jsonb→TEXT + `json_valid`; `action` CHECK; `actor` FK without `ON DELETE SET NULL` (users are deactivated, never deleted; the table is append-only); `org_id` dropped. |
| `client_credits` | `client_credits` | `amount_delta`→`amount_delta_piasters`; `source_type` text CHECK (6 values incl. `opening`); `method` enum→CHECK; `occurred_on` date→TEXT. ON DELETE RESTRICT on contact kept. Append-only. |
| `vendor_credits` | `vendor_credits` | Same as `client_credits`. |
| `order_attachments` | `order_attachments` | `order_type` enum→CHECK; `byte_size` bigint→INTEGER with the bucket's 10 MB ceiling as a CHECK; NEW `mime_type` allow-list CHECK (was Supabase bucket config); `storage_path UNIQUE` (the extra duplicate index `order_attachments_path_idx` is not recreated). |
| `platform_admins` | — | DROPPED (multi-tenant platform layer). |
| `permission_keys` | `permission_keys` | Unchanged (38 keys); `WITHOUT ROWID`. |
| `user_permissions` | `user_permissions` | `org_id` dropped; FK `user_id`→`users(id)` ON DELETE CASCADE; `perm` FK to `permission_keys`. |
| `user_sites` | `user_sites` | Same; `org_id` dropped. |
| `contact_opening_balances` | `contact_opening_balances` | `amount_delta`→`amount_delta_piasters`; `kind` CHECK; `as_of` date→TEXT. Append-only. |
| `expense_categories` | `expense_categories` | `UNIQUE(org_id,name_ar)`→`UNIQUE(name_ar)`; name non-blank CHECK kept. |
| `expense_rules` | `expense_rules` | `amount`→`amount_piasters`; `method <> 'credit'`→CHECK of 3 values; shape/range/day-of-week(1–7 ISO)/day-of-month CHECKs kept. |
| `expenses` | `expenses` | `amount`→`amount_piasters`; `method` as above; `expenses_rule_due`/`expenses_void_shape` kept (+ non-blank reason); partial unique index `expenses_rule_occurrence_idx` kept. |
| `expense_rule_skips` | `expense_rule_skips` | Unchanged shape; append-only; unique `(rule_id,due_date)`. |
| `pc_file_bytes` (PC 0101) | `attachment_blobs` | `path`→`storage_path` PK; `bytea`→BLOB; `org_id`,`mime_type`,`byte_size`,`created_by` dropped (metadata lives in `order_attachments`); size CHECK; row removed automatically when its attachment row is deleted. |
| (new) | `app_meta`, `app_context`, `role_presets`, `user_sessions`, `auth_events` | Schema version/first-run flag; the actor/editing context row (§1); role presets as data instead of a function; desktop sessions; security event log. |

### 3.1 Enum types (12) and text-check lists

| PG type | Values (final) | Last changed | SQLite |
|---|---|---|---|
| `app_role` | admin, manager, staff, **accountant** | 0037 | `users.role`, `role_presets.role` CHECK |
| `contact_type` | vendor, client, both | 0001 | `contacts.type` CHECK |
| `payment_method` | cash, instapay, bank_transfer, **credit** | 0013 | `payments.method`, `client/vendor_credits.method` CHECK; `contact_payment_methods`, `expense*` use the 3 non-credit values |
| `unit_type` | kg, unit | 0001 | `items.unit_type`, `po_conversions.output_unit` |
| `po_status` | open, fully_converted, closed | 0001 | `purchase_orders.status` (`closed` is never set by any trigger) |
| `so_status` | draft, invoiced, placed, closed | 0001 | `sales_orders.status` |
| `payment_parent` | po, so | 0001 | `payments.parent_type` |
| `stock_source` | po_conversion, sale, adjustment, transfer, **opening** | 0034 | `stock_movements.source_type` |
| `cash_source` | payment, manual, **expense** | 0041 | `cash_movements.source_type` |
| `po_type` | general, itemized | 0019 | `purchase_orders.po_type` |
| `order_doc_type` | purchase, sale | 0022 | `order_attachments.order_type` |
| `so_discount_type` | none, amount, percent | 0030 | `sales_orders.discount_type` |
| text CHECKs | credit `source_type` (6), opening `kind` (2), expense `frequency` (3) | 0014/0023/0039/0042 | same CHECKs |

Also: extension `pgcrypto` (`gen_random_uuid`, `crypt`, `gen_salt`) → v4 default expression + app-side password hashing.

## 4. Catalog

Counting rules: "definitions" = lines matching `^\s*create (or replace )?function` etc. in the migration files (comments removed); "unique" = distinct names. Both are reconciled in §12.

### 4.1 Legend — "where it must live"

| Code | Meaning |
|---|---|
| **TRG** | SQLite trigger (name given) — enforced in the DB whatever code writes |
| **GEN** | generated column / CHECK / reference table |
| **VIEW** | SQLite view |
| **SVC** | app service method (a transaction recipe is in §9) |
| **AUTHZ** | authorization check in the app (no SQL equivalent of RLS) |
| **QRY** | parameterised query in the service layer (§10) |
| **DROP** | not ported (tenancy / platform / Supabase plumbing / dead code) |

### 4.2 Functions & RPCs — 93 unique names, 117 definitions in web 0001–0044 + 9 in PC 0100/0101

Disposition totals: SQLite trigger 32, service method 26, not ported 16, SQLite view 8, authorization check 6, service query 3, generated col / CHECK / table 2.

| Function | Migration(s) | What it does | Where it lives in the native app |
|---|---|---|---|
| `current_app_role` | 0003 | Returns the caller's role from profiles (SECURITY DEFINER so policies can call it). | **AUTHZ** — `session.user.role` (from `users.role`). |
| `get_cash_balance` | 0003 | Drawer balance for (site\|all) as of a date. INVOKER. | **VIEW** — `v_cash_drawer_balance`; dated form = QRY. Apply `cash.view` + branch scope. |
| `get_contact_balance` | 0003, 0014, 0023, 0039 ×4 | Running balance, positive = contact owes us. Final body (0039): receivable - payable - client_credit + vendor_credit + opening(receivable) - opening(payable). | **VIEW** — `v_contact_balance` (components exposed for the statement). |
| `get_dashboard` | 0003, 0014, 0023 ×3 | jsonb for one day + site (or all): opening/closing cash, sales_total, collections (+by method, incl. credit money-in, excl. method 'credit'), payments_out_by_method (incl. vendor-advance money-out), low_stock, top 5 sold items, per-site closing drawers. INVOKER (RLS filtered). | **QRY** — `DashboardService.forDay(siteId\|null, localDate)`: typed DTO built from the ledger queries in notes §10. Gate on `dashboard.view`, then each section on its own view permission + branch scope (the DB did that implicitly through RLS). Day = local calendar day -> UTC bounds. |
| `get_stock` | 0003 | Quantity on hand for (site\|all sites, item) as of a date (`created_at < as_of+1`). SECURITY INVOKER: RLS limited the sum to the caller's permitted rows. | **VIEW** — `v_stock_balance`, `v_stock_total`; dated form = QRY (notes §10). Apply `inventory.view` + branch scope in the service. |
| `is_admin` | 0003 | `role = 'admin'` for auth.uid(). Used by policies and RPC gates. | **AUTHZ** — `user.role === 'admin'`. Admin bypasses every permission check, exactly as `has_perm()` does. |
| `is_manager_or_admin` | 0003 | `role in (admin, manager)`. Replaced in every policy/RPC by `has_perm(...)` in 0032; survives only in `pc_file_*`. | **AUTHZ** — Not needed. Use the specific permission key (e.g. `attachments.manage`). |
| `next_doc_number` | 0003, 0024 ×2 | Atomic per (scope,year) counter via UPSERT on `doc_counters` (0024 added org_id and dropped the 2-arg form). | **TRG** — UPSERT inside `trg_po_assign_code` and `trg_so_assign_invoice_number` (same transaction as the insert). |
| `po_converted_kg` | 0003 | SUM(kg_consumed) of a PO. | **VIEW** — `v_po_kg_progress.converted_kg_milli`. |
| `po_remaining_kg` | 0003 | total_kg - converted. | **VIEW** — `v_po_kg_progress.remaining_kg_milli`. |
| `block_mutation` | 0004 | RAISE on UPDATE/DELETE: the append-only guard (bound to stock_movements, cash_movements, client_credits, vendor_credits, contact_opening_balances, expense_rule_skips). | **TRG** — `trg_<table>_append_only_upd/_del` (RAISE(ABORT)); also bound to audit_log, auth_events, stock_transfers (hardening). |
| `payment_apply` | 0004, 0008 ×2 | AFTER INSERT: SO payments back-fill site; cash -> cash_movements (+ for SO, - for PO); auto-close a fully collected PLACED SO. | **TRG** — `trg_payment_apply`. |
| `payment_validate` | 0004, 0008, 0016 ×3 | BEFORE INSERT on payments: parent exists, SO not draft, cash needs a drawer site, chosen contact account belongs to the order's contact and matches the method, overpayment blocked. | **TRG** — `trg_payment_validate`. |
| `po_assign_code` | 0004, 0024 ×2 | BEFORE INSERT on purchase_orders: `PO-YYYY-####` from order_date's year. | **TRG** — `trg_po_assign_code`. |
| `po_conversion_apply` | 0004 | AFTER INSERT: positive stock movement at the destination site; PO -> fully_converted at 0 remaining. | **TRG** — `trg_po_conversion_apply`. |
| `po_conversion_check` | 0004 | BEFORE INSERT: kg_consumed <= remaining of the PO. | **TRG** — `trg_po_conversion_check` (also rejects non-general POs). |
| `po_conversion_reverse` | 0004 | BEFORE DELETE: refuse if site stock insufficient; compensating negative movement; re-open PO. | **TRG** — `trg_po_conversion_reverse`. |
| `po_guard_total_kg` | 0004, 0018 ×2 | BEFORE UPDATE: total_kg frozen once a conversion exists (unless app.editing). | **TRG** — `trg_po_guard_total_kg`. |
| `so_line_guard` | 0004, 0018 ×2 | Lines editable only while the SO is draft (or app.editing). | **TRG** — `trg_so_line_guard_ins/upd/del`. |
| `so_placement_apply` | 0004, 0008 ×2 | AFTER UPDATE of status: invoiced->placed deducts stock at the SO site (insufficient -> error) then auto-closes if fully collected; placed->invoiced restores stock. | **TRG** — `trg_so_placement_apply` (fixed: also restores stock for closed->invoiced). |
| `so_recompute_total` | 0004, 0030 ×2 | After line change: refresh the order's subtotal (0030; total before). | **TRG** — `trg_so_recompute_total_ins/upd/del`; total/discount are GENERATED columns. |
| `so_status_transition` | 0004, 0008, 0018, 0024, 0032 ×5 | BEFORE UPDATE: legal-transition whitelist (bypass under app.editing); draft->invoiced needs lines and assigns `SO-YYYY-####`; 0032 added `sales.invoice`/`sales.place`/`sales.cancel_placement` gates. NB: 0032's body calls the removed 2-arg `next_doc_number` -> invoicing fails on PostgreSQL. | **TRG** — `trg_so_status_transition` + `trg_so_assign_invoice_number`. The three permission gates are AUTHZ in `SalesService.invoice/place/cancelPlacement`. |
| `transfer_apply` | 0004 | AFTER INSERT on stock_transfers: source out-row + target in-row; insufficient stock rejected. | **TRG** — `trg_transfer_apply`. |
| `cash_movement_floor_guard` | 0008 | BEFORE INSERT: a negative movement may not take a drawer below 0. | **TRG** — `trg_cash_movements_floor`. |
| `payment_delete_apply` | 0008 | AFTER DELETE: compensating cash row; re-open a closed SO that is no longer fully collected. | **TRG** — `trg_payment_delete_apply`. |
| `stock_movement_floor_guard` | 0008 | BEFORE INSERT: a negative movement may not take site/item stock below 0. | **TRG** — `trg_stock_movements_floor`. |
| `organization_touch` | 0009 | BEFORE UPDATE: updated_at := now(). | **TRG** — `trg_business_settings_touch` (and `trg_users_touch` for users). |
| `audit_row` | 0011, 0024 ×2 | Generic row-snapshot audit trigger function (actor = auth.uid()). | **TRG** — 38 generated triggers `trg_audit_*` writing `json_object(...)` to `audit_log`; actor from `app_context`. |
| `client_credit_guard` | 0014 | AFTER INSERT: client credit balance may not go negative. | **TRG** — `trg_client_credit_nonneg`. |
| `credit_apply` | 0014 | Apply client credit to an SO as a method='credit' payment + 'applied' credit row. Gate `payments.credit`. | **SVC** — `CreditService.applyClientCredit()` - recipe R2. |
| `credit_deposit` | 0014 | Add client credit (cash into drawer or instapay). No permission check inside. | **SVC** — `CreditService.depositClient()` - recipe R3. Gate `payments.record` (G3). |
| `credit_refund` | 0014 | Refund client credit (cash out of drawer or instapay). Gate `payments.credit`. | **SVC** — `CreditService.refundClient()` - recipe R4. |
| `get_client_credit` | 0014 | SUM(client_credits.amount_delta) of a contact. | **VIEW** — `v_client_credit_balance`. |
| `so_overpay` | 0014, 0016 ×2 | Settle an SO to its total and bank the excess as client credit; cash tender enters the drawer in full. No permission check inside (comment says staff+). | **SVC** — `PaymentService.paySalesOrder(allowOverpay)` - recipe R1. Gate with `payments.record` (web gap G3). |
| `admin_update_purchase_order` | 0018 | Admin edit of a general PO: total_kg >= converted, total >= paid, recompute open/fully_converted. Gate `purchases.edit`. | **SVC** — `PurchaseService.adminEdit()` - recipe R6. |
| `admin_update_sales_order` | 0018, 0030 ×2 | Admin edit of an SO at ANY status: rewind to draft (returning stock), rewrite header+lines+discount, re-check payments <= total, replay lifecycle (re-deducting stock). Uses `set_config('app.editing')`. Gate `sales.edit_locked`. | **SVC** — `SalesService.adminEdit()` - recipe R5 (uses `app_context.editing`). |
| `create_itemized_po` | 0019, 0020 ×2 | Create an itemized PO with lines, optionally creating new items on the fly. Gate `purchases.create`. | **SVC** — `PurchaseService.createItemized()` - recipe R7. |
| `po_line_apply` | 0019, 0020 ×2 | 0019 land-on-save; turned into a no-op by 0020. | **DROP** — Dead code; not ported. |
| `po_line_recompute_total` | 0019 | Keeps itemized `purchase_orders.total_amount` = SUM(line_total). | **TRG** — `trg_po_line_recompute_ins/upd/del`. |
| `po_line_reverse` | 0019, 0020 ×2 | 0019 reversal on line delete; no-op since 0020. | **DROP** — Dead code; not ported. |
| `update_itemized_po` | 0019, 0020 ×2 | Rewrite an itemized PO's header+lines (cascading away conversions, guarded). Gate `purchases.edit`. | **SVC** — `PurchaseService.updateItemized()` - recipe R7. Must also verify new total >= paid (web gap G2). |
| `po_itemized_remaining` | 0020 | Units still to receive across an itemized PO's lines. | **VIEW** — `v_po_itemized_progress.remaining_milli`. |
| `po_line_conversion_apply` | 0020, 0021 ×2 | AFTER INSERT: stock in at the chosen site; PO fully_converted when nothing remains. | **TRG** — `trg_plc_apply`. |
| `po_line_conversion_check` | 0020, 0021 ×2 | BEFORE INSERT: qty <= line's remaining (0021 also stamped item_id/po_id). | **TRG** — `trg_plc_check` (+ denorm in `trg_plc_apply`). |
| `po_line_conversion_reverse` | 0020, 0021 ×2 | BEFORE DELETE: guarded compensating movement; re-open PO. Uses denormalised ids so it survives cascades. | **TRG** — `trg_plc_reverse`. |
| `order_attachments_cascade` | 0022 | BEFORE DELETE on POs/SOs: delete their attachment rows. | **TRG** — `trg_po_attachments_cascade`, `trg_so_attachments_cascade` (+ `trg_attachment_blob_cleanup` for the bytes). |
| `get_vendor_credit` | 0023 | SUM(vendor_credits.amount_delta) of a contact. | **VIEW** — `v_vendor_credit_balance`. |
| `po_overpay` | 0023 | Mirror of so_overpay for purchase orders: excess becomes a vendor advance; cash leaves the drawer. No permission check inside. | **SVC** — `PaymentService.payPurchaseOrder(allowOverpay)` - recipe R1. Gate `payments.record` (G3). |
| `vendor_credit_apply` | 0023 | Apply a vendor advance to a PO (method='credit'). Gate `payments.credit`. | **SVC** — recipe R2 (vendor side). |
| `vendor_credit_deposit` | 0023 | Prepay a vendor (cash out of drawer / instapay). No permission check inside. | **SVC** — recipe R3 (vendor side). Gate `payments.record` (G3). |
| `vendor_credit_guard` | 0023 | AFTER INSERT: vendor advance balance may not go negative. | **TRG** — `trg_vendor_credit_nonneg`. |
| `vendor_credit_refund` | 0023 | Vendor returns an advance (cash into drawer / instapay). Gate `payments.credit`. | **SVC** — recipe R4 (vendor side). |
| `assign_order_seq` | 0024 | BEFORE INSERT on purchase_orders/sales_orders: set `order_seq` when NULL. | **TRG** — `trg_po_assign_seq`, `trg_so_assign_seq` (AFTER INSERT, fills 0 -> n; re-SELECT the row after insert). |
| `current_org` | 0024 | org_id of auth.uid()'s profile; used by every policy and default. | **DROP** — Single business per database file. |
| `next_org_seq` | 0024 | Per-org running number (`doc_counters` year 0) for «أمر شراء N» / «أمر بيع N». | **TRG** — UPSERT inside `trg_po_assign_seq` / `trg_so_assign_seq`. |
| `assert_attachment_parent_org` | 0026 | Attachment's polymorphic parent must be in the same org. | **DROP** — Parent *existence* is checked by `trg_order_attachments_parent`. |
| `assert_org` | 0026 | Raise if a uuid belongs to another org. | **DROP** — No tenancy. |
| `assert_payment_parent_org` | 0026 | Payment's polymorphic parent must be in the same org. | **DROP** — Parent *existence* is checked by `trg_payment_validate`. |
| `assert_ref_same_org` | 0026 | Generic trigger: a referenced row must be in the same org (bound dynamically to 28 FK edges + 6 expense edges). | **DROP** — Plain `REFERENCES` constraints. |
| `is_platform_admin` | 0027 | Is auth.uid() in platform_admins. | **DROP** — No platform layer. |
| `platform_list_orgs` | 0027 | Platform console listing of orgs. | **DROP** — No platform layer. |
| `provision_organization` | 0027 | Create org + first admin profile + first site + counters (service role). | **DROP** — Replaced by `seed.sql` + the first-run flow. |
| `org_public_name` | 0029 | Anonymous login-page lookup of a business name by slug. | **DROP** — Login screen reads `business_settings.business_name` directly. |
| `so_apply_discount` | 0030, 0032 ×2 | BEFORE INSERT/UPDATE: derive discount_amount/total from (type,value,subtotal); percent<=100; discount<=subtotal; cannot drop total below collected; `sales.discount` gate. | **GEN** — GENERATED `discount_amount_piasters`/`total_amount_piasters` + CHECKs + `trg_so_discount_ins/upd`. The `sales.discount` permission is AUTHZ in `SalesService.setDiscount()`. |
| `admin_set_user_access` | 0031, 0032 ×2 | One atomic write of name/role/active/permissions/branches. Guards: users.manage; non-admin cannot touch admins nor grant permissions they lack; cannot change own role/active; cannot remove last active admin; unknown permission keys rejected; non-all_sites needs >=1 branch. | **SVC** — `UserService.setAccess()` in one `BEGIN IMMEDIATE` tx. DB keeps the structural guards (`trg_users_last_admin_*`, `trg_users_no_self_lockout`, FK on `perm`). The 'cannot grant what you lack' and 'only admin edits admins' rules are AUTHZ in the service. |
| `can_use_site` | 0031 | NULL site -> true; admin / `profiles.all_sites` -> true; else row in `user_sites`. | **AUTHZ** — `canUseSite(user, siteId)`; SQL form: `v_user_site_access`. NULL site is allowed. |
| `has_perm` | 0031 | `is_admin() OR exists(user_permissions row)` for auth.uid(). | **AUTHZ** — `can(user, key)`; SQL form: `SELECT 1 FROM v_user_permissions WHERE user_id=? AND perm=?`. |
| `profile_seed_permissions` | 0031 | AFTER INSERT on profiles: copy the role preset into `user_permissions`. | **TRG** — `trg_users_seed_permissions`. |
| `role_preset_permissions` | 0031, 0040, 0042 ×3 | Permission set a role starts with (final body from 0042). Presets are COPIED to the user, never linked. | **GEN** — Table `role_presets` (reference data in schema.sql) + `trg_users_seed_permissions`. |
| `user_scope_set_org` | 0031 | BEFORE INSERT/UPDATE on user_permissions/user_sites: stamp org_id from the profile; refuse a site from another org. | **DROP** — No orgs. 'site must exist' is a plain FK. |
| `so_write_guard` | 0032 | BEFORE UPDATE (0032): header edits need `sales.draft` while draft, `sales.edit_locked` after. | **AUTHZ** — Service-level: `SalesService.update()` checks the permission by current status. Not in DB. |
| `organization_validate_timezone` | 0033 | BEFORE INSERT/UPDATE of organization.timezone: must exist in `pg_timezone_names`. | **SVC** — Validate against the IANA/Windows zone list in the app; table only has `CHECK(trim(timezone)<>'')`. |
| `set_opening_stock` | 0035 | Admin: set stock at (site,item) to a counted quantity by posting the delta as source_type 'opening'. | **SVC** — `InventoryService.setOpeningStock()` - recipe R8. AUTHZ admin. |
| `delete_organization` | 0036 | Service-role destructive purge of an org and all its rows (dry-run default). | **DROP** — Replaced by 'delete the database file' / restore from backup. |
| `get_contact_opening` | 0039 | SUM(contact_opening_balances.amount_delta) for (contact, kind). | **QRY** — `SELECT COALESCE(SUM(amount_delta_piasters),0) FROM contact_opening_balances WHERE contact_id=? AND kind=?`. |
| `set_contact_opening_balance` | 0039 | Admin: set a contact's pre-go-live receivable/payable/client_advance/vendor_advance to a figure by posting the delta. | **SVC** — `ContactService.setOpeningBalance()` - recipe R9. AUTHZ admin. |
| `settle_contact_opening` | 0039 | Pay off part of an opening receivable/payable (cash moves the drawer). Gate `payments.record`. | **SVC** — `ContactService.settleOpening()` - recipe R10. |
| `get_financial_summary` | 0040 | jsonb aggregate for a date range (+optional site): period sales/purchases/collections/payouts, position (inventory value at cost, drawers, receivables, payables, credits), monthly series. DEFINER; gate `financials.view`; totals only. | **SVC** — `FinancialService.summary(from,to,siteId)`; AUTHZ `financials.view`. Same SQL ideas as the dashboard; sum value as `SUM((qty_milli*cost_piasters+500)/1000)` per row to avoid int64 overflow. |
| `expense_block_edit` | 0042 | BEFORE UPDATE: amount/method/site/category/date/rule/due_date immutable (void instead). | **TRG** — `trg_expense_block_edit`. |
| `expense_cash_post` | 0042 | AFTER INSERT: cash expense -> negative cash_movements ('مصروف: category — description'). | **TRG** — `trg_expense_cash_post`. |
| `expense_due_list` | 0042 | Which recurring-rule occurrences (max 180 days back) are due and neither posted nor skipped. Uses generate_series / isodow. | **QRY** — Recursive-CTE query in notes §10 (tested). AUTHZ `expenses.view` + branch scope. |
| `expense_validate` | 0042 | BEFORE INSERT/UPDATE on expenses: category exists; INSERT needs an active category. | **TRG** — FK + `trg_expense_validate`. |
| `get_expense_summary` | 0042, 0044 ×2 | jsonb: expense total/count, by category, method, site, month for a range. Final body (0044): `financials.view` sees org-wide, otherwise only permitted branches. Voided excluded. | **SVC** — `ExpenseService.summary()`; AUTHZ: needs `financials.view` OR `expenses.view`; site-scope the rows unless `financials.view`. |
| `void_expense` | 0042 | Void an expense (reason required, not already voided, branch permitted): stamps void columns and posts the compensating drawer row for cash. Gate `expenses.void`. | **SVC** — `ExpenseService.void()`: AUTHZ + sets `voided_at/voided_by/void_reason`; drawer compensation is `trg_expense_void_post`. |
| `auth.email` | 0100 | auth.email() redefined (PC 0100). | **DROP** — n/a. |
| `pc_create_user` | 0100 | Admin-only user creation (username ^[a-z0-9._-]+$, password >= 6, role admin/manager/staff - note: no accountant). | **SVC** — `UserService.create()`: AUTHZ `users.manage`; all 4 roles; hash with Argon2id; the INSERT trigger seeds the role preset. |
| `pc_first_run_bootstrap` | 0100 | Create the owner: auth user (bcrypt) + clear placeholder org + provision_organization. | **SVC** — `AuthService.completeFirstRun(businessName, adminName, username, password)`: UPDATE the seed admin, hash the password, set `first_run_completed`. |
| `pc_needs_setup` | 0100 | True when no profile exists (fresh install). | **SVC** — `AuthService.needsSetup()`: first-run if an admin has `password_hash IS NULL`. |
| `auth.role` | 0100 | auth.role() redefined (PC 0100). | **DROP** — No PostgREST roles. |
| `auth.uid` | 0100 | auth.uid() redefined (PC 0100) to read the `request.jwt.claims` GUC. | **DROP** — `app_context.actor_user_id` / the session's user. |
| `pc_file_delete` | 0101 | Delete attachment bytes (manager/admin). | **SVC** — `AttachmentService.remove()`: AUTHZ `attachments.manage`; deleting the metadata row cascades to the blob via `trg_attachment_blob_cleanup`. |
| `pc_file_get` | 0101 | Return bytes only if an `order_attachments` row for that path is visible. | **SVC** — `AttachmentService.read()`: AUTHZ `sales.view` or `purchases.view` (order type). |
| `pc_file_put` | 0101 | Store attachment bytes (<=10 MB) after role check manager/admin and org-prefixed path. | **SVC** — `AttachmentService.add()`: AUTHZ `attachments.manage`; insert metadata + `attachment_blobs` row in one tx (mime/size are CHECKs). |

### 4.3 Triggers — 71 distinct static names in the migrations (73 `CREATE TRIGGER` statements) + 34 created dynamically

| Trigger | Migration(s) | Table | What it does | SQLite |
|---|---|---|---|---|
| `trg_cash_movements_append_only` | 0004 | cash_movements | BEFORE U/D: block_mutation() | `trg_cash_movements_append_only_upd/_del` |
| `trg_payment_apply` | 0004 | payments | AFTER INSERT: drawer, auto-close | `trg_payment_apply` |
| `trg_payment_validate` | 0004 | payments | BEFORE INSERT: overpay etc. | `trg_payment_validate` |
| `trg_po_assign_code` | 0004 | purchase_orders | BEFORE INSERT: PO-YYYY-#### | `trg_po_assign_code` |
| `trg_po_conversion_apply` | 0004 | po_conversions | AFTER INSERT: stock in, status | `trg_po_conversion_apply` |
| `trg_po_conversion_check` | 0004 | po_conversions | BEFORE INSERT: kg <= remaining | `trg_po_conversion_check` |
| `trg_po_conversion_reverse` | 0004 | po_conversions | BEFORE DELETE: guarded reversal | `trg_po_conversion_reverse` |
| `trg_po_guard_total_kg` | 0004 | purchase_orders | BEFORE UPDATE: total_kg frozen after first conversion | `trg_po_guard_total_kg` |
| `trg_so_line_guard` | 0004 | sales_order_lines | BEFORE I/U/D: draft only | `trg_so_line_guard_ins/upd/del` |
| `trg_so_placement_apply` | 0004 | sales_orders | AFTER UPDATE: stock out/in, auto-close | `trg_so_placement_apply` |
| `trg_so_recompute_total` | 0004 | sales_order_lines | AFTER I/U/D: refresh SO subtotal | `trg_so_recompute_total_ins/upd/del` |
| `trg_so_status_transition` | 0004 | sales_orders | BEFORE UPDATE: whitelist + invoice number | `trg_so_status_transition` + `trg_so_assign_invoice_number` |
| `trg_stock_movements_append_only` | 0004 | stock_movements | BEFORE U/D: block_mutation() | `trg_stock_movements_append_only_upd/_del` |
| `trg_transfer_apply` | 0004 | stock_transfers | AFTER INSERT: two movements | `trg_transfer_apply` |
| `trg_cash_movements_floor` | 0008 | cash_movements | BEFORE INSERT: no negative drawer | `trg_cash_movements_floor` |
| `trg_payment_delete_apply` | 0008 | payments | AFTER DELETE: compensate drawer, re-open SO | `trg_payment_delete_apply` |
| `trg_stock_movements_floor` | 0008 | stock_movements | BEFORE INSERT: no negative stock | `trg_stock_movements_floor` |
| `trg_organization_touch` | 0009, 0024 | organization | BEFORE UPDATE: updated_at | `trg_business_settings_touch` |
| `trg_audit_cash_manual` | 0011 | cash_movements | audit insert of 'manual' | `trg_audit_cash_manual` |
| `trg_audit_contacts` | 0011 | contacts | audit I/U/D | `trg_audit_contacts_ins/upd/del` |
| `trg_audit_conv_del` | 0011 | po_conversions | audit delete | `trg_audit_conv_del` |
| `trg_audit_conv_ins` | 0011 | po_conversions | audit insert | `trg_audit_conv_ins` |
| `trg_audit_items` | 0011 | items | audit I/U/D | `trg_audit_items_ins/upd/del` |
| `trg_audit_org` | 0011 | organization | audit update (0011) - silently lost when 0024 dropped the legacy table | `trg_audit_business_settings` (restored - hardening) |
| `trg_audit_payments_del` | 0011 | payments | audit delete | `trg_audit_payments_del` |
| `trg_audit_payments_ins` | 0011 | payments | audit insert | `trg_audit_payments_ins` |
| `trg_audit_po_ins` | 0011 | purchase_orders | audit insert | `trg_audit_po_ins` |
| `trg_audit_po_upd` | 0011 | purchase_orders | audit status change | `trg_audit_po_upd` |
| `trg_audit_profiles` | 0011 | profiles | audit I/U/D | `trg_audit_users_ins/upd/del` (password_hash never snapshotted) |
| `trg_audit_sites` | 0011 | sites | audit I/U | `trg_audit_sites_ins/upd` |
| `trg_audit_so_ins` | 0011 | sales_orders | audit insert | `trg_audit_so_ins` |
| `trg_audit_so_upd` | 0011 | sales_orders | audit status change | `trg_audit_so_upd` |
| `trg_audit_stock_adj` | 0011, 0035 | stock_movements | audit insert of 'adjustment' (+ 'opening' from 0035) | `trg_audit_stock_adj` |
| `trg_audit_transfer_ins` | 0011 | stock_transfers | audit insert | `trg_audit_transfer_ins` |
| `trg_audit_client_credits` | 0014 | client_credits | audit insert | `trg_audit_client_credits` |
| `trg_client_credit_nonneg` | 0014 | client_credits | AFTER INSERT: balance >= 0 | `trg_client_credit_nonneg` |
| `trg_client_credits_append_only` | 0014 | client_credits | BEFORE U/D: block_mutation() | `trg_client_credits_append_only_upd/_del` |
| `trg_po_line_apply` | 0019 | po_lines | AFTER INSERT (0019) - function became a no-op in 0020 | DROP (dead) |
| `trg_po_line_recompute_total` | 0019 | po_lines | AFTER I/U/D: PO total = SUM(lines) | `trg_po_line_recompute_ins/upd/del` |
| `trg_po_line_reverse` | 0019 | po_lines | BEFORE DELETE (0019) - no-op since 0020 | DROP (dead) |
| `trg_plc_apply` | 0020 | po_line_conversions | AFTER INSERT: stock in, status | `trg_plc_apply` |
| `trg_plc_check` | 0020 | po_line_conversions | BEFORE INSERT: qty <= line remaining | `trg_plc_check` |
| `trg_plc_reverse` | 0020 | po_line_conversions | BEFORE DELETE: guarded reversal | `trg_plc_reverse` |
| `trg_audit_attachment_del` | 0022 | order_attachments | audit delete | `trg_audit_attachment_del` |
| `trg_audit_attachment_ins` | 0022 | order_attachments | audit insert | `trg_audit_attachment_ins` |
| `trg_po_attachments_cascade` | 0022 | purchase_orders | BEFORE DELETE: drop attachment rows | `trg_po_attachments_cascade` |
| `trg_so_attachments_cascade` | 0022 | sales_orders | BEFORE DELETE: drop attachment rows | `trg_so_attachments_cascade` |
| `trg_audit_vendor_credits` | 0023 | vendor_credits | audit insert | `trg_audit_vendor_credits` |
| `trg_vendor_credit_nonneg` | 0023 | vendor_credits | AFTER INSERT: balance >= 0 | `trg_vendor_credit_nonneg` |
| `trg_vendor_credits_append_only` | 0023 | vendor_credits | BEFORE U/D: block_mutation() | `trg_vendor_credits_append_only_upd/_del` |
| `trg_po_assign_seq` | 0024 | purchase_orders | BEFORE INSERT: order_seq | `trg_po_assign_seq` |
| `trg_so_assign_seq` | 0024 | sales_orders | BEFORE INSERT: order_seq | `trg_so_assign_seq` |
| `trg_org_ref_attachments_parent` | 0026 | order_attachments | polymorphic parent same-org | DROP (existence in `trg_order_attachments_parent`) |
| `trg_org_ref_payments_parent` | 0026 | payments | polymorphic parent same-org | DROP (existence in `trg_payment_validate`) |
| `trg_audit_so_discount` | 0030 | sales_orders | audit discount type/value change | `trg_audit_so_discount` |
| `trg_so_apply_discount` | 0030 | sales_orders | BEFORE I/U: discount derivation + guards | GENERATED columns + `trg_so_discount_ins/upd` |
| `trg_profile_seed_permissions` | 0031 | profiles | AFTER INSERT: copy role preset | `trg_users_seed_permissions` |
| `trg_user_permissions_org` | 0031 | user_permissions | stamp org_id | DROP |
| `trg_user_sites_org` | 0031 | user_sites | stamp org_id, site same org | DROP (FK) |
| `trg_so_write_guard` | 0032 | sales_orders | BEFORE UPDATE: sales.draft / sales.edit_locked | AUTHZ in service |
| `trg_organization_timezone` | 0033 | organization | BEFORE I/U: timezone valid | SVC validation |
| `trg_audit_contact_opening_balances` | 0039 | contact_opening_balances | audit insert | `trg_audit_contact_opening_balances` |
| `trg_contact_opening_balances_append_only` | 0039 | contact_opening_balances | BEFORE U/D: block_mutation() | `trg_contact_opening_balances_append_only_upd/_del` |
| `trg_audit_expense_categories` | 0042 | expense_categories | audit I/U/D | `trg_audit_expense_categories_ins/upd/del` |
| `trg_audit_expense_rule_skips` | 0042 | expense_rule_skips | audit insert | `trg_audit_expense_rule_skips` |
| `trg_audit_expense_rules` | 0042 | expense_rules | audit I/U/D | `trg_audit_expense_rules_ins/upd/del` |
| `trg_audit_expenses` | 0042 | expenses | audit I/U | `trg_audit_expenses_ins/upd` |
| `trg_expense_block_edit` | 0042 | expenses | BEFORE UPDATE: immutable fields | `trg_expense_block_edit` |
| `trg_expense_cash_post` | 0042 | expenses | AFTER INSERT: drawer row | `trg_expense_cash_post` |
| `trg_expense_rule_skips_append_only` | 0042 | expense_rule_skips | BEFORE U/D: block_mutation() | `trg_expense_rule_skips_append_only_upd/_del` |
| `trg_expense_validate` | 0042 | expenses | BEFORE I/U: category active | `trg_expense_validate` |

**Dynamic triggers** (created inside `DO $$` loops, so a plain grep does not see them): `trg_org_ref_<table>_<column>` ×28 (0026, bound to `assert_ref_same_org`) and ×6 for the expense tables (0042). All 34 are **DROP** (tenancy). Net live PG count = 71 + 34 − 1 (`trg_audit_org` disappeared with `organization_legacy` in 0024) = **104**, which matches `pg_trigger` on the verification instance.

### 4.4 RLS policies — 85 distinct (table, name) pairs, 216 `CREATE POLICY` statements, **82 live** in the final state

RLS is **enabled** on every table (`doc_counters`, `platform_admins`, `pc_file_bytes` have RLS and *no policy* = no direct client access). The live set equals `pg_policies` on the verification instance (82 incl. the 3 on `storage.objects`). Every live policy becomes an app-layer rule in §5.

| Table | Policy | Op | Migration(s) (×n = re-created n times) | Status | Native |
|---|---|---|---|---|---|
| `cash_movements` | `cm_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `cash_movements` | `cm_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_payment_methods` | `pm_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_payment_methods` | `pm_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_payment_methods` | `pm_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_payment_methods` | `pm_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_phones` | `phones_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_phones` | `phones_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_phones` | `phones_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_phones` | `phones_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contacts` | `contacts_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contacts` | `contacts_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contacts` | `contacts_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `contacts` | `contacts_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `items` | `items_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `items` | `items_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `items` | `items_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `items` | `items_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `payments` | `payments_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `payments` | `payments_read` | SELECT | 0005, 0025, 0032, 0040 ×4 | LIVE | **AUTHZ** — see §5 matrix row |
| `payments` | `payments_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_conversions` | `conv_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_conversions` | `conv_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_conversions` | `conv_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `profiles` | `profiles_admin` | — | 0005 | superseded / dropped | **DROP** — replaced by profiles_admin_write/update/delete in 0006 |
| `profiles` | `profiles_read` | SELECT | 0005, 0025 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `purchase_orders` | `po_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `purchase_orders` | `po_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `purchase_orders` | `po_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `purchase_orders` | `po_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_order_lines` | `sol_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_order_lines` | `sol_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_order_lines` | `sol_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_order_lines` | `sol_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_orders` | `so_delete` | DELETE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_orders` | `so_insert` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_orders` | `so_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sales_orders` | `so_update` | UPDATE | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sites` | `sites_admin` | — | 0005 | superseded / dropped | **DROP** — replaced by sites_admin_write/update/delete in 0006 |
| `sites` | `sites_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `stock_movements` | `sm_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `stock_movements` | `sm_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `stock_transfers` | `transfer_read` | SELECT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `stock_transfers` | `transfer_write` | INSERT | 0005, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `profiles` | `profiles_admin_delete` | DELETE | 0006, 0025 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `profiles` | `profiles_admin_update` | UPDATE | 0006, 0025 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `profiles` | `profiles_admin_write` | INSERT | 0006, 0025 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `sites` | `sites_admin_delete` | DELETE | 0006, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sites` | `sites_admin_update` | UPDATE | 0006, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `sites` | `sites_admin_write` | INSERT | 0006, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `organization` | `org_insert` | — | 0009 | superseded / dropped | **DROP** — died with `organization_legacy` (0024 `drop table … cascade`); creating a business is provisioning, not a tenant action |
| `organization` | `org_read` | SELECT | 0009, 0024, 0025 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `organization` | `org_update` | UPDATE | 0009, 0024, 0025, 0032 ×4 | LIVE | **AUTHZ** — see §5 matrix row |
| `audit_log` | `audit_read` | SELECT | 0011, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `client_credits` | `client_credits_read` | SELECT | 0014, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_lines` | `po_lines_read` | SELECT | 0019, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_line_conversions` | `plc_delete` | DELETE | 0020, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_line_conversions` | `plc_read` | SELECT | 0020, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `po_line_conversions` | `plc_write` | INSERT | 0020, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `order_attachments` | `order_attachments_delete` | DELETE | 0022, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `order_attachments` | `order_attachments_insert` | INSERT | 0022, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `order_attachments` | `order_attachments_read` | SELECT | 0022, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `storage.objects` | `order_docs_delete` | DELETE | 0022, 0028, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `storage.objects` | `order_docs_insert` | INSERT | 0022, 0028, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `storage.objects` | `order_docs_read` | SELECT | 0022, 0028 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `vendor_credits` | `vendor_credits_read` | SELECT | 0023, 0025, 0032 ×3 | LIVE | **AUTHZ** — see §5 matrix row |
| `permission_keys` | `permission_keys_read` | SELECT | 0031 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_permissions` | `user_permissions_delete` | DELETE | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_permissions` | `user_permissions_read` | SELECT | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_permissions` | `user_permissions_write` | INSERT | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_sites` | `user_sites_delete` | DELETE | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_sites` | `user_sites_read` | SELECT | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `user_sites` | `user_sites_write` | INSERT | 0031, 0032 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `contact_opening_balances` | `contact_opening_balances_read` | SELECT | 0039 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_categories` | `expense_categories_read` | SELECT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_categories` | `expense_categories_update` | UPDATE | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_categories` | `expense_categories_write` | INSERT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rule_skips` | `expense_rule_skips_read` | SELECT | 0042, 0043 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rule_skips` | `expense_rule_skips_write` | INSERT | 0042, 0043 ×2 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rules` | `expense_rules_delete` | DELETE | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rules` | `expense_rules_read` | SELECT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rules` | `expense_rules_update` | UPDATE | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expense_rules` | `expense_rules_write` | INSERT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expenses` | `expenses_read` | SELECT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |
| `expenses` | `expenses_write` | INSERT | 0042 | LIVE | **AUTHZ** — see §5 matrix row |

## 5. Authorization: RLS → application layer

Principles carried over unchanged from web 0031/0032:
* `role` stays as the **preset** a user was created from; **admin bypasses every permission check** (recovery path) — `has_perm()` is `is_admin() OR row`.
* Everyone else is governed by **`user_permissions`** (38 keys, areas: sales, purchases, inventory, payments, cash, contacts, documents, reports, expenses, admin) and by **branch scope** (`users.all_sites` / `user_sites`; NULL site = open to all).
* Reads were *branch-scoped in the DB* (`can_use_site`) — in the native app every query that returns branch-owned rows must add `site_id IN (SELECT site_id FROM v_user_site_access WHERE user_id=:u)` (or the equivalent in code). This is the easiest rule to forget; wrap it in the repository layer.

Check helpers (SQL form): `can(u,k)` = `EXISTS(SELECT 1 FROM v_user_permissions WHERE user_id=:u AND perm=:k)`; `site(s)` = `s IS NULL OR EXISTS(SELECT 1 FROM v_user_site_access WHERE user_id=:u AND site_id=:s)`. Reject inactive users at login.

### 5.1 Default grants per role (the presets, final = migration 0042)

| Role | Permission keys |
|---|---|
| **admin** | all 38 (and bypass) |
| **manager** (26) | sales.view/draft/discount/invoice/place/cancel_placement · purchases.view/create/edit/convert · inventory.view/items/adjust/transfer · payments.record/credit · cash.view · contacts.view/manage · attachments.manage · expenses.view/record/manage · dashboard.view · reports.view · audit.view |
| **staff** (12) | sales.view/draft/discount · purchases.view · inventory.view · payments.record · cash.view · contacts.view · expenses.view/record · dashboard.view · reports.view |
| **accountant** (1) | financials.view (aggregate RPCs only — **sees no rows at all**) |

Not in any preset (admin-only unless granted): `sales.edit_locked`, `sales.delete`, `purchases.delete`, `inventory.item_delete`, `payments.delete`, `cash.manual`, `contacts.delete`, `expenses.void`, `users.manage`, `sites.manage`, `settings.manage`, `financials.view` (except accountant).

### 5.2 Role × table × operation (all 82 live policies)

`can(k)` = permission key; `site(col)` = branch scope on that column; ✔ = allowed by the **default preset** of that role (A=admin, M=manager, S=staff, Ac=accountant; a custom per-user grant can change any non-admin cell). Predicates are the real policy text with the `org_id = current_org()` clause stripped.

| Table | Op | Policy (file of live version) | Requirement | A | M | S | Ac | Note |
|---|---|---|---|---|---|---|---|---|
| organization | SELECT | `org_read` (0025) | `any signed-in user` | ✔ | ✔ | ✔ | ✔ |  |
| organization | UPDATE | `org_update` (0032) | `can(settings.manage)` | ✔ | — | — | — |  |
| sites | SELECT | `sites_read` (0032) | `site(id)` | ✔ | ✔ | ✔ | ✔ |  |
| sites | INSERT | `sites_admin_write` (0032) | `can(sites.manage)` | ✔ | — | — | — |  |
| sites | UPDATE | `sites_admin_update` (0032) | `can(sites.manage)` | ✔ | — | — | — |  |
| sites | DELETE | `sites_admin_delete` (0032) | `can(sites.manage)` | ✔ | — | — | — |  |
| profiles | SELECT | `profiles_read` (0025) | `any signed-in user` | ✔ | ✔ | ✔ | ✔ |  |
| profiles | INSERT | `profiles_admin_write` (0025) | `role=admin` | ✔ | — | — | — |  |
| profiles | UPDATE | `profiles_admin_update` (0025) | `role=admin` | ✔ | — | — | — |  |
| profiles | DELETE | `profiles_admin_delete` (0025) | `role=admin` | ✔ | — | — | — |  |
| permission_keys | SELECT | `permission_keys_read` (0031) | `any signed-in user` | ✔ | ✔ | ✔ | ✔ |  |
| user_permissions | SELECT | `user_permissions_read` (0032) | `own row or can(users.manage)` | ✔ | ✔ | ✔ | ✔ | own rows: everyone; all rows: `users.manage` |
| user_permissions | INSERT | `user_permissions_write` (0032) | `can(users.manage)` | ✔ | — | — | — |  |
| user_permissions | DELETE | `user_permissions_delete` (0032) | `can(users.manage)` | ✔ | — | — | — |  |
| user_sites | SELECT | `user_sites_read` (0032) | `own row or can(users.manage)` | ✔ | ✔ | ✔ | ✔ | own rows: everyone; all rows: `users.manage` |
| user_sites | INSERT | `user_sites_write` (0032) | `can(users.manage)` | ✔ | — | — | — |  |
| user_sites | DELETE | `user_sites_delete` (0032) | `can(users.manage)` | ✔ | — | — | — |  |
| contacts | SELECT | `contacts_read` (0032) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| contacts | INSERT | `contacts_write` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contacts | UPDATE | `contacts_update` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contacts | DELETE | `contacts_delete` (0032) | `can(contacts.delete)` | ✔ | — | — | — |  |
| contact_phones | SELECT | `phones_read` (0032) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| contact_phones | INSERT | `phones_write` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contact_phones | UPDATE | `phones_update` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contact_phones | DELETE | `phones_delete` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contact_payment_methods | SELECT | `pm_read` (0032) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| contact_payment_methods | INSERT | `pm_write` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contact_payment_methods | UPDATE | `pm_update` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| contact_payment_methods | DELETE | `pm_delete` (0032) | `can(contacts.manage)` | ✔ | ✔ | — | — |  |
| items | SELECT | `items_read` (0032) | `can(inventory.view)` | ✔ | ✔ | ✔ | — |  |
| items | INSERT | `items_write` (0032) | `can(inventory.items)` | ✔ | ✔ | — | — |  |
| items | UPDATE | `items_update` (0032) | `can(inventory.items)` | ✔ | ✔ | — | — |  |
| items | DELETE | `items_delete` (0032) | `can(inventory.item_delete)` | ✔ | — | — | — |  |
| purchase_orders | SELECT | `po_read` (0032) | `can(purchases.view)` | ✔ | ✔ | ✔ | — |  |
| purchase_orders | INSERT | `po_write` (0032) | `can(purchases.create)` | ✔ | ✔ | — | — |  |
| purchase_orders | UPDATE | `po_update` (0032) | `can(purchases.edit)` | ✔ | ✔ | — | — |  |
| purchase_orders | DELETE | `po_delete` (0032) | `can(purchases.delete)` | ✔ | — | — | — |  |
| po_lines | SELECT | `po_lines_read` (0032) | `can(purchases.view)` | ✔ | ✔ | ✔ | — |  |
| po_conversions | SELECT | `conv_read` (0032) | `can(purchases.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| po_conversions | INSERT | `conv_write` (0032) | `can(purchases.convert) and site(site_id)` | ✔ | ✔ | — | — |  |
| po_conversions | DELETE | `conv_delete` (0032) | `can(purchases.delete) and site(site_id)` | ✔ | — | — | — |  |
| po_line_conversions | SELECT | `plc_read` (0032) | `can(purchases.view)` | ✔ | ✔ | ✔ | — |  |
| po_line_conversions | INSERT | `plc_write` (0032) | `can(purchases.convert)` | ✔ | ✔ | — | — |  |
| po_line_conversions | DELETE | `plc_delete` (0032) | `can(purchases.delete)` | ✔ | — | — | — |  |
| sales_orders | SELECT | `so_read` (0032) | `can(sales.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| sales_orders | INSERT | `so_insert` (0032) | `can(sales.draft) and site(site_id) and status = 'draft'` | ✔ | ✔ | ✔ | — |  |
| sales_orders | UPDATE | `so_update` (0032) | `site(site_id) and ( can(sales.draft) or can(sales.invoice) or can(sales.place) or can(sales.cancel_placement) or can(sales.edit_locked))` | ✔ | ✔ | ✔ | — |  |
| sales_orders | DELETE | `so_delete` (0032) | `can(sales.delete) and site(site_id)` | ✔ | — | — | — |  |
| sales_order_lines | SELECT | `sol_read` (0032) | `can(sales.view)` | ✔ | ✔ | ✔ | — |  |
| sales_order_lines | INSERT | `sol_write` (0032) | `can(sales.draft) and ⟨parent SO is draft⟩` | ✔ | ✔ | ✔ | — |  |
| sales_order_lines | UPDATE | `sol_update` (0032) | `can(sales.draft) and ⟨parent SO is draft⟩` | ✔ | ✔ | ✔ | — |  |
| sales_order_lines | DELETE | `sol_delete` (0032) | `can(sales.draft) and ⟨parent SO is draft⟩` | ✔ | ✔ | ✔ | — |  |
| payments | SELECT | `payments_read` (0040) | `site(site_id) and ( (parent_type = 'so' and can(sales.view)) or (parent_type = 'po' and can(purchases.view)) )` | ✔ | ✔ | ✔ | — |  |
| payments | INSERT | `payments_write` (0032) | `can(payments.record) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| payments | DELETE | `payments_delete` (0032) | `can(payments.delete) and site(site_id)` | ✔ | — | — | — |  |
| stock_movements | SELECT | `sm_read` (0032) | `can(inventory.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| stock_movements | INSERT | `sm_write` (0032) | `can(inventory.adjust) and site(site_id) and source_type = 'adjustment'` | ✔ | ✔ | — | — |  |
| stock_transfers | SELECT | `transfer_read` (0032) | `can(inventory.view) and (site(from_site) or site(to_site))` | ✔ | ✔ | ✔ | — |  |
| stock_transfers | INSERT | `transfer_write` (0032) | `can(inventory.transfer) and site(from_site) and site(to_site)` | ✔ | ✔ | — | — |  |
| cash_movements | SELECT | `cm_read` (0032) | `can(cash.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| cash_movements | INSERT | `cm_write` (0032) | `can(cash.manual) and site(site_id) and source_type = 'manual'` | ✔ | — | — | — |  |
| client_credits | SELECT | `client_credits_read` (0032) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| vendor_credits | SELECT | `vendor_credits_read` (0032) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| contact_opening_balances | SELECT | `contact_opening_balances_read` (0039) | `can(contacts.view)` | ✔ | ✔ | ✔ | — |  |
| order_attachments | SELECT | `order_attachments_read` (0032) | `can(sales.view) or can(purchases.view)` | ✔ | ✔ | ✔ | — |  |
| order_attachments | INSERT | `order_attachments_insert` (0032) | `can(attachments.manage)` | ✔ | ✔ | — | — |  |
| order_attachments | DELETE | `order_attachments_delete` (0032) | `can(attachments.manage)` | ✔ | ✔ | — | — |  |
| storage.objects | SELECT | `order_docs_read` (0028) | `bucket_id = 'order-docs' and ⟨an order_attachments row exists for the path (read under its own policy)⟩` | ✔ | ✔ | ✔ | — | effective gate = `order_attachments_read` (the EXISTS runs under that policy) |
| storage.objects | INSERT | `order_docs_insert` (0032) | `bucket_id = 'order-docs' and can(attachments.manage) and path under own org folder (n/a in native)` | ✔ | ✔ | — | — |  |
| storage.objects | DELETE | `order_docs_delete` (0032) | `bucket_id = 'order-docs' and can(attachments.manage) and ⟨an order_attachments row exists for the path (read under its own policy)⟩` | ✔ | ✔ | — | — | also needs the attachment row to be visible (read gate) |
| audit_log | SELECT | `audit_read` (0032) | `can(audit.view)` | ✔ | ✔ | — | — |  |
| expense_categories | SELECT | `expense_categories_read` (0042) | `can(expenses.view)` | ✔ | ✔ | ✔ | — |  |
| expense_categories | INSERT | `expense_categories_write` (0042) | `can(expenses.manage)` | ✔ | ✔ | — | — |  |
| expense_categories | UPDATE | `expense_categories_update` (0042) | `can(expenses.manage)` | ✔ | ✔ | — | — |  |
| expense_rules | SELECT | `expense_rules_read` (0042) | `can(expenses.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| expense_rules | INSERT | `expense_rules_write` (0042) | `can(expenses.manage) and site(site_id)` | ✔ | ✔ | — | — |  |
| expense_rules | UPDATE | `expense_rules_update` (0042) | `can(expenses.manage) and site(site_id)` | ✔ | ✔ | — | — |  |
| expense_rules | DELETE | `expense_rules_delete` (0042) | `can(expenses.manage) and site(site_id)` | ✔ | ✔ | — | — |  |
| expenses | SELECT | `expenses_read` (0042) | `can(expenses.view) and site(site_id)` | ✔ | ✔ | ✔ | — |  |
| expenses | INSERT | `expenses_write` (0042) | `can(expenses.record) and site(site_id) and voided_at is null` | ✔ | ✔ | ✔ | — |  |
| expense_rule_skips | SELECT | `expense_rule_skips_read` (0043) | `can(expenses.view) and ⟨the rule's branch is permitted⟩` | ✔ | ✔ | ✔ | — |  |
| expense_rule_skips | INSERT | `expense_rule_skips_write` (0043) | `can(expenses.record) and ⟨the rule's branch is permitted⟩` | ✔ | ✔ | ✔ | — |  |

### 5.3 Operations with no policy (= denied to clients; only a trigger/RPC could write)

**none** means PostgREST could never perform that operation; in the native app it is *not exposed as a generic CRUD method* — only through the service methods of §5.4 (or by triggers). Notably every ledger/line table has no direct write path except where a row above says otherwise.

| Table | SELECT | INSERT | UPDATE | DELETE |
|---|---|---|---|---|
| `organization` | `org_read` | **none** | `org_update` | **none** |
| `sites` | `sites_read` | `sites_admin_write` | `sites_admin_update` | `sites_admin_delete` |
| `profiles` | `profiles_read` | `profiles_admin_write` | `profiles_admin_update` | `profiles_admin_delete` |
| `contacts` | `contacts_read` | `contacts_write` | `contacts_update` | `contacts_delete` |
| `contact_phones` | `phones_read` | `phones_write` | `phones_update` | `phones_delete` |
| `contact_payment_methods` | `pm_read` | `pm_write` | `pm_update` | `pm_delete` |
| `items` | `items_read` | `items_write` | `items_update` | `items_delete` |
| `purchase_orders` | `po_read` | `po_write` | `po_update` | `po_delete` |
| `po_conversions` | `conv_read` | `conv_write` | **none** | `conv_delete` |
| `sales_orders` | `so_read` | `so_insert` | `so_update` | `so_delete` |
| `sales_order_lines` | `sol_read` | `sol_write` | `sol_update` | `sol_delete` |
| `payments` | `payments_read` | `payments_write` | **none** | `payments_delete` |
| `stock_movements` | `sm_read` | `sm_write` | **none** | **none** |
| `stock_transfers` | `transfer_read` | `transfer_write` | **none** | **none** |
| `cash_movements` | `cm_read` | `cm_write` | **none** | **none** |
| `doc_counters` | **none** | **none** | **none** | **none** |
| `audit_log` | `audit_read` | **none** | **none** | **none** |
| `client_credits` | `client_credits_read` | **none** | **none** | **none** |
| `po_lines` | `po_lines_read` | **none** | **none** | **none** |
| `po_line_conversions` | `plc_read` | `plc_write` | **none** | `plc_delete` |
| `order_attachments` | `order_attachments_read` | `order_attachments_insert` | **none** | `order_attachments_delete` |
| `vendor_credits` | `vendor_credits_read` | **none** | **none** | **none** |
| `platform_admins` | **none** | **none** | **none** | **none** |
| `permission_keys` | `permission_keys_read` | **none** | **none** | **none** |
| `user_permissions` | `user_permissions_read` | `user_permissions_write` | **none** | `user_permissions_delete` |
| `user_sites` | `user_sites_read` | `user_sites_write` | **none** | `user_sites_delete` |
| `contact_opening_balances` | `contact_opening_balances_read` | **none** | **none** | **none** |
| `expense_categories` | `expense_categories_read` | `expense_categories_write` | `expense_categories_update` | **none** |
| `expense_rules` | `expense_rules_read` | `expense_rules_write` | `expense_rules_update` | `expense_rules_delete` |
| `expenses` | `expenses_read` | `expenses_write` | **none** | **none** |
| `expense_rule_skips` | `expense_rule_skips_read` | `expense_rule_skips_write` | **none** | **none** |
| `pc_file_bytes` | **none** | **none** | **none** | **none** |

### 5.4 SECURITY DEFINER RPC gates (these bypass RLS, so each carries its own check) and what the native service must enforce

| RPC → service method | Web gate (final) | Native requirement |
|---|---|---|
| `so_overpay`, `po_overpay` | **none** (any signed-in user) | `payments.record` + `site(drawer)` — web gap G3 |
| `credit_deposit`, `vendor_credit_deposit` | **none** | `payments.record` + `site` — G3 |
| `credit_apply`, `credit_refund`, `vendor_credit_apply`, `vendor_credit_refund` | `payments.credit` (0032) | same |
| `settle_contact_opening` | `payments.record` | same |
| `create_itemized_po` | `purchases.create` | same |
| `update_itemized_po`, `admin_update_purchase_order` | `purchases.edit` | same (+ payments ≤ new total, G2) |
| `admin_update_sales_order` | `sales.edit_locked` | same |
| `set_opening_stock`, `set_contact_opening_balance` | `is_admin()` | role = admin |
| `admin_set_user_access` | `users.manage`; non-admins additionally: cannot create/edit admins, cannot grant permissions they lack; nobody can change own role/active or remove the last active admin | same (last two are also DB triggers) |
| `get_financial_summary` | `financials.view` | same |
| `get_expense_summary` | `financials.view` **or** `expenses.view`; org-wide only with `financials.view`, else branch-scoped (0044) | same |
| `expense_due_list` | `expenses.view` + `site` | same |
| `void_expense` | `expenses.void` + `site`; reason required | same |
| `pc_create_user` | caller role = admin; roles admin/manager/staff only | `users.manage`; all four roles |
| `pc_file_put` / `pc_file_delete` | `is_manager_or_admin()` (role-based, inconsistent with 0032) | `attachments.manage` |
| `pc_file_get` | attachment row visible | `sales.view` (sale) / `purchases.view` (purchase) |
| `get_dashboard`, `get_stock`, `get_cash_balance`, `get_contact_balance`, `po_*_kg`, `po_itemized_remaining`, `get_client_credit`, `get_vendor_credit` | SECURITY INVOKER → silently filtered by the §5.2 read policies (`dashboard.view` itself was **UI-only**, G5) | Check `dashboard.view`, then per section: `cash.view`, `sales.view`, `purchases.view`, `inventory.view`, `contacts.view` + branch scope. For `get_contact_balance` prefer full figures for anyone with `contacts.view` (a branch-filtered balance is misleading). |
| `provision_organization`, `platform_list_orgs`, `delete_organization`, `is_platform_admin` | service role only | dropped |

### 5.5 Permission checks that lived *inside triggers* (not policies) in web — now service-level

| Web trigger (0032) | Rule | Native |
|---|---|---|
| `so_status_transition` | draft→invoiced needs `sales.invoice`; invoiced→placed `sales.place`; placed→invoiced `sales.cancel_placement` (skipped while `app.editing`) | `SalesService.invoice/place/cancelPlacement` |
| `so_write_guard` | header edit with no status change: `sales.draft` while draft, `sales.edit_locked` afterwards | `SalesService.update` |
| `so_apply_discount` | setting/changing a non-`none` discount needs `sales.discount` (skipped while editing) | `SalesService.setDiscount` |
| `so_insert` policy | staff may only insert `status='draft'` | `SalesService.createDraft` always inserts draft |

### 5.6 Web-side rules that were UI-only or implicit (carry into the native UI too)
Hide forbidden actions (CLAUDE.md rule 9) **and** enforce them in the service. The DB cannot do the second half any more, so unit-test every service method with a user lacking the key.

## 6. What cannot be ported 1:1, and the replacement

| # | PostgreSQL feature | Replacement in SQLite / native |
|---|---|---|
| 1 | Row Level Security, roles `anon`/`authenticated`/`service_role`, `GRANT/REVOKE EXECUTE`, SECURITY DEFINER vs INVOKER | §5 app-layer rules; the app is the only DB client. |
| 2 | `auth.uid()`, JWT claims, GoTrue, `auth.users`, Edge Functions (`admin-create-user`, `create-organization`) | `users` table + `AuthService`/`UserService`; `app_context.actor_user_id` set per write tx. |
| 3 | `set_config('app.editing','on',true)` (tx-local flag that makes guard triggers stand down) | `app_context.editing` set to 1 inside the tx and back to 0 before COMMIT (rolls back on failure). Used only by recipes R5–R7. |
| 4 | BEFORE triggers that assign to `NEW` (code/seq/invoice number, discount math, denormalised ids) | GENERATED columns (discount/total/line total) and AFTER-INSERT/UPDATE triggers that `UPDATE` the row. Consequence: `order_seq`, `order_code`, `invoice_number`, `po_line_conversions.item_id/po_id`, `payments.site_id` (SO) are **filled after** the statement — re-SELECT; `INSERT … RETURNING` shows the pre-trigger values. |
| 5 | `RAISE EXCEPTION '…%…', values` | Static `CODE\|Arabic` messages; numbers re-queried by the service. |
| 6 | Sequences (`purchase_order_seq`, `sales_order_seq`, `nextval`, `setval`, `minvalue 0` hack in the PC prelude) | `doc_counters` rows `po_seq`/`so_seq` (year 0) bumped by UPSERT in the same tx; gap-free because the tx rolls back with the insert. |
| 7 | `NUMERIC(12,2/3)` | Scaled integers (§2). |
| 8 | `jsonb` return values (`get_dashboard`, `get_financial_summary`, `get_expense_summary`, `expense_due_list`, RPC results) | Typed DTOs built from the §10 queries. `audit_log.data` becomes JSON text (`json_object`). |
| 9 | `ALTER TYPE … ADD VALUE` (needed its own migration/transaction: 0013, 0034, 0037, 0041) | CHECK lists cannot be altered: a new value = SQLite "12-step" table rebuild (create new, copy, drop, rename; `PRAGMA foreign_keys=OFF` + `foreign_key_check`). Keep value lists in one migration helper. |
| 10 | `pg_advisory_xact_lock`, `SELECT … FOR UPDATE` (set_opening_stock, settle_contact_opening, admin editors) | `BEGIN IMMEDIATE` — single writer, so the read-modify-write recipes are race-free. |
| 11 | `generate_series`, `date_trunc`, `extract(isodow)`, `interval` (expense_due_list, monthly series) | Recursive CTE + `strftime` (C9 — verified identical output). |
| 12 | `pg_timezone_names` timezone validation | App validates IANA/Windows zone; DB only checks non-empty. |
| 13 | `now()` per-transaction, `current_date` (session TZ) | Timestamps: `strftime('%Y-%m-%dT%H:%M:%fZ','now')` (UTC, per statement). Dates: `date('now','localtime')` (Windows local clock) — or, better, the service passes the business-local date explicitly. |
| 14 | `timestamptz::date` casts (UTC) in dashboard/summary | Bind UTC `[start,end)` of the local day (C4/C5). |
| 15 | Supabase Storage (bucket `order-docs`, signed URLs, 3 `storage.objects` policies, `storage.foldername`) and the PC `pc_file_*` shim | `attachment_blobs` + `AttachmentService`; no signed URLs; limits are CHECKs. |
| 16 | Realtime publication, PostgREST, extensions | N/A. |
| 17 | `ON DELETE CASCADE` + trigger interplay | SQLite fires triggers for FK cascades only with `foreign_keys=ON` (hence the rule above). `expense_rule_skips` is append-only, so — exactly as in PG — a rule that has skips or posted expenses cannot be deleted (deactivate it). |
| 18 | Text collation/search | SQLite compares Arabic text bytewise. Sort/search in the app (`Intl.Collator('ar-EG')`, tashkeel/alef normalisation) or load ICU. `username` is lower-case ASCII by CHECK, so `=` is safe. |
| 19 | Multi-user access over the LAN (several app instances) | Out of scope: single PC, local file. If a second machine ever needs access, run the DB behind one service process; do not share the file. |
| 20 | `COMMENT ON …` metadata | Moved into SQL comments in `schema.sql`. |

## 7. Defects found in the web migrations, and hardening applied

### 7.1 Bugs reproduced on PostgreSQL 16.15 (fixed in the SQLite port)

| ID | Defect | Evidence | Port behaviour |
|---|---|---|---|
| **B1** | **Invoicing a sales order fails.** 0024 replaced `next_doc_number(text,int)` by `next_doc_number(uuid,text,int)`, but 0032 re-defined `so_status_transition()` with the old call `next_doc_number('so', y)`. | `update sales_orders set status='invoiced'` → `ERROR: function next_doc_number(unknown, integer) does not exist`. (Check whether the live Supabase project was patched out-of-band before assuming production is affected.) | `trg_so_assign_invoice_number` implements the intended `SO-YYYY-####`. |
| **B2** | **Admin-editing a *closed* sales order double-deducts stock.** `so_placement_apply` returns stock only for `placed→invoiced`; the editor's first step for a closed order is `closed→invoiced`, which matches no branch. Stock then gets deducted again on replay. | 10 sold, stock 40; after `admin_update_sales_order` on the closed order → stock **30** (expected 40). | `trg_so_placement_apply` returns stock for `{placed,closed}→{invoiced,draft}`; `closed→placed` (payment deleted) still does not. |
| **B3** | **Admin-editing a sales order that carries a fixed-amount discount fails.** The editor deletes the lines first; `trg_so_recompute_total` sets subtotal 0 and `so_apply_discount` raises "discount exceeds subtotal" before the RPC can rewrite the discount. | `ERROR: الخصم (20.00) يتجاوز إجمالي البنود (0.00)` from `admin_update_sales_order(...,'amount',20)` (reproduced on a draft order; the code path is identical for other statuses). | `discount_amount` is clamped by the generated column; the over-discount triggers stand down while `app_context.editing=1`. |

### 7.2 Gaps kept (same as web — the service layer must cover them)
* **G1** Deleting a `method='credit'` payment does not restore the consumed client credit / vendor advance (only cash payments are compensated). Post a compensating `client_credits`/`vendor_credits` row in the delete service.
* **G2** `update_itemized_po` does not check that the new total is ≥ amount already paid (`admin_update_purchase_order` does). Check in `PurchaseService`.
* **G3** `so_overpay`, `po_overpay`, `credit_deposit`, `vendor_credit_deposit` have no permission check (the 0014/0023 comments say "staff+"), so even an `accountant` could call them. Native: require `payments.record`.
* **G4** `pc_file_*` gate on role, not on `attachments.manage`.
* **G5** `dashboard.view` is UI-only in web (the DB filtered through RLS instead).
* **G6** `pc_create_user` cannot create an `accountant`.
* **G7** `supabase/seed.sql` predates 0024: it inserts without `org_id` and hashes with `crypt`, so by inspection it cannot run unchanged on the final schema (NOT NULL `org_id`, `current_org()` is NULL for a superuser). Irrelevant here — see §8.
* **G8** A recurring-expense rule that has posted expenses cannot be deleted (the FK `SET NULL` hits `expense_block_edit`); same in SQLite.

### 7.3 Hardening added in SQLite (marked `-- HARDENING:` in `schema.sql`)
1. `audit_log`, `auth_events`, `stock_transfers` made append-only (web relied on "no policy").
2. `payments` immutable (only the SO-site back-fill and the FK `SET NULL` of the account link are allowed); `po_conversions`, `po_line_conversions` UPDATE blocked ("delete + re-add" was only a missing policy).
3. Deleting a sales order that is `placed`/`closed` or has payments, or a purchase order with payments, is refused (would orphan stock/cash).
4. General-PO conversions are refused on itemized POs (web silently skipped the check via NULL arithmetic).
5. `cash_movements.reason` must be non-blank (rule 5), expense void reason non-blank, void is one-way, and the **compensating drawer row for a void is a trigger** (`trg_expense_void_post`) instead of RPC code.
6. `order_attachments`: mime allow-list + 10 MB as CHECKs, parent-existence trigger, blob cleanup trigger.
7. `trg_audit_business_settings` restores the business-identity audit that 0011 intended but 0024 lost.
8. Last-active-admin and self-lockout rules also enforced by triggers (web: RPC only).
9. `po_lines.line_total_piasters` is NOT NULL.

## 8. Seed

**`supabase/seed.sql`** (267 lines, demo only, never part of provisioning): 3 `auth.users` + `auth.identities` + `profiles` (admin أحمد جمعة / manager محمود شبانة / staff سيد عبد الله; demo password `shabana123`), **2 sites** (الفرع الرئيسي – دمياط, فرع رأس البر), **15 items** (aluminium catalogue, kg/unit), **10 contacts** with phones and payment methods, opening cash for both drawers (150,000 / 60,000 EGP), **8 purchase orders**, their **conversions** (which auto-post stock), PO payments, and **sales orders** in several lifecycle states with lines, placements and collections. It relies on the triggers (POs → conversions → stock → SOs → payments) and on running as superuser.

**`sqlite/seed.sql`** (new, minimal, idempotent) — and nothing else:
* `business_settings` row (name/address/phone from migration 0009; currency EGP; timezone Africa/Cairo)
* one default branch `الفرع الرئيسي`
* one placeholder admin `admin` with `password_hash = NULL`, `must_change_password = 1` — **no password exists anywhere**; the first-run screen sets it (Argon2id) and flips `app_meta.first_run_completed`. The login path must refuse any `password_hash IS NULL` user outside that flow.
* the 8 default expense categories (0042)
* `doc_counters` rows `po_seq`/`so_seq` = 0

Reference data the app cannot run without (`permission_keys` ×38, `role_presets`, `app_meta.schema_version`) is in `schema.sql` §10 so it exists before any user is inserted. If a demo dataset is wanted later, write it as service-level fixtures (not raw ledger inserts) so the triggers produce the stock/cash rows.

## 9. Service-layer protocol and recipes

**Every business operation:**
```
BEGIN IMMEDIATE;
UPDATE app_context SET actor_user_id = :uid, editing = 0;     -- editing = 1 only in R5–R7
... statements ...
-- (editing editors) UPDATE app_context SET editing = 0;
COMMIT;                  -- any error: ROLLBACK (app_context rolls back with it)
```
Authorization is checked **before** `BEGIN` (or inside, reading `v_user_permissions`). All `created_by` columns are set by the service to `:uid`.

**Error codes raised by the schema** (service maps `CODE` → Arabic label in `labels.ts`; the text after `|` is the web message without its interpolated figures):

| Code | Message (Arabic) |
|---|---|
| `ATTACHMENT_PARENT_NOT_FOUND` | الأمر المرفق به غير موجود |
| `CASH_FLOOR` | رصيد الخزينة في الفرع لا يكفي لهذه الحركة |
| `CASH_SITE_REQUIRED` | يجب تحديد الفرع (الخزينة) للدفعة النقدية |
| `CLIENT_CREDIT_NEGATIVE` | لا يمكن أن يصبح رصيد العميل سالبًا |
| `CONVERSION_REVERSE_INSUFFICIENT` | لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي |
| `DISCOUNT_BELOW_PAID` | المحصّل من العميل يتجاوز إجمالي الأمر بعد الخصم |
| `DISCOUNT_EXCEEDS_SUBTOTAL` | الخصم يتجاوز إجمالي البنود |
| `DISCOUNT_PERCENT_OVER_100` | نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪ |
| `EXPENSE_ALREADY_VOIDED` | هذا المصروف مُلغى بالفعل |
| `EXPENSE_CATEGORY_INACTIVE` | فئة المصروف غير مفعّلة |
| `EXPENSE_IMMUTABLE` | لا يمكن تعديل مصروف مُسجَّل — قم بإلغائه وسجّل مصروفًا صحيحًا |
| `LAST_ADMIN` | لا يمكن إزالة آخر مدير نظام في المنشأة |
| `LEDGER_READONLY` | هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة) |
| `ORDER_DELETE_BLOCKED` | لا يمكن حذف أمر منفّذ أو عليه دفعات — ألغِ التنفيذ واحذف الدفعات أولاً |
| `PAYMENT_IMMUTABLE` | لا يمكن تعديل دفعة مسجلة — احذفها وأعد تسجيلها |
| `PAYMENT_OVERPAY` | المبلغ يتجاوز المتبقي على الأمر |
| `PLC_EXCEEDS_REMAINING` | الكمية المحوّلة تتجاوز المتبقي من البند |
| `PM_NOT_OWNED` | طريقة الدفع المحددة لا تخص جهة الاتصال |
| `PO_CONVERSION_EXCEEDS_REMAINING` | الكمية المستهلكة تتجاوز المتبقي من أمر الشراء |
| `PO_LINE_NOT_FOUND` | بند الأمر غير موجود |
| `PO_NOT_FOUND` | أمر الشراء غير موجود |
| `PO_NOT_GENERAL` | التحويل بالكيلوهات متاح للأمر العام فقط |
| `PO_TOTAL_KG_FROZEN` | لا يمكن تعديل إجمالي الكيلوهات بعد بدء التحويل إلى المخزون |
| `ROW_IMMUTABLE` | لا يمكن تعديل هذا السجل — احذفه وأعد إدخاله |
| `SELF_ROLE_CHANGE` | لا يمكنك تغيير دورك أو تعطيل حسابك بنفسك |
| `SO_DRAFT_NO_PAYMENT` | لا يمكن تسجيل دفعة على مسودة أمر بيع — قم بالفوترة أولاً |
| `SO_INVOICE_NO_LINES` | لا يمكن فوترة أمر بيع بدون بنود |
| `SO_LINES_LOCKED` | لا يمكن تعديل بنود أمر بيع بعد فوترته |
| `SO_NOT_FOUND` | أمر البيع غير موجود |
| `SO_STOCK_INSUFFICIENT` | الكمية غير كافية في المخزون للصنف المطلوب |
| `SO_TRANSITION_ILLEGAL` | انتقال حالة غير مسموح به لأمر البيع |
| `STOCK_FLOOR` | لا يمكن تنفيذ الحركة: الرصيد الحالي للصنف في الفرع لا يكفي |
| `TRANSFER_STOCK_INSUFFICIENT` | الكمية غير كافية في الفرع المصدر للنقل |
| `VENDOR_CREDIT_NEGATIVE` | لا يمكن أن يصبح رصيد الدفعة المقدّمة سالبًا |

**Recipes** (the RPC bodies, as the sequence of statements the service issues; "T:" marks what a trigger does for you):

* **Cash-row dating (S-2).** `trg_payment_apply` writes the drawer row with `created_at = payments.paid_at`, so a back-dated cash payment lands on its own day for both the dashboard and the drawer (fixes DIV-27). The row stays append-only and the floor trigger still sums every row regardless of date. The compensating row of `trg_payment_delete_apply` keeps the time of deletion. Service-written rows that belong to a payment (R1/R3 excess/advance) must set `created_at` = the payment's `paid_at` explicitly.
* **R1 `so_overpay` / `po_overpay`** — gate `payments.record`. Validate `amount>0`, method≠credit; load order (`total`, client|vendor, site); `paid=SUM(payments)`; `remaining=max(total−paid,0)`; `excess=amount−remaining`; `drawer = coalesce(site, SO.site)` (PO: site required if cash). If `remaining>0`: `INSERT payments(parent, amount=remaining, method, site_id = cash?drawer:NULL, paid_at, note, contact_payment_method_id)` (T: cash row, auto-close). If `excess>0`: `INSERT client_credits(contact=client, +excess, 'overpayment', source_id=order, method, site_id = cash?drawer:NULL, occurred_on=local date(paid_at))` and, if cash, `INSERT cash_movements(drawer, +excess, 'payment', source_id=order, 'رصيد زائد من دفعة بيع')`. PO side: `vendor_credits` + `cash_movements(drawer, −excess, 'payment', 'دفعة مقدّمة زائدة لأمر شراء')`. Return `{paid: remaining, credit_added: max(excess,0)}`.
* **R2 `credit_apply` / `vendor_credit_apply`** — gate `payments.credit`. `amount>0`; order exists; `amount ≤ total−paid`; `amount ≤ credit balance` (`v_client_credit_balance`/`v_vendor_credit_balance`); `INSERT payments(…, method='credit', site_id NULL, note default 'دفع من رصيد العميل' / 'خصم من الدفعة المقدّمة للمورّد')`; `INSERT client_credits|vendor_credits(contact, −amount, 'applied', source_id=order, method NULL)`.
* **R3 `credit_deposit` / `vendor_credit_deposit`** — gate `payments.record`. `amount>0`; method ∈ {cash, instapay}; contact exists; if cash: site required and `INSERT cash_movements(site, +amount | −amount for vendor, 'manual', source_id=contact, reason = note ?? 'إيداع رصيد للعميل' | 'دفعة مقدّمة لمورّد')` (T: floor guard); `INSERT client_credits|vendor_credits(contact, +amount, 'deposit', method, site if cash, occurred_on = today, note)`.
* **R4 `credit_refund` / `vendor_credit_refund`** — gate `payments.credit`. As R3 with signs flipped (client: cash **out**, reason 'استرداد رصيد للعميل'; vendor: cash **in**, reason 'استرداد دفعة مقدّمة من مورّد'); credit row `−amount, 'refund'`; first check `amount ≤ credit balance`.
* **R5 `admin_update_sales_order(id, site, client, date, lines[], discount_type, discount_value)`** — gate `sales.edit_locked`. `SELECT status`; `UPDATE app_context SET editing=1`; if old ∈ {placed, closed}: `status='invoiced'` (T: stock returned — fixed B2); if old ≠ draft: `status='draft'`; `UPDATE site/client/date`; `DELETE` lines; `INSERT` new lines; `UPDATE discount_type, discount_value_x100` (after the lines); `total ≥ paid` else ROLLBACK ("المدفوع يتجاوز إجمالي الأمر بعد التعديل"); replay: if old ≠ draft `status='invoiced'`; if old ∈ {placed, closed} `status='placed'` (T: re-deducts; may raise `SO_STOCK_INSUFFICIENT`; auto-closes when collected); finally `editing=0`. Cash is untouched.
* **R6 `admin_update_purchase_order`** — gate `purchases.edit`; general POs only. `total_kg>0`; `total_kg ≥ SUM(conversions)`; `total ≥ SUM(payments)`; `editing=1`; `UPDATE vendor, product_name, notes, order_date, total_kg_milli, price_per_kg_piasters, total_amount_piasters, status = (converted ≥ total_kg ? 'fully_converted' : 'open')`; `editing=0`.
* **R7 `create_itemized_po` / `update_itemized_po`** — gates `purchases.create` / `purchases.edit`. Create: vendor required, ≥1 line, `INSERT purchase_orders(po_type='itemized', total_kg NULL, price_per_kg NULL, total 0, notes)`; per line create the item when `item_id` is empty (`name_ar` trimmed, unit, price 0, threshold 0, active) then `INSERT po_lines` (T: PO total = Σ line totals). Update: PO must be itemized; `UPDATE vendor, date, notes, status='open'`; `DELETE FROM po_lines WHERE po_id` (cascades the line conversions; T: guarded stock reversal, may raise `CONVERSION_REVERSE_INSUFFICIENT`); re-insert lines; **then verify new total ≥ paid (G2)**.
* **R8 `set_opening_stock(site, item, qty_milli, note)`** — admin. `qty ≥ 0`; site/item exist; `current = SUM(qty_delta_milli)` for the pair; `delta = qty − current`; if 0 return; `INSERT stock_movements(site, item, delta, 'opening', note ?? 'رصيد افتتاحي')` (T: audited, floor-guarded).
* **R9 `set_contact_opening_balance(contact, kind, amount, as_of, note)`** — admin. `kind ∈ {receivable, payable, client_advance, vendor_advance}`; amount ≥ 0; contact type must be client|both for receivable/client_advance and vendor|both for payable/vendor_advance. receivable/payable: `delta = amount − SUM(contact_opening_balances.kind)` → `INSERT contact_opening_balances(kind, delta, as_of)`. advances: `delta = amount − SUM(credits WHERE source_type='opening')` → `INSERT client_credits|vendor_credits(delta, 'opening', method NULL, site NULL, occurred_on = as_of)`. **Never writes cash.**
* **R10 `settle_contact_opening(contact, kind∈{receivable,payable}, amount, method∈{cash,instapay}, site, note)`** — gate `payments.record`. `0 < amount ≤ SUM(opening where kind)`; if cash: site required and `INSERT cash_movements(site, receivable ? +amount : −amount, 'manual', source_id=contact, note ?? 'تسوية رصيد افتتاحي')`; `INSERT contact_opening_balances(kind, −amount, as_of=today, note ?? 'تسوية رصيد افتتاحي')`.
* **R11 `void_expense(id, reason)`** — gate `expenses.void` + `site`. reason non-blank; not already voided; `UPDATE expenses SET voided_at = now(), voided_by = :uid, void_reason = trim(:reason)` (T: compensating cash row for cash expenses).
* **R12 record / settle / skip an expense** — gate `expenses.record` + `site`. Settle a due occurrence = `INSERT expenses(…, rule_id, due_date)` (unique per live occurrence); skip = `INSERT expense_rule_skips(rule_id, due_date, reason)`.
* **R13 users** — `create`: AUTHZ `users.manage` (non-admins cannot create admins); hash password; `INSERT users` (T: copies role preset). `setAccess`: one tx — `UPDATE users SET full_name, role, active, all_sites`; **replace** `user_permissions` (`DELETE` then `INSERT` the full set) and `user_sites`; reject unknown keys (FK does), reject `all_sites=0` with no branches, non-admins cannot grant keys they lack or touch admins. Login: look up by lower-cased `username`; refuse if `active=0`, `locked_until > now`, or `password_hash IS NULL`; on failure `failed_attempts+1` (lock e.g. 15 min after 5) and an `auth_events` row; on success reset counters, set `last_login_at`, create a `user_sessions` row (store `sha256(token)`).

## 10. Query cookbook (all executed against `schema.sql`)

Views already cover "now" balances: `v_stock_balance`, `v_stock_total`, `v_cash_drawer_balance`, `v_so_balance`, `v_po_balance`, `v_po_kg_progress`, `v_po_itemized_progress`, `v_client_credit_balance`, `v_vendor_credit_balance`, `v_contact_balance`, `v_low_stock`, `v_user_permissions`, `v_user_site_access`. Dated ("as of day D") forms need parameters:

**C1.** Stock on hand as of the end of a local day (get_stock with p_as_of). `:end_utc` = UTC instant of 00:00 local time on the NEXT day, formatted `YYYY-MM-DDTHH:MM:SS.000Z`; `:site` NULL = all branches.

```sql
SELECT COALESCE(SUM(qty_delta_milli), 0) AS qty_milli
  FROM stock_movements
 WHERE item_id = :item
   AND (:site IS NULL OR site_id = :site)
   AND created_at < :end_utc;
```

**C2.** Cash drawer as of the end of a local day (get_cash_balance). Opening of day D = the same query with D-1's `:end_utc` (rule 10).

```sql
SELECT COALESCE(SUM(amount_delta_piasters), 0) AS balance_piasters
  FROM cash_movements
 WHERE (:site IS NULL OR site_id = :site)
   AND created_at < :end_utc;
```

**C3.** Dashboard: sales invoiced on the day (non-draft), by order_date.

```sql
SELECT COALESCE(SUM(total_amount_piasters), 0) AS sales_total_piasters
  FROM sales_orders
 WHERE order_date = :day AND status <> 'draft'
   AND (:site IS NULL OR site_id = :site);
```

**C4.** Dashboard: collections by method on the day = SO payments (real tenders, not `credit`) + client-credit money-in (overpayment/deposit). `:start_utc`/`:end_utc` bound the LOCAL day; `occurred_on` is already a local date.

```sql
SELECT method, SUM(amt) AS amount_piasters FROM (
  SELECT p.method AS method, p.amount_piasters AS amt
    FROM payments p JOIN sales_orders so ON so.id = p.parent_id
   WHERE p.parent_type = 'so' AND p.method <> 'credit'
     AND p.paid_at >= :start_utc AND p.paid_at < :end_utc
     AND (:site IS NULL OR so.site_id = :site)
  UNION ALL
  SELECT cc.method, cc.amount_delta_piasters
    FROM client_credits cc
   WHERE cc.occurred_on = :day AND cc.amount_delta_piasters > 0
     AND cc.source_type IN ('overpayment','deposit') AND cc.method IS NOT NULL
     AND (:site IS NULL OR cc.site_id = :site)
) GROUP BY method;
```

**C5.** Dashboard: payments out by method on the day = PO payments (not `credit`) + vendor-advance money-out. (Mirror image of the query above.)

```sql
SELECT method, SUM(amt) AS amount_piasters FROM (
  SELECT p.method AS method, p.amount_piasters AS amt
    FROM payments p
   WHERE p.parent_type = 'po' AND p.method <> 'credit'
     AND p.paid_at >= :start_utc AND p.paid_at < :end_utc
     AND (:site IS NULL OR p.site_id = :site)
  UNION ALL
  SELECT vc.method, vc.amount_delta_piasters
    FROM vendor_credits vc
   WHERE vc.occurred_on = :day AND vc.amount_delta_piasters > 0
     AND vc.source_type IN ('overpayment','deposit') AND vc.method IS NOT NULL
     AND (:site IS NULL OR vc.site_id = :site)
) GROUP BY method;
```

**C6.** Dashboard: top 5 items sold on the day (quantity out).

```sql
SELECT i.name_ar, SUM(-sm.qty_delta_milli) AS moved_milli
  FROM stock_movements sm JOIN items i ON i.id = sm.item_id
 WHERE sm.source_type = 'sale'
   AND sm.created_at >= :start_utc AND sm.created_at < :end_utc
   AND (:site IS NULL OR sm.site_id = :site)
 GROUP BY i.name_ar ORDER BY moved_milli DESC LIMIT 5;
```

**C7.** Dashboard: low stock as of the end of the day (dated variant of `v_low_stock`).

```sql
SELECT i.id AS item_id, i.name_ar, s.id AS site_id, s.name_ar AS site_name,
       COALESCE(b.q, 0) AS qty_milli, i.low_stock_threshold_milli AS threshold_milli
  FROM items i CROSS JOIN sites s
  LEFT JOIN (SELECT site_id, item_id, SUM(qty_delta_milli) AS q FROM stock_movements
              WHERE created_at < :end_utc GROUP BY site_id, item_id) b
         ON b.site_id = s.id AND b.item_id = i.id
 WHERE i.active = 1 AND s.active = 1 AND i.low_stock_threshold_milli > 0
   AND (:site IS NULL OR s.id = :site)
   AND COALESCE(b.q, 0) <= i.low_stock_threshold_milli
 ORDER BY COALESCE(b.q, 0) * 1.0 / i.low_stock_threshold_milli;   -- ordering ratio only; REAL is fine here, never for money
```

**C8.** Financial summary: receivables / payables as of `:to` (local date) and inventory value at cost. Value is summed PER ROW with integer rounding so the total can never overflow int64.

```sql
SELECT
  (SELECT COALESCE(SUM(total_amount_piasters),0) FROM sales_orders WHERE status <> 'draft' AND order_date <= :to)
- (SELECT COALESCE(SUM(p.amount_piasters),0) FROM payments p JOIN sales_orders so ON so.id = p.parent_id
    WHERE p.parent_type = 'so' AND so.status <> 'draft' AND substr(p.paid_at,1,10) <= :to)
+ (SELECT COALESCE(SUM(amount_delta_piasters),0) FROM contact_opening_balances WHERE kind = 'receivable' AND as_of <= :to)
  AS receivables_piasters,
  (SELECT COALESCE(SUM((v.q * i.cost_price_piasters + 500) / 1000), 0)
     FROM (SELECT site_id, item_id, SUM(qty_delta_milli) AS q FROM stock_movements
            WHERE created_at < :end_utc AND (:site IS NULL OR site_id = :site) GROUP BY site_id, item_id) v
     JOIN items i ON i.id = v.item_id AND i.active = 1
     JOIN sites s ON s.id = v.site_id AND s.active = 1) AS inventory_value_piasters;
```

**C9.** expense_due_list: recurring occurrences that fell due (max 180 days back) and are neither posted nor skipped. Weekly uses ISO weekday (1 = Monday); monthly clamps day_of_month to the month length.

```sql
WITH RECURSIVE occ(rule_id, d) AS (
  SELECT r.id, max(r.start_date, date(:as_of, '-180 days'))
    FROM expense_rules r
   WHERE r.active = 1 AND (:site IS NULL OR r.site_id = :site)
     AND max(r.start_date, date(:as_of, '-180 days')) <= min(COALESCE(r.end_date, :as_of), :as_of)
  UNION ALL
  SELECT o.rule_id, date(o.d, '+1 day')
    FROM occ o JOIN expense_rules r ON r.id = o.rule_id
   WHERE date(o.d, '+1 day') <= min(COALESCE(r.end_date, :as_of), :as_of)
)
SELECT r.id AS rule_id, r.name_ar, r.frequency, c.name_ar AS category_name, s.name_ar AS site_name,
       r.amount_piasters, r.method, o.d AS due_date,
       CAST(julianday(:as_of) - julianday(o.d) AS INTEGER) AS days_late
  FROM occ o
  JOIN expense_rules r      ON r.id = o.rule_id
  JOIN expense_categories c ON c.id = r.category_id
  JOIN sites s              ON s.id = r.site_id
 WHERE CASE r.frequency
         WHEN 'daily'  THEN 1
         WHEN 'weekly' THEN (CASE strftime('%w', o.d) WHEN '0' THEN 7 ELSE CAST(strftime('%w', o.d) AS INTEGER) END) = r.day_of_week
         ELSE CAST(strftime('%d', o.d) AS INTEGER)
              = min(r.day_of_month, CAST(strftime('%d', date(o.d, 'start of month', '+1 month', '-1 day')) AS INTEGER))
       END
   AND NOT EXISTS (SELECT 1 FROM expenses e WHERE e.rule_id = r.id AND e.due_date = o.d AND e.voided_at IS NULL)
   AND NOT EXISTS (SELECT 1 FROM expense_rule_skips k WHERE k.rule_id = r.id AND k.due_date = o.d)
 ORDER BY o.d, r.name_ar;
```



Opening of day D = closing of D−1 for **both stock and cash** (rule 10): run C1/C2 with the end-of-(D−1) bound.

## 11. Migrating existing data (PostgreSQL → SQLite)

1. Create **Phase 1** only: run `schema.sql` up to the marker `>>> PHASE 1 ENDS HERE` (tables, indexes, reference data). If you run the whole file, import with the triggers **dropped**, otherwise every historical row is posted twice.
2. Export from PG with conversions: money `(round(x*100))::bigint`; qty `(round(q*1000))::bigint`; `timestamptz` → `to_char(t AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')`; `date` → `to_char(d,'YYYY-MM-DD')`; boolean → `::int`; `jsonb` → `::text`; drop `org_id`.
3. Load in FK order: `sites`, `users` (from `profiles`; **same UUIDs**; `auth.users` hashes are bcrypt (`$2a$/$2b$/$2y$`) and **are accepted by the CHECK since amendment S-1** (`GLOB '$2[aby]$*'`, verify-only; rehash to Argon2id on first login) → import `password_hash` as is with `must_change_password` from the source; only NULL/empty/non-bcrypt values become `password_hash = NULL, must_change_password = 1`, see 05 §7), `user_permissions`, `user_sites`, `contacts`, `contact_phones`, `contact_payment_methods`, `items`, `purchase_orders` (keep `order_code`, `order_seq`), `po_conversions`, `po_lines`, `po_line_conversions`, `sales_orders` (**do not insert** `discount_amount`/`total_amount` — generated; insert `subtotal`, `discount_type`, `discount_value_x100`), `sales_order_lines` (`line_total` generated), `payments`, `stock_movements`, `stock_transfers`, `cash_movements`, `client_credits`, `vendor_credits`, `contact_opening_balances`, `doc_counters` (`po`,`so` by year; `po_seq`/`so_seq` year 0 = max `order_seq`), `order_attachments` + `attachment_blobs` (from `pc_file_bytes`), `audit_log`, `expense_*`, `business_settings` (from `organization`).
4. Run Phase 2 (triggers + views).
5. **Verify before go-live** (all must be 0 differences): per `(site,item)` stock vs PG `get_stock`; per site drawer vs `get_cash_balance`; per contact `v_contact_balance` vs `get_contact_balance`; per SO `total_amount_piasters` and per line `line_total_piasters` vs PG ×100 (proves the generated columns reproduce history); row counts of every table; `PRAGMA foreign_key_check`; `PRAGMA integrity_check`.

## 12. Self-check — catalog vs migrations

| Object | grep count in web `0001–0044` | + PC-only `0100/0101` | **Unique names** | **Catalog rows in this file** | Result |
|---|---|---|---|---|---|
| `CREATE TABLE` | 32 (`organization` twice) | 1 (`pc_file_bytes`) | 32 | §3 maps **32** PG tables (+5 new SQLite tables) | OK — every PG table appears in §3 |
| `CREATE [OR REPLACE] FUNCTION` | 117 definitions | 9 | 93 | 93 | OK (84 web names + 9 PC = 93; matches `pg_proc` on the verification instance, 84) |
| `CREATE POLICY` | 216 statements | 0 | 85 (table,name) pairs; **82 live** | 85 catalog rows; 82 matrix rows | OK — matches `pg_policies` (82) |
| `CREATE TRIGGER` (static) | 73 statements | 0 | 71 | 71 (+34 dynamic, all DROP) | OK |

The generator that produced §4 refuses to run if a name found in the migrations is missing from the catalog or vice-versa (assertions on `set(found) == set(catalog)`), and the policy matrix asserts 82 rows.

SQLite side: 36 tables · 13 views · 108 triggers (38 audit) · 61 indexes.

## 13. Appendix — smoke-test output (`cd sqlite && sqlite3 :memory: < smoke-test.sql`)

```
>>> expecting 1 error: PO_CONVERSION_EXCEEDS_REMAINING
Runtime error near line 25: PO_CONVERSION_EXCEEDS_REMAINING|الكمية المستهلكة تتجاوز المتبقي من أمر الشراء (19)
>>> expecting 2 errors: LEDGER_READONLY
Runtime error near line 33: LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة) (19)
Runtime error near line 34: LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة) (19)
>>> expecting 1 error: PAYMENT_OVERPAY
Runtime error near line 49: PAYMENT_OVERPAY|المبلغ يتجاوز المتبقي على الأمر (19)

===== SMOKE RESULT =====
┌──────────────────────────────────────────────────────┬────────┐
│                         name                         │ result │
├──────────────────────────────────────────────────────┼────────┤
│ PO converted_kg <= total_kg: over-conversion refused │ PASS   │
│ append-only: stock_movements UPDATE/DELETE blocked   │ PASS   │
│ stock view: 50 - 10 = 40 units                       │ PASS   │
│ overpayment blocked (still exactly 1 payment)        │ PASS   │
│ v_so_balance = 25.00 EGP outstanding                 │ PASS   │
│ SO auto-closed when collected == total               │ PASS   │
│ v_cash_drawer_balance = 125.00 EGP                   │ PASS   │
│ v_contact_balance = -1000.00 (we owe vendor)         │ PASS   │
│ audit_log captured the payments                      │ PASS   │
└──────────────────────────────────────────────────────┴────────┘
┌───────────────────┐
│      verdict      │
├───────────────────┤
│ ALL CHECKS PASSED │
└───────────────────┘
```
