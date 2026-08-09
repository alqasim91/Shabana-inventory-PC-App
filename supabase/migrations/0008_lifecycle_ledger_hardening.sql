-- ============================================================================
-- 0008_lifecycle_ledger_hardening.sql — fixes from the post-Phase-7 code review
-- ----------------------------------------------------------------------------
-- Five confirmed bugs, all reproduced against the live DB before fixing:
--
--  A. Auto-close fired from status 'invoiced' too: a client paying in full
--     right after الفوترة closed the order WITHOUT ever deducting stock
--     (placement skipped, no UI action left on a closed order).
--  B. Deleting a cash payment left its cash_movement behind (append-only), so
--     the drawer permanently kept money that officially was never received.
--     A closed order also stayed closed with remaining > 0.
--  C. Payments were accepted on DRAFT sales orders. Draft payments appear in
--     neither the contact balance nor the statement (both exclude drafts),
--     and a fully-paid draft could never auto-close later (the close check
--     only runs on payment insert).
--  D. so_status_transition guarded nothing except draft→invoiced: any manager
--     credential could jump draft→placed directly (stock deducted with a NULL
--     invoice number), re-draft an invoiced order, or reopen a closed one.
--  E. Ledger guards were asymmetric: sales/transfers/reversals check stock,
--     but a manual تسوية could drive stock to -99,725 (verified), a multi-line
--     SO with the same item twice could cumulatively overdraw stock (each line
--     was validated against the same starting balance), and a cash payment
--     could overdraw a physical drawer.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Whitelist sales-order lifecycle transitions (fixes D).
--    Legal:  draft→invoiced (assigns number, needs lines)
--            invoiced→placed (deducts stock)  ·  placed→invoiced (admin cancel)
--            placed→closed (auto-close on full collection)
--            closed→placed (system reopen when a payment is deleted, fix B)
--    Everything else is rejected with an Arabic message.
--    SECURITY DEFINER + pinned search_path kept from 0006 (nested
--    next_doc_number call needs the definer's EXECUTE rights).
-- ---------------------------------------------------------------------------
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

  if not (
       (old.status = 'draft'    and new.status = 'invoiced')
    or (old.status = 'invoiced' and new.status = 'placed')
    or (old.status = 'placed'   and new.status = 'invoiced')
    or (old.status = 'placed'   and new.status = 'closed')
    or (old.status = 'closed'   and new.status = 'placed')
  ) then
    raise exception 'انتقال حالة غير مسموح به لأمر البيع (% ← %)', old.status, new.status;
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

-- ---------------------------------------------------------------------------
-- 2. Block payments on draft sales orders (fixes C).
-- ---------------------------------------------------------------------------
create or replace function payment_validate()
returns trigger language plpgsql set search_path = public as $$
declare parent_total numeric; already_paid numeric; so_status_val so_status;
begin
  if new.parent_type = 'so' then
    select total_amount, status into parent_total, so_status_val
      from sales_orders where id = new.parent_id;
    if parent_total is null then
      raise exception 'أمر البيع غير موجود';
    end if;
    if so_status_val = 'draft' then
      raise exception 'لا يمكن تسجيل دفعة على مسودة أمر بيع — قم بالفوترة أولاً';
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

-- ---------------------------------------------------------------------------
-- 3. Auto-close only from 'placed' (fixes A) — a fully-paid invoiced order now
--    stays invoiced until it is actually placed.
-- ---------------------------------------------------------------------------
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
    -- auto-close on full collection (rule 6) — only once stock has been placed
    if parent_total > 0 and total_paid >= parent_total - 0.005 then
      update sales_orders set status = 'closed'
        where id = new.parent_id and status = 'placed';
    end if;
  end if;

  return new;
end;
$$;

-- ---------------------------------------------------------------------------
-- 4. Placement auto-closes an already-fully-collected order (companion to 3:
--    invoice → collect in full → place should land on 'closed', not strand
--    the order at 'placed' waiting for a payment that will never come).
-- ---------------------------------------------------------------------------
create or replace function so_placement_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare ln record; avail numeric; total_paid numeric;
begin
  if new.status = old.status then
    return new;
  end if;

  -- تنفيذ: place the order → sell stock out
  if new.status = 'placed' and old.status = 'invoiced' then
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

    -- already fully collected while invoiced → close now (recursive update is
    -- safe: placed→closed passes the whitelist and matches no branch here).
    select coalesce(sum(amount),0) into total_paid
      from payments where parent_type = 'so' and parent_id = new.id;
    if new.total_amount > 0 and total_paid >= new.total_amount - 0.005 then
      update sales_orders set status = 'closed' where id = new.id and status = 'placed';
    end if;

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

-- ---------------------------------------------------------------------------
-- 5. Deleting a payment now reverses its side effects (fixes B):
--    cash → compensating cash_movement (drawer gives the money back);
--    a closed SO that is no longer fully collected reopens to 'placed'.
-- ---------------------------------------------------------------------------
create or replace function payment_delete_apply()
returns trigger
language plpgsql
security definer set search_path = public
as $$
declare parent_total numeric; total_paid numeric; so_status_val so_status;
begin
  if old.method = 'cash' and old.site_id is not null then
    insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
    values (
      old.site_id,
      case when old.parent_type = 'so' then -old.amount else old.amount end,
      'payment', old.id,
      case when old.parent_type = 'so' then 'إلغاء تحصيل نقدي (حذف دفعة)'
           else 'إلغاء سداد نقدي (حذف دفعة)' end,
      auth.uid()
    );
  end if;

  if old.parent_type = 'so' then
    select total_amount, status into parent_total, so_status_val
      from sales_orders where id = old.parent_id;
    if so_status_val = 'closed' then
      select coalesce(sum(amount),0) into total_paid
        from payments where parent_type = 'so' and parent_id = old.parent_id;
      if parent_total > 0 and total_paid < parent_total - 0.005 then
        update sales_orders set status = 'placed' where id = old.parent_id;
      end if;
    end if;
  end if;

  return old;
end;
$$;
create trigger trg_payment_delete_apply
  after delete on payments
  for each row execute function payment_delete_apply();

-- trigger-only, like the other SECURITY DEFINER internals (0006)
revoke execute on function payment_delete_apply() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 6. Uniform ledger floor guards (fixes E). Every negative insert is checked
--    against the CURRENT balance, which also closes the cumulative multi-line
--    overdraw hole (rows inserted earlier in the same statement are visible
--    to the row-level trigger of later rows).
--    The existing pre-checks in transfer/placement/reversal stay as the
--    user-friendly first line; these guards are the invariant of last resort.
-- ---------------------------------------------------------------------------
create or replace function stock_movement_floor_guard()
returns trigger language plpgsql set search_path = public as $$
declare bal numeric;
begin
  if new.qty_delta < 0 then
    bal := get_stock(new.site_id, new.item_id, null);
    if bal + new.qty_delta < -0.0005 then
      raise exception 'لا يمكن تنفيذ الحركة: الرصيد الحالي للصنف في الفرع (%) لا يكفي', bal;
    end if;
  end if;
  return new;
end;
$$;
create trigger trg_stock_movements_floor
  before insert on stock_movements
  for each row execute function stock_movement_floor_guard();

create or replace function cash_movement_floor_guard()
returns trigger language plpgsql set search_path = public as $$
declare bal numeric;
begin
  if new.amount_delta < 0 then
    bal := get_cash_balance(new.site_id, null);
    if bal + new.amount_delta < -0.005 then
      raise exception 'رصيد الخزينة في الفرع (%) لا يكفي لهذه الحركة', bal;
    end if;
  end if;
  return new;
end;
$$;
create trigger trg_cash_movements_floor
  before insert on cash_movements
  for each row execute function cash_movement_floor_guard();
