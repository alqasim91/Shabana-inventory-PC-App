-- ============================================================================
-- 0019_itemized_po.sql — second purchase-order kind: itemized (أمر مصنّف)
-- ----------------------------------------------------------------------------
-- Until now every PO was a single product bought in KG and converted to stock
-- via po_conversions (now called an "أمر عام" / general order). This adds a
-- second kind — "أمر مصنّف" / itemized — that is a plain line list, each line
-- already an inventory item at a known quantity, unit price, AND destination
-- branch. Because a line fully specifies (item, site, qty), it lands straight
-- into that branch's stock the moment it is saved — no separate conversion step.
--
-- Design:
--   * purchase_orders gains a po_type discriminator (default 'general', so
--     EVERY existing PO is unchanged). For itemized orders total_kg/price_per_kg
--     are meaningless and stay NULL; total_amount = Σ line totals (trigger-kept).
--   * po_lines holds the itemized lines. Insert → one positive stock_movement at
--     the line's site; delete → a compensating negative movement (guarded by
--     available stock), mirroring po_conversions exactly.
--   * Two SECURITY DEFINER RPCs (create/update) do the whole thing atomically,
--     including creating any brand-new inventory item named on a line.
--
-- Additive only — existing rows, the weight-PO path, payments, the ledger and
-- the admin-edit machinery are all untouched.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Discriminator + relax the weight columns for itemized orders.
-- ---------------------------------------------------------------------------
create type po_type as enum ('general', 'itemized');

alter table purchase_orders add column po_type po_type not null default 'general';

-- Weight fields are required for general orders, absent for itemized ones.
alter table purchase_orders alter column total_kg     drop not null;
alter table purchase_orders alter column price_per_kg drop not null;

alter table purchase_orders add constraint po_type_fields_chk check (
     (po_type = 'general'  and total_kg is not null and price_per_kg is not null)
  or (po_type = 'itemized' and total_kg is null     and price_per_kg is null)
);

-- ---------------------------------------------------------------------------
-- 2. po_lines — itemized lines, each with its own destination branch.
-- ---------------------------------------------------------------------------
create table po_lines (
  id         uuid          primary key default gen_random_uuid(),
  po_id      uuid          not null references purchase_orders(id) on delete cascade,
  item_id    uuid          not null references items(id),
  site_id    uuid          not null references sites(id),           -- destination فرع
  qty        numeric(12,3) not null check (qty > 0),
  unit_price numeric(12,2) not null check (unit_price >= 0),
  line_total numeric(12,2) generated always as (round(qty * unit_price, 2)) stored,
  created_by uuid          references auth.users(id),
  created_at timestamptz   not null default now()
);
create index po_lines_po_idx   on po_lines(po_id);
create index po_lines_item_idx on po_lines(item_id);
create index po_lines_site_idx on po_lines(site_id);

-- Read for everyone; writes go only through the SECURITY DEFINER RPCs below
-- (which also create new items and keep the total in sync), never direct.
alter table po_lines enable row level security;
create policy po_lines_read on po_lines for select to authenticated using (true);

-- ---------------------------------------------------------------------------
-- 3. Triggers: line → stock, and keep the itemized order total in sync.
-- ---------------------------------------------------------------------------

-- Insert a line → the goods arrive at that branch (positive stock movement).
create or replace function po_line_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.site_id, new.item_id, new.qty, 'po_conversion', new.id, new.created_by,
          'استلام من ' || (select order_code from purchase_orders where id = new.po_id));
  return new;
end;
$$;
create trigger trg_po_line_apply
  after insert on po_lines
  for each row execute function po_line_apply();

-- Delete a line (edit/removal) → give the stock back, but only if the branch
-- still holds it (same guard as reversing a conversion).
create or replace function po_line_reverse()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  if get_stock(old.site_id, old.item_id, null) - old.qty < -0.0005 then
    raise exception 'لا يمكن تعديل/حذف البند: الكمية لم تعد متوفرة في مخزون الفرع';
  end if;
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (old.site_id, old.item_id, -old.qty, 'adjustment', old.id, auth.uid(),
          'عكس بند أمر شراء مصنّف');
  return old;
end;
$$;
create trigger trg_po_line_reverse
  before delete on po_lines
  for each row execute function po_line_reverse();

-- Keep total_amount = Σ line_total for the parent (itemized) order.
create or replace function po_line_recompute_total()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare v_po uuid := coalesce(new.po_id, old.po_id);
begin
  update purchase_orders
    set total_amount = coalesce((select sum(line_total) from po_lines where po_id = v_po), 0)
    where id = v_po;
  return null;
end;
$$;
create trigger trg_po_line_recompute_total
  after insert or update or delete on po_lines
  for each row execute function po_line_recompute_total();

