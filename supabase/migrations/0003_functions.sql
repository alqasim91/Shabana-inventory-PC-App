-- ============================================================================
-- 0003_functions.sql — Helper & read functions
-- ============================================================================
-- Read functions are the ONLY sanctioned way to ask "how much stock / cash /
-- balance is there as of a date". They always compute from the append-only
-- ledgers so history is reproducible (rule 10: opening of D = closing of D−1).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- Role helpers. SECURITY DEFINER + a stable search_path so RLS policies can
-- call them without recursing back through profiles' own RLS.
-- ---------------------------------------------------------------------------
create or replace function current_app_role()
returns app_role
language sql
stable
security definer
set search_path = public
as $$
  select role from profiles where user_id = auth.uid();
$$;

create or replace function is_admin()
returns boolean
language sql stable security definer set search_path = public
as $$ select coalesce(current_app_role() = 'admin', false); $$;

-- manager OR admin — the "can mutate business data" tier.
create or replace function is_manager_or_admin()
returns boolean
language sql stable security definer set search_path = public
as $$ select coalesce(current_app_role() in ('admin','manager'), false); $$;

-- ---------------------------------------------------------------------------
-- next_doc_number — atomic per-(scope, year) counter for PO / invoice codes.
-- ---------------------------------------------------------------------------
create or replace function next_doc_number(p_scope text, p_year int)
returns int
language plpgsql
security definer set search_path = public   -- writes doc_counters (RLS-locked to definer)
as $$
declare v int;
begin
  insert into doc_counters(scope, year, last_value)
  values (p_scope, p_year, 1)
  on conflict (scope, year)
    do update set last_value = doc_counters.last_value + 1
  returning last_value into v;
  return v;
end;
$$;

-- ---------------------------------------------------------------------------
-- get_stock(site_id, item_id, as_of) — quantity on hand.
-- site_id NULL = aggregate across ALL sites (كل الفروع). as_of NULL = now.
-- ---------------------------------------------------------------------------
create or replace function get_stock(
  p_site_id uuid,
  p_item_id uuid,
  p_as_of   date default null
)
returns numeric
language sql
stable
as $$
  select coalesce(sum(qty_delta), 0)
  from stock_movements
  where item_id = p_item_id
    and (p_site_id is null or site_id = p_site_id)
    and (p_as_of  is null or created_at < (p_as_of + 1));  -- through end of as_of day
$$;

-- ---------------------------------------------------------------------------
-- get_cash_balance(site_id, as_of) — cash drawer balance.
-- site_id NULL = all drawers combined. as_of NULL = now.
-- ---------------------------------------------------------------------------
create or replace function get_cash_balance(
  p_site_id uuid,
  p_as_of   date default null
)
returns numeric
language sql
stable
as $$
  select coalesce(sum(amount_delta), 0)
  from cash_movements
  where (p_site_id is null or site_id = p_site_id)
    and (p_as_of  is null or created_at < (p_as_of + 1));
$$;

-- ---------------------------------------------------------------------------
-- get_contact_balance(contact_id) — net running balance (له/عليه), rule 8.
-- Convention: POSITIVE = the contact owes us (receivable, عليه),
--             NEGATIVE = we owe the contact (payable, له).
-- Client side: unpaid sales orders (non-draft). Vendor side: unpaid POs.
-- A "both" contact nets the two.
-- ---------------------------------------------------------------------------
create or replace function get_contact_balance(p_contact_id uuid)
returns numeric
language sql
stable
as $$
  with receivable as (   -- what clients still owe us on their sales orders
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
  payable as (           -- what we still owe vendors on their purchase orders
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
       - coalesce((select amt from payable), 0);
$$;

-- ---------------------------------------------------------------------------
-- po_converted_kg / po_remaining_kg — how much of a PO has become stock (rule 2).
-- ---------------------------------------------------------------------------
create or replace function po_converted_kg(p_po_id uuid)
returns numeric
language sql stable
as $$
  select coalesce(sum(kg_consumed), 0) from po_conversions where po_id = p_po_id;
$$;

create or replace function po_remaining_kg(p_po_id uuid)
returns numeric
language sql stable
as $$
  select po.total_kg - po_converted_kg(p_po_id)
  from purchase_orders po where po.id = p_po_id;
$$;

-- ---------------------------------------------------------------------------
-- get_dashboard(site_id, date) — everything the لوحة التحكم needs for one day,
-- one site (or كل الفروع when site_id is NULL). Rule 10.
-- Returns jsonb:
--   { opening_cash, closing_cash, sales_total, collections_total,
--     collections_by_method{}, payments_out_by_method{},
--     low_stock[], top_items[], site_drawers[] }
-- All figures are computed from the ledgers up to end-of-day p_date.
-- ---------------------------------------------------------------------------
create or replace function get_dashboard(
  p_site_id uuid,
  p_date    date default current_date
)
returns jsonb
language plpgsql
stable
as $$
declare
  result jsonb;
begin
  select jsonb_build_object(

    -- Opening drawer = closing of the previous day (rule 10).
    'opening_cash', get_cash_balance(p_site_id, p_date - 1),
    'closing_cash', get_cash_balance(p_site_id, p_date),

    -- Sales invoiced on p_date (by SO order_date), scoped to site.
    'sales_total', coalesce((
      select sum(so.total_amount) from sales_orders so
      where so.order_date = p_date
        and so.status <> 'draft'
        and (p_site_id is null or so.site_id = p_site_id)
    ), 0),

    -- Money collected on p_date (SO payments), all methods.
    'collections_total', coalesce((
      select sum(p.amount) from payments p
      join sales_orders so on so.id = p.parent_id and p.parent_type = 'so'
      where p.paid_at::date = p_date
        and (p_site_id is null or so.site_id = p_site_id)
    ), 0),

    -- Collections broken down by method.
    'collections_by_method', coalesce((
      select jsonb_object_agg(method, amt) from (
        select p.method, sum(p.amount) amt from payments p
        join sales_orders so on so.id = p.parent_id and p.parent_type = 'so'
        where p.paid_at::date = p_date
          and (p_site_id is null or so.site_id = p_site_id)
        group by p.method
      ) q
    ), '{}'::jsonb),

    -- Money paid out on p_date (PO payments), by method. PO payment site is the
    -- drawer it was taken from (payments.site_id).
    'payments_out_by_method', coalesce((
      select jsonb_object_agg(method, amt) from (
        select p.method, sum(p.amount) amt from payments p
        where p.parent_type = 'po'
          and p.paid_at::date = p_date
          and (p_site_id is null or p.site_id = p_site_id)
        group by p.method
      ) q
    ), '{}'::jsonb),

    -- Low-stock list as of end of p_date (qty <= threshold, threshold > 0).
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

    -- Top moving items by out-quantity (sales) on p_date.
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

    -- Per-site closing drawers (used by the كل الفروع aggregate view).
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
