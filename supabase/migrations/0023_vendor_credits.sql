-- 0023 — Vendor advance wallet (عربون / دفعة مقدّمة)
-- ---------------------------------------------------------------------------
-- The mirror image of client_credits (0014), for money we PREPAY a vendor:
-- a down-payment made before — or beyond — any purchase order. Created by
-- overpaying a PO or by a direct deposit; spent by applying it to a PO (a
-- non-cash 'credit' payment) or refunded back from the vendor.
--
-- Same append-only signed-ledger pattern:
--   balance = Σ(amount_delta), + = advance we hold at the vendor, - = consumed.
--
-- The cash direction is FLIPPED versus client credit: a client deposit is cash
-- IN, but prepaying a vendor is cash OUT of our drawer; a client refund is cash
-- OUT, but a vendor returning our money is cash IN. All writes go through the
-- SECURITY DEFINER RPCs below so the drawer side-effects and role gates are
-- enforced server-side and can never be half-applied.
-- ---------------------------------------------------------------------------

create table vendor_credits (
  id           uuid primary key default gen_random_uuid(),
  contact_id   uuid          not null references contacts(id) on delete restrict,
  amount_delta numeric(12,2) not null,                 -- signed: + advance added, - consumed
  source_type  text          not null check (source_type in
                 ('overpayment','deposit','applied','refund','adjustment')),
  source_id    uuid,                                    -- PO id for overpayment/applied
  method       payment_method,                          -- tender for money-out/in (cash|instapay); null for 'applied'
  site_id      uuid          references sites(id),       -- drawer touched (cash only)
  occurred_on  date          not null default current_date,
  note         text,
  created_by   uuid          default auth.uid() references auth.users(id),
  created_at   timestamptz   not null default now()
);
create index vc_contact_idx on vendor_credits(contact_id);
create index vc_occurred_idx on vendor_credits(occurred_on);

-- Running advance balance for a vendor contact.
create or replace function get_vendor_credit(p_contact_id uuid)
returns numeric
language sql stable set search_path = public
as $$
  select coalesce(sum(amount_delta), 0) from vendor_credits where contact_id = p_contact_id;
$$;

-- Append-only: no edits/deletes (corrections are compensating rows).
create trigger trg_vendor_credits_append_only
  before update or delete on vendor_credits
  for each row execute function block_mutation();

-- Balance can never go negative (defensive; the RPCs also pre-check).
create or replace function vendor_credit_guard()
returns trigger language plpgsql security definer set search_path = public as $$
begin
  if get_vendor_credit(new.contact_id) < -0.005 then
    raise exception 'لا يمكن أن يصبح رصيد الدفعة المقدّمة سالبًا';
  end if;
  return new;
end;
$$;
create trigger trg_vendor_credit_nonneg
  after insert on vendor_credits
  for each row execute function vendor_credit_guard();
-- trigger-only, like the other guard functions: not a callable RPC.
revoke execute on function vendor_credit_guard() from public, anon, authenticated;

-- RLS: everyone reads; nobody writes directly (RPCs only).
alter table vendor_credits enable row level security;
create policy vendor_credits_read on vendor_credits for select to authenticated using (true);

-- Audit every advance movement (who did what).
create trigger trg_audit_vendor_credits
  after insert on vendor_credits
  for each row execute function audit_row();

-- ---------------------------------------------------------------------------
-- get_contact_balance now also nets the vendor advance: money we hold AT a
-- vendor is an asset (they owe us goods) and pushes the balance the same way a
-- receivable does. (Reproduces the 0014 body verbatim + the vendor term.)
-- ---------------------------------------------------------------------------
create or replace function get_contact_balance(p_contact_id uuid)
returns numeric
language sql
stable set search_path = public
as $$
  with receivable as (
    select coalesce(sum(so.total_amount), 0)
         - coalesce((
             select sum(p.amount) from payments p
             where p.parent_type = 'so'
               and p.parent_id in (select id from sales_orders
                                   where client_id = p_contact_id
                                     and status <> 'draft')
           ), 0) as amt
    from sales_orders so
    where so.client_id = p_contact_id
      and so.status <> 'draft'
  ),
  payable as (
    select coalesce(sum(po.total_amount), 0)
         - coalesce((
             select sum(p.amount) from payments p
             where p.parent_type = 'po'
               and p.parent_id in (select id from purchase_orders
                                   where vendor_id = p_contact_id)
           ), 0) as amt
    from purchase_orders po
    where po.vendor_id = p_contact_id
  )
  select coalesce((select amt from receivable), 0)
       - coalesce((select amt from payable), 0)
       - get_client_credit(p_contact_id)
       + get_vendor_credit(p_contact_id);
