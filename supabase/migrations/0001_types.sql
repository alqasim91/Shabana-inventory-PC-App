-- ============================================================================
-- 0001_types.sql — Extensions & enum types
-- مخزون شبانه — Inventory Management PWA
-- ============================================================================
-- Postgres enums keep the domain vocabulary in the database itself, so a bad
-- role / status / method can never be written from any client. All enum labels
-- are stored in English (stable identifiers); Arabic display labels live in the
-- app's /src/labels.ts, never in the DB.
-- ============================================================================

create extension if not exists "pgcrypto";   -- gen_random_uuid()

-- Who a person is in the system (rule 9).
create type app_role as enum ('admin', 'manager', 'staff');

-- A contact can be a vendor, a client, or both (rule 8).
create type contact_type as enum ('vendor', 'client', 'both');

-- How money moved. cash payments additionally hit the site cash drawer (rule 5).
create type payment_method as enum ('cash', 'instapay', 'bank_transfer');

-- Items are counted either by weight (كجم) or by piece (وحدة).
create type unit_type as enum ('kg', 'unit');

-- Purchase order lifecycle. open until fully converted into stock (rule 2).
create type po_status as enum ('open', 'fully_converted', 'closed');

-- Sales order lifecycle (rule 6). closed is set automatically on full collection.
create type so_status as enum ('draft', 'invoiced', 'placed', 'closed');

-- A payment belongs either to a purchase order or a sales order (rule 4).
create type payment_parent as enum ('po', 'so');

-- Why a stock_movements row exists. Every quantity change is one of these.
create type stock_source as enum ('po_conversion', 'sale', 'adjustment', 'transfer');

-- Why a cash_movements row exists: an order payment, or a manual drawer action.
create type cash_source as enum ('payment', 'manual');
