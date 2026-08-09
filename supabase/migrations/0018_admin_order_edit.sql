-- ============================================================================
-- 0018_admin_order_edit.sql — admin can fully edit an order at any status
-- ----------------------------------------------------------------------------
-- Business ask: an admin must be able to correct a sales/purchase order after
-- it is invoiced, placed, or even closed — including its line items — without
-- desyncing stock or cash. We do this SAFELY, entirely server-side, in one
-- transaction per edit:
--
--   * A transaction-local flag `app.editing = 'on'` (set only by the two admin
--     RPCs below) tells the existing guard triggers to stand down for that one
--     controlled edit. Every other write path keeps its guards intact.
--   * The SO editor rewinds the order to draft — RETURNING any stock it had
--     deducted via the normal placement-reversal trigger — rewrites the header
--     and lines, revalidates payments against the new total, then replays the
--     lifecycle back to where it was, RE-DEDUCTING stock (and failing loudly if
--     the branch no longer has enough). Net stock effect = old lines out, new
--     lines in. Cash is untouched (payments are not changed here).
--   * The PO editor validates total_kg >= already-converted kg and
--     total_amount >= already-paid, then updates in place and recomputes the
--     open/fully_converted flag.
--
-- Anything invalid (insufficient stock, payments over the new total) raises an
-- Arabic error and the whole edit rolls back — the order is left exactly as it
-- was. Both RPCs are admin-only (is_admin(), enforced server-side).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Teach the guards to yield to a controlled admin edit.
-- ---------------------------------------------------------------------------

-- SO lines: normally frozen once the order leaves draft.
create or replace function so_line_guard()
returns trigger language plpgsql
set search_path = public as $$
declare so_status_val so_status;
begin
  if coalesce(current_setting('app.editing', true), '') = 'on' then
    return coalesce(new, old);
  end if;
  select status into so_status_val from sales_orders
    where id = coalesce(new.so_id, old.so_id);
  if so_status_val <> 'draft' then
    raise exception 'لا يمكن تعديل بنود أمر بيع بعد فوترته';
  end if;
  return coalesce(new, old);
end;
$$;

-- SO lifecycle: normally a strict whitelist. Under an admin edit, any rewind is
-- allowed; the invoice-number assignment on draft→invoiced still runs.
create or replace function so_status_transition()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare y int := extract(year from coalesce(new.order_date, current_date));
begin
  if new.status = old.status then
    return new;
  end if;

  if coalesce(current_setting('app.editing', true), '') <> 'on' then
    if not (
         (old.status = 'draft'    and new.status = 'invoiced')
      or (old.status = 'invoiced' and new.status = 'placed')
      or (old.status = 'placed'   and new.status = 'invoiced')
      or (old.status = 'placed'   and new.status = 'closed')
      or (old.status = 'closed'   and new.status = 'placed')
    ) then
      raise exception 'انتقال حالة غير مسموح به لأمر البيع (% ← %)', old.status, new.status;
    end if;
  end if;

  if old.status = 'draft' and new.status = 'invoiced' then
    if not exists (select 1 from sales_order_lines where so_id = new.id) then
      raise exception 'لا يمكن فوترة أمر بيع بدون بنود';
    end if;
    if new.invoice_number is null then
      new.invoice_number := 'SO-' || y || '-' ||
        lpad(next_doc_number('so', y)::text, 4, '0');
    end if;
  end if;

  return new;
end;
$$;

-- PO total_kg: normally frozen once conversions exist.
create or replace function po_guard_total_kg()
returns trigger language plpgsql
set search_path = public as $$
begin
  if coalesce(current_setting('app.editing', true), '') <> 'on'
     and new.total_kg is distinct from old.total_kg
     and exists (select 1 from po_conversions where po_id = old.id) then
    raise exception 'لا يمكن تعديل إجمالي الكيلوهات بعد بدء التحويل إلى المخزون';
  end if;
  return new;
end;
$$;

-- ---------------------------------------------------------------------------
-- 2. Admin SO editor — rewind → rewrite → replay, keeping stock exact.
-- ---------------------------------------------------------------------------
create or replace function admin_update_sales_order(
  p_id uuid,
  p_site uuid,
  p_client uuid,
  p_date date,
  p_lines jsonb
) returns void
language plpgsql
security definer set search_path = public
as $$
declare
  v_old_status so_status;
  v_total numeric;
  v_paid  numeric;