$$;

-- ---------------------------------------------------------------------------
-- get_dashboard: "payments out today" must reflect real money out.
--   * exclude method 'credit' from PO payments (applying an advance isn't new
--     cash out — the cash left the drawer when the advance was created)
--   * add vendor advance money-out (overpayment excess + deposits) made today
-- Everything else is reproduced verbatim from 0014.
-- ---------------------------------------------------------------------------
create or replace function get_dashboard(
  p_site_id uuid,
  p_date    date default current_date
)
returns jsonb
language plpgsql
stable set search_path = public
as $$
declare
  result jsonb;
begin
  select jsonb_build_object(

    'opening_cash', get_cash_balance(p_site_id, p_date - 1),
    'closing_cash', get_cash_balance(p_site_id, p_date),

    'sales_total', coalesce((
      select sum(so.total_amount) from sales_orders so
      where so.order_date = p_date
        and so.status <> 'draft'
        and (p_site_id is null or so.site_id = p_site_id)
    ), 0),

    -- SO payments (real tenders) + credit money-in received today.
    'collections_total',
      coalesce((
        select sum(p.amount) from payments p
        join sales_orders so on so.id = p.parent_id and p.parent_type = 'so'
        where p.paid_at::date = p_date
          and p.method <> 'credit'
          and (p_site_id is null or so.site_id = p_site_id)
      ), 0)
      + coalesce((
        select sum(cc.amount_delta) from client_credits cc
        where cc.occurred_on = p_date
          and cc.amount_delta > 0
          and cc.source_type in ('overpayment','deposit')
          and (p_site_id is null or cc.site_id = p_site_id)
      ), 0),

    'collections_by_method', coalesce((
      select jsonb_object_agg(method, amt) from (
        select method, sum(amt) as amt from (
          select p.method::text as method, p.amount as amt from payments p
          join sales_orders so on so.id = p.parent_id and p.parent_type = 'so'
          where p.paid_at::date = p_date
            and p.method <> 'credit'
            and (p_site_id is null or so.site_id = p_site_id)
          union all
          select cc.method::text, cc.amount_delta from client_credits cc
          where cc.occurred_on = p_date
            and cc.amount_delta > 0
            and cc.source_type in ('overpayment','deposit')
            and cc.method is not null
            and (p_site_id is null or cc.site_id = p_site_id)
        ) u
        group by method
      ) q
    ), '{}'::jsonb),

    -- PO payments (real tenders) + vendor advance money-out made today.
    'payments_out_by_method', coalesce((
      select jsonb_object_agg(method, amt) from (
        select method, sum(amt) as amt from (
          select p.method::text as method, p.amount as amt from payments p
          where p.parent_type = 'po'
            and p.paid_at::date = p_date
            and p.method <> 'credit'
            and (p_site_id is null or p.site_id = p_site_id)
          union all
          select vc.method::text, vc.amount_delta from vendor_credits vc
          where vc.occurred_on = p_date
            and vc.amount_delta > 0
            and vc.source_type in ('overpayment','deposit')
            and vc.method is not null
            and (p_site_id is null or vc.site_id = p_site_id)
        ) u
        group by method
      ) q
    ), '{}'::jsonb),

    'low_stock', coalesce((
      select jsonb_agg(row_to_json(l)) from (
        select i.id as item_id, i.name_ar, i.unit_type,
               s.id as site_id, s.name_ar as site_name,
               get_stock(s.id, i.id, p_date) as qty,
               i.low_stock_threshold as threshold
        from items i
        cross join sites s
        where i.active and s.active
          and i.low_stock_threshold > 0
          and (p_site_id is null or s.id = p_site_id)
          and get_stock(s.id, i.id, p_date) <= i.low_stock_threshold
        order by (get_stock(s.id, i.id, p_date) / nullif(i.low_stock_threshold,0)) asc
      ) l
    ), '[]'::jsonb),

    'top_items', coalesce((
      select jsonb_agg(row_to_json(t)) from (
        select i.name_ar, sum(-sm.qty_delta) as moved
        from stock_movements sm
        join items i on i.id = sm.item_id
        where sm.source_type = 'sale'
          and sm.created_at::date = p_date
          and (p_site_id is null or sm.site_id = p_site_id)
        group by i.name_ar
        order by sum(-sm.qty_delta) desc
        limit 5
      ) t
    ), '[]'::jsonb),

    'site_drawers', coalesce((
      select jsonb_agg(row_to_json(d)) from (
        select s.id as site_id, s.name_ar as site_name,
               get_cash_balance(s.id, p_date) as closing_cash
        from sites s
        where s.active
          and (p_site_id is null or s.id = p_site_id)
        order by s.name_ar
      ) d
    ), '[]'::jsonb)

  ) into result;

  return result;
