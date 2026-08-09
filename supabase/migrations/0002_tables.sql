-- ============================================================================
-- 0002_tables.sql — Core tables
-- ============================================================================
-- Money is NUMERIC(12,2) everywhere (never float). Weights are NUMERIC(12,3)
-- to allow gram precision on KG purchases. stock_movements and cash_movements
-- are APPEND-ONLY ledgers — balances are always sums, never stored editable
-- numbers (see CONVENTIONS in CLAUDE.md).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- sites — physical branches (فروع). Everything stock/cash is scoped here.
-- ---------------------------------------------------------------------------
create table sites (
  id         uuid primary key default gen_random_uuid(),
  name_ar    text        not null,
  active     boolean     not null default true,
  created_at timestamptz not null default now()
);

-- ---------------------------------------------------------------------------
-- profiles — one row per auth user, carrying their role (rule 9).
-- ---------------------------------------------------------------------------
create table profiles (
  user_id    uuid primary key references auth.users(id) on delete cascade,
  full_name  text        not null,
  role       app_role    not null default 'staff',
  active     boolean     not null default true,
  created_at timestamptz not null default now()
);

-- ---------------------------------------------------------------------------
-- contacts — vendors / clients / both (rule 8).
-- ---------------------------------------------------------------------------
create table contacts (
  id         uuid primary key default gen_random_uuid(),
  type       contact_type not null,
  name       text         not null,
  address    text,
  notes      text,
  created_by uuid references auth.users(id),
  created_at timestamptz  not null default now()
);
create index contacts_type_idx on contacts(type);

-- Extra phones live in their own table; exactly one is the primary (mandatory).
create table contact_phones (
  id         uuid primary key default gen_random_uuid(),
  contact_id uuid    not null references contacts(id) on delete cascade,
  phone      text    not null,
  is_primary boolean not null default false
);
create index contact_phones_contact_idx on contact_phones(contact_id);
-- At most one primary phone per contact.
create unique index contact_phones_one_primary
  on contact_phones(contact_id) where is_primary;

-- Repeatable payment methods. instapay → number; bank_transfer → bank + account.
create table contact_payment_methods (
  id             uuid primary key default gen_random_uuid(),
  contact_id     uuid           not null references contacts(id) on delete cascade,
  method         payment_method not null,
  instapay_number text,
  bank_name      text,
  account_number text,
  -- shape must match the method chosen
  constraint contact_pm_shape check (
    (method = 'instapay'      and instapay_number is not null) or
    (method = 'bank_transfer' and bank_name is not null and account_number is not null) or
    (method = 'cash')
  )
);
create index contact_pm_contact_idx on contact_payment_methods(contact_id);

-- ---------------------------------------------------------------------------
-- items — the catalogue. sale_price preset here, prefilled into SO lines (rule 7).
-- ---------------------------------------------------------------------------
create table items (
  id                  uuid primary key default gen_random_uuid(),
  name_ar             text          not null,
  unit_type           unit_type     not null,
  low_stock_threshold numeric(12,3) not null default 0,
  sale_price          numeric(12,2) not null default 0,
  active              boolean       not null default true,
  created_at          timestamptz   not null default now()
);

-- ---------------------------------------------------------------------------
-- purchase_orders — bought in KG, site-agnostic at creation (rule 2 & 3).
-- ---------------------------------------------------------------------------
create table purchase_orders (
  order_code   text unique,                       -- PO-YYYY-#### (assigned by trigger)
  id           uuid primary key default gen_random_uuid(),
  vendor_id    uuid          not null references contacts(id),
  order_date   date          not null default current_date,
  total_kg     numeric(12,3) not null check (total_kg > 0),
  price_per_kg numeric(12,2) not null check (price_per_kg >= 0),
  total_amount numeric(12,2) not null check (total_amount >= 0),
  status       po_status     not null default 'open',
  created_by   uuid references auth.users(id),
  created_at   timestamptz   not null default now()
);
create index po_vendor_idx on purchase_orders(vendor_id);
create index po_status_idx on purchase_orders(status);

-- ---------------------------------------------------------------------------
-- po_conversions — free-entry conversions of PO weight into stock at a site.
-- Each row → one positive stock_movement (handled by trigger). Rule 2.
-- ---------------------------------------------------------------------------
create table po_conversions (
  id              uuid primary key default gen_random_uuid(),
  po_id           uuid          not null references purchase_orders(id) on delete cascade,
  site_id         uuid          not null references sites(id),
  item_id         uuid          not null references items(id),
  kg_consumed     numeric(12,3) not null check (kg_consumed > 0),
  output_qty      numeric(12,3) not null check (output_qty > 0),
  output_unit     unit_type     not null,
  conversion_date date          not null default current_date,
  created_by      uuid references auth.users(id),
  created_at      timestamptz   not null default now()
);
create index po_conv_po_idx   on po_conversions(po_id);
create index po_conv_site_idx on po_conversions(site_id);
create index po_conv_item_idx on po_conversions(item_id);

