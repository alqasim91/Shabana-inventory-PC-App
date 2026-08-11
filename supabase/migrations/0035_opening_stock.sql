-- ============================================================================
-- 0035_opening_stock.sql — admin sets the real, counted stock of an item
-- ----------------------------------------------------------------------------
-- Business ask: a shop that already has inventory should not have to start
-- this app from zero. An admin states what is actually on the shelf, per صنف
-- per فرع, and the ledger absorbs the difference.
--
-- WHY "SET TO", NOT "ADD"
-- تسوية (0005/0032) already lets a manager post a signed delta. That is the
-- wrong instrument for a migration: the operator holds a counted quantity, not
-- a difference, and making them subtract by hand is exactly where a stock
-- count goes wrong. This RPC takes the count and computes the delta itself.
--
-- WHY IT STAYS APPEND-ONLY
-- Nothing here edits or deletes a movement — the invariant in CLAUDE.md holds.
-- Setting an item to 40 when the ledger says 25 writes one +15 row of type
-- 'opening'. Re-stating it as 30 later writes −10. The history stays readable:
-- you can always see what was claimed, by whom, and when.
--
-- WHY ADMIN-ONLY AND NOT A PERMISSION KEY
-- This overrides the ledger — the one number the whole app is built to make
-- untamperable. The owner asked for it to be an admin power, so it is gated on
-- is_admin() rather than on a toggle an admin could hand to a manager. Turning
-- it into a delegable permission later is one INSERT into permission_keys plus
-- swapping the guard below for has_perm(); nothing else changes.
-- ============================================================================

-- 0034 must have landed first, in its own transaction (Postgres refuses to use
-- a new enum value in the transaction that created it). Fail with a sentence
-- that says so, rather than a bare enum error from the trigger below.
do $$
begin
  if not exists (
    select 1 from pg_enum e join pg_type t on t.oid = e.enumtypid
    where t.typname = 'stock_source' and e.enumlabel = 'opening'
  ) then
    raise exception 'شغّل 0034_stock_source_opening.sql أولًا وبمفرده';
  end if;
end $$;

-- ---------------------------------------------------------------------------
-- 1. set_opening_stock — the whole feature
-- ---------------------------------------------------------------------------
create or replace function set_opening_stock(
  p_site_id uuid,
  p_item_id uuid,
  p_qty     numeric,
  p_note    text default null
)
returns numeric
language plpgsql
security definer set search_path = public
as $$
declare
  v_org     uuid := current_org();
  v_current numeric;
  v_delta   numeric;
begin
  if not is_admin() then
    raise exception 'ضبط الرصيد الافتتاحي متاح للمدير العام فقط';
  end if;

  if v_org is null then
    raise exception 'تعذر تحديد المنشأة الحالية';
  end if;

  if p_qty is null or p_qty < 0 then
    raise exception 'الرصيد الفعلي لا يمكن أن يكون سالبًا';
  end if;

  -- SECURITY DEFINER bypasses RLS, so these two checks are the ONLY thing
  -- standing between one business and another's stock. They are not optional.
  if not exists (select 1 from sites where id = p_site_id and org_id = v_org) then
    raise exception 'الفرع غير موجود';
  end if;
  if not exists (select 1 from items where id = p_item_id and org_id = v_org) then
    raise exception 'الصنف غير موجود';
  end if;

  -- Serialize per (فرع, صنف): two admins counting the same shelf at the same
  -- moment would otherwise both read the old balance and both post a delta.
  perform pg_advisory_xact_lock(hashtextextended(p_site_id::text || ':' || p_item_id::text, 0));

  -- Deliberately NOT get_stock(): that one is invoker-rights and takes no org,
  -- so under a definer it would happily sum across every tenant.
  select coalesce(sum(qty_delta), 0) into v_current
  from stock_movements
  where org_id = v_org and site_id = p_site_id and item_id = p_item_id;

  v_delta := round(p_qty, 3) - v_current;

  -- Already correct — writing a zero row would be noise in the item's history.
  if v_delta = 0 then
    return 0;
  end if;

  insert into stock_movements (org_id, site_id, item_id, qty_delta, source_type, created_by, note)
  values (v_org, p_site_id, p_item_id, v_delta, 'opening', auth.uid(),
          coalesce(nullif(btrim(p_note), ''), 'رصيد افتتاحي'));

  return v_delta;
end;
$$;

comment on function set_opening_stock(uuid, uuid, numeric, text) is
  'Admin-only. Sets an item''s stock at a فرع to a counted quantity by posting the difference as an ''opening'' movement. Returns the delta written (0 = already correct).';

revoke execute on function set_opening_stock(uuid, uuid, numeric, text) from public, anon;
grant  execute on function set_opening_stock(uuid, uuid, numeric, text) to authenticated;

-- ---------------------------------------------------------------------------
-- 2. Direct inserts stay impossible
-- ---------------------------------------------------------------------------
-- sm_write (0032) permits `source_type = 'adjustment'` and nothing else, so a
-- client cannot post an 'opening' row of its own — the RPC above is the only
-- door, and it is the only place the admin check lives. Left untouched on
-- purpose; this comment exists so nobody "fixes" the omission later.

-- ---------------------------------------------------------------------------
-- 3. Audit — an opening balance is a human decision about money
-- ---------------------------------------------------------------------------
drop trigger if exists trg_audit_stock_adj on stock_movements;
create trigger trg_audit_stock_adj after insert on stock_movements
  for each row when (new.source_type in ('adjustment', 'opening'))
  execute function audit_row();

-- ---------------------------------------------------------------------------
-- 4. Post-flight
-- ---------------------------------------------------------------------------
do $$
begin
  if not exists (
    select 1 from pg_proc where proname = 'set_opening_stock'
  ) then
    raise exception 'set_opening_stock did not get created';
  end if;

  if not exists (
    select 1 from pg_trigger
    where tgname = 'trg_audit_stock_adj'
      and pg_get_triggerdef(oid) like '%opening%'
  ) then
    raise exception 'audit trigger was not widened to cover opening balances';
  end if;
end $$;
