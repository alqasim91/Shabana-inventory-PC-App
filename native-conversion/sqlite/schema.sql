-- ============================================================================
-- schema.sql — مخزون شبانه (شبانة لتجارة الألوميتال) — SQLite schema
-- Native Windows desktop edition, single PC, single local file database.
--
-- EQUIVALENT TO: the FINAL state of supabase/migrations/0001..0044 (web app)
--                + the data-bearing parts of the PC-only 0100/0101 migrations.
-- Target engine: SQLite >= 3.38 (STRICT tables need 3.37, generated columns 3.31,
--                RETURNING 3.35). Developed and verified on 3.45.1.
--
-- HOW TO LOAD
--   sqlite3 shabana.db < schema.sql          (then run seed.sql once)
--   Every connection must run  PRAGMA foreign_keys = ON;  (it is per-connection
--   and OFF by default in SQLite — the statement below only covers this script).
--
-- RECOMMENDED CONNECTION SETTINGS (set by the app on open; not stored in the file
-- except journal_mode which persists):
--   PRAGMA journal_mode = WAL;        -- readers never block the writer; crash safe
--   PRAGMA synchronous  = NORMAL;     -- with WAL: durable across app crash; FULL if
--                                     --   you want durability across power loss too
--   PRAGMA foreign_keys = ON;
--   PRAGMA busy_timeout = 5000;
--   PRAGMA recursive_triggers = OFF;  -- default. The schema is written so it is
--                                     --   correct with it OFF *or* ON.
--   Writes: use BEGIN IMMEDIATE for every business operation (one writer at a
--   time; also what makes the app_context protocol below safe).
--
-- ----------------------------------------------------------------------------
-- UNITS & TYPE CONVENTIONS (decision record — see 03-DB-PORTING-NOTES.md §2)
-- ----------------------------------------------------------------------------
--  Money  NUMERIC(12,2) EGP        -> INTEGER PIASTERS (EGP x 100).  Column
--         suffix `_piasters`. Never REAL. 1234.56 EGP is stored as 123456.
--  Qty/KG NUMERIC(12,3)            -> INTEGER MILLI-UNITS (x 1000). Column suffix
--         `_milli`. 2.5 kg is stored as 2500. Applies to kg and to unit counts.
--  Discount value (NUMERIC(12,2))  -> `discount_value_x100`: the number the user
--         typed x 100. For discount_type='amount' that is piasters; for
--         'percent' it is basis points (12.5 % = 1250).
--  Line total = round_half_up(qty_milli * unit_price_piasters / 1000), done in
--         integer arithmetic: (qty_milli * price + 500) / 1000.
--  RANGE LIMITS (deliberately tighter than Postgres so a product can never
--         overflow int64 — SQLite silently turns an overflowing integer product
--         into REAL, which would corrupt money): qty_milli on lines <= 1e9
--         (1,000,000.000), unit prices <= 1e9 piasters (10,000,000.00 EGP),
--         every stored money total <= 999,999,999,999 piasters (= NUMERIC(12,2)).
--  UUID   uuid                     -> TEXT, 36-char lower-case v4 (default
--         expression below). Existing PG UUIDs migrate unchanged.
--  Time   timestamptz              -> TEXT ISO-8601 UTC, fixed shape
--         'YYYY-MM-DDTHH:MM:SS.sssZ' (so string order == time order). A CHECK on
--         every column rejects any other shape.
--  Date   date                     -> TEXT 'YYYY-MM-DD' (business-local calendar
--         date; defaults use the Windows local clock, i.e. Africa/Cairo on the PC).
--  Bool   boolean                  -> INTEGER 0/1 with CHECK.
--  Enum   CREATE TYPE ... AS ENUM  -> TEXT + CHECK (col IN (...)).
--  jsonb  audit_log.data           -> TEXT validated by json_valid().
--  "Day" semantics: the web app cut days at UTC midnight (session TZ). The native
--         app should cut at business-local midnight: convert the local day to a
--         UTC [start, end) pair in the service layer and bind it as parameters.
--
-- TENANCY: single business per database file. org_id / organization / slug /
--          platform_admins / provisioning are DROPPED; `organization` becomes the
--          one-row table business_settings. (multi-tenant-plan.md + PC-DIVERGENCE
--          .md: the PC edition always has exactly one organization.)
--
-- AUTH:    auth.users + profiles are merged into `users` (own password hash,
--          lockout, must_change_password). No JWT, no GoTrue.
--
-- AUTHORIZATION: SQLite has no RLS. Role/permission/branch rules live in the app
--          service layer (see §RLS matrix in the porting notes). This schema
--          enforces DATA invariants (append-only ledgers, floors, overpayment,
--          lifecycle, caps) with triggers/CHECKs, which hold no matter which code
--          path writes.
--
-- TRIGGER ERRORS: SQLite's RAISE() accepts only a literal message, so messages
--          are static Arabic strings prefixed with a stable machine code:
--              'PAYMENT_OVERPAY|المبلغ يتجاوز المتبقي على الأمر'
--          The service layer splits on the first '|', maps the code, and may
--          re-query the numbers the Postgres messages used to interpolate.
--
-- app_context PROTOCOL (replaces auth.uid() and set_config('app.editing')):
--          inside the SAME write transaction as the business operation, the
--          service runs   UPDATE app_context SET actor_user_id = :uid, editing = 0;
--          and (admin order editors only) sets editing = 1 for the rewind/rewrite
--          window. Because it is written inside the transaction it rolls back with
--          it and can never leak to another operation; BEGIN IMMEDIATE serialises
--          writers. (Alternative: register app functions app_actor()/app_editing()
--          per connection and replace the two subqueries — mechanical.)
--          created_by columns are filled BY THE SERVICE (SQLite has no
--          column default that can read app_context); triggers that write ledger
--          rows copy created_by from the parent row or use the actor.
--
-- HARDENING vs the web schema is marked `-- HARDENING:` and listed in the notes.
-- ============================================================================

PRAGMA foreign_keys = ON;
-- PRAGMA journal_mode = WAL;     -- run once on the real file (no effect in :memory:)
PRAGMA user_version = 1;          -- schema version; bump with each future migration

-- ============================================================================
-- 1. APP META, CONTEXT, SETTINGS
-- ============================================================================

CREATE TABLE app_meta (
  key   TEXT NOT NULL PRIMARY KEY,
  value TEXT NOT NULL
) STRICT, WITHOUT ROWID;

-- One row. Carries "who is acting" and the "admin edit window" flag into triggers.
CREATE TABLE app_context (
  id            INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
  actor_user_id TEXT,                                   -- no FK on purpose
  editing       INTEGER NOT NULL DEFAULT 0 CHECK (editing IN (0, 1))
) STRICT;
INSERT INTO app_context (id, actor_user_id, editing) VALUES (1, NULL, 0);

-- organization (singleton) + currency/timezone from 0033. Display settings only.
CREATE TABLE business_settings (
  id            INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
  business_name TEXT    NOT NULL CHECK (trim(business_name) <> ''),
  address_line  TEXT,
  phone_line    TEXT,
  currency      TEXT    NOT NULL DEFAULT 'EGP'
                CHECK (currency IN ('EGP','SAR','AED','QAR','USD','EUR','TRY','MAD','LBP','ILS','DZD','SYP')),
  timezone      TEXT    NOT NULL DEFAULT 'Africa/Cairo' CHECK (trim(timezone) <> ''),  -- IANA name; validated by the app (no pg_timezone_names)
  updated_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at) IS updated_at)
) STRICT;

-- sites — فروع
CREATE TABLE sites (
  id         TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  name_ar    TEXT    NOT NULL CHECK (trim(name_ar) <> ''),
  active     INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  created_at TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;

-- ============================================================================
-- 2. IDENTITY & ACCESS  (auth.users + profiles + user_permissions + user_sites)
-- ============================================================================

CREATE TABLE users (
  id                   TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  username             TEXT    NOT NULL UNIQUE
                       CHECK (length(username) BETWEEN 1 AND 64
                              AND username NOT GLOB '*[^a-z0-9._-]*'),   -- same rule as pc_create_user: ^[a-z0-9._-]+$
  email                TEXT    UNIQUE CHECK (email IS NULL OR instr(email, '@') > 1),  -- optional; login is by username
  full_name            TEXT    NOT NULL CHECK (trim(full_name) <> ''),
  role                 TEXT    NOT NULL DEFAULT 'staff'
                       CHECK (role IN ('admin','manager','staff','accountant')),
  active               INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  all_sites            INTEGER NOT NULL DEFAULT 1 CHECK (all_sites IN (0, 1)),  -- 0 => only rows in user_sites
  -- Encoded password hash, self-describing, e.g.
  --   $argon2id$v=19$m=65536,t=3,p=1$<salt_b64>$<hash_b64>      (preferred)
  --   $pbkdf2-sha256$i=600000$<salt_b64>$<hash_b64>             (fallback)
  -- NULL only for the first-run placeholder admin (must_change_password = 1).
  password_hash        TEXT    CHECK (password_hash IS NULL
                                      OR password_hash GLOB '$argon2id$*'
                                      OR password_hash GLOB '$pbkdf2-sha256$*'),
  password_changed_at  TEXT    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', password_changed_at) IS password_changed_at),
  must_change_password INTEGER NOT NULL DEFAULT 1 CHECK (must_change_password IN (0, 1)),
  failed_attempts      INTEGER NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
  locked_until         TEXT    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', locked_until) IS locked_until),          -- login refused while now < locked_until
  last_login_at        TEXT    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', last_login_at) IS last_login_at),
  created_at           TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  updated_at           TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at) IS updated_at),
  CHECK (password_hash IS NOT NULL OR must_change_password = 1)
) STRICT;

-- Desktop sessions (idle timeout / lock screen / "log out everywhere"). Optional
-- for a single-user-at-a-time app but cheap. Store only a HASH of the token.
CREATE TABLE user_sessions (
  id           TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  user_id      TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash   TEXT NOT NULL UNIQUE,
  created_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  last_seen_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', last_seen_at) IS last_seen_at),
  expires_at   TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', expires_at) IS expires_at),
  revoked_at   TEXT CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', revoked_at) IS revoked_at),
  client_info  TEXT
) STRICT;
CREATE INDEX user_sessions_user_idx ON user_sessions(user_id);

-- Security event log (login ok/fail, lockout, password change, session end).
-- Separate from audit_log (which snapshots business rows). Append-only.
CREATE TABLE auth_events (
  id         TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  user_id    TEXT REFERENCES users(id),
  username   TEXT,                                     -- as typed, even if no such user
  event      TEXT NOT NULL CHECK (event IN ('login_ok','login_failed','locked','unlocked',
                                            'logout','password_changed','password_reset',
                                            'session_expired')),
  detail     TEXT,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX auth_events_user_idx    ON auth_events(user_id, created_at);
CREATE INDEX auth_events_created_idx ON auth_events(created_at);

-- Vocabulary of what can be permitted (0031 + 0040 + 0042). New keys arrive by migration.
CREATE TABLE permission_keys (
  key  TEXT    NOT NULL PRIMARY KEY,
  area TEXT    NOT NULL,
  sort INTEGER NOT NULL,
  note TEXT
) STRICT, WITHOUT ROWID;

-- role_preset_permissions(app_role) function -> a table. Presets are COPIED to a
-- user when the user is created (trg_users_seed_permissions); editing a preset
-- later touches nobody. admin = every key (and admins bypass the rows anyway).
CREATE TABLE role_presets (
  role TEXT NOT NULL CHECK (role IN ('admin','manager','staff','accountant')),
  perm TEXT NOT NULL REFERENCES permission_keys(key) ON DELETE CASCADE,
  PRIMARY KEY (role, perm)
) STRICT, WITHOUT ROWID;

CREATE TABLE user_permissions (
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  perm    TEXT NOT NULL REFERENCES permission_keys(key),
  PRIMARY KEY (user_id, perm)
) STRICT, WITHOUT ROWID;

-- Branch scoping. all_sites=0 with NO rows here means "no branch at all"
-- (an empty set must never mean "everywhere" — 0031 design note).
CREATE TABLE user_sites (
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  site_id TEXT NOT NULL REFERENCES sites(id) ON DELETE CASCADE,
  PRIMARY KEY (user_id, site_id)
) STRICT, WITHOUT ROWID;
CREATE INDEX user_sites_site_idx ON user_sites(site_id);

-- ============================================================================
-- 3. MASTER DATA: contacts, items
-- ============================================================================

CREATE TABLE contacts (
  id         TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  type       TEXT NOT NULL CHECK (type IN ('vendor','client','both')),
  name       TEXT NOT NULL CHECK (trim(name) <> ''),
  address    TEXT,
  notes      TEXT,
  created_by TEXT REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX contacts_type_idx       ON contacts(type);
CREATE INDEX contacts_created_by_idx ON contacts(created_by);

-- Exactly one primary phone per contact at most (rule 8: "primary mandatory" is
-- an app rule in the web schema too — only uniqueness is a DB constraint).
CREATE TABLE contact_phones (
  id         TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  contact_id TEXT    NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
  phone      TEXT    NOT NULL CHECK (trim(phone) <> ''),
  is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1))
) STRICT;
CREATE INDEX contact_phones_contact_idx ON contact_phones(contact_id);
CREATE UNIQUE INDEX contact_phones_one_primary ON contact_phones(contact_id) WHERE is_primary = 1;

