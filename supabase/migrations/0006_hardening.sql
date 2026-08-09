-- ============================================================================
-- 0006_hardening.sql — Security hardening from Supabase advisors
-- ============================================================================
-- Two real findings from `get_advisors`:
--
-- 1. MUTABLE SEARCH_PATH: every function lacked `set search_path`, so a
--    session-level search_path change (or a malicious schema shadowing a
--    built-in) could redirect an unqualified call inside the function body.
--    Fix: pin every function's search_path explicitly.
--
-- 2. SECURITY DEFINER functions directly RPC-callable: next_doc_number,
--    payment_apply, po_conversion_apply, po_conversion_reverse,
--    so_placement_apply, transfer_apply are meant to run ONLY as triggers
--    (Postgres invokes trigger functions regardless of EXECUTE grants — the
--    revoke below does not break their trigger use). Left public-callable,
--    a signed-in user could invoke them directly via PostgREST RPC and,
--    running as the function owner, write ledger rows or advance a document
--    counter with none of the surrounding trigger/RLS context. Fix: revoke
--    direct EXECUTE from anon/authenticated.
--
-- current_app_role/is_admin/is_manager_or_admin stay EXECUTE-able by
-- `authenticated` (RLS policies call them as the querying role), but not by
-- `anon` (anon never has table access, so it gains nothing from them).
--
-- Also: profiles_admin/sites_admin were `FOR ALL`, which duplicates
-- profiles_read/sites_read's SELECT grant and makes Postgres evaluate two
-- permissive policies per read. Narrowed to INSERT/UPDATE/DELETE.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- Pin search_path on every function that lacked it.
-- ---------------------------------------------------------------------------
alter function get_stock(uuid, uuid, date)      set search_path = public;
alter function get_cash_balance(uuid, date)     set search_path = public;
alter function get_contact_balance(uuid)        set search_path = public;
alter function po_converted_kg(uuid)            set search_path = public;
alter function po_remaining_kg(uuid)            set search_path = public;
alter function get_dashboard(uuid, date)        set search_path = public;

alter function po_assign_code()                 set search_path = public;
alter function po_guard_total_kg()              set search_path = public;
alter function po_conversion_check()            set search_path = public;
alter function so_line_guard()                  set search_path = public;
alter function so_recompute_total()             set search_path = public;
alter function so_status_transition()           set search_path = public;
alter function payment_validate()               set search_path = public;
alter function block_mutation()                 set search_path = public;

-- ---------------------------------------------------------------------------
-- po_assign_code() and so_status_transition() call next_doc_number() as plain
-- (SECURITY INVOKER) trigger functions — so once next_doc_number's EXECUTE is
-- revoked from authenticated below, the nested call would be checked against
-- the invoking user and fail, breaking PO/SO numbering entirely. Making these
-- two SECURITY DEFINER switches the nested call's effective role to the
-- function owner, which keeps EXECUTE. (Verified against a local Postgres:
-- without this, invoicing an SO fails with "permission denied for function
-- next_doc_number".) They deserve the same "trigger-only" restriction as the
-- rest of this section, for the same reason.
-- ---------------------------------------------------------------------------
alter function po_assign_code()       security definer;
alter function so_status_transition() security definer;

-- ---------------------------------------------------------------------------
-- Internal-only SECURITY DEFINER functions: trigger-invoked, never direct RPC.
-- ---------------------------------------------------------------------------
revoke execute on function next_doc_number(text, int) from public, anon, authenticated;
revoke execute on function payment_apply()            from public, anon, authenticated;
revoke execute on function po_conversion_apply()       from public, anon, authenticated;
revoke execute on function po_conversion_reverse()     from public, anon, authenticated;
revoke execute on function so_placement_apply()        from public, anon, authenticated;
revoke execute on function transfer_apply()            from public, anon, authenticated;
revoke execute on function po_assign_code()            from public, anon, authenticated;
revoke execute on function so_status_transition()      from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Role helpers: keep callable by authenticated (RLS depends on it); anon
-- gains nothing from them since anon has no table grants anyway. Supabase
-- grants EXECUTE on new public-schema functions to anon/authenticated via
-- default privileges, on top of Postgres's own implicit PUBLIC grant — both
-- must be revoked explicitly (verified locally: revoking only one left anon
-- still able to call is_admin()), then authenticated re-granted.
-- ---------------------------------------------------------------------------
revoke execute on function current_app_role()      from public, anon, authenticated;
revoke execute on function is_admin()               from public, anon, authenticated;
revoke execute on function is_manager_or_admin()    from public, anon, authenticated;
grant  execute on function current_app_role()      to authenticated;
grant  execute on function is_admin()               to authenticated;
grant  execute on function is_manager_or_admin()    to authenticated;

-- ---------------------------------------------------------------------------
-- De-duplicate permissive policies: split the admin "FOR ALL" policies so
-- they no longer also cover SELECT (already granted by the *_read policies).
-- ---------------------------------------------------------------------------
drop policy sites_admin on sites;
create policy sites_admin_write  on sites for insert to authenticated with check (is_admin());
create policy sites_admin_update on sites for update to authenticated using (is_admin()) with check (is_admin());
create policy sites_admin_delete on sites for delete to authenticated using (is_admin());

drop policy profiles_admin on profiles;
create policy profiles_admin_write  on profiles for insert to authenticated with check (is_admin());
create policy profiles_admin_update on profiles for update to authenticated using (is_admin()) with check (is_admin());
create policy profiles_admin_delete on profiles for delete to authenticated using (is_admin());

-- ---------------------------------------------------------------------------
-- Missing covering indexes on foreign keys the advisor flagged.
-- ---------------------------------------------------------------------------
create index cash_movements_created_by_idx  on cash_movements(created_by);
create index contacts_created_by_idx        on contacts(created_by);
create index payments_created_by_idx        on payments(created_by);
create index payments_site_id_idx           on payments(site_id);
create index po_conversions_created_by_idx  on po_conversions(created_by);
create index purchase_orders_created_by_idx on purchase_orders(created_by);
create index sales_order_lines_item_id_idx  on sales_order_lines(item_id);
create index sales_orders_created_by_idx    on sales_orders(created_by);
create index stock_movements_created_by_idx on stock_movements(created_by);
create index stock_transfers_created_by_idx on stock_transfers(created_by);
create index stock_transfers_from_site_idx  on stock_transfers(from_site);
create index stock_transfers_item_id_idx    on stock_transfers(item_id);
create index stock_transfers_to_site_idx    on stock_transfers(to_site);