end;
$$;

-- ===========================================================================
-- RPCs — the only way to write vendor_credits. All SECURITY DEFINER.
-- ===========================================================================

-- 1) Overpay a purchase order: settle the PO to its total, bank the excess as a
--    vendor advance. Cash tender leaves the drawer in full. (staff+)
create or replace function po_overpay(
  p_po_id     uuid,
  p_amount    numeric,
  p_method    payment_method,
  p_site_id   uuid,
  p_paid_at   timestamptz,
  p_note      text,
  p_method_ref uuid
)
returns jsonb
language plpgsql security definer set search_path = public
as $$
declare
  v_total numeric; v_paid numeric; v_remaining numeric; v_excess numeric; v_vendor uuid;
begin
  if p_amount is null or p_amount <= 0 then raise exception 'المبلغ غير صالح'; end if;
  if p_method = 'credit' then raise exception 'طريقة الدفع غير صالحة'; end if;

  select total_amount, vendor_id into v_total, v_vendor from purchase_orders where id = p_po_id;
  if v_total is null then raise exception 'أمر الشراء غير موجود'; end if;
  if p_method = 'cash' and p_site_id is null then raise exception 'يجب تحديد الفرع (الخزينة)'; end if;

  select coalesce(sum(amount),0) into v_paid from payments where parent_type = 'po' and parent_id = p_po_id;
  v_remaining := greatest(v_total - v_paid, 0);
  v_excess := p_amount - v_remaining;

  -- Order payment for the remaining portion (its cash leaves the drawer via the
  -- payment trigger).
  if v_remaining > 0.005 then
    insert into payments(parent_type, parent_id, amount, method, site_id, paid_at, note, contact_payment_method_id)
      values('po', p_po_id, v_remaining, p_method,
             case when p_method = 'cash' then p_site_id else null end, p_paid_at, p_note, p_method_ref);
  end if;

  -- Excess → vendor advance (+ its cash out of the drawer if paid cash).
  if v_excess > 0.005 then
    insert into vendor_credits(contact_id, amount_delta, source_type, source_id, method, site_id, occurred_on, note)
      values(v_vendor, v_excess, 'overpayment', p_po_id, p_method,
             case when p_method = 'cash' then p_site_id else null end, p_paid_at::date, p_note);
    if p_method = 'cash' then
      insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
        values(p_site_id, -v_excess, 'payment', p_po_id, 'دفعة مقدّمة زائدة لأمر شراء', auth.uid());
    end if;
  end if;

  return jsonb_build_object('paid', v_remaining, 'credit_added', greatest(v_excess, 0));
end;
$$;

-- 2) Apply a stored advance to a purchase order (non-cash payment). (manager+)
create or replace function vendor_credit_apply(
  p_po_id   uuid,
  p_amount  numeric,
  p_paid_at timestamptz,
  p_note    text
)
returns jsonb
language plpgsql security definer set search_path = public
as $$
declare v_total numeric; v_paid numeric; v_remaining numeric; v_vendor uuid; v_avail numeric;
begin
  if not is_manager_or_admin() then raise exception 'غير مصرح: خصم الدفعة المقدّمة للمديرين فقط'; end if;
  if p_amount is null or p_amount <= 0 then raise exception 'المبلغ غير صالح'; end if;

  select total_amount, vendor_id into v_total, v_vendor from purchase_orders where id = p_po_id;
  if v_total is null then raise exception 'أمر الشراء غير موجود'; end if;

  select coalesce(sum(amount),0) into v_paid from payments where parent_type = 'po' and parent_id = p_po_id;
  v_remaining := v_total - v_paid;
  if p_amount > v_remaining + 0.005 then
    raise exception 'المبلغ يتجاوز المتبقي على الأمر (المتبقي %)', v_remaining;
  end if;

  v_avail := get_vendor_credit(v_vendor);
  if p_amount > v_avail + 0.005 then
    raise exception 'الدفعة المقدّمة غير كافية (المتاح %)', v_avail;
  end if;

  insert into payments(parent_type, parent_id, amount, method, site_id, paid_at, note)
    values('po', p_po_id, p_amount, 'credit', null, p_paid_at, coalesce(p_note, 'خصم من الدفعة المقدّمة للمورّد'));
  insert into vendor_credits(contact_id, amount_delta, source_type, source_id, method, site_id, occurred_on, note)
    values(v_vendor, -p_amount, 'applied', p_po_id, null, null, p_paid_at::date, p_note);

  return jsonb_build_object('applied', p_amount, 'credit_left', v_avail - p_amount);
