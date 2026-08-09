-- ============================================================================
-- 0004_triggers.sql — Business rules as triggers & constraints
-- ============================================================================
-- These enforce the invariants that the app must NEVER be able to violate,
-- regardless of which client writes. Arabic error messages surface directly to
-- the UI. Trigger functions that write to the append-only ledgers are
-- SECURITY DEFINER so they bypass the ledgers' insert RLS (the ledgers are
-- meant to be written only through these controlled paths).
-- ============================================================================

-- ===========================================================================
-- PURCHASE ORDERS
-- ===========================================================================

-- Assign PO-YYYY-#### on insert.
create or replace function po_assign_code()
returns trigger language plpgsql as $$
declare y int := extract(year from coalesce(new.order_date, current_date));
begin
  if new.order_code is null then
    new.order_code := 'PO-' || y || '-' ||
      lpad(next_doc_number('po', y)::text, 4, '0');
  end if;
  return new;
end;
$$;
create trigger trg_po_assign_code
  before insert on purchase_orders
  for each row execute function po_assign_code();

-- Block editing total_kg once any conversion exists (rule: convertible weight is
-- frozen after conversions start).
create or replace function po_guard_total_kg()
returns trigger language plpgsql as $$
begin
  if new.total_kg is distinct from old.total_kg
     and exists (select 1 from po_conversions where po_id = old.id) then
    raise exception 'لا يمكن تعديل إجمالي الكيلوهات بعد بدء التحويل إلى المخزون';
  end if;
  return new;
end;
$$;
create trigger trg_po_guard_total_kg
  before update on purchase_orders
  for each row execute function po_guard_total_kg();

-- ===========================================================================
-- PO CONVERSIONS  →  stock in  (rule 2)
-- ===========================================================================

-- Reject a conversion that would consume more KG than the PO has left.
create or replace function po_conversion_check()
returns trigger language plpgsql as $$
declare remaining numeric;
begin
  select po.total_kg - coalesce((
           select sum(kg_consumed) from po_conversions
           where po_id = new.po_id and id <> new.id
         ), 0)
    into remaining
  from purchase_orders po where po.id = new.po_id;

  if new.kg_consumed > remaining + 0.0005 then
    raise exception 'الكمية المستهلكة (% كجم) تتجاوز المتبقي من أمر الشراء (% كجم)',
      new.kg_consumed, remaining;
  end if;
  return new;
end;
$$;
create trigger trg_po_conversion_check
  before insert on po_conversions
  for each row execute function po_conversion_check();

-- After a conversion: write the positive stock movement and, if the PO is now
-- fully consumed, flip its status to fully_converted.
create or replace function po_conversion_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.site_id, new.item_id, new.output_qty, 'po_conversion', new.id, new.created_by,
          'تحويل من ' || (select order_code from purchase_orders where id = new.po_id));

  if po_remaining_kg(new.po_id) <= 0.0005 then
    update purchase_orders set status = 'fully_converted'
      where id = new.po_id and status = 'open';
  end if;
  return new;
end;
$$;
create trigger trg_po_conversion_apply
  after insert on po_conversions
  for each row execute function po_conversion_apply();

-- Reversal (admin deletes a conversion): only allowed if the destination site
-- still has enough of that item; writes a compensating negative movement.
create or replace function po_conversion_reverse()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  if get_stock(old.site_id, old.item_id, null) - old.output_qty < -0.0005 then
    raise exception 'لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي';
  end if;

  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (old.site_id, old.item_id, -old.output_qty, 'adjustment', old.id, auth.uid(),
          'إلغاء تحويل من أمر الشراء');

  -- Re-open the PO if it was auto-closed by this conversion.
  update purchase_orders set status = 'open'
    where id = old.po_id and status = 'fully_converted';
  return old;
end;
$$;
create trigger trg_po_conversion_reverse
  before delete on po_conversions
  for each row execute function po_conversion_reverse();

-- ===========================================================================
-- SALES ORDER LINES  →  keep total_amount in sync; lock after draft
-- ===========================================================================

-- Lines may only change while the order is a draft (rule 6: فوترة locks lines).
create or replace function so_line_guard()
returns trigger language plpgsql as $$
declare so_status_val so_status;
begin
  select status into so_status_val from sales_orders
    where id = coalesce(new.so_id, old.so_id);
  if so_status_val <> 'draft' then
    raise exception 'لا يمكن تعديل بنود أمر بيع بعد فوترته';
  end if;
  return coalesce(new, old);
end;
$$;
create trigger trg_so_line_guard
  before insert or update or delete on sales_order_lines
  for each row execute function so_line_guard();

-- Recompute the order total from its lines after any line change.
create or replace function so_recompute_total()
returns trigger language plpgsql as $$
declare v_so uuid := coalesce(new.so_id, old.so_id);
begin
  update sales_orders
    set total_amount = coalesce((
      select sum(line_total) from sales_order_lines where so_id = v_so
    ), 0)
  where id = v_so;
  return null;
end;
$$;
create trigger trg_so_recompute_total
  after insert or update or delete on sales_order_lines
  for each row execute function so_recompute_total();

-- ===========================================================================
-- SALES ORDER lifecycle  (rule 6)
-- ===========================================================================

-- On draft → invoiced: assign SO-YYYY-####. Guard illegal transitions.
create or replace function so_status_transition()
returns trigger language plpgsql as $$
declare y int := extract(year from coalesce(new.order_date, current_date));
begin
  if new.status = old.status then
    return new;
  end if;

  -- invoicing: freeze number, require at least one line
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
create trigger trg_so_status_transition
  before update on sales_orders
  for each row execute function so_status_transition();

