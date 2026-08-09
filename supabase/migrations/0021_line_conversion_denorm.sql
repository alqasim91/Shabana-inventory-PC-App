-- ============================================================================
-- 0021_line_conversion_denorm.sql — make itemized conversion reversal cascade-safe
-- ----------------------------------------------------------------------------
-- po_line_conversions.reverse needs the line's item to give stock back, and the
-- PO to re-open its status. It used to read those by joining po_lines — but on a
-- CASCADE delete (deleting a whole PO, or update_itemized_po rewriting the lines)
-- the parent po_lines row is already gone when the child's BEFORE-DELETE trigger
-- runs, so the join returned NULL and the reversal wrongly raised "stock
-- insufficient". Denormalize item_id + po_id onto the conversion row so the
-- reverse (and apply) never depend on the parent still existing.
--
-- Additive: adds two nullable columns and replaces three trigger functions.
-- ============================================================================

alter table po_line_conversions add column item_id uuid references items(id);
alter table po_line_conversions add column po_id   uuid; -- denormalized; no FK so it never blocks a PO delete

-- Backfill any existing rows (none in prod yet, but correct regardless).
update po_line_conversions plc
  set item_id = pl.item_id, po_id = pl.po_id
  from po_lines pl
  where pl.id = plc.po_line_id;

-- BEFORE INSERT: stamp the denormalized fields from the (still-present) parent,
-- and keep the remaining-quantity guard.
create or replace function po_line_conversion_check()
returns trigger language plpgsql set search_path = public as $$
declare v_ordered numeric; v_done numeric; v_item uuid; v_po uuid;
begin
  select qty, item_id, po_id into v_ordered, v_item, v_po from po_lines where id = new.po_line_id;
  if v_ordered is null then
    raise exception 'بند الأمر غير موجود';
  end if;
  new.item_id := v_item;
  new.po_id := v_po;
  select coalesce(sum(qty), 0) into v_done from po_line_conversions
    where po_line_id = new.po_line_id and id <> new.id;
  if new.qty > v_ordered - v_done + 0.0005 then
    raise exception 'الكمية المحوّلة (%) تتجاوز المتبقي من البند (%)', new.qty, v_ordered - v_done;
  end if;
  return new;
end;
$$;

-- APPLY: use the denormalized item_id/po_id (no join to po_lines).
create or replace function po_line_conversion_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.site_id, new.item_id, new.qty, 'po_conversion', new.id, new.created_by,
          'استلام من ' || (select order_code from purchase_orders where id = new.po_id));

  if po_itemized_remaining(new.po_id) <= 0.0005 then
    update purchase_orders set status = 'fully_converted' where id = new.po_id and status = 'open';
  end if;
  return new;
end;
$$;

-- REVERSE: use the denormalized fields so it works even when the parent po_lines
-- (and PO) are being cascade-deleted in the same statement.
create or replace function po_line_conversion_reverse()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  if get_stock(old.site_id, old.item_id, null) - old.qty < -0.0005 then
    raise exception 'لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي';
  end if;
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (old.site_id, old.item_id, -old.qty, 'adjustment', old.id, auth.uid(), 'إلغاء تحويل بند مصنّف');

  update purchase_orders set status = 'open' where id = old.po_id and status = 'fully_converted';
  return old;
end;
$$;
