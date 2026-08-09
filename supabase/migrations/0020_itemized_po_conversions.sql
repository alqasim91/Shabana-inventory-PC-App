-- ============================================================================
-- 0020_itemized_po_conversions.sql — itemized PO: convert-per-site, editable total
-- ----------------------------------------------------------------------------
-- Revises the itemized PO (0019) to match how the weight PO already works and
-- the core rule ("POs are site-agnostic at creation; conversion targets a site"):
--
--   * An itemized line no longer carries a destination branch and no longer
--     lands stock on save. A line is just (item, qty, unit_price, line_total).
--   * Stock lands through a CONVERSION step, splittable across branches: each
--     `po_line_conversions` row receives some quantity of a line's item into a
--     chosen site, tracked against the line's remaining. Mirrors po_conversions.
--   * line_total is now a plain stored column so the UI can drive it from the
--     total (which back-computes unit_price) — same 3-field freedom as the
--     weight PO's total_kg / price_per_kg / total_amount.
--
-- Safe to restructure: there are no itemized orders / po_lines in production.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Undo 0019's land-on-save behaviour, non-destructively (nothing dropped —
--    this runs against a live DB; unused bits are kept rather than removed).
-- ---------------------------------------------------------------------------
-- Itemized lines are now site-agnostic: the per-line destination site is unused.
-- Keep the column but allow NULL (new lines won't set it).
alter table po_lines alter column site_id drop not null;

-- line_total: generated → plain stored, in place, so the UI can drive it from
-- the total (which back-computes unit_price) — same freedom as the weight PO.
alter table po_lines alter column line_total drop expression;

-- Neutralize the land-on-save triggers (kept as no-ops rather than dropped):
-- stock now lands only through po_line_conversions below.
create or replace function po_line_apply()
returns trigger language plpgsql as $$
begin return new; end;
$$;
create or replace function po_line_reverse()
returns trigger language plpgsql as $$
begin return old; end;
$$;

-- ---------------------------------------------------------------------------
-- 2. po_line_conversions — receive a line's item into a branch (splittable).
-- ---------------------------------------------------------------------------
create table po_line_conversions (
  id              uuid          primary key default gen_random_uuid(),
  po_line_id      uuid          not null references po_lines(id) on delete cascade,
  site_id         uuid          not null references sites(id),
  qty             numeric(12,3) not null check (qty > 0),
  conversion_date date          not null default current_date,
  created_by      uuid          references auth.users(id),
  created_at      timestamptz   not null default now()
);
create index plc_line_idx on po_line_conversions(po_line_id);
create index plc_site_idx on po_line_conversions(site_id);

alter table po_line_conversions enable row level security;
create policy plc_read   on po_line_conversions for select to authenticated using (true);
create policy plc_write  on po_line_conversions for insert to authenticated with check (is_manager_or_admin());
create policy plc_delete on po_line_conversions for delete to authenticated using (is_admin());

-- Units still to receive across all of an itemized PO's lines.
create or replace function po_itemized_remaining(p_po_id uuid)
returns numeric language sql stable set search_path = public as $$
  select coalesce(sum(pl.qty - coalesce(c.done, 0)), 0)
  from po_lines pl
  left join (
    select po_line_id, sum(qty) as done from po_line_conversions group by po_line_id
  ) c on c.po_line_id = pl.id
  where pl.po_id = p_po_id;
$$;

-- Reject converting more than a line's remaining quantity.
create or replace function po_line_conversion_check()
returns trigger language plpgsql set search_path = public as $$
declare v_ordered numeric; v_done numeric;
begin
  select qty into v_ordered from po_lines where id = new.po_line_id;
  if v_ordered is null then
    raise exception 'بند الأمر غير موجود';
  end if;
  select coalesce(sum(qty), 0) into v_done from po_line_conversions
    where po_line_id = new.po_line_id and id <> new.id;
  if new.qty > v_ordered - v_done + 0.0005 then
    raise exception 'الكمية المحوّلة (%) تتجاوز المتبقي من البند (%)', new.qty, v_ordered - v_done;
  end if;
  return new;
end;
$$;
create trigger trg_plc_check
  before insert on po_line_conversions
  for each row execute function po_line_conversion_check();

-- Apply: positive stock movement at the chosen site for the line's item;
-- flip the PO to fully_converted when nothing remains across its lines.
create or replace function po_line_conversion_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare v_item uuid; v_po uuid;
begin
  select pl.item_id, pl.po_id into v_item, v_po from po_lines pl where pl.id = new.po_line_id;
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.site_id, v_item, new.qty, 'po_conversion', new.id, new.created_by,
          'استلام من ' || (select order_code from purchase_orders where id = v_po));

  if po_itemized_remaining(v_po) <= 0.0005 then
    update purchase_orders set status = 'fully_converted' where id = v_po and status = 'open';
  end if;
  return new;
end;
$$;
create trigger trg_plc_apply
  after insert on po_line_conversions
  for each row execute function po_line_conversion_apply();

-- Reverse (admin deletes a conversion, or edit cascades): guarded negative
-- movement; re-open the PO if it had been auto-closed.
create or replace function po_line_conversion_reverse()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare v_item uuid; v_po uuid;
begin
  select pl.item_id, pl.po_id into v_item, v_po from po_lines pl where pl.id = old.po_line_id;
  if get_stock(old.site_id, v_item, null) - old.qty < -0.0005 then
    raise exception 'لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي';
  end if;
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (old.site_id, v_item, -old.qty, 'adjustment', old.id, auth.uid(), 'إلغاء تحويل بند مصنّف');

  update purchase_orders set status = 'open' where id = v_po and status = 'fully_converted';
  return old;
end;
$$;
create trigger trg_plc_reverse
  before delete on po_line_conversions
  for each row execute function po_line_conversion_reverse();

-- ---------------------------------------------------------------------------
-- 3. Rewrite the create/update RPCs: no per-line site, carry line_total, no
--    stock on save.  p_lines: [{ item_id?, new_name?, new_unit?, qty,
--    unit_price, line_total }]
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

    insert into po_lines (po_id, item_id, qty, unit_price, line_total, created_by)
    values (
      v_po_id, v_item_id,
      (v_line->>'qty')::numeric, (v_line->>'unit_price')::numeric, (v_line->>'line_total')::numeric, v_uid
    );
  end loop;

  return v_po_id;
end;
$$;

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
    set vendor_id = p_vendor, order_date = coalesce(p_date, order_date), notes = p_notes,
        status = 'open'
    where id = p_id;

  -- Rewriting lines cascades away their conversions (returning that stock,
  -- guarded); the admin re-converts afterwards.
  delete from po_lines where po_id = p_id;

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

    insert into po_lines (po_id, item_id, qty, unit_price, line_total, created_by)
    values (
      p_id, v_item_id,
      (v_line->>'qty')::numeric, (v_line->>'unit_price')::numeric, (v_line->>'line_total')::numeric, v_uid
    );
  end loop;
end;
$$;

revoke execute on function create_itemized_po(uuid, date, text, jsonb) from public, anon;
revoke execute on function update_itemized_po(uuid, uuid, date, text, jsonb) from public, anon;
grant  execute on function create_itemized_po(uuid, date, text, jsonb) to authenticated;
grant  execute on function update_itemized_po(uuid, uuid, date, text, jsonb) to authenticated;