-- On invoiced → placed: deduct stock at the SO's site (reject if insufficient).
-- On placed → invoiced (admin cancel): write compensating positive movements.
create or replace function so_placement_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare ln record; avail numeric;
begin
  if new.status = old.status then
    return new;
  end if;

  -- تنفيذ: place the order → sell stock out
  if new.status = 'placed' and old.status <> 'placed' then
    for ln in select * from sales_order_lines where so_id = new.id loop
      avail := get_stock(new.site_id, ln.item_id, null);
      if avail < ln.qty - 0.0005 then
        raise exception 'الكمية غير كافية في المخزون للصنف المطلوب (المتاح % / المطلوب %)',
          avail, ln.qty;
      end if;
    end loop;
    -- all lines validated → write the movements
    for ln in select * from sales_order_lines where so_id = new.id loop
      insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
      values (new.site_id, ln.item_id, -ln.qty, 'sale', ln.id, new.created_by,
              'بيع ' || coalesce(new.invoice_number, ''));
    end loop;

  -- إلغاء التنفيذ (admin): give the stock back
  elsif old.status = 'placed' and new.status = 'invoiced' then
    for ln in select * from sales_order_lines where so_id = new.id loop
      insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
      values (new.site_id, ln.item_id, ln.qty, 'adjustment', ln.id, auth.uid(),
              'إلغاء تنفيذ ' || coalesce(new.invoice_number, ''));
    end loop;
  end if;

  return new;
end;
$$;
create trigger trg_so_placement_apply
  after update on sales_orders
  for each row execute function so_placement_apply();

-- ===========================================================================
-- PAYMENTS  (rule 4 & 5)
-- ===========================================================================

-- Before insert: default the drawer site, require it for cash, block overpayment.
create or replace function payment_validate()
returns trigger language plpgsql as $$
declare parent_total numeric; already_paid numeric;
begin
  if new.parent_type = 'so' then
    select total_amount into parent_total from sales_orders where id = new.parent_id;
    if parent_total is null then
      raise exception 'أمر البيع غير موجود';
    end if;
    -- SO cash payment defaults to the SO's own site drawer.
    if new.site_id is null then
      select site_id into new.site_id from sales_orders where id = new.parent_id;
    end if;
  else -- po
    select total_amount into parent_total from purchase_orders where id = new.parent_id;
    if parent_total is null then
      raise exception 'أمر الشراء غير موجود';
    end if;
  end if;

  -- A cash payment MUST name the drawer it touches.
  if new.method = 'cash' and new.site_id is null then
    raise exception 'يجب تحديد الفرع (الخزينة) للدفعة النقدية';
  end if;

  select coalesce(sum(amount), 0) into already_paid
  from payments
  where parent_type = new.parent_type and parent_id = new.parent_id
    and id <> new.id;

  if already_paid + new.amount > parent_total + 0.005 then
    raise exception 'المبلغ يتجاوز المتبقي على الأمر (الإجمالي % / المدفوع مسبقًا %)',
      parent_total, already_paid;
  end if;

  return new;
end;
$$;
create trigger trg_payment_validate
  before insert on payments
  for each row execute function payment_validate();

-- After insert: cash payment hits the drawer (+ for a sale, − for a purchase);
-- and a fully-collected sales order auto-closes.
create or replace function payment_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare parent_total numeric; total_paid numeric;
begin
  if new.method = 'cash' then
    insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
    values (
      new.site_id,
      case when new.parent_type = 'so' then new.amount else -new.amount end,
      'payment', new.id,
      case when new.parent_type = 'so' then 'تحصيل نقدي من بيع'
           else 'سداد نقدي لأمر شراء' end,
      new.created_by
    );
  end if;

  if new.parent_type = 'so' then
    select total_amount into parent_total from sales_orders where id = new.parent_id;
    select coalesce(sum(amount),0) into total_paid
      from payments where parent_type = 'so' and parent_id = new.parent_id;
    -- auto-close on full collection (rule 6)
    if parent_total > 0 and total_paid >= parent_total - 0.005 then
      update sales_orders set status = 'closed'
        where id = new.parent_id and status in ('invoiced','placed');
    end if;
  end if;

  return new;
end;
$$;
create trigger trg_payment_apply
  after insert on payments
  for each row execute function payment_apply();

-- ===========================================================================
-- STOCK TRANSFERS  →  two movements  (rule 3, inventory)
-- ===========================================================================

create or replace function transfer_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare avail numeric;
begin
  avail := get_stock(new.from_site, new.item_id, null);
  if avail < new.qty - 0.0005 then
    raise exception 'الكمية غير كافية في الفرع المصدر للنقل (المتاح % / المطلوب %)',
      avail, new.qty;
  end if;

  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.from_site, new.item_id, -new.qty, 'transfer', new.id, new.created_by, 'نقل صادر');
  insert into stock_movements(site_id, item_id, qty_delta, source_type, source_id, created_by, note)
  values (new.to_site, new.item_id, new.qty, 'transfer', new.id, new.created_by, 'نقل وارد');
  return new;
end;
$$;
create trigger trg_transfer_apply
  after insert on stock_transfers
  for each row execute function transfer_apply();

-- ===========================================================================
-- APPEND-ONLY guards — ledgers can be inserted, never updated or deleted.
-- ===========================================================================
create or replace function block_mutation()
returns trigger language plpgsql as $$
begin
  raise exception 'هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)';
end;
$$;
create trigger trg_stock_movements_append_only
  before update or delete on stock_movements
  for each row execute function block_mutation();
create trigger trg_cash_movements_append_only
  before update or delete on cash_movements
  for each row execute function block_mutation();
