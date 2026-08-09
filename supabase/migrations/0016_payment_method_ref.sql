-- 0016 — tie an instapay / bank-transfer payment to a specific stored account
-- on the contact's card, instead of accepting a bare method with no destination.
-- ---------------------------------------------------------------------------

alter table payments
  add column contact_payment_method_id uuid references contact_payment_methods(id) on delete set null;

-- payment_validate now also checks that a chosen contact payment method really
-- belongs to the order's contact (client for SO, vendor for PO) and matches the
-- payment method. (Reproduces the 0008 body verbatim + the new block.)
create or replace function payment_validate()
returns trigger language plpgsql set search_path = public as $$
declare
  parent_total numeric; already_paid numeric; so_status_val so_status;
  v_contact uuid; v_pm record;
begin
  if new.parent_type = 'so' then
    select total_amount, status, client_id into parent_total, so_status_val, v_contact
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
    select total_amount, vendor_id into parent_total, v_contact
      from purchase_orders where id = new.parent_id;
    if parent_total is null then
      raise exception 'أمر الشراء غير موجود';
    end if;
  end if;

  -- A cash payment MUST name the drawer it touches.
  if new.method = 'cash' and new.site_id is null then
    raise exception 'يجب تحديد الفرع (الخزينة) للدفعة النقدية';
  end if;

  -- A chosen stored account must belong to this contact and match the method.
  if new.contact_payment_method_id is not null then
    select * into v_pm from contact_payment_methods where id = new.contact_payment_method_id;
    if v_pm.id is null or v_pm.contact_id <> v_contact or v_pm.method <> new.method then
      raise exception 'طريقة الدفع المحددة لا تخص جهة الاتصال';
    end if;
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

-- so_overpay gains a method-ref param so an instapay/bank overpayment records
-- the account too. Drop + recreate (signature change) and re-grant.
drop function if exists so_overpay(uuid, numeric, payment_method, uuid, timestamptz, text);

create function so_overpay(
  p_so_id uuid, p_amount numeric, p_method payment_method, p_site_id uuid,
  p_paid_at timestamptz, p_note text, p_method_ref uuid
) returns jsonb language plpgsql security definer set search_path = public as $$
declare
  v_total numeric; v_paid numeric; v_remaining numeric; v_excess numeric;
  v_client uuid; v_so_site uuid; v_site uuid;
begin
  if p_amount is null or p_amount <= 0 then raise exception 'المبلغ غير صالح'; end if;
  if p_method = 'credit' then raise exception 'طريقة الدفع غير صالحة'; end if;
  select total_amount, client_id, site_id into v_total, v_client, v_so_site from sales_orders where id = p_so_id;
  if v_total is null then raise exception 'أمر البيع غير موجود'; end if;
  select coalesce(sum(amount),0) into v_paid from payments where parent_type = 'so' and parent_id = p_so_id;
  v_remaining := greatest(v_total - v_paid, 0);
  v_excess := p_amount - v_remaining;
  v_site := coalesce(p_site_id, v_so_site);
  if v_remaining > 0.005 then
    insert into payments(parent_type, parent_id, amount, method, site_id, paid_at, note, contact_payment_method_id)
      values('so', p_so_id, v_remaining, p_method, case when p_method = 'cash' then v_site else null end, p_paid_at, p_note, p_method_ref);
  end if;
  if v_excess > 0.005 then
    insert into client_credits(contact_id, amount_delta, source_type, source_id, method, site_id, occurred_on, note)
      values(v_client, v_excess, 'overpayment', p_so_id, p_method, case when p_method = 'cash' then v_site else null end, p_paid_at::date, p_note);
    if p_method = 'cash' then
      insert into cash_movements(site_id, amount_delta, source_type, source_id, reason, created_by)
        values(v_site, v_excess, 'payment', p_so_id, 'رصيد زائد من دفعة بيع', auth.uid());
    end if;
  end if;
  return jsonb_build_object('paid', v_remaining, 'credit_added', greatest(v_excess, 0));
end;
$$;

revoke execute on function so_overpay(uuid, numeric, payment_method, uuid, timestamptz, text, uuid) from public, anon;
grant execute on function so_overpay(uuid, numeric, payment_method, uuid, timestamptz, text, uuid) to authenticated;