begin
  if not is_admin() then
    raise exception 'هذا الإجراء متاح لمدير النظام فقط';
  end if;
  if jsonb_typeof(p_lines) is distinct from 'array' or jsonb_array_length(p_lines) = 0 then
    raise exception 'يجب إدخال بند واحد على الأقل';
  end if;

  select status into v_old_status from sales_orders where id = p_id for update;
  if v_old_status is null then
    raise exception 'أمر البيع غير موجود';
  end if;

  perform set_config('app.editing', 'on', true);

  -- Give stock back (placed→invoiced writes the compensating movements) and
  -- rewind to draft so the lines can be rewritten.
  if v_old_status in ('placed', 'closed') then
    update sales_orders set status = 'invoiced' where id = p_id;
  end if;
  if v_old_status <> 'draft' then
    update sales_orders set status = 'draft' where id = p_id;
  end if;

  update sales_orders
    set site_id = p_site, client_id = p_client, order_date = p_date
    where id = p_id;

  delete from sales_order_lines where so_id = p_id;
  insert into sales_order_lines (so_id, item_id, qty, unit_price)
  select p_id, (e->>'item_id')::uuid, (e->>'qty')::numeric, (e->>'unit_price')::numeric
  from jsonb_array_elements(p_lines) e;

  -- trg_so_recompute_total has refreshed total_amount; payments must still fit.
  select total_amount into v_total from sales_orders where id = p_id;
  select coalesce(sum(amount), 0) into v_paid
    from payments where parent_type = 'so' and parent_id = p_id;
  if v_paid > v_total + 0.005 then
    raise exception 'المدفوع (%) يتجاوز إجمالي الأمر بعد التعديل (%)', v_paid, v_total;
  end if;

  -- Replay the lifecycle back to where it started, re-deducting stock.
  if v_old_status <> 'draft' then
    update sales_orders set status = 'invoiced' where id = p_id;
    if v_old_status in ('placed', 'closed') then
      update sales_orders set status = 'placed' where id = p_id;  -- may raise: الكمية غير كافية
      -- placement auto-closes a fully-collected order; close explicitly otherwise-safe.
      if v_old_status = 'closed' and v_total > 0 and v_paid >= v_total - 0.005 then
        update sales_orders set status = 'closed' where id = p_id and status = 'placed';
      end if;
    end if;
  end if;
end;
$$;

-- ---------------------------------------------------------------------------
-- 3. Admin PO editor — validate against conversions/payments, update in place.
-- ---------------------------------------------------------------------------
create or replace function admin_update_purchase_order(
  p_id uuid,
  p_vendor uuid,
  p_product text,
  p_notes text,
  p_date date,
  p_total_kg numeric,
  p_ppk numeric,
  p_total numeric
) returns void
language plpgsql
security definer set search_path = public
as $$
declare
  v_converted numeric;
  v_paid numeric;
begin
  if not is_admin() then
    raise exception 'هذا الإجراء متاح لمدير النظام فقط';
  end if;
  if not (p_total_kg > 0) then
    raise exception 'إجمالي الكيلوهات يجب أن يكون أكبر من صفر';
  end if;

  select coalesce(sum(kg_consumed), 0) into v_converted
    from po_conversions where po_id = p_id;
  if p_total_kg < v_converted - 0.0005 then
    raise exception 'إجمالي الكيلوهات (%) أقل من المحوّل بالفعل (%)', p_total_kg, v_converted;
  end if;

  select coalesce(sum(amount), 0) into v_paid
    from payments where parent_type = 'po' and parent_id = p_id;
  if p_total < v_paid - 0.005 then
    raise exception 'الإجمالي الجديد (%) أقل من المدفوع بالفعل (%)', p_total, v_paid;
  end if;

  perform set_config('app.editing', 'on', true);

  update purchase_orders
    set vendor_id    = p_vendor,
        product_name = p_product,
        notes        = p_notes,
        order_date   = p_date,
        total_kg     = p_total_kg,
        price_per_kg = p_ppk,
        total_amount = p_total,
        status = case
                   when v_converted >= p_total_kg - 0.0005 then 'fully_converted'::po_status
                   else 'open'::po_status
                 end
    where id = p_id;
end;
$$;

revoke execute on function admin_update_sales_order(uuid, uuid, uuid, date, jsonb)      from public, anon;
revoke execute on function admin_update_purchase_order(uuid, uuid, text, text, date, numeric, numeric, numeric) from public, anon;
grant  execute on function admin_update_sales_order(uuid, uuid, uuid, date, jsonb)      to authenticated;
grant  execute on function admin_update_purchase_order(uuid, uuid, text, text, date, numeric, numeric, numeric) to authenticated;