-- ---------------------------------------------------------------------------
-- sales_orders — belong to exactly one site (rule 6).
-- ---------------------------------------------------------------------------
create table sales_orders (
  id             uuid primary key default gen_random_uuid(),
  site_id        uuid        not null references sites(id),
  client_id      uuid        not null references contacts(id),
  status         so_status   not null default 'draft',
  order_date     date        not null default current_date,
  total_amount   numeric(12,2) not null default 0 check (total_amount >= 0),
  invoice_number text unique,                      -- SO-YYYY-#### (assigned at invoicing)
  created_by     uuid references auth.users(id),
  created_at     timestamptz not null default now()
);
create index so_site_idx   on sales_orders(site_id);
create index so_client_idx on sales_orders(client_id);
create index so_status_idx on sales_orders(status);

-- Line items. line_total is derived (never hand-entered). Editable only while draft.
create table sales_order_lines (
  id         uuid primary key default gen_random_uuid(),
  so_id      uuid          not null references sales_orders(id) on delete cascade,
  item_id    uuid          not null references items(id),
  qty        numeric(12,3) not null check (qty > 0),
  unit_price numeric(12,2) not null check (unit_price >= 0),
  line_total numeric(12,2) generated always as (round(qty * unit_price, 2)) stored
);
create index sol_so_idx on sales_order_lines(so_id);

-- ---------------------------------------------------------------------------
-- payments — shared ledger for both POs and SOs (rule 4).
-- site_id = which cash drawer a cash payment hits. For SO payments it defaults
-- to the SO's site; for PO cash payments it must be supplied (rule 5).
-- ---------------------------------------------------------------------------
create table payments (
  id          uuid primary key default gen_random_uuid(),
  parent_type payment_parent not null,
  parent_id   uuid           not null,
  amount      numeric(12,2)  not null check (amount > 0),
  method      payment_method not null,
  site_id     uuid references sites(id),
  paid_at     timestamptz    not null default now(),
  note        text,
  created_by  uuid references auth.users(id),
  created_at  timestamptz    not null default now()
);
create index payments_parent_idx on payments(parent_type, parent_id);
create index payments_paid_at_idx on payments(paid_at);

-- ---------------------------------------------------------------------------
-- stock_movements — APPEND-ONLY signed ledger. Stock = sum(qty_delta). Rule 2/6.
-- ---------------------------------------------------------------------------
create table stock_movements (
  id          uuid primary key default gen_random_uuid(),
  site_id     uuid          not null references sites(id),
  item_id     uuid          not null references items(id),
  qty_delta   numeric(12,3) not null,               -- signed: + in, - out
  source_type stock_source  not null,
  source_id   uuid,                                  -- conversion/line/transfer/... id
  note        text,
  created_by  uuid references auth.users(id),
  created_at  timestamptz   not null default now()
);
create index sm_site_item_idx on stock_movements(site_id, item_id);
create index sm_item_idx      on stock_movements(item_id);
create index sm_created_idx   on stock_movements(created_at);

-- ---------------------------------------------------------------------------
-- stock_transfers — moves stock between two sites; writes two stock_movements.
-- ---------------------------------------------------------------------------
create table stock_transfers (
  id         uuid primary key default gen_random_uuid(),
  from_site  uuid          not null references sites(id),
  to_site    uuid          not null references sites(id),
  item_id    uuid          not null references items(id),
  qty        numeric(12,3) not null check (qty > 0),
  note       text,
  created_by uuid references auth.users(id),
  created_at timestamptz   not null default now(),
  constraint transfer_distinct_sites check (from_site <> to_site)
);

-- ---------------------------------------------------------------------------
-- cash_movements — APPEND-ONLY per-site drawer ledger. Drawer = sum(amount_delta).
-- Rule 5.
-- ---------------------------------------------------------------------------
create table cash_movements (
  id          uuid primary key default gen_random_uuid(),
  site_id     uuid          not null references sites(id),
  amount_delta numeric(12,2) not null,              -- signed: + in, - out
  source_type cash_source   not null,
  source_id   uuid,                                  -- payment id, or null for manual
  reason      text          not null,               -- mandatory note (رصيد/إيداع/سحب/تسوية)
  created_by  uuid references auth.users(id),
  created_at  timestamptz   not null default now()
);
create index cm_site_idx    on cash_movements(site_id);
create index cm_created_idx on cash_movements(created_at);

-- ---------------------------------------------------------------------------
-- doc_counters — per-year atomic sequence backing PO / invoice numbering.
-- ---------------------------------------------------------------------------
create table doc_counters (
  scope      text not null,      -- 'po' | 'so'
  year       int  not null,
  last_value int  not null default 0,
  primary key (scope, year)
);
