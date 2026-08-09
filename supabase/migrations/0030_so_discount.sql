-- ============================================================================
-- 0030_so_discount.sql — خصم على أمر البيع (مبلغ أو نسبة)
-- ----------------------------------------------------------------------------
-- Business ask: a sales order must be able to carry a discount, entered either
-- as a flat amount (ج.م) or as a percentage of the order.
--
-- The whole design turns on one decision: `total_amount` KEEPS ITS MEANING —
-- "what the client owes". Everything downstream already reads it (the payments
-- ledger and its overpayment guard, the auto-close on full collection, the
-- client statement and running balance, the client-credit RPCs, the dashboard
-- and reports), and none of it changes. What changes is where that number comes
-- from:
--
--     subtotal        = sum(sales_order_lines.line_total)   ← was total_amount
--     discount_amount = derived from (discount_type, discount_value, subtotal)
--     total_amount    = subtotal - discount_amount           ← the payable
--
-- `discount_amount` is STORED rather than recomputed on read: a percentage is
-- rounded to piastres exactly once, at write time, so the invoice, the ledger,
-- the statement and the drawer can never disagree by a piastre — and a later
-- change to the lines cannot silently rewrite the discount printed on a paper
-- invoice without also rewriting the total.
--
-- Two triggers keep the three columns true:
--   * trg_so_recompute_total (rewritten) — lines change → refresh `subtotal`.
--   * trg_so_apply_discount  (new)       — any header write → re-derive
--     discount_amount and total_amount from whatever subtotal now is.
-- The first updates the row, which fires the second, so a line edit re-applies
-- the discount automatically. BEFORE triggers don't re-fire on their own row,
-- so there is no recursion.
--
-- Guards (all raise Arabic errors and roll the write back):
--   * a percentage above ١٠٠٪
--   * a discount larger than the lines it is discounting
--   * a discount that would drop the total below what has ALREADY been
--     collected — the mirror image of the existing overpayment guard.
-- The last one stands down under `app.editing = 'on'` (migration 0018), because
-- the admin editor deliberately empties the lines mid-transaction and does its
-- own payments-vs-total check once the rewrite is complete.
--
-- Who may discount: whoever may edit the order. While draft that is anyone who
-- can edit a draft (staff included — they can already set the unit price on
-- every line, which is the same authority by another name); once invoiced, only
-- an admin, through admin_update_sales_order below.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Vocabulary
-- ---------------------------------------------------------------------------
do $$
begin
  if not exists (select 1 from pg_type where typname = 'so_discount_type') then
    create type so_discount_type as enum ('none', 'amount', 'percent');
  end if;
end
$$;

comment on type so_discount_type is
  'كيف أُدخل الخصم: بدون / مبلغ ثابت بالجنيه / نسبة مئوية من إجمالي البنود';

-- ---------------------------------------------------------------------------
-- 2. Columns
-- ---------------------------------------------------------------------------
alter table sales_orders
  add column if not exists subtotal        numeric(12,2)    not null default 0,
  add column if not exists discount_type   so_discount_type not null default 'none',
  add column if not exists discount_value  numeric(12,2)    not null default 0,
  add column if not exists discount_amount numeric(12,2)    not null default 0;

-- Every existing order is un-discounted, so its lines total IS its total.
update sales_orders set subtotal = total_amount where subtotal = 0 and total_amount <> 0;

alter table sales_orders
  drop constraint if exists so_subtotal_nonneg,
  drop constraint if exists so_discount_value_nonneg,
  drop constraint if exists so_discount_amount_range;

alter table sales_orders
  add constraint so_subtotal_nonneg       check (subtotal >= 0),
  add constraint so_discount_value_nonneg check (discount_value >= 0),
  -- The derived money-off can never be negative nor exceed what it discounts.
  add constraint so_discount_amount_range check (discount_amount >= 0 and discount_amount <= subtotal);

comment on column sales_orders.subtotal        is 'مجموع بنود الأمر قبل الخصم — يُحسب تلقائيًا من البنود';
comment on column sales_orders.discount_type   is 'نوع الخصم: بدون / مبلغ / نسبة';
comment on column sales_orders.discount_value  is 'القيمة كما أدخلها المستخدم: جنيهات إذا كان النوع مبلغ، وإلا نسبة مئوية';
comment on column sales_orders.discount_amount is 'قيمة الخصم بالجنيه بعد الاحتساب — مخزّنة حتى لا تختلف الفاتورة عن الدفتر بقرش';
comment on column sales_orders.total_amount    is 'المستحق على العميل = subtotal - discount_amount';

-- ---------------------------------------------------------------------------
-- 3. Lines → subtotal (was: lines → total_amount)
-- ---------------------------------------------------------------------------
create or replace function so_recompute_total()
returns trigger language plpgsql
set search_path = public as $$
declare v_so uuid := coalesce(new.so_id, old.so_id);
begin
  -- Writing subtotal fires trg_so_apply_discount, which re-derives the discount
  -- against the new lines and refreshes total_amount.
  update sales_orders
    set subtotal = coalesce((
      select sum(line_total) from sales_order_lines where so_id = v_so
    ), 0)
  where id = v_so;
  return null;