end;
$$;

-- 3) Add an advance directly (a down-payment before any PO). Cash leaves the
--    drawer. (staff+)
create or replace function vendor_credit_deposit(
  p_contact_id uuid,
  p_amount     numeric,
  p_method     payment_method,
  p_site_id    uuid,
  p_note       text
)
returns jsonb
language plpgsql security definer set search_path = public
as $$
begin
  if p_amount is null or p_amount <= 0 then raise exception 'المبلغ غير صالح'; end if;
  if p_method not in ('cash','instapay') then raise exception 'طريقة غير مدعومة (نقدي أو انستاباي)'; end if;
  if not exists (select 1 from contacts where id = p_contact_id) then
    raise exception 'جهة الاتصال غير موجودة';
  end if;

  if p_method = 'cash' then
    if p_site_id is null then raise exception 'يجب تحديد الفرع (الخزينة)'; end if;
    -- Cash OUT of the drawer; the floor guard blocks a prepayment it can't cover.
    insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
      values(p_site_id, -p_amount, 'manual', p_contact_id, coalesce(p_note, 'دفعة مقدّمة لمورّد'), auth.uid());
  end if;

  insert into vendor_credits(contact_id, amount_delta, source_type, method, site_id, occurred_on, note)
    values(p_contact_id, p_amount, 'deposit', p_method,
           case when p_method = 'cash' then p_site_id else null end, current_date, p_note);

  return jsonb_build_object('credit_added', p_amount);
end;
$$;

-- 4) Refund an advance from the vendor (cash back into a drawer, or instapay).
--    (manager+)
create or replace function vendor_credit_refund(
  p_contact_id uuid,
  p_amount     numeric,
  p_method     payment_method,
  p_site_id    uuid,
  p_note       text
)
returns jsonb
language plpgsql security definer set search_path = public
as $$
declare v_avail numeric;
begin
  if not is_manager_or_admin() then raise exception 'غير مصرح: الاسترداد للمديرين فقط'; end if;
  if p_amount is null or p_amount <= 0 then raise exception 'المبلغ غير صالح'; end if;
  if p_method not in ('cash','instapay') then raise exception 'طريقة غير مدعومة (نقدي أو انستاباي)'; end if;

  v_avail := get_vendor_credit(p_contact_id);
  if p_amount > v_avail + 0.005 then
    raise exception 'الدفعة المقدّمة غير كافية (المتاح %)', v_avail;
  end if;

  if p_method = 'cash' then
    if p_site_id is null then raise exception 'يجب تحديد الفرع (الخزينة)'; end if;
    -- The vendor returns our money → cash IN.
    insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
      values(p_site_id, p_amount, 'manual', p_contact_id, coalesce(p_note, 'استرداد دفعة مقدّمة من مورّد'), auth.uid());
  end if;

  insert into vendor_credits(contact_id, amount_delta, source_type, method, site_id, occurred_on, note)
    values(p_contact_id, -p_amount, 'refund', p_method,
           case when p_method = 'cash' then p_site_id else null end, current_date, p_note);

  return jsonb_build_object('refunded', p_amount, 'credit_left', v_avail - p_amount);
end;
$$;

-- Grants: callable by any signed-in user; role gates live inside each function.
revoke execute on function po_overpay(uuid, numeric, payment_method, uuid, timestamptz, text, uuid) from public, anon;
revoke execute on function vendor_credit_apply(uuid, numeric, timestamptz, text)                     from public, anon;
revoke execute on function vendor_credit_deposit(uuid, numeric, payment_method, uuid, text)          from public, anon;
revoke execute on function vendor_credit_refund(uuid, numeric, payment_method, uuid, text)           from public, anon;
grant execute on function po_overpay(uuid, numeric, payment_method, uuid, timestamptz, text, uuid) to authenticated;
grant execute on function vendor_credit_apply(uuid, numeric, timestamptz, text)                     to authenticated;
grant execute on function vendor_credit_deposit(uuid, numeric, payment_method, uuid, text)          to authenticated;
grant execute on function vendor_credit_refund(uuid, numeric, payment_method, uuid, text)           to authenticated;
grant execute on function get_vendor_credit(uuid) to authenticated;