CREATE TABLE contact_payment_methods (
  id              TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  contact_id      TEXT NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
  method          TEXT NOT NULL CHECK (method IN ('cash','instapay','bank_transfer')),
  instapay_number TEXT,
  bank_name       TEXT,
  account_number  TEXT,
  CONSTRAINT contact_pm_shape CHECK (
    (method = 'instapay'      AND instapay_number IS NOT NULL) OR
    (method = 'bank_transfer' AND bank_name IS NOT NULL AND account_number IS NOT NULL) OR
    (method = 'cash')
  )
) STRICT;
CREATE INDEX contact_pm_contact_idx ON contact_payment_methods(contact_id);

CREATE TABLE items (
  id                        TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  name_ar                   TEXT    NOT NULL CHECK (trim(name_ar) <> ''),
  unit_type                 TEXT    NOT NULL CHECK (unit_type IN ('kg','unit')),
  low_stock_threshold_milli INTEGER NOT NULL DEFAULT 0 CHECK (low_stock_threshold_milli BETWEEN 0 AND 999999999999),
  sale_price_piasters       INTEGER NOT NULL DEFAULT 0 CHECK (sale_price_piasters BETWEEN 0 AND 1000000000),
  cost_price_piasters       INTEGER NOT NULL DEFAULT 0 CHECK (cost_price_piasters BETWEEN 0 AND 1000000000),  -- 0040: valuation only
  active                    INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  created_at                TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;


-- ============================================================================
-- 4. PURCHASING
-- ============================================================================

-- po_type 'general'  : bought in KG, converted into stock through po_conversions.
-- po_type 'itemized' : plain line list (po_lines), received through po_line_conversions.
-- order_code (PO-YYYY-####) and order_seq (running 1,2,3 shown as «أمر شراء N») are
-- assigned by AFTER INSERT triggers from doc_counters. order_seq therefore reads
-- 0 inside the INSERT statement (and in its RETURNING clause): RE-SELECT the row
-- after inserting. 0 means "not yet assigned" and is excluded from uniqueness.
CREATE TABLE purchase_orders (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  order_code            TEXT    UNIQUE,
  order_seq             INTEGER NOT NULL DEFAULT 0 CHECK (order_seq >= 0),
  po_type               TEXT    NOT NULL DEFAULT 'general' CHECK (po_type IN ('general','itemized')),
  vendor_id             TEXT    NOT NULL REFERENCES contacts(id),
  order_date            TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(order_date) IS order_date),
  total_kg_milli        INTEGER CHECK (total_kg_milli > 0 AND total_kg_milli <= 999999999999),
  price_per_kg_piasters INTEGER CHECK (price_per_kg_piasters >= 0 AND price_per_kg_piasters <= 999999999999),
  total_amount_piasters INTEGER NOT NULL CHECK (total_amount_piasters BETWEEN 0 AND 999999999999),
  status                TEXT    NOT NULL DEFAULT 'open' CHECK (status IN ('open','fully_converted','closed')),
  product_name          TEXT,
  notes                 TEXT,
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CONSTRAINT po_type_fields_chk CHECK (
       (po_type = 'general'  AND total_kg_milli IS NOT NULL AND price_per_kg_piasters IS NOT NULL)
    OR (po_type = 'itemized' AND total_kg_milli IS NULL     AND price_per_kg_piasters IS NULL)
  )
) STRICT;
CREATE UNIQUE INDEX po_order_seq_key ON purchase_orders(order_seq) WHERE order_seq > 0;
CREATE INDEX po_vendor_idx             ON purchase_orders(vendor_id);
CREATE INDEX po_status_idx             ON purchase_orders(status);
CREATE INDEX purchase_orders_created_by_idx ON purchase_orders(created_by);

-- General PO: free-entry conversion of purchased KG into stock at ONE site (rule 2).
CREATE TABLE po_conversions (
  id                  TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  po_id               TEXT    NOT NULL REFERENCES purchase_orders(id) ON DELETE CASCADE,
  site_id             TEXT    NOT NULL REFERENCES sites(id),
  item_id             TEXT    NOT NULL REFERENCES items(id),
  kg_consumed_milli   INTEGER NOT NULL CHECK (kg_consumed_milli  BETWEEN 1 AND 999999999999),
  output_qty_milli    INTEGER NOT NULL CHECK (output_qty_milli   BETWEEN 1 AND 999999999999),
  output_unit         TEXT    NOT NULL CHECK (output_unit IN ('kg','unit')),
  conversion_date     TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(conversion_date) IS conversion_date),
  created_by          TEXT    REFERENCES users(id),
  created_at          TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX po_conv_po_idx   ON po_conversions(po_id);
CREATE INDEX po_conv_site_idx ON po_conversions(site_id);
CREATE INDEX po_conv_item_idx ON po_conversions(item_id);
CREATE INDEX po_conversions_created_by_idx ON po_conversions(created_by);

-- Itemized PO lines. line_total is a plain stored value (0020: the UI drives it
-- from the total and back-computes unit_price). NOT NULL here (nullable in PG).
-- site_id is the unused legacy 0019 column, kept nullable so PG rows migrate 1:1.
CREATE TABLE po_lines (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  po_id                 TEXT    NOT NULL REFERENCES purchase_orders(id) ON DELETE CASCADE,
  item_id               TEXT    NOT NULL REFERENCES items(id),
  site_id               TEXT    REFERENCES sites(id),
  qty_milli             INTEGER NOT NULL CHECK (qty_milli BETWEEN 1 AND 1000000000),
  unit_price_piasters   INTEGER NOT NULL CHECK (unit_price_piasters BETWEEN 0 AND 1000000000),
  line_total_piasters   INTEGER NOT NULL CHECK (line_total_piasters BETWEEN 0 AND 999999999999),
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX po_lines_po_idx   ON po_lines(po_id);
CREATE INDEX po_lines_item_idx ON po_lines(item_id);
CREATE INDEX po_lines_site_idx ON po_lines(site_id);

-- Receive part/all of an itemized line into a branch (splittable). item_id/po_id
-- are denormalised (0021) so reversal never needs the parent line to still exist;
-- trg_plc_apply fills them right after insert (they are NULL inside the INSERT).
CREATE TABLE po_line_conversions (
  id              TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  po_line_id      TEXT    NOT NULL REFERENCES po_lines(id) ON DELETE CASCADE,
  site_id         TEXT    NOT NULL REFERENCES sites(id),
  qty_milli       INTEGER NOT NULL CHECK (qty_milli BETWEEN 1 AND 1000000000),
  conversion_date TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(conversion_date) IS conversion_date),
  item_id         TEXT    REFERENCES items(id),
  po_id           TEXT,                                  -- denormalised, deliberately no FK (0021)
  created_by      TEXT    REFERENCES users(id),
  created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX plc_line_idx ON po_line_conversions(po_line_id);
CREATE INDEX plc_site_idx ON po_line_conversions(site_id);
CREATE INDEX plc_po_idx   ON po_line_conversions(po_id);

-- ============================================================================
-- 5. SALES
-- ============================================================================

-- total_amount = subtotal - discount ("what the client owes"; everything
-- downstream reads it). subtotal is maintained by line triggers; discount_amount
-- and total_amount are GENERATED so they can never disagree with their inputs
-- (the web needed a BEFORE trigger for this; SQLite triggers cannot rewrite NEW).
-- discount_amount is clamped to subtotal so total can never go negative; the
-- trg_so_discount_* triggers REJECT an over-discount unless app_context.editing=1
-- (admin editor deletes the lines mid-edit — that is what broke 0030's editor).
-- order_seq / invoice_number: see purchase_orders note. invoice_number is set by
-- trg_so_assign_invoice_number on draft -> invoiced.
CREATE TABLE sales_orders (
  id                       TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  order_seq                INTEGER NOT NULL DEFAULT 0 CHECK (order_seq >= 0),
  site_id                  TEXT    NOT NULL REFERENCES sites(id),
  client_id                TEXT    NOT NULL REFERENCES contacts(id),
  status                   TEXT    NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','invoiced','placed','closed')),
  order_date               TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(order_date) IS order_date),
  invoice_number           TEXT    UNIQUE,
  subtotal_piasters        INTEGER NOT NULL DEFAULT 0 CHECK (subtotal_piasters BETWEEN 0 AND 999999999999),
  discount_type            TEXT    NOT NULL DEFAULT 'none' CHECK (discount_type IN ('none','amount','percent')),
  discount_value_x100      INTEGER NOT NULL DEFAULT 0 CHECK (discount_value_x100 BETWEEN 0 AND 999999999999),
  discount_amount_piasters INTEGER GENERATED ALWAYS AS (
      min(subtotal_piasters,
          CASE discount_type
            WHEN 'percent' THEN (subtotal_piasters * discount_value_x100 + 5000) / 10000
            WHEN 'amount'  THEN discount_value_x100
            ELSE 0
          END)
  ) STORED,
  total_amount_piasters    INTEGER GENERATED ALWAYS AS (subtotal_piasters - discount_amount_piasters) STORED,
  created_by               TEXT    REFERENCES users(id),
  created_at               TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CONSTRAINT so_discount_none_zero  CHECK (discount_type <> 'none' OR discount_value_x100 = 0),
  CONSTRAINT so_discount_pct_max    CHECK (discount_type <> 'percent' OR discount_value_x100 <= 10000),
  CONSTRAINT so_discount_amount_range CHECK (discount_amount_piasters >= 0 AND discount_amount_piasters <= subtotal_piasters)
) STRICT;
CREATE UNIQUE INDEX so_order_seq_key ON sales_orders(order_seq) WHERE order_seq > 0;
CREATE INDEX so_site_idx   ON sales_orders(site_id);
CREATE INDEX so_client_idx ON sales_orders(client_id);
CREATE INDEX so_status_idx ON sales_orders(status);
CREATE INDEX sales_orders_created_by_idx ON sales_orders(created_by);

CREATE TABLE sales_order_lines (
  id                  TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  so_id               TEXT    NOT NULL REFERENCES sales_orders(id) ON DELETE CASCADE,
  item_id             TEXT    NOT NULL REFERENCES items(id),
  qty_milli           INTEGER NOT NULL CHECK (qty_milli BETWEEN 1 AND 1000000000),
  unit_price_piasters INTEGER NOT NULL CHECK (unit_price_piasters BETWEEN 0 AND 1000000000),
  line_total_piasters INTEGER GENERATED ALWAYS AS ((qty_milli * unit_price_piasters + 500) / 1000) STORED
) STRICT;
CREATE INDEX sol_so_idx                  ON sales_order_lines(so_id);
CREATE INDEX sales_order_lines_item_id_idx ON sales_order_lines(item_id);

-- ============================================================================
-- 6. PAYMENTS, CREDIT WALLETS, OPENING BALANCES, CASH
-- ============================================================================

-- Shared ledger for POs and SOs. parent_id is polymorphic (no FK) — existence is
-- enforced by trg_payment_validate. site_id = the drawer a CASH payment touches
-- (for SO payments it is back-filled from the SO right after insert).
-- method 'credit' = paid out of the client's stored credit / vendor advance (never
-- touches a drawer). Rows are immutable (trg_payments_immutable); correct a
-- payment by deleting it (admin) and re-adding — the delete compensates the drawer.
CREATE TABLE payments (
  id                        TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  parent_type               TEXT    NOT NULL CHECK (parent_type IN ('po','so')),
  parent_id                 TEXT    NOT NULL,
  amount_piasters           INTEGER NOT NULL CHECK (amount_piasters BETWEEN 1 AND 999999999999),
  method                    TEXT    NOT NULL CHECK (method IN ('cash','instapay','bank_transfer','credit')),
  site_id                   TEXT    REFERENCES sites(id),
  paid_at                   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', paid_at) IS paid_at),
  note                      TEXT,
  contact_payment_method_id TEXT    REFERENCES contact_payment_methods(id) ON DELETE SET NULL,
  created_by                TEXT    REFERENCES users(id),
  created_at                TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX payments_parent_idx     ON payments(parent_type, parent_id);
CREATE INDEX payments_paid_at_idx    ON payments(paid_at);
CREATE INDEX payments_site_id_idx    ON payments(site_id);
CREATE INDEX payments_created_by_idx ON payments(created_by);

-- Client credit wallet (0014/0039). balance = SUM(amount_delta). Append-only.
CREATE TABLE client_credits (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  contact_id            TEXT    NOT NULL REFERENCES contacts(id) ON DELETE RESTRICT,
  amount_delta_piasters INTEGER NOT NULL CHECK (amount_delta_piasters BETWEEN -999999999999 AND 999999999999),
  source_type           TEXT    NOT NULL CHECK (source_type IN ('overpayment','deposit','applied','refund','adjustment','opening')),
  source_id             TEXT,
  method                TEXT    CHECK (method IN ('cash','instapay','bank_transfer','credit')),
  site_id               TEXT    REFERENCES sites(id),
  occurred_on           TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(occurred_on) IS occurred_on),
  note                  TEXT,
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX cc_contact_idx  ON client_credits(contact_id);
CREATE INDEX cc_occurred_idx ON client_credits(occurred_on);

-- Vendor advance wallet (0023/0039): money we PREPAID a vendor. Append-only.
CREATE TABLE vendor_credits (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  contact_id            TEXT    NOT NULL REFERENCES contacts(id) ON DELETE RESTRICT,
  amount_delta_piasters INTEGER NOT NULL CHECK (amount_delta_piasters BETWEEN -999999999999 AND 999999999999),
  source_type           TEXT    NOT NULL CHECK (source_type IN ('overpayment','deposit','applied','refund','adjustment','opening')),
  source_id             TEXT,
  method                TEXT    CHECK (method IN ('cash','instapay','bank_transfer','credit')),
  site_id               TEXT    REFERENCES sites(id),
  occurred_on           TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(occurred_on) IS occurred_on),
  note                  TEXT,
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX vc_contact_idx  ON vendor_credits(contact_id);
CREATE INDEX vc_occurred_idx ON vendor_credits(occurred_on);

-- Pre-go-live receivable / payable per contact (0039). Append-only signed ledger.
CREATE TABLE contact_opening_balances (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  contact_id            TEXT    NOT NULL REFERENCES contacts(id) ON DELETE RESTRICT,
  kind                  TEXT    NOT NULL CHECK (kind IN ('receivable','payable')),
  amount_delta_piasters INTEGER NOT NULL CHECK (amount_delta_piasters BETWEEN -999999999999 AND 999999999999),
  as_of                 TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(as_of) IS as_of),
  note                  TEXT,
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX contact_opening_balances_contact_kind_idx ON contact_opening_balances(contact_id, kind);

-- Cash drawer ledger (rule 5). Drawer balance = SUM(amount_delta) per site.
-- Append-only; never negative (trg_cash_movements_floor).
CREATE TABLE cash_movements (
  id                    TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  site_id               TEXT    NOT NULL REFERENCES sites(id),
  amount_delta_piasters INTEGER NOT NULL CHECK (amount_delta_piasters BETWEEN -999999999999 AND 999999999999),
  source_type           TEXT    NOT NULL CHECK (source_type IN ('payment','manual','expense')),
  source_id             TEXT,
  reason                TEXT    NOT NULL CHECK (trim(reason) <> ''),   -- HARDENING: web only had NOT NULL; rule 5 says mandatory note
  created_by            TEXT    REFERENCES users(id),
  created_at            TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX cm_site_idx    ON cash_movements(site_id);
CREATE INDEX cm_created_idx ON cash_movements(created_at);
CREATE INDEX cash_movements_created_by_idx ON cash_movements(created_by);

-- ============================================================================
-- 7. STOCK
-- ============================================================================

-- Stock ledger (rule 2/6). Stock = SUM(qty_delta) per (site,item). Append-only;
-- never negative (trg_stock_movements_floor).
CREATE TABLE stock_movements (
  id              TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  site_id         TEXT    NOT NULL REFERENCES sites(id),
  item_id         TEXT    NOT NULL REFERENCES items(id),
  qty_delta_milli INTEGER NOT NULL CHECK (qty_delta_milli BETWEEN -999999999999 AND 999999999999),
  source_type     TEXT    NOT NULL CHECK (source_type IN ('po_conversion','sale','adjustment','transfer','opening')),
  source_id       TEXT,
  note            TEXT,
  created_by      TEXT    REFERENCES users(id),
  created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX sm_site_item_idx ON stock_movements(site_id, item_id);
CREATE INDEX sm_item_idx      ON stock_movements(item_id);
CREATE INDEX sm_created_idx   ON stock_movements(created_at);
CREATE INDEX stock_movements_created_by_idx ON stock_movements(created_by);

CREATE TABLE stock_transfers (
  id         TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  from_site  TEXT    NOT NULL REFERENCES sites(id),
  to_site    TEXT    NOT NULL REFERENCES sites(id),
  item_id    TEXT    NOT NULL REFERENCES items(id),
  qty_milli  INTEGER NOT NULL CHECK (qty_milli BETWEEN 1 AND 999999999999),
  note       TEXT,
  created_by TEXT    REFERENCES users(id),
  created_at TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CONSTRAINT transfer_distinct_sites CHECK (from_site <> to_site)
) STRICT;
CREATE INDEX stock_transfers_from_site_idx  ON stock_transfers(from_site);
CREATE INDEX stock_transfers_to_site_idx    ON stock_transfers(to_site);
CREATE INDEX stock_transfers_item_id_idx    ON stock_transfers(item_id);
CREATE INDEX stock_transfers_created_by_idx ON stock_transfers(created_by);

-- ============================================================================
-- 8. EXPENSES (0041-0044)
-- ============================================================================

CREATE TABLE expense_categories (
  id         TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  name_ar    TEXT    NOT NULL CHECK (trim(name_ar) <> ''),
  sort       INTEGER NOT NULL DEFAULT 100,
  active     INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  created_by TEXT    REFERENCES users(id),
  created_at TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE UNIQUE INDEX expense_categories_name_idx ON expense_categories(name_ar);

-- A recurring-expense DEFINITION only: posts nothing, moves no money.
CREATE TABLE expense_rules (
  id              TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  name_ar         TEXT    NOT NULL CHECK (trim(name_ar) <> ''),
  category_id     TEXT    NOT NULL REFERENCES expense_categories(id) ON DELETE RESTRICT,
  site_id         TEXT    NOT NULL REFERENCES sites(id) ON DELETE RESTRICT,
  amount_piasters INTEGER NOT NULL CHECK (amount_piasters BETWEEN 1 AND 999999999999),
  method          TEXT    NOT NULL DEFAULT 'cash' CHECK (method IN ('cash','instapay','bank_transfer')),  -- <> 'credit'
  description     TEXT,
  frequency       TEXT    NOT NULL CHECK (frequency IN ('daily','weekly','monthly')),
  day_of_week     INTEGER CHECK (day_of_week  BETWEEN 1 AND 7),     -- ISO: 1 = Monday
  day_of_month    INTEGER CHECK (day_of_month BETWEEN 1 AND 31),
  start_date      TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(start_date) IS start_date),
  end_date        TEXT    CHECK (date(end_date) IS end_date),
  active          INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  created_by      TEXT    REFERENCES users(id),
  created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CONSTRAINT expense_rules_shape CHECK (
       (frequency = 'daily'   AND day_of_week IS NULL     AND day_of_month IS NULL)
    OR (frequency = 'weekly'  AND day_of_week IS NOT NULL AND day_of_month IS NULL)
    OR (frequency = 'monthly' AND day_of_month IS NOT NULL AND day_of_week IS NULL)),
  CONSTRAINT expense_rules_range CHECK (end_date IS NULL OR end_date >= start_date)
) STRICT;
CREATE INDEX expense_rules_active_idx ON expense_rules(active);

-- Posted expenses. Immutable except for voiding (trg_expense_block_edit); a cash
-- expense posts a drawer row on insert and a compensating one on void.
CREATE TABLE expenses (
  id              TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  category_id     TEXT    NOT NULL REFERENCES expense_categories(id) ON DELETE RESTRICT,
  site_id         TEXT    NOT NULL REFERENCES sites(id) ON DELETE RESTRICT,
  amount_piasters INTEGER NOT NULL CHECK (amount_piasters BETWEEN 1 AND 999999999999),
  method          TEXT    NOT NULL DEFAULT 'cash' CHECK (method IN ('cash','instapay','bank_transfer')),  -- <> 'credit'
  expense_date    TEXT    NOT NULL DEFAULT (date('now','localtime')) CHECK (date(expense_date) IS expense_date),
  description     TEXT,
  rule_id         TEXT    REFERENCES expense_rules(id) ON DELETE SET NULL,
  due_date        TEXT    CHECK (date(due_date) IS due_date),
  voided_at       TEXT    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', voided_at) IS voided_at),
  voided_by       TEXT    REFERENCES users(id),
  void_reason     TEXT,
  created_by      TEXT    REFERENCES users(id),
  created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CONSTRAINT expenses_rule_due   CHECK ((rule_id IS NULL) = (due_date IS NULL)),
  CONSTRAINT expenses_void_shape CHECK (
       (voided_at IS NULL AND voided_by IS NULL AND void_reason IS NULL)
    OR (voided_at IS NOT NULL AND void_reason IS NOT NULL AND trim(void_reason) <> ''))
) STRICT;
CREATE INDEX expenses_date_idx     ON expenses(expense_date);
CREATE INDEX expenses_site_idx     ON expenses(site_id);
CREATE INDEX expenses_category_idx ON expenses(category_id);
CREATE UNIQUE INDEX expenses_rule_occurrence_idx ON expenses(rule_id, due_date)
  WHERE rule_id IS NOT NULL AND voided_at IS NULL;

-- "Skip this occurrence" decisions. Append-only.
CREATE TABLE expense_rule_skips (
  id         TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  rule_id    TEXT NOT NULL REFERENCES expense_rules(id) ON DELETE CASCADE,
  due_date   TEXT NOT NULL CHECK (date(due_date) IS due_date),
  reason     TEXT NOT NULL CHECK (trim(reason) <> ''),
  created_by TEXT REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE UNIQUE INDEX expense_rule_skips_occurrence_idx ON expense_rule_skips(rule_id, due_date);

-- ============================================================================
-- 9. DOCUMENT NUMBERING, ATTACHMENTS, AUDIT
-- ============================================================================

-- scope: 'po' | 'so' (year = calendar year of the order_date; PO-YYYY-####,
-- SO-YYYY-####) and 'po_seq' | 'so_seq' with year = 0 (running order numbers).
CREATE TABLE doc_counters (
  scope      TEXT    NOT NULL,
  year       INTEGER NOT NULL,
  last_value INTEGER NOT NULL DEFAULT 0 CHECK (last_value >= 0),
  PRIMARY KEY (scope, year)
) STRICT, WITHOUT ROWID;

-- Attachment metadata (0022). Polymorphic parent => existence checked by
-- trg_order_attachments_parent. Limits that lived in the Supabase bucket config
-- (10 MB, mime allow-list) are CHECKs here. The BYTES are in attachment_blobs
-- (same single-file backup, like the PC edition's pc_file_bytes); storing files
-- on disk instead is a supported alternative — drop attachment_blobs then.
CREATE TABLE order_attachments (
  id           TEXT    NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  order_type   TEXT    NOT NULL CHECK (order_type IN ('purchase','sale')),
  order_id     TEXT    NOT NULL,
  storage_path TEXT    NOT NULL UNIQUE,            -- '<order_type>/<order_id>/<uuid>.<ext>'
  file_name    TEXT    NOT NULL,
  mime_type    TEXT    NOT NULL CHECK (mime_type IN ('image/jpeg','image/png','image/webp','image/heic','application/pdf')),
  byte_size    INTEGER NOT NULL CHECK (byte_size > 0 AND byte_size <= 10485760),
  created_by   TEXT    REFERENCES users(id),
  created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX order_attachments_order_idx ON order_attachments(order_type, order_id, created_at);

CREATE TABLE attachment_blobs (
  storage_path TEXT    NOT NULL PRIMARY KEY,
  bytes        BLOB    NOT NULL,
  created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at),
  CHECK (length(bytes) > 0 AND length(bytes) <= 10485760)
) STRICT, WITHOUT ROWID;

-- Who did what. Written ONLY by the audit triggers below (and by the service for
-- non-row events). HARDENING: append-only here (web relied on "no policy").
-- actor FK without ON DELETE action: users are deactivated, never deleted.
CREATE TABLE audit_log (
  id         TEXT NOT NULL PRIMARY KEY DEFAULT (lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',1+abs(random())%4,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6)))),
  actor      TEXT REFERENCES users(id),
  action     TEXT NOT NULL CHECK (action IN ('insert','update','delete')),
  entity     TEXT NOT NULL,
  entity_id  TEXT,
  data       TEXT NOT NULL CHECK (json_valid(data)),
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')) CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at) IS created_at)
) STRICT;
CREATE INDEX audit_log_created_at_idx ON audit_log(created_at DESC);
CREATE INDEX audit_log_actor_idx      ON audit_log(actor);
CREATE INDEX audit_log_entity_idx     ON audit_log(entity, entity_id);

-- ============================================================================
-- 10. REFERENCE DATA (required for the app to function; idempotent)
-- ============================================================================
-- >>> PHASE 1 ENDS HERE (tables, indexes, reference data). A bulk importer that
-- >>> migrates existing Postgres rows should load them NOW and only then run
-- >>> everything below (PHASE 2 = triggers + views): otherwise the ledger triggers
-- >>> would post every historical row a second time. See porting notes §11.
INSERT OR IGNORE INTO app_meta (key, value) VALUES ('schema_version', '1');
INSERT OR IGNORE INTO app_meta (key, value) VALUES ('first_run_completed', '0');

INSERT OR IGNORE INTO permission_keys (key, area, sort, note) VALUES
  ('users.manage', 'admin', 10, 'create users and set their permissions'),
  ('sites.manage', 'admin', 20, 'add and edit branches'),
  ('settings.manage', 'admin', 30, 'edit the business identity on invoices'),
  ('cash.view', 'cash', 10, 'see branch cash drawer balances and movements'),
  ('cash.manual', 'cash', 20, 'manual drawer إيداع / سحب / تسوية'),
  ('contacts.view', 'contacts', 10, 'see contacts, balances and statements'),
  ('contacts.manage', 'contacts', 20, 'create and edit contacts, phones, payment methods'),
  ('contacts.delete', 'contacts', 30, 'delete a contact'),
  ('attachments.manage', 'documents', 10, 'attach and remove order documents'),
  ('expenses.view', 'expenses', 10, 'see expense rows, categories and recurring rules'),
  ('expenses.record', 'expenses', 20, 'record an expense and settle/skip a due recurring one'),
  ('expenses.manage', 'expenses', 30, 'add and edit categories and recurring rules'),
  ('expenses.void', 'expenses', 40, 'void an expense (posts a compensating drawer row)'),
  ('inventory.view', 'inventory', 10, 'see items and stock levels'),
  ('inventory.items', 'inventory', 20, 'create and edit items, incl. the sale price'),
  ('inventory.adjust', 'inventory', 30, 'manual stock adjustment (تسوية)'),
  ('inventory.transfer', 'inventory', 40, 'transfer stock between branches'),
  ('inventory.item_delete', 'inventory', 50, 'delete an item'),
  ('payments.record', 'payments', 10, 'record a payment against an order'),
  ('payments.credit', 'payments', 20, 'use or refund a client credit / vendor advance'),
  ('payments.delete', 'payments', 30, 'delete a payment'),
  ('purchases.view', 'purchases', 10, 'see purchase orders, lines and conversions'),
  ('purchases.create', 'purchases', 20, 'create a purchase order'),
  ('purchases.edit', 'purchases', 30, 'edit a purchase order'),
  ('purchases.convert', 'purchases', 40, 'convert purchased KG into branch stock'),
  ('purchases.delete', 'purchases', 50, 'delete a purchase order or reverse a conversion'),
  ('dashboard.view', 'reports', 5, 'open the daily dashboard'),
  ('reports.view', 'reports', 10, 'open the reports page'),
  ('audit.view', 'reports', 20, 'read the audit log — who did what'),
  ('financials.view', 'reports', 30, 'read the aggregate financial summary — totals only, no rows'),
  ('sales.view', 'sales', 10, 'see sales orders and their lines'),
  ('sales.draft', 'sales', 20, 'create and edit draft sales orders'),
  ('sales.discount', 'sales', 30, 'set a discount on a sales order'),
  ('sales.invoice', 'sales', 40, 'draft -> invoiced (freezes lines, issues a number)'),
  ('sales.place', 'sales', 50, 'invoiced -> placed (deducts branch stock)'),
  ('sales.cancel_placement', 'sales', 60, 'placed -> invoiced (returns the stock)'),
  ('sales.edit_locked', 'sales', 70, 'edit an order after invoicing (reverses & re-applies stock)'),
  ('sales.delete', 'sales', 80, 'delete a sales order');

-- admin = every key (they also bypass the rows entirely, as is_admin() does in web).
INSERT OR IGNORE INTO role_presets (role, perm) SELECT 'admin', key FROM permission_keys;
INSERT OR IGNORE INTO role_presets (role, perm) VALUES
  ('manager','attachments.manage'),('manager','audit.view'),('manager','cash.view'),
  ('manager','contacts.manage'),('manager','contacts.view'),('manager','dashboard.view'),
  ('manager','expenses.manage'),('manager','expenses.record'),('manager','expenses.view'),
  ('manager','inventory.adjust'),('manager','inventory.items'),('manager','inventory.transfer'),
  ('manager','inventory.view'),('manager','payments.credit'),('manager','payments.record'),
  ('manager','purchases.convert'),('manager','purchases.create'),('manager','purchases.edit'),
  ('manager','purchases.view'),('manager','reports.view'),('manager','sales.cancel_placement'),
  ('manager','sales.discount'),('manager','sales.draft'),('manager','sales.invoice'),
  ('manager','sales.place'),('manager','sales.view'),
  ('staff','cash.view'),('staff','contacts.view'),('staff','dashboard.view'),
  ('staff','expenses.record'),('staff','expenses.view'),('staff','inventory.view'),
  ('staff','payments.record'),('staff','purchases.view'),('staff','reports.view'),
  ('staff','sales.discount'),('staff','sales.draft'),('staff','sales.view'),
  ('accountant','financials.view');


-- ============================================================================
-- 11. TRIGGERS — data invariants (PHASE 2) (the "must never be violated by any client" set)
-- ============================================================================
-- SQLite triggers cannot modify NEW, so the Postgres BEFORE-trigger pattern
-- "compute a column then return NEW" became either (a) a GENERATED column,
-- (b) an AFTER trigger that UPDATEs the row (numbering, denormalised ids), or
-- (c) a service-layer responsibility (noted per table). Every nested UPDATE below
-- is guarded by `UPDATE OF <column>` and/or WHEN so nothing loops, with
-- recursive_triggers either OFF (default) or ON.
-- Authorization (has_perm / can_use_site / is_admin) is NOT in the database.
-- ----------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- 11.1 APPEND-ONLY LEDGERS (block_mutation + its 7 trigger bindings in web).
--      HARDENING additions: audit_log, auth_events, stock_transfers.
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_stock_movements_append_only_upd BEFORE UPDATE ON stock_movements
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_stock_movements_append_only_del BEFORE DELETE ON stock_movements
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_cash_movements_append_only_upd BEFORE UPDATE ON cash_movements
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_cash_movements_append_only_del BEFORE DELETE ON cash_movements
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_client_credits_append_only_upd BEFORE UPDATE ON client_credits
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_client_credits_append_only_del BEFORE DELETE ON client_credits
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_vendor_credits_append_only_upd BEFORE UPDATE ON vendor_credits
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_vendor_credits_append_only_del BEFORE DELETE ON vendor_credits
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_contact_opening_balances_append_only_upd BEFORE UPDATE ON contact_opening_balances
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_contact_opening_balances_append_only_del BEFORE DELETE ON contact_opening_balances
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_expense_rule_skips_append_only_upd BEFORE UPDATE ON expense_rule_skips
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_expense_rule_skips_append_only_del BEFORE DELETE ON expense_rule_skips
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_audit_log_append_only_upd BEFORE UPDATE ON audit_log
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_audit_log_append_only_del BEFORE DELETE ON audit_log
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_auth_events_append_only_upd BEFORE UPDATE ON auth_events
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_auth_events_append_only_del BEFORE DELETE ON auth_events
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_stock_transfers_append_only_upd BEFORE UPDATE ON stock_transfers
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;
CREATE TRIGGER trg_stock_transfers_append_only_del BEFORE DELETE ON stock_transfers
BEGIN SELECT RAISE(ABORT, 'LEDGER_READONLY|هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)'); END;

-- ---------------------------------------------------------------------------
-- 11.2 LEDGER FLOORS — neither a branch's stock of an item nor a drawer may go
--      negative (0008 §6). Row-level: rows inserted earlier in the same
--      statement are visible, so cumulative overdraw is caught too.
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_stock_movements_floor
BEFORE INSERT ON stock_movements
WHEN new.qty_delta_milli < 0
BEGIN
  SELECT RAISE(ABORT, 'STOCK_FLOOR|لا يمكن تنفيذ الحركة: الرصيد الحالي للصنف في الفرع لا يكفي')
  WHERE (SELECT coalesce(sum(qty_delta_milli), 0) FROM stock_movements
          WHERE site_id = new.site_id AND item_id = new.item_id) + new.qty_delta_milli < 0;
END;

CREATE TRIGGER trg_cash_movements_floor
BEFORE INSERT ON cash_movements
WHEN new.amount_delta_piasters < 0
BEGIN
  SELECT RAISE(ABORT, 'CASH_FLOOR|رصيد الخزينة في الفرع لا يكفي لهذه الحركة')
  WHERE (SELECT coalesce(sum(amount_delta_piasters), 0) FROM cash_movements
          WHERE site_id = new.site_id) + new.amount_delta_piasters < 0;
END;

-- ---------------------------------------------------------------------------
-- 11.3 DOCUMENT NUMBERING (next_doc_number / next_org_seq / po_assign_code /
--      assign_order_seq / the numbering half of so_status_transition).
--      Atomic: the UPSERT and the UPDATE run inside the inserting transaction.
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_po_assign_code
AFTER INSERT ON purchase_orders
WHEN new.order_code IS NULL
BEGIN
  INSERT INTO doc_counters (scope, year, last_value)
  VALUES ('po', CAST(strftime('%Y', new.order_date) AS INTEGER), 1)
  ON CONFLICT (scope, year) DO UPDATE SET last_value = last_value + 1;
  UPDATE purchase_orders
     SET order_code = 'PO-' || strftime('%Y', new.order_date) || '-' ||
         printf('%04d', (SELECT last_value FROM doc_counters
                          WHERE scope = 'po' AND year = CAST(strftime('%Y', new.order_date) AS INTEGER)))
   WHERE id = new.id;
END;

CREATE TRIGGER trg_po_assign_seq
AFTER INSERT ON purchase_orders
WHEN new.order_seq = 0
BEGIN
  INSERT INTO doc_counters (scope, year, last_value) VALUES ('po_seq', 0, 1)
  ON CONFLICT (scope, year) DO UPDATE SET last_value = last_value + 1;
  UPDATE purchase_orders
     SET order_seq = (SELECT last_value FROM doc_counters WHERE scope = 'po_seq' AND year = 0)
   WHERE id = new.id;
END;

CREATE TRIGGER trg_so_assign_seq
AFTER INSERT ON sales_orders
WHEN new.order_seq = 0
BEGIN
  INSERT INTO doc_counters (scope, year, last_value) VALUES ('so_seq', 0, 1)
  ON CONFLICT (scope, year) DO UPDATE SET last_value = last_value + 1;
  UPDATE sales_orders
     SET order_seq = (SELECT last_value FROM doc_counters WHERE scope = 'so_seq' AND year = 0)
   WHERE id = new.id;
END;

-- draft -> invoiced assigns SO-YYYY-#### (frozen thereafter).
-- NOTE: web migration 0032 re-defined so_status_transition calling the removed
-- 2-argument next_doc_number(), which makes invoicing fail in PostgreSQL. This
-- port implements the intended behaviour.
CREATE TRIGGER trg_so_assign_invoice_number
AFTER UPDATE OF status ON sales_orders
WHEN old.status = 'draft' AND new.status = 'invoiced' AND new.invoice_number IS NULL
BEGIN
  INSERT INTO doc_counters (scope, year, last_value)
  VALUES ('so', CAST(strftime('%Y', new.order_date) AS INTEGER), 1)
  ON CONFLICT (scope, year) DO UPDATE SET last_value = last_value + 1;
  UPDATE sales_orders
     SET invoice_number = 'SO-' || strftime('%Y', new.order_date) || '-' ||
         printf('%04d', (SELECT last_value FROM doc_counters
                          WHERE scope = 'so' AND year = CAST(strftime('%Y', new.order_date) AS INTEGER)))
   WHERE id = new.id;
END;

-- ---------------------------------------------------------------------------
-- 11.4 GENERAL PURCHASE ORDERS & CONVERSIONS (rule 2)
-- ---------------------------------------------------------------------------
-- total_kg frozen once any conversion exists (po_guard_total_kg).
CREATE TRIGGER trg_po_guard_total_kg
BEFORE UPDATE OF total_kg_milli ON purchase_orders
WHEN new.total_kg_milli IS NOT old.total_kg_milli
 AND (SELECT editing FROM app_context WHERE id = 1) = 0
 AND EXISTS (SELECT 1 FROM po_conversions WHERE po_id = old.id)
BEGIN
  SELECT RAISE(ABORT, 'PO_TOTAL_KG_FROZEN|لا يمكن تعديل إجمالي الكيلوهات بعد بدء التحويل إلى المخزون');
END;

-- converted_kg <= total_kg  (po_conversion_check). The web version silently let a
-- conversion through on an itemized PO (NULL arithmetic); HARDENING: rejected.
CREATE TRIGGER trg_po_conversion_check
BEFORE INSERT ON po_conversions
BEGIN
  SELECT RAISE(ABORT, 'PO_NOT_FOUND|أمر الشراء غير موجود')
  WHERE NOT EXISTS (SELECT 1 FROM purchase_orders WHERE id = new.po_id);
  SELECT RAISE(ABORT, 'PO_NOT_GENERAL|التحويل بالكيلوهات متاح للأمر العام فقط')
  WHERE (SELECT po_type FROM purchase_orders WHERE id = new.po_id) <> 'general';
  SELECT RAISE(ABORT, 'PO_CONVERSION_EXCEEDS_REMAINING|الكمية المستهلكة تتجاوز المتبقي من أمر الشراء')
  WHERE new.kg_consumed_milli >
        (SELECT total_kg_milli FROM purchase_orders WHERE id = new.po_id)
      - (SELECT coalesce(sum(kg_consumed_milli), 0) FROM po_conversions WHERE po_id = new.po_id);
END;

-- conversion -> positive stock movement; PO flips to fully_converted at 0 remaining.
CREATE TRIGGER trg_po_conversion_apply
AFTER INSERT ON po_conversions
BEGIN
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (new.site_id, new.item_id, new.output_qty_milli, 'po_conversion', new.id, new.created_by,
          'تحويل من ' || coalesce((SELECT order_code FROM purchase_orders WHERE id = new.po_id), ''));
  UPDATE purchase_orders SET status = 'fully_converted'
   WHERE id = new.po_id AND status = 'open'
     AND total_kg_milli - (SELECT coalesce(sum(kg_consumed_milli), 0) FROM po_conversions WHERE po_id = new.po_id) <= 0;
END;

-- Reversal (DELETE of a conversion): guarded compensating row; re-open the PO.
-- Also fires for ON DELETE CASCADE when a whole PO is deleted.
CREATE TRIGGER trg_po_conversion_reverse
BEFORE DELETE ON po_conversions
BEGIN
  SELECT RAISE(ABORT, 'CONVERSION_REVERSE_INSUFFICIENT|لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي')
  WHERE (SELECT coalesce(sum(qty_delta_milli), 0) FROM stock_movements
          WHERE site_id = old.site_id AND item_id = old.item_id) - old.output_qty_milli < 0;
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (old.site_id, old.item_id, -old.output_qty_milli, 'adjustment', old.id,
          (SELECT actor_user_id FROM app_context WHERE id = 1), 'إلغاء تحويل من أمر الشراء');
  UPDATE purchase_orders SET status = 'open' WHERE id = old.po_id AND status = 'fully_converted';
END;

-- HARDENING: no UPDATE policy existed ("corrected by delete + re-add"); make it a rule.
CREATE TRIGGER trg_po_conversions_immutable
BEFORE UPDATE OF po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit ON po_conversions
BEGIN
  SELECT RAISE(ABORT, 'ROW_IMMUTABLE|لا يمكن تعديل هذا السجل — احذفه وأعد إدخاله');
END;

-- ---------------------------------------------------------------------------
-- 11.5 ITEMIZED PURCHASE ORDERS (po_line_*; the 0019 land-on-save no-op
--      triggers po_line_apply / po_line_reverse are intentionally NOT ported).
-- ---------------------------------------------------------------------------
-- total_amount = SUM(line_total) while lines change (po_line_recompute_total).
CREATE TRIGGER trg_po_line_recompute_ins
AFTER INSERT ON po_lines
BEGIN
  UPDATE purchase_orders
     SET total_amount_piasters = coalesce((SELECT sum(line_total_piasters) FROM po_lines WHERE po_id = new.po_id), 0)
   WHERE id = new.po_id;
END;
CREATE TRIGGER trg_po_line_recompute_upd
AFTER UPDATE ON po_lines
BEGIN
  UPDATE purchase_orders
     SET total_amount_piasters = coalesce((SELECT sum(line_total_piasters) FROM po_lines WHERE po_id = new.po_id), 0)
   WHERE id = new.po_id;
  UPDATE purchase_orders
     SET total_amount_piasters = coalesce((SELECT sum(line_total_piasters) FROM po_lines WHERE po_id = old.po_id), 0)
   WHERE id = old.po_id AND old.po_id <> new.po_id;
END;
CREATE TRIGGER trg_po_line_recompute_del
AFTER DELETE ON po_lines
BEGIN
  UPDATE purchase_orders
     SET total_amount_piasters = coalesce((SELECT sum(line_total_piasters) FROM po_lines WHERE po_id = old.po_id), 0)
   WHERE id = old.po_id;
END;

-- A conversion may not exceed what is left on its line (po_line_conversion_check).
CREATE TRIGGER trg_plc_check
BEFORE INSERT ON po_line_conversions
BEGIN
  SELECT RAISE(ABORT, 'PO_LINE_NOT_FOUND|بند الأمر غير موجود')
  WHERE NOT EXISTS (SELECT 1 FROM po_lines WHERE id = new.po_line_id);
  SELECT RAISE(ABORT, 'PLC_EXCEEDS_REMAINING|الكمية المحوّلة تتجاوز المتبقي من البند')
  WHERE new.qty_milli >
        (SELECT qty_milli FROM po_lines WHERE id = new.po_line_id)
      - (SELECT coalesce(sum(qty_milli), 0) FROM po_line_conversions WHERE po_line_id = new.po_line_id);
END;

-- Fill the denormalised ids, post the stock, flip the PO when nothing remains
-- across ALL its lines (po_line_conversion_apply + the BEFORE-INSERT denorm of 0021).
CREATE TRIGGER trg_plc_apply
AFTER INSERT ON po_line_conversions
BEGIN
  UPDATE po_line_conversions
     SET item_id = (SELECT item_id FROM po_lines WHERE id = new.po_line_id),
         po_id   = (SELECT po_id   FROM po_lines WHERE id = new.po_line_id)
   WHERE id = new.id;
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (new.site_id,
          (SELECT item_id FROM po_lines WHERE id = new.po_line_id),
          new.qty_milli, 'po_conversion', new.id, new.created_by,
          'استلام من ' || coalesce((SELECT p.order_code FROM purchase_orders p
                                     JOIN po_lines l ON l.po_id = p.id WHERE l.id = new.po_line_id), ''));
  UPDATE purchase_orders SET status = 'fully_converted'
   WHERE id = (SELECT po_id FROM po_lines WHERE id = new.po_line_id)
     AND status = 'open'
     AND (SELECT coalesce(sum(qty_milli), 0) FROM po_lines
           WHERE po_id = (SELECT po_id FROM po_lines WHERE id = new.po_line_id))
       - (SELECT coalesce(sum(qty_milli), 0) FROM po_line_conversions
           WHERE po_id = (SELECT po_id FROM po_lines WHERE id = new.po_line_id)) <= 0;
END;

-- Reverse via the DENORMALISED item_id/po_id so it works when the parent line/PO
-- is being cascade-deleted in the same statement (0021).
CREATE TRIGGER trg_plc_reverse
BEFORE DELETE ON po_line_conversions
BEGIN
  SELECT RAISE(ABORT, 'CONVERSION_REVERSE_INSUFFICIENT|لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي')
  WHERE (SELECT coalesce(sum(qty_delta_milli), 0) FROM stock_movements
          WHERE site_id = old.site_id AND item_id = old.item_id) - old.qty_milli < 0;
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (old.site_id, old.item_id, -old.qty_milli, 'adjustment', old.id, (SELECT actor_user_id FROM app_context WHERE id = 1), 'إلغاء تحويل بند مصنّف');
  UPDATE purchase_orders SET status = 'open' WHERE id = old.po_id AND status = 'fully_converted';
END;

-- HARDENING: conversions are corrected by delete + re-add (no UPDATE policy in web).
CREATE TRIGGER trg_plc_immutable
BEFORE UPDATE OF po_line_id, site_id, qty_milli ON po_line_conversions
BEGIN
  SELECT RAISE(ABORT, 'ROW_IMMUTABLE|لا يمكن تعديل هذا السجل — احذفه وأعد إدخاله');
END;

-- ---------------------------------------------------------------------------
-- 11.6 SALES ORDER LINES  (so_line_guard, so_recompute_total)
-- ---------------------------------------------------------------------------
-- Lines are editable only while the order is a draft (or during an admin edit).
CREATE TRIGGER trg_so_line_guard_ins
BEFORE INSERT ON sales_order_lines
WHEN (SELECT editing FROM app_context WHERE id = 1) = 0 AND (SELECT status FROM sales_orders WHERE id = new.so_id) <> 'draft'
BEGIN
  SELECT RAISE(ABORT, 'SO_LINES_LOCKED|لا يمكن تعديل بنود أمر بيع بعد فوترته');
END;
CREATE TRIGGER trg_so_line_guard_upd
BEFORE UPDATE ON sales_order_lines
WHEN (SELECT editing FROM app_context WHERE id = 1) = 0
 AND ((SELECT status FROM sales_orders WHERE id = new.so_id) <> 'draft'
   OR (SELECT status FROM sales_orders WHERE id = old.so_id) <> 'draft')
BEGIN
  SELECT RAISE(ABORT, 'SO_LINES_LOCKED|لا يمكن تعديل بنود أمر بيع بعد فوترته');
END;
CREATE TRIGGER trg_so_line_guard_del
BEFORE DELETE ON sales_order_lines
WHEN (SELECT editing FROM app_context WHERE id = 1) = 0 AND (SELECT status FROM sales_orders WHERE id = old.so_id) <> 'draft'
BEGIN
  SELECT RAISE(ABORT, 'SO_LINES_LOCKED|لا يمكن تعديل بنود أمر بيع بعد فوترته');
END;

-- subtotal follows the lines; total/discount are GENERATED from it.
CREATE TRIGGER trg_so_recompute_total_ins
AFTER INSERT ON sales_order_lines
BEGIN
  UPDATE sales_orders
     SET subtotal_piasters = coalesce((SELECT sum(line_total_piasters) FROM sales_order_lines WHERE so_id = new.so_id), 0)
   WHERE id = new.so_id;
END;
CREATE TRIGGER trg_so_recompute_total_upd
AFTER UPDATE ON sales_order_lines
BEGIN
  UPDATE sales_orders
     SET subtotal_piasters = coalesce((SELECT sum(line_total_piasters) FROM sales_order_lines WHERE so_id = new.so_id), 0)
   WHERE id = new.so_id;
  UPDATE sales_orders
     SET subtotal_piasters = coalesce((SELECT sum(line_total_piasters) FROM sales_order_lines WHERE so_id = old.so_id), 0)
   WHERE id = old.so_id AND old.so_id <> new.so_id;
END;
CREATE TRIGGER trg_so_recompute_total_del
AFTER DELETE ON sales_order_lines
BEGIN
  UPDATE sales_orders
     SET subtotal_piasters = coalesce((SELECT sum(line_total_piasters) FROM sales_order_lines WHERE so_id = old.so_id), 0)
   WHERE id = old.so_id;
END;

-- ---------------------------------------------------------------------------
-- 11.7 SALES ORDER DISCOUNT VALIDATION (so_apply_discount — validation half;
--      the computation half is the GENERATED columns on sales_orders).
--      Skipped while app_context.editing = 1 (admin editor), exactly like web.
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_so_discount_ins
BEFORE INSERT ON sales_orders
BEGIN
  SELECT RAISE(ABORT, 'DISCOUNT_PERCENT_OVER_100|نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪')
  WHERE new.discount_type = 'percent' AND new.discount_value_x100 > 10000;
  SELECT RAISE(ABORT, 'DISCOUNT_EXCEEDS_SUBTOTAL|الخصم يتجاوز إجمالي البنود')
  WHERE (SELECT editing FROM app_context WHERE id = 1) = 0 AND new.discount_type = 'amount' AND new.discount_value_x100 > new.subtotal_piasters;
END;

CREATE TRIGGER trg_so_discount_upd
BEFORE UPDATE OF subtotal_piasters, discount_type, discount_value_x100 ON sales_orders
BEGIN
  SELECT RAISE(ABORT, 'DISCOUNT_PERCENT_OVER_100|نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪')
  WHERE new.discount_type = 'percent' AND new.discount_value_x100 > 10000;
  SELECT RAISE(ABORT, 'DISCOUNT_EXCEEDS_SUBTOTAL|الخصم يتجاوز إجمالي البنود')
  WHERE (SELECT editing FROM app_context WHERE id = 1) = 0 AND new.discount_type = 'amount' AND new.discount_value_x100 > new.subtotal_piasters;
  -- money already collected cannot be discounted away (mirror of the overpayment guard)
  SELECT RAISE(ABORT, 'DISCOUNT_BELOW_PAID|المحصّل من العميل يتجاوز إجمالي الأمر بعد الخصم')
  WHERE (SELECT editing FROM app_context WHERE id = 1) = 0
    AND (new.subtotal_piasters - min(new.subtotal_piasters, CASE new.discount_type WHEN 'percent' THEN (new.subtotal_piasters * new.discount_value_x100 + 5000) / 10000 WHEN 'amount' THEN new.discount_value_x100 ELSE 0 END)) < old.total_amount_piasters
    AND (SELECT coalesce(sum(amount_piasters), 0) FROM payments
          WHERE parent_type = 'so' AND parent_id = new.id) > (new.subtotal_piasters - min(new.subtotal_piasters, CASE new.discount_type WHEN 'percent' THEN (new.subtotal_piasters * new.discount_value_x100 + 5000) / 10000 WHEN 'amount' THEN new.discount_value_x100 ELSE 0 END));
END;

-- ---------------------------------------------------------------------------
-- 11.8 SALES ORDER LIFECYCLE  (so_status_transition, so_placement_apply)
-- ---------------------------------------------------------------------------
-- Whitelist: draft->invoiced, invoiced->placed, placed->invoiced (admin cancel),
-- placed->closed (auto), closed->placed (auto re-open when a payment is deleted).
-- Under app_context.editing = 1 any rewind is allowed (admin editor).
CREATE TRIGGER trg_so_status_transition
BEFORE UPDATE OF status ON sales_orders
WHEN new.status <> old.status
BEGIN
  SELECT RAISE(ABORT, 'SO_TRANSITION_ILLEGAL|انتقال حالة غير مسموح به لأمر البيع')
  WHERE (SELECT editing FROM app_context WHERE id = 1) = 0
    AND NOT (   (old.status = 'draft'    AND new.status = 'invoiced')
             OR (old.status = 'invoiced' AND new.status = 'placed')
             OR (old.status = 'placed'   AND new.status = 'invoiced')
             OR (old.status = 'placed'   AND new.status = 'closed')
             OR (old.status = 'closed'   AND new.status = 'placed'));
  SELECT RAISE(ABORT, 'SO_INVOICE_NO_LINES|لا يمكن فوترة أمر بيع بدون بنود')
  WHERE old.status = 'draft' AND new.status = 'invoiced'
    AND NOT EXISTS (SELECT 1 FROM sales_order_lines WHERE so_id = new.id);
END;

-- invoiced->placed : sell the stock out of the SO's site, then auto-close if already
--                    fully collected.
-- placed|closed -> invoiced|draft : give the stock back (admin cancel / admin edit).
--   BUG FIX vs web: 0008's trigger only returned stock for placed->invoiced, so the
--   admin editor double-deducted stock when editing a CLOSED order (verified on
--   PostgreSQL 16: 40 expected, 30 actual). closed->placed (payment deleted) must
--   NOT return stock, and does not match here.
CREATE TRIGGER trg_so_placement_apply
AFTER UPDATE OF status ON sales_orders
WHEN new.status <> old.status
BEGIN
  SELECT RAISE(ABORT, 'SO_STOCK_INSUFFICIENT|الكمية غير كافية في المخزون للصنف المطلوب')
    FROM sales_order_lines l
   WHERE old.status = 'invoiced' AND new.status = 'placed' AND l.so_id = new.id
     AND (SELECT coalesce(sum(qty_delta_milli), 0) FROM stock_movements
           WHERE site_id = new.site_id AND item_id = l.item_id) < l.qty_milli;

  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  SELECT new.site_id, l.item_id, -l.qty_milli, 'sale', l.id, new.created_by,
         'بيع ' || coalesce(new.invoice_number, '')
    FROM sales_order_lines l
   WHERE old.status = 'invoiced' AND new.status = 'placed' AND l.so_id = new.id;

  -- already fully collected while invoiced -> close now
  UPDATE sales_orders SET status = 'closed'
   WHERE id = new.id AND old.status = 'invoiced' AND new.status = 'placed'
     AND status = 'placed' AND total_amount_piasters > 0
     AND (SELECT coalesce(sum(amount_piasters), 0) FROM payments
           WHERE parent_type = 'so' AND parent_id = new.id) >= total_amount_piasters;

  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  SELECT new.site_id, l.item_id, l.qty_milli, 'adjustment', l.id, (SELECT actor_user_id FROM app_context WHERE id = 1),
         'إلغاء تنفيذ ' || coalesce(new.invoice_number, '')
    FROM sales_order_lines l
   WHERE old.status IN ('placed', 'closed') AND new.status IN ('invoiced', 'draft') AND l.so_id = new.id;
END;

-- HARDENING: deleting an order that is placed/closed or has payments would orphan
-- stock/cash effects (the web schema allowed it). Cancel placement / delete the
-- payments first, exactly as the compensating-row convention requires.
CREATE TRIGGER trg_so_delete_guard
BEFORE DELETE ON sales_orders
WHEN old.status IN ('placed', 'closed')
  OR EXISTS (SELECT 1 FROM payments WHERE parent_type = 'so' AND parent_id = old.id)
BEGIN
  SELECT RAISE(ABORT, 'ORDER_DELETE_BLOCKED|لا يمكن حذف أمر منفّذ أو عليه دفعات — ألغِ التنفيذ واحذف الدفعات أولاً');
END;
CREATE TRIGGER trg_po_delete_guard
BEFORE DELETE ON purchase_orders
WHEN EXISTS (SELECT 1 FROM payments WHERE parent_type = 'po' AND parent_id = old.id)
BEGIN
  SELECT RAISE(ABORT, 'ORDER_DELETE_BLOCKED|لا يمكن حذف أمر منفّذ أو عليه دفعات — ألغِ التنفيذ واحذف الدفعات أولاً');
END;

-- ---------------------------------------------------------------------------
-- 11.9 PAYMENTS (rule 4 & 5): overpayment block, drawer movement, auto-close
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_payment_validate
BEFORE INSERT ON payments
BEGIN
  SELECT RAISE(ABORT, 'SO_NOT_FOUND|أمر البيع غير موجود')
  WHERE new.parent_type = 'so' AND NOT EXISTS (SELECT 1 FROM sales_orders WHERE id = new.parent_id);
  SELECT RAISE(ABORT, 'PO_NOT_FOUND|أمر الشراء غير موجود')
  WHERE new.parent_type = 'po' AND NOT EXISTS (SELECT 1 FROM purchase_orders WHERE id = new.parent_id);
  SELECT RAISE(ABORT, 'SO_DRAFT_NO_PAYMENT|لا يمكن تسجيل دفعة على مسودة أمر بيع — قم بالفوترة أولاً')
  WHERE new.parent_type = 'so' AND (SELECT status FROM sales_orders WHERE id = new.parent_id) = 'draft';
  -- SO cash defaults to the SO's own drawer (back-filled after insert); a PO cash
  -- payment MUST name the drawer.
  SELECT RAISE(ABORT, 'CASH_SITE_REQUIRED|يجب تحديد الفرع (الخزينة) للدفعة النقدية')
  WHERE new.method = 'cash' AND new.site_id IS NULL AND new.parent_type = 'po';
  -- a chosen stored account must belong to this order's contact and match the method
  SELECT RAISE(ABORT, 'PM_NOT_OWNED|طريقة الدفع المحددة لا تخص جهة الاتصال')
  WHERE new.contact_payment_method_id IS NOT NULL
    AND NOT EXISTS (
      SELECT 1 FROM contact_payment_methods pm
       WHERE pm.id = new.contact_payment_method_id
         AND pm.method = new.method
         AND pm.contact_id = CASE new.parent_type
               WHEN 'so' THEN (SELECT client_id FROM sales_orders   WHERE id = new.parent_id)
               ELSE           (SELECT vendor_id FROM purchase_orders WHERE id = new.parent_id) END);
  -- OVERPAYMENT BLOCK (exact integer piasters; web used a 0.005 tolerance)
  SELECT RAISE(ABORT, 'PAYMENT_OVERPAY|المبلغ يتجاوز المتبقي على الأمر')
  WHERE (SELECT coalesce(sum(amount_piasters), 0) FROM payments
          WHERE parent_type = new.parent_type AND parent_id = new.parent_id) + new.amount_piasters
      > CASE new.parent_type
          WHEN 'so' THEN (SELECT total_amount_piasters FROM sales_orders   WHERE id = new.parent_id)
          ELSE           (SELECT total_amount_piasters FROM purchase_orders WHERE id = new.parent_id) END;
END;

-- Order matters, so ONE trigger: (1) back-fill the SO site, (2) cash movement,
-- (3) auto-close a fully-collected PLACED sales order (rule 6; 0008 fix A).
CREATE TRIGGER trg_payment_apply
AFTER INSERT ON payments
BEGIN
  UPDATE payments
     SET site_id = (SELECT site_id FROM sales_orders WHERE id = new.parent_id)
   WHERE id = new.id AND site_id IS NULL AND parent_type = 'so';

  INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, source_id, reason, created_by)
  SELECT coalesce(new.site_id, (SELECT site_id FROM sales_orders WHERE id = new.parent_id)),
         CASE new.parent_type WHEN 'so' THEN new.amount_piasters ELSE -new.amount_piasters END,
         'payment', new.id,
         CASE new.parent_type WHEN 'so' THEN 'تحصيل نقدي من بيع' ELSE 'سداد نقدي لأمر شراء' END,
         new.created_by
   WHERE new.method = 'cash';

  UPDATE sales_orders SET status = 'closed'
   WHERE new.parent_type = 'so' AND id = new.parent_id AND status = 'placed'
     AND total_amount_piasters > 0
     AND (SELECT coalesce(sum(amount_piasters), 0) FROM payments
           WHERE parent_type = 'so' AND parent_id = new.parent_id) >= total_amount_piasters;
END;

-- Deleting a payment reverses its effects (0008 fix B): compensating drawer row,
-- and a closed SO that is no longer fully collected re-opens to 'placed'.
-- KNOWN GAP (same as web): deleting a method='credit' payment does NOT restore the
-- consumed client credit / vendor advance — the service must post that explicitly.
CREATE TRIGGER trg_payment_delete_apply
AFTER DELETE ON payments
BEGIN
  INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, source_id, reason, created_by)
  SELECT old.site_id,
         CASE old.parent_type WHEN 'so' THEN -old.amount_piasters ELSE old.amount_piasters END,
         'payment', old.id,
         CASE old.parent_type WHEN 'so' THEN 'إلغاء تحصيل نقدي (حذف دفعة)' ELSE 'إلغاء سداد نقدي (حذف دفعة)' END,
         (SELECT actor_user_id FROM app_context WHERE id = 1)
   WHERE old.method = 'cash' AND old.site_id IS NOT NULL;

  UPDATE sales_orders SET status = 'placed'
   WHERE old.parent_type = 'so' AND id = old.parent_id AND status = 'closed'
     AND total_amount_piasters > 0
     AND (SELECT coalesce(sum(amount_piasters), 0) FROM payments
           WHERE parent_type = 'so' AND parent_id = old.parent_id) < total_amount_piasters;
END;

-- HARDENING: payments had no UPDATE policy ("delete + re-add"). Only the one-time
-- site back-fill (NULL -> value) and the FK's SET NULL on contact_payment_method_id
-- are allowed.
CREATE TRIGGER trg_payments_immutable
BEFORE UPDATE ON payments
WHEN new.parent_type    IS NOT old.parent_type
  OR new.parent_id      IS NOT old.parent_id
  OR new.amount_piasters IS NOT old.amount_piasters
  OR new.method         IS NOT old.method
  OR new.paid_at        IS NOT old.paid_at
  OR new.note           IS NOT old.note
  OR new.created_by     IS NOT old.created_by
  OR new.created_at     IS NOT old.created_at
  OR (new.site_id IS NOT old.site_id AND old.site_id IS NOT NULL)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_IMMUTABLE|لا يمكن تعديل دفعة مسجلة — احذفها وأعد تسجيلها');
END;

-- ---------------------------------------------------------------------------
-- 11.10 STOCK TRANSFERS: one out-row at the source, one in-row at the target
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_transfer_apply
AFTER INSERT ON stock_transfers
BEGIN
  SELECT RAISE(ABORT, 'TRANSFER_STOCK_INSUFFICIENT|الكمية غير كافية في الفرع المصدر للنقل')
  WHERE (SELECT coalesce(sum(qty_delta_milli), 0) FROM stock_movements
          WHERE site_id = new.from_site AND item_id = new.item_id) < new.qty_milli;
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (new.from_site, new.item_id, -new.qty_milli, 'transfer', new.id, new.created_by, 'نقل صادر');
  INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, source_id, created_by, note)
  VALUES (new.to_site, new.item_id, new.qty_milli, 'transfer', new.id, new.created_by, 'نقل وارد');
END;

-- ---------------------------------------------------------------------------
-- 11.11 CREDIT WALLETS never negative (client_credit_guard / vendor_credit_guard)
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_client_credit_nonneg
AFTER INSERT ON client_credits
BEGIN
  SELECT RAISE(ABORT, 'CLIENT_CREDIT_NEGATIVE|لا يمكن أن يصبح رصيد العميل سالبًا')
  WHERE (SELECT coalesce(sum(amount_delta_piasters), 0) FROM client_credits WHERE contact_id = new.contact_id) < 0;
END;
CREATE TRIGGER trg_vendor_credit_nonneg
AFTER INSERT ON vendor_credits
BEGIN
  SELECT RAISE(ABORT, 'VENDOR_CREDIT_NEGATIVE|لا يمكن أن يصبح رصيد الدفعة المقدّمة سالبًا')
  WHERE (SELECT coalesce(sum(amount_delta_piasters), 0) FROM vendor_credits WHERE contact_id = new.contact_id) < 0;
END;

-- ---------------------------------------------------------------------------
-- 11.12 EXPENSES  (expense_validate, expense_cash_post, expense_block_edit and
--       the drawer-compensation half of void_expense, moved into a trigger so a
--       void can never skip it)
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_expense_validate
BEFORE INSERT ON expenses
BEGIN
  SELECT RAISE(ABORT, 'EXPENSE_CATEGORY_INACTIVE|فئة المصروف غير مفعّلة')
  WHERE (SELECT active FROM expense_categories WHERE id = new.category_id) = 0;
END;

CREATE TRIGGER trg_expense_cash_post
AFTER INSERT ON expenses
WHEN new.method = 'cash'
BEGIN
  INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, source_id, reason, created_by)
  VALUES (new.site_id, -new.amount_piasters, 'expense', new.id,
          'مصروف: ' || coalesce((SELECT name_ar FROM expense_categories WHERE id = new.category_id), 'أخرى')
                    || coalesce(' — ' || nullif(trim(new.description), ''), ''),
          new.created_by);
END;

CREATE TRIGGER trg_expense_block_edit
BEFORE UPDATE ON expenses
WHEN new.amount_piasters IS NOT old.amount_piasters
  OR new.method          IS NOT old.method
  OR new.site_id         IS NOT old.site_id
  OR new.category_id     IS NOT old.category_id
  OR new.expense_date    IS NOT old.expense_date
  OR new.rule_id         IS NOT old.rule_id
  OR new.due_date        IS NOT old.due_date
BEGIN
  SELECT RAISE(ABORT, 'EXPENSE_IMMUTABLE|لا يمكن تعديل مصروف مُسجَّل — قم بإلغائه وسجّل مصروفًا صحيحًا');
END;

-- HARDENING: a void is one-way (web only had the RPC's "already voided" check).
CREATE TRIGGER trg_expense_void_once
BEFORE UPDATE OF voided_at ON expenses
WHEN old.voided_at IS NOT NULL AND new.voided_at IS NOT old.voided_at
BEGIN
  SELECT RAISE(ABORT, 'EXPENSE_ALREADY_VOIDED|هذا المصروف مُلغى بالفعل');
END;

CREATE TRIGGER trg_expense_void_post
AFTER UPDATE OF voided_at ON expenses
WHEN old.voided_at IS NULL AND new.voided_at IS NOT NULL AND new.method = 'cash'
BEGIN
  INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, source_id, reason, created_by)
  VALUES (new.site_id, new.amount_piasters, 'expense', new.id,
          'إلغاء مصروف: ' || coalesce((SELECT name_ar FROM expense_categories WHERE id = new.category_id), 'أخرى')
                          || ' — ' || trim(new.void_reason),
          (SELECT actor_user_id FROM app_context WHERE id = 1));
END;

-- ---------------------------------------------------------------------------
-- 11.13 ATTACHMENTS (order_attachments_cascade + parent existence)
-- ---------------------------------------------------------------------------
CREATE TRIGGER trg_order_attachments_parent
BEFORE INSERT ON order_attachments
BEGIN
  SELECT RAISE(ABORT, 'ATTACHMENT_PARENT_NOT_FOUND|الأمر المرفق به غير موجود')
  WHERE NOT EXISTS (SELECT 1 FROM purchase_orders WHERE id = new.order_id AND new.order_type = 'purchase')
    AND NOT EXISTS (SELECT 1 FROM sales_orders    WHERE id = new.order_id AND new.order_type = 'sale');
END;
CREATE TRIGGER trg_po_attachments_cascade
BEFORE DELETE ON purchase_orders
BEGIN
  DELETE FROM order_attachments WHERE order_type = 'purchase' AND order_id = old.id;
END;
CREATE TRIGGER trg_so_attachments_cascade
BEFORE DELETE ON sales_orders
BEGIN
  DELETE FROM order_attachments WHERE order_type = 'sale' AND order_id = old.id;
END;
-- replaces pc_file_delete(): bytes can never outlive their metadata row
CREATE TRIGGER trg_attachment_blob_cleanup
AFTER DELETE ON order_attachments
BEGIN
  DELETE FROM attachment_blobs WHERE storage_path = old.storage_path;
END;

-- ---------------------------------------------------------------------------
-- 11.14 USERS (profile_seed_permissions + the lock-out rules of admin_set_user_access)
-- ---------------------------------------------------------------------------
-- A new account starts from its role's preset (copied, not linked).
CREATE TRIGGER trg_users_seed_permissions
AFTER INSERT ON users
BEGIN
  INSERT OR IGNORE INTO user_permissions (user_id, perm)
  SELECT new.id, perm FROM role_presets WHERE role = new.role;
END;

-- Never remove the last active admin, by demotion, deactivation or delete.
CREATE TRIGGER trg_users_last_admin_upd
BEFORE UPDATE OF role, active ON users
WHEN old.role = 'admin' AND old.active = 1 AND (new.role <> 'admin' OR new.active = 0)
 AND NOT EXISTS (SELECT 1 FROM users WHERE role = 'admin' AND active = 1 AND id <> old.id)
BEGIN
  SELECT RAISE(ABORT, 'LAST_ADMIN|لا يمكن إزالة آخر مدير نظام في المنشأة');
END;
CREATE TRIGGER trg_users_last_admin_del
BEFORE DELETE ON users
WHEN old.role = 'admin' AND old.active = 1
 AND NOT EXISTS (SELECT 1 FROM users WHERE role = 'admin' AND active = 1 AND id <> old.id)
BEGIN
  SELECT RAISE(ABORT, 'LAST_ADMIN|لا يمكن إزالة آخر مدير نظام في المنشأة');
END;

-- You cannot change your own role / active flag (needs app_context.actor_user_id).
CREATE TRIGGER trg_users_no_self_lockout
BEFORE UPDATE OF role, active ON users
WHEN old.id = (SELECT actor_user_id FROM app_context WHERE id = 1) AND (new.role IS NOT old.role OR new.active IS NOT old.active)
BEGIN
  SELECT RAISE(ABORT, 'SELF_ROLE_CHANGE|لا يمكنك تغيير دورك أو تعطيل حسابك بنفسك');
END;

CREATE TRIGGER trg_users_touch
AFTER UPDATE OF username, email, full_name, role, active, all_sites, password_hash,
                must_change_password, failed_attempts, locked_until, last_login_at ON users
BEGIN
  UPDATE users SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = new.id;
END;

-- organization_touch
CREATE TRIGGER trg_business_settings_touch
AFTER UPDATE OF business_name, address_line, phone_line, currency, timezone ON business_settings
BEGIN
  UPDATE business_settings SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = new.id;
END;

-- ============================================================================
-- 12. AUDIT TRIGGERS (audit_row() -> one generated trigger per binding).
--     Snapshot = json_object of every column (generated ones included).
--     Secrets (password_hash, failed_attempts, locked_until) are never snapshotted;
--     users UPDATE auditing fires only on identity/role/access changes, not logins.
--     entity names: profiles -> users, organization -> business_settings.
-- ============================================================================
CREATE TRIGGER trg_audit_payments_ins AFTER INSERT ON payments
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'payments', new.id,
          json_object('id', new.id, 'parent_type', new.parent_type, 'parent_id', new.parent_id,
                       'amount_piasters', new.amount_piasters, 'method', new.method,
                       'site_id', new.site_id, 'paid_at', new.paid_at, 'note', new.note,
                       'contact_payment_method_id', new.contact_payment_method_id,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_payments_del AFTER DELETE ON payments
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'payments', old.id,
          json_object('id', old.id, 'parent_type', old.parent_type, 'parent_id', old.parent_id,
                       'amount_piasters', old.amount_piasters, 'method', old.method,
                       'site_id', old.site_id, 'paid_at', old.paid_at, 'note', old.note,
                       'contact_payment_method_id', old.contact_payment_method_id,
                       'created_by', old.created_by, 'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_so_ins AFTER INSERT ON sales_orders
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'sales_orders', new.id,
          json_object('id', new.id, 'order_seq', new.order_seq, 'site_id', new.site_id,
                       'client_id', new.client_id, 'status', new.status, 'order_date', new.order_date,
                       'invoice_number', new.invoice_number, 'subtotal_piasters', new.subtotal_piasters,
                       'discount_type', new.discount_type,
                       'discount_value_x100', new.discount_value_x100,
                       'discount_amount_piasters', new.discount_amount_piasters,
                       'total_amount_piasters', new.total_amount_piasters, 'created_by', new.created_by,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_so_upd AFTER UPDATE OF status ON sales_orders
WHEN old.status IS NOT new.status
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'sales_orders', new.id,
          json_object('id', new.id, 'order_seq', new.order_seq, 'site_id', new.site_id,
                       'client_id', new.client_id, 'status', new.status, 'order_date', new.order_date,
                       'invoice_number', new.invoice_number, 'subtotal_piasters', new.subtotal_piasters,
                       'discount_type', new.discount_type,
                       'discount_value_x100', new.discount_value_x100,
                       'discount_amount_piasters', new.discount_amount_piasters,
                       'total_amount_piasters', new.total_amount_piasters, 'created_by', new.created_by,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_so_discount AFTER UPDATE OF discount_type, discount_value_x100 ON sales_orders
WHEN old.discount_type IS NOT new.discount_type OR old.discount_value_x100 IS NOT new.discount_value_x100
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'sales_orders', new.id,
          json_object('id', new.id, 'order_seq', new.order_seq, 'site_id', new.site_id,
                       'client_id', new.client_id, 'status', new.status, 'order_date', new.order_date,
                       'invoice_number', new.invoice_number, 'subtotal_piasters', new.subtotal_piasters,
                       'discount_type', new.discount_type,
                       'discount_value_x100', new.discount_value_x100,
                       'discount_amount_piasters', new.discount_amount_piasters,
                       'total_amount_piasters', new.total_amount_piasters, 'created_by', new.created_by,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_po_ins AFTER INSERT ON purchase_orders
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'purchase_orders', new.id,
          json_object('id', new.id, 'order_code', new.order_code, 'order_seq', new.order_seq,
                       'po_type', new.po_type, 'vendor_id', new.vendor_id, 'order_date', new.order_date,
                       'total_kg_milli', new.total_kg_milli,
                       'price_per_kg_piasters', new.price_per_kg_piasters,
                       'total_amount_piasters', new.total_amount_piasters, 'status', new.status,
                       'product_name', new.product_name, 'notes', new.notes,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_po_upd AFTER UPDATE OF status ON purchase_orders
WHEN old.status IS NOT new.status
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'purchase_orders', new.id,
          json_object('id', new.id, 'order_code', new.order_code, 'order_seq', new.order_seq,
                       'po_type', new.po_type, 'vendor_id', new.vendor_id, 'order_date', new.order_date,
                       'total_kg_milli', new.total_kg_milli,
                       'price_per_kg_piasters', new.price_per_kg_piasters,
                       'total_amount_piasters', new.total_amount_piasters, 'status', new.status,
                       'product_name', new.product_name, 'notes', new.notes,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_conv_ins AFTER INSERT ON po_conversions
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'po_conversions', new.id,
          json_object('id', new.id, 'po_id', new.po_id, 'site_id', new.site_id, 'item_id', new.item_id,
                       'kg_consumed_milli', new.kg_consumed_milli,
                       'output_qty_milli', new.output_qty_milli, 'output_unit', new.output_unit,
                       'conversion_date', new.conversion_date, 'created_by', new.created_by,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_conv_del AFTER DELETE ON po_conversions
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'po_conversions', old.id,
          json_object('id', old.id, 'po_id', old.po_id, 'site_id', old.site_id, 'item_id', old.item_id,
                       'kg_consumed_milli', old.kg_consumed_milli,
                       'output_qty_milli', old.output_qty_milli, 'output_unit', old.output_unit,
                       'conversion_date', old.conversion_date, 'created_by', old.created_by,
                       'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_transfer_ins AFTER INSERT ON stock_transfers
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'stock_transfers', new.id,
          json_object('id', new.id, 'from_site', new.from_site, 'to_site', new.to_site,
                       'item_id', new.item_id, 'qty_milli', new.qty_milli, 'note', new.note,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_stock_adj AFTER INSERT ON stock_movements
WHEN new.source_type IN ('adjustment','opening')
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'stock_movements', new.id,
          json_object('id', new.id, 'site_id', new.site_id, 'item_id', new.item_id,
                       'qty_delta_milli', new.qty_delta_milli, 'source_type', new.source_type,
                       'source_id', new.source_id, 'note', new.note, 'created_by', new.created_by,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_cash_manual AFTER INSERT ON cash_movements
WHEN new.source_type = 'manual'
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'cash_movements', new.id,
          json_object('id', new.id, 'site_id', new.site_id,
                       'amount_delta_piasters', new.amount_delta_piasters,
                       'source_type', new.source_type, 'source_id', new.source_id, 'reason', new.reason,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_items_ins AFTER INSERT ON items
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'items', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'unit_type', new.unit_type,
                       'low_stock_threshold_milli', new.low_stock_threshold_milli,
                       'sale_price_piasters', new.sale_price_piasters,
                       'cost_price_piasters', new.cost_price_piasters, 'active', new.active,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_items_upd AFTER UPDATE ON items
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'items', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'unit_type', new.unit_type,
                       'low_stock_threshold_milli', new.low_stock_threshold_milli,
                       'sale_price_piasters', new.sale_price_piasters,
                       'cost_price_piasters', new.cost_price_piasters, 'active', new.active,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_items_del AFTER DELETE ON items
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'items', old.id,
          json_object('id', old.id, 'name_ar', old.name_ar, 'unit_type', old.unit_type,
                       'low_stock_threshold_milli', old.low_stock_threshold_milli,
                       'sale_price_piasters', old.sale_price_piasters,
                       'cost_price_piasters', old.cost_price_piasters, 'active', old.active,
                       'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_contacts_ins AFTER INSERT ON contacts
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'contacts', new.id,
          json_object('id', new.id, 'type', new.type, 'name', new.name, 'address', new.address,
                       'notes', new.notes, 'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_contacts_upd AFTER UPDATE ON contacts
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'contacts', new.id,
          json_object('id', new.id, 'type', new.type, 'name', new.name, 'address', new.address,
                       'notes', new.notes, 'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_contacts_del AFTER DELETE ON contacts
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'contacts', old.id,
          json_object('id', old.id, 'type', old.type, 'name', old.name, 'address', old.address,
                       'notes', old.notes, 'created_by', old.created_by, 'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_users_ins AFTER INSERT ON users
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'users', new.id,
          json_object('id', new.id, 'username', new.username, 'email', new.email,
                       'full_name', new.full_name, 'role', new.role, 'active', new.active,
                       'all_sites', new.all_sites, 'password_changed_at', new.password_changed_at,
                       'must_change_password', new.must_change_password,
                       'last_login_at', new.last_login_at, 'created_at', new.created_at,
                       'updated_at', new.updated_at));
END;
CREATE TRIGGER trg_audit_users_upd AFTER UPDATE OF username, email, full_name, role, active, all_sites, must_change_password ON users
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'users', new.id,
          json_object('id', new.id, 'username', new.username, 'email', new.email,
                       'full_name', new.full_name, 'role', new.role, 'active', new.active,
                       'all_sites', new.all_sites, 'password_changed_at', new.password_changed_at,
                       'must_change_password', new.must_change_password,
                       'last_login_at', new.last_login_at, 'created_at', new.created_at,
                       'updated_at', new.updated_at));
END;
CREATE TRIGGER trg_audit_users_del AFTER DELETE ON users
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'users', old.id,
          json_object('id', old.id, 'username', old.username, 'email', old.email,
                       'full_name', old.full_name, 'role', old.role, 'active', old.active,
                       'all_sites', old.all_sites, 'password_changed_at', old.password_changed_at,
                       'must_change_password', old.must_change_password,
                       'last_login_at', old.last_login_at, 'created_at', old.created_at,
                       'updated_at', old.updated_at));
END;
CREATE TRIGGER trg_audit_sites_ins AFTER INSERT ON sites
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'sites', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'active', new.active,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_sites_upd AFTER UPDATE ON sites
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'sites', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'active', new.active,
                       'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_business_settings AFTER UPDATE ON business_settings
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'business_settings', new.id,
          json_object('id', new.id, 'business_name', new.business_name,
                       'address_line', new.address_line, 'phone_line', new.phone_line,
                       'currency', new.currency, 'timezone', new.timezone, 'updated_at', new.updated_at));
END;
CREATE TRIGGER trg_audit_client_credits AFTER INSERT ON client_credits
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'client_credits', new.id,
          json_object('id', new.id, 'contact_id', new.contact_id,
                       'amount_delta_piasters', new.amount_delta_piasters,
                       'source_type', new.source_type, 'source_id', new.source_id, 'method', new.method,
                       'site_id', new.site_id, 'occurred_on', new.occurred_on, 'note', new.note,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_vendor_credits AFTER INSERT ON vendor_credits
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'vendor_credits', new.id,
          json_object('id', new.id, 'contact_id', new.contact_id,
                       'amount_delta_piasters', new.amount_delta_piasters,
                       'source_type', new.source_type, 'source_id', new.source_id, 'method', new.method,
                       'site_id', new.site_id, 'occurred_on', new.occurred_on, 'note', new.note,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_attachment_ins AFTER INSERT ON order_attachments
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'order_attachments', new.id,
          json_object('id', new.id, 'order_type', new.order_type, 'order_id', new.order_id,
                       'storage_path', new.storage_path, 'file_name', new.file_name,
                       'mime_type', new.mime_type, 'byte_size', new.byte_size,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_attachment_del AFTER DELETE ON order_attachments
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'order_attachments', old.id,
          json_object('id', old.id, 'order_type', old.order_type, 'order_id', old.order_id,
                       'storage_path', old.storage_path, 'file_name', old.file_name,
                       'mime_type', old.mime_type, 'byte_size', old.byte_size,
                       'created_by', old.created_by, 'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_contact_opening_balances AFTER INSERT ON contact_opening_balances
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'contact_opening_balances', new.id,
          json_object('id', new.id, 'contact_id', new.contact_id, 'kind', new.kind,
                       'amount_delta_piasters', new.amount_delta_piasters, 'as_of', new.as_of,
                       'note', new.note, 'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expenses_ins AFTER INSERT ON expenses
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'expenses', new.id,
          json_object('id', new.id, 'category_id', new.category_id, 'site_id', new.site_id,
                       'amount_piasters', new.amount_piasters, 'method', new.method,
                       'expense_date', new.expense_date, 'description', new.description,
                       'rule_id', new.rule_id, 'due_date', new.due_date, 'voided_at', new.voided_at,
                       'voided_by', new.voided_by, 'void_reason', new.void_reason,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expenses_upd AFTER UPDATE ON expenses
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'expenses', new.id,
          json_object('id', new.id, 'category_id', new.category_id, 'site_id', new.site_id,
                       'amount_piasters', new.amount_piasters, 'method', new.method,
                       'expense_date', new.expense_date, 'description', new.description,
                       'rule_id', new.rule_id, 'due_date', new.due_date, 'voided_at', new.voided_at,
                       'voided_by', new.voided_by, 'void_reason', new.void_reason,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expense_rules_ins AFTER INSERT ON expense_rules
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'expense_rules', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'category_id', new.category_id,
                       'site_id', new.site_id, 'amount_piasters', new.amount_piasters,
                       'method', new.method, 'description', new.description, 'frequency', new.frequency,
                       'day_of_week', new.day_of_week, 'day_of_month', new.day_of_month,
                       'start_date', new.start_date, 'end_date', new.end_date, 'active', new.active,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expense_rules_upd AFTER UPDATE ON expense_rules
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'expense_rules', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'category_id', new.category_id,
                       'site_id', new.site_id, 'amount_piasters', new.amount_piasters,
                       'method', new.method, 'description', new.description, 'frequency', new.frequency,
                       'day_of_week', new.day_of_week, 'day_of_month', new.day_of_month,
                       'start_date', new.start_date, 'end_date', new.end_date, 'active', new.active,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expense_rules_del AFTER DELETE ON expense_rules
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'expense_rules', old.id,
          json_object('id', old.id, 'name_ar', old.name_ar, 'category_id', old.category_id,
                       'site_id', old.site_id, 'amount_piasters', old.amount_piasters,
                       'method', old.method, 'description', old.description, 'frequency', old.frequency,
                       'day_of_week', old.day_of_week, 'day_of_month', old.day_of_month,
                       'start_date', old.start_date, 'end_date', old.end_date, 'active', old.active,
                       'created_by', old.created_by, 'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_expense_categories_ins AFTER INSERT ON expense_categories
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'expense_categories', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'sort', new.sort, 'active', new.active,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expense_categories_upd AFTER UPDATE ON expense_categories
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'update', 'expense_categories', new.id,
          json_object('id', new.id, 'name_ar', new.name_ar, 'sort', new.sort, 'active', new.active,
                       'created_by', new.created_by, 'created_at', new.created_at));
END;
CREATE TRIGGER trg_audit_expense_categories_del AFTER DELETE ON expense_categories
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'delete', 'expense_categories', old.id,
          json_object('id', old.id, 'name_ar', old.name_ar, 'sort', old.sort, 'active', old.active,
                       'created_by', old.created_by, 'created_at', old.created_at));
END;
CREATE TRIGGER trg_audit_expense_rule_skips AFTER INSERT ON expense_rule_skips
BEGIN
  INSERT INTO audit_log (actor, action, entity, entity_id, data)
  VALUES ((SELECT actor_user_id FROM app_context WHERE id = 1), 'insert', 'expense_rule_skips', new.id,
          json_object('id', new.id, 'rule_id', new.rule_id, 'due_date', new.due_date,
                       'reason', new.reason, 'created_by', new.created_by, 'created_at', new.created_at));
END;


-- ============================================================================
-- 13. VIEWS — balances are always SUMs over the ledgers; nothing is stored.
--     "As of a date" variants need a parameter, so they are queries in the service
--     layer (examples in 03-DB-PORTING-NOTES.md §6). All amounts: piasters;
--     all quantities: milli-units.
-- ============================================================================

-- get_stock(site,item,NULL): quantity on hand per branch per item.
-- (Aggregate across all branches = GROUP BY item_id over this view.)
CREATE VIEW v_stock_balance AS
SELECT site_id, item_id, SUM(qty_delta_milli) AS qty_milli
  FROM stock_movements
 GROUP BY site_id, item_id;

-- get_stock(NULL,item,NULL): all-branches total per item.
CREATE VIEW v_stock_total AS
SELECT item_id, SUM(qty_delta_milli) AS qty_milli
  FROM stock_movements
 GROUP BY item_id;

-- get_cash_balance(site,NULL): drawer per site (every site listed, 0 if untouched).
CREATE VIEW v_cash_drawer_balance AS
SELECT s.id AS site_id, s.name_ar AS site_name, s.active,
       COALESCE((SELECT SUM(cm.amount_delta_piasters) FROM cash_movements cm WHERE cm.site_id = s.id), 0) AS balance_piasters
  FROM sites s;

-- Sales order balance: total - SUM(payments incl. method 'credit').
CREATE VIEW v_so_balance AS
SELECT so.id AS so_id, so.site_id, so.client_id, so.status, so.order_date,
       so.total_amount_piasters AS total_piasters,
       COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                  WHERE p.parent_type = 'so' AND p.parent_id = so.id), 0) AS paid_piasters,
       so.total_amount_piasters
       - COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                    WHERE p.parent_type = 'so' AND p.parent_id = so.id), 0) AS balance_piasters
  FROM sales_orders so;

-- Purchase order balance.
CREATE VIEW v_po_balance AS
SELECT po.id AS po_id, po.vendor_id, po.po_type, po.status, po.order_date,
       po.total_amount_piasters AS total_piasters,
       COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                  WHERE p.parent_type = 'po' AND p.parent_id = po.id), 0) AS paid_piasters,
       po.total_amount_piasters
       - COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                    WHERE p.parent_type = 'po' AND p.parent_id = po.id), 0) AS balance_piasters
  FROM purchase_orders po;

-- po_converted_kg / po_remaining_kg (general POs): total_kg, converted_kg, remaining_kg.
CREATE VIEW v_po_kg_progress AS
SELECT po.id AS po_id,
       po.total_kg_milli,
       COALESCE((SELECT SUM(c.kg_consumed_milli) FROM po_conversions c WHERE c.po_id = po.id), 0) AS converted_kg_milli,
       po.total_kg_milli
       - COALESCE((SELECT SUM(c.kg_consumed_milli) FROM po_conversions c WHERE c.po_id = po.id), 0) AS remaining_kg_milli
  FROM purchase_orders po
 WHERE po.po_type = 'general';

-- po_itemized_remaining: units still to receive on an itemized PO.
CREATE VIEW v_po_itemized_progress AS
SELECT po.id AS po_id,
       COALESCE((SELECT SUM(l.qty_milli) FROM po_lines l WHERE l.po_id = po.id), 0) AS ordered_milli,
       COALESCE((SELECT SUM(c.qty_milli) FROM po_line_conversions c WHERE c.po_id = po.id), 0) AS received_milli,
       COALESCE((SELECT SUM(l.qty_milli) FROM po_lines l WHERE l.po_id = po.id), 0)
       - COALESCE((SELECT SUM(c.qty_milli) FROM po_line_conversions c WHERE c.po_id = po.id), 0) AS remaining_milli
  FROM purchase_orders po
 WHERE po.po_type = 'itemized';

-- get_client_credit / get_vendor_credit
CREATE VIEW v_client_credit_balance AS
SELECT contact_id, SUM(amount_delta_piasters) AS balance_piasters FROM client_credits GROUP BY contact_id;
CREATE VIEW v_vendor_credit_balance AS
SELECT contact_id, SUM(amount_delta_piasters) AS balance_piasters FROM vendor_credits GROUP BY contact_id;

-- get_contact_balance (final 0039 body). Sign: POSITIVE = the contact owes us
-- (عليه), NEGATIVE = we owe the contact (له). Components exposed for statements.
--   balance = receivable - payable - client_credit + vendor_credit
--             + opening(receivable) - opening(payable)
CREATE VIEW v_contact_balance AS
SELECT contact_id,
       receivable_piasters, payable_piasters,
       client_credit_piasters, vendor_credit_piasters, opening_net_piasters,
       receivable_piasters - payable_piasters
         - client_credit_piasters + vendor_credit_piasters
         + opening_net_piasters AS balance_piasters
  FROM (
    SELECT c.id AS contact_id,
           COALESCE((SELECT SUM(so.total_amount_piasters) FROM sales_orders so
                      WHERE so.client_id = c.id AND so.status <> 'draft'), 0)
         - COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                       JOIN sales_orders so ON so.id = p.parent_id
                      WHERE p.parent_type = 'so' AND so.client_id = c.id AND so.status <> 'draft'), 0)
           AS receivable_piasters,
           COALESCE((SELECT SUM(po.total_amount_piasters) FROM purchase_orders po
                      WHERE po.vendor_id = c.id), 0)
         - COALESCE((SELECT SUM(p.amount_piasters) FROM payments p
                       JOIN purchase_orders po ON po.id = p.parent_id
                      WHERE p.parent_type = 'po' AND po.vendor_id = c.id), 0)
           AS payable_piasters,
           COALESCE((SELECT SUM(amount_delta_piasters) FROM client_credits WHERE contact_id = c.id), 0)
           AS client_credit_piasters,
           COALESCE((SELECT SUM(amount_delta_piasters) FROM vendor_credits WHERE contact_id = c.id), 0)
           AS vendor_credit_piasters,
           COALESCE((SELECT SUM(CASE kind WHEN 'receivable' THEN amount_delta_piasters
                                          ELSE -amount_delta_piasters END)
                       FROM contact_opening_balances WHERE contact_id = c.id), 0)
           AS opening_net_piasters
      FROM contacts c
  );

-- Dashboard "low stock" list as of NOW (qty <= threshold, threshold > 0, active only).
-- The dated variant (get_stock(...,p_date)) is a service query.
CREATE VIEW v_low_stock AS
SELECT i.id AS item_id, i.name_ar, i.unit_type,
       s.id AS site_id, s.name_ar AS site_name,
       COALESCE(b.qty_milli, 0) AS qty_milli,
       i.low_stock_threshold_milli AS threshold_milli
  FROM items i
  CROSS JOIN sites s
  LEFT JOIN v_stock_balance b ON b.site_id = s.id AND b.item_id = i.id
 WHERE i.active = 1 AND s.active = 1
   AND i.low_stock_threshold_milli > 0
   AND COALESCE(b.qty_milli, 0) <= i.low_stock_threshold_milli;

-- has_perm(key) as a view (is_admin() bypass included): the permissions a user holds.
-- Service check:  SELECT 1 FROM v_user_permissions WHERE user_id = :u AND perm = :key
CREATE VIEW v_user_permissions AS
SELECT u.id AS user_id, k.key AS perm
  FROM users u JOIN permission_keys k ON u.role = 'admin'
UNION
SELECT user_id, perm FROM user_permissions;

-- can_use_site(site) as a view: the branches a user may see/act on.
-- (can_use_site(NULL) is TRUE in the web app: a site-agnostic row is open to all —
--  handle NULL in the service.) Inactive users must additionally be rejected at login.
CREATE VIEW v_user_site_access AS
SELECT u.id AS user_id, s.id AS site_id
  FROM users u JOIN sites s ON u.role = 'admin' OR u.all_sites = 1
UNION
SELECT user_id, site_id FROM user_sites;
