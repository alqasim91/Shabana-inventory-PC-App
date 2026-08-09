-- 0015 — advisor fixes for the credit feature
-- 1. get_dashboard was re-created in 0014 with CREATE OR REPLACE, which reset the
--    search_path that 0006 had pinned. Re-pin it.
alter function get_dashboard(uuid, date) set search_path = public;

-- 2. client_credit_guard() is a trigger function only — it must never be a
--    callable RPC. Revoke EXECUTE (mirrors audit_row / block_mutation).
revoke execute on function client_credit_guard() from public, anon, authenticated;