end;
$$;

-- ---------------------------------------------------------------------------
-- 4. Discount → discount_amount + total_amount
-- ---------------------------------------------------------------------------
create or replace function so_apply_discount()
returns trigger language plpgsql
set search_path = public as $$
declare
  v_disc numeric(12,2);
  v_paid numeric(12,2);
begin
  -- "بدون خصم" must not carry a stale number behind it.
  if new.discount_type = 'none' then
    new.discount_value := 0;
  end if;

  if new.discount_type = 'percent' and new.discount_value > 100 then
    raise exception 'نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪';
  end if;

  v_disc := case new.discount_type
              when 'percent' then round(new.subtotal * new.discount_value / 100, 2)
              when 'amount'  then new.discount_value
              else 0
            end;

  -- An order cannot be discounted past zero. The 0.005 slack is the same
  -- half-piastre tolerance the payment guards use.
  if v_disc > new.subtotal + 0.005 then
    raise exception 'الخصم (%) يتجاوز إجمالي البنود (%)', v_disc, new.subtotal;
  end if;
  v_disc := least(v_disc, new.subtotal);   -- clamp the rounding, never the intent

  new.discount_amount := v_disc;
  new.total_amount    := new.subtotal - v_disc;

  -- Money already in the drawer cannot be discounted away. Skipped under a
  -- controlled admin edit, which empties the lines on purpose and revalidates
  -- payments itself once the rewrite is done.
  if tg_op = 'UPDATE'
     and new.total_amount < old.total_amount
     and coalesce(current_setting('app.editing', true), '') <> 'on'
  then
    select coalesce(sum(amount), 0) into v_paid
      from payments where parent_type = 'so' and parent_id = new.id;
    if v_paid > new.total_amount + 0.005 then
      raise exception 'المحصّل من العميل (%) يتجاوز إجمالي الأمر بعد الخصم (%)',
        v_paid, new.total_amount;
    end if;
  end if;

  return new;
end;
$$;

drop trigger if exists trg_so_apply_discount on sales_orders;
create trigger trg_so_apply_discount
  before insert or update on sales_orders
  for each row execute function so_apply_discount();

-- Bring every existing row through the new trigger once, so subtotal /
-- discount_amount / total_amount are consistent from day one.
update sales_orders set subtotal = subtotal;

-- ---------------------------------------------------------------------------
-- 5. Admin editor — carry the discount through the rewind→rewrite→replay
-- ---------------------------------------------------------------------------
-- The signature grows two parameters. They are DEFAULTED so an old client that
-- still calls with five arguments keeps working — but the five-argument version
-- must be dropped first, or every such call becomes ambiguous between the two.
drop function if exists admin_update_sales_order(uuid, uuid, uuid, date, jsonb);

create or replace function admin_update_sales_order(
  p_id uuid,
  p_site uuid,
  p_client uuid,
  p_date date,
  p_lines jsonb,
  p_discount_type text default 'none',
  p_discount_value numeric default 0
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

  -- Lines first, discount second: trg_so_recompute_total has just refreshed
  -- subtotal, so the discount is validated against the order's FINAL lines.
  update sales_orders
    set discount_type  = coalesce(p_discount_type, 'none')::so_discount_type,
        discount_value = coalesce(p_discount_value, 0)
    where id = p_id;

  -- total_amount is now post-discount; the payments must still fit inside it.
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
      if v_old_status = 'closed' and v_total > 0 and v_paid >= v_total - 0.005 then
        update sales_orders set status = 'closed' where id = p_id and status = 'placed';
      end if;
    end if;
  end if;
end;
$$;

revoke execute on function admin_update_sales_order(uuid, uuid, uuid, date, jsonb, text, numeric) from public, anon;
grant  execute on function admin_update_sales_order(uuid, uuid, uuid, date, jsonb, text, numeric) to authenticated;

-- ---------------------------------------------------------------------------
-- 6. Audit — a discount is a human decision about money, so it gets a row
-- ---------------------------------------------------------------------------
-- The existing SO audit trigger fires on status changes only, deliberately
-- skipping the recompute churn of a draft being built. A discount is the one
-- header edit worth recording on its own: it moves money without touching a
-- line, and "who gave this client ٪١٠?" is a question an owner will ask.
--
-- Keyed on the INTENT (type/value as entered), not on discount_amount — with a
-- percentage the amount also moves every time a line changes, which is churn,
-- not a decision.
drop trigger if exists trg_audit_so_discount on sales_orders;
create trigger trg_audit_so_discount after update on sales_orders
  for each row when (
    old.discount_type  is distinct from new.discount_type
    or old.discount_value is distinct from new.discount_value
  ) execute function audit_row();