-- ---------------------------------------------------------------------------
-- 4. create_itemized_po — one atomic call: order + new items + lines + stock.
--    p_lines: [{ item_id?, new_name?, new_unit?, site_id, qty, unit_price }]
--    A line either names an existing item_id OR provides new_name + new_unit
--    (a fresh inventory item is created for it).
-- ---------------------------------------------------------------------------
create or replace function create_itemized_po(
  p_vendor uuid,
  p_date   date,
  p_notes  text,
  p_lines  jsonb
) returns uuid
language plpgsql
security definer set search_path = public
as $$
declare
  v_po_id   uuid;
  v_uid     uuid := auth.uid();
  v_line    jsonb;
  v_item_id uuid;
begin
  if not is_manager_or_admin() then
    raise exception 'هذا الإجراء متاح للمدير أو المسؤول فقط';
  end if;
  if p_vendor is null then
    raise exception 'المورد مطلوب';
  end if;
  if jsonb_typeof(p_lines) is distinct from 'array' or jsonb_array_length(p_lines) = 0 then
    raise exception 'أضف صنفًا واحدًا على الأقل';
  end if;

  insert into purchase_orders
    (vendor_id, order_date, po_type, total_kg, price_per_kg, total_amount, product_name, notes, created_by)
  values
    (p_vendor, coalesce(p_date, current_date), 'itemized', null, null, 0, null, p_notes, v_uid)
  returning id into v_po_id;

  for v_line in select * from jsonb_array_elements(p_lines)
  loop
    v_item_id := nullif(v_line->>'item_id', '')::uuid;

    if v_item_id is null then
      if coalesce(trim(v_line->>'new_name'), '') = '' then
        raise exception 'اسم الصنف الجديد مطلوب';
      end if;
      insert into items (name_ar, unit_type, sale_price, low_stock_threshold, active)
      values (trim(v_line->>'new_name'), (v_line->>'new_unit')::unit_type, 0, 0, true)
      returning id into v_item_id;
    end if;

    insert into po_lines (po_id, item_id, site_id, qty, unit_price, created_by)
    values (
      v_po_id,
      v_item_id,
      (v_line->>'site_id')::uuid,
      (v_line->>'qty')::numeric,
      (v_line->>'unit_price')::numeric,
      v_uid
    );
  end loop;

  return v_po_id;
end;
$$;

-- ---------------------------------------------------------------------------
-- 5. update_itemized_po — admin edit: rewrite header + lines, stock re-synced.
--    Deleting the old lines reverses their stock (guarded); re-inserting the
--    new ones re-applies it. Anything invalid rolls the whole edit back.
-- ---------------------------------------------------------------------------
create or replace function update_itemized_po(
  p_id     uuid,
  p_vendor uuid,
  p_date   date,
  p_notes  text,
  p_lines  jsonb
) returns void
language plpgsql
security definer set search_path = public
as $$
declare
  v_type    po_type;
  v_uid     uuid := auth.uid();
  v_line    jsonb;
  v_item_id uuid;
begin
  if not is_admin() then
    raise exception 'هذا الإجراء متاح لمدير النظام فقط';
  end if;
  select po_type into v_type from purchase_orders where id = p_id for update;
  if v_type is null then
    raise exception 'أمر الشراء غير موجود';
  end if;
  if v_type is distinct from 'itemized' then
    raise exception 'هذا الأمر ليس أمرًا مصنّفًا';
  end if;
  if jsonb_typeof(p_lines) is distinct from 'array' or jsonb_array_length(p_lines) = 0 then
    raise exception 'أضف صنفًا واحدًا على الأقل';
  end if;

  update purchase_orders
    set vendor_id = p_vendor, order_date = coalesce(p_date, order_date), notes = p_notes
    where id = p_id;

  delete from po_lines where po_id = p_id;  -- trg_po_line_reverse returns the stock

  for v_line in select * from jsonb_array_elements(p_lines)
  loop
    v_item_id := nullif(v_line->>'item_id', '')::uuid;
    if v_item_id is null then
      if coalesce(trim(v_line->>'new_name'), '') = '' then
        raise exception 'اسم الصنف الجديد مطلوب';
      end if;
      insert into items (name_ar, unit_type, sale_price, low_stock_threshold, active)
      values (trim(v_line->>'new_name'), (v_line->>'new_unit')::unit_type, 0, 0, true)
      returning id into v_item_id;
    end if;

    insert into po_lines (po_id, item_id, site_id, qty, unit_price, created_by)
    values (
      p_id, v_item_id, (v_line->>'site_id')::uuid,
      (v_line->>'qty')::numeric, (v_line->>'unit_price')::numeric, v_uid
    );
  end loop;
end;
$$;

revoke execute on function create_itemized_po(uuid, date, text, jsonb) from public, anon;
revoke execute on function update_itemized_po(uuid, uuid, date, text, jsonb) from public, anon;
grant  execute on function create_itemized_po(uuid, date, text, jsonb) to authenticated;
grant  execute on function update_itemized_po(uuid, uuid, date, text, jsonb) to authenticated;
