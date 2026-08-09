-- ============================================================================
-- 0025_org_rls.sql — org-scoped Row Level Security
-- ----------------------------------------------------------------------------
-- Part 2 of 5 (see multi-tenant-plan.md). THIS is the migration that actually
-- isolates tenants. Everything before it only made isolation possible.
--
-- Every policy gains one clause: `org_id = current_org()`. Role logic
-- (is_admin / is_manager_or_admin) is untouched — org and role are orthogonal
-- and compose. 61 policies existed; this rebuilds 60 (org_insert is dropped by
-- 0024, since creating a business is provisioning, not a tenant action). They
-- are written out longhand rather than generated in a loop: RLS is the
-- security boundary of the whole product, and a reviewer must be able to read
-- each policy and see exactly what it permits.
--
-- Policies are rewritten from the LIVE pg_policies state, not from the older
-- migration files — 0005 created `profiles_admin` and `sites_admin` FOR ALL
-- policies that were later replaced by per-command ones, and rebuilding from
-- the files would have resurrected them.
--
-- storage.objects (3 policies) is deliberately NOT here — it needs org-prefixed
-- object paths, which is 0028.
--
-- No recursion risk on profiles: current_org() is SECURITY DEFINER owned by
-- postgres, and RLS is not FORCEd, so its read of profiles bypasses profiles'
-- own policy. This is the same proven pattern current_app_role() already uses.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- Pre-flight: a NULL org_id on any profile means `org_id = current_org()`
-- evaluates to NULL → false → zero rows, i.e. that user is locked out of the
-- entire application. Fail the migration loudly rather than ship a lockout.
-- ---------------------------------------------------------------------------
do $$
declare n int;
begin
  select count(*) into n from profiles where org_id is null;
  if n > 0 then
    raise exception 'ABORT: % profile(s) have NULL org_id — applying org RLS would lock them out', n;
  end if;
  if not exists (select 1 from pg_proc where proname = 'current_org') then
    raise exception 'ABORT: current_org() missing — apply 0024 first';
  end if;
end $$;

-- ---------------------------------------------------------------------------
-- organization — keyed on `id`, not `org_id`. Each business sees only itself.
-- ---------------------------------------------------------------------------
-- No anon/public policy: the login page never looks an org up by slug, it only
-- uses the slug to build the email (ahmed@<slug>.local). A wrong slug fails as
-- "invalid credentials", which also avoids leaking the client list.
drop policy if exists org_read   on organization;
drop policy if exists org_update on organization;
create policy org_read on organization for select to authenticated
  using (id = current_org());
create policy org_update on organization for update to authenticated
  using (id = current_org() and is_admin())
  with check (id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- sites
-- ---------------------------------------------------------------------------
drop policy if exists sites_read          on sites;
drop policy if exists sites_admin_write   on sites;
drop policy if exists sites_admin_update  on sites;
drop policy if exists sites_admin_delete  on sites;
create policy sites_read on sites for select to authenticated
  using (org_id = current_org());
create policy sites_admin_write on sites for insert to authenticated
  with check (org_id = current_org() and is_admin());
create policy sites_admin_update on sites for update to authenticated
  using (org_id = current_org() and is_admin())
  with check (org_id = current_org() and is_admin());
create policy sites_admin_delete on sites for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- profiles
-- ---------------------------------------------------------------------------
drop policy if exists profiles_read          on profiles;
drop policy if exists profiles_admin_write   on profiles;
drop policy if exists profiles_admin_update  on profiles;
drop policy if exists profiles_admin_delete  on profiles;
create policy profiles_read on profiles for select to authenticated
  using (org_id = current_org());
create policy profiles_admin_write on profiles for insert to authenticated
  with check (org_id = current_org() and is_admin());
create policy profiles_admin_update on profiles for update to authenticated
  using (org_id = current_org() and is_admin())
  with check (org_id = current_org() and is_admin());
create policy profiles_admin_delete on profiles for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- contacts + children
-- ---------------------------------------------------------------------------
drop policy if exists contacts_read   on contacts;
drop policy if exists contacts_write  on contacts;
drop policy if exists contacts_update on contacts;
drop policy if exists contacts_delete on contacts;
create policy contacts_read on contacts for select to authenticated
  using (org_id = current_org());
create policy contacts_write on contacts for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy contacts_update on contacts for update to authenticated
  using (org_id = current_org() and is_manager_or_admin())
  with check (org_id = current_org() and is_manager_or_admin());
create policy contacts_delete on contacts for delete to authenticated
  using (org_id = current_org() and is_admin());

drop policy if exists phones_read   on contact_phones;
drop policy if exists phones_write  on contact_phones;
drop policy if exists phones_update on contact_phones;
drop policy if exists phones_delete on contact_phones;
create policy phones_read on contact_phones for select to authenticated
  using (org_id = current_org());
create policy phones_write on contact_phones for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy phones_update on contact_phones for update to authenticated
  using (org_id = current_org() and is_manager_or_admin())
  with check (org_id = current_org() and is_manager_or_admin());
create policy phones_delete on contact_phones for delete to authenticated
  using (org_id = current_org() and is_manager_or_admin());

drop policy if exists pm_read   on contact_payment_methods;
drop policy if exists pm_write  on contact_payment_methods;
drop policy if exists pm_update on contact_payment_methods;
drop policy if exists pm_delete on contact_payment_methods;
create policy pm_read on contact_payment_methods for select to authenticated
  using (org_id = current_org());
create policy pm_write on contact_payment_methods for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy pm_update on contact_payment_methods for update to authenticated
  using (org_id = current_org() and is_manager_or_admin())
  with check (org_id = current_org() and is_manager_or_admin());
create policy pm_delete on contact_payment_methods for delete to authenticated
  using (org_id = current_org() and is_manager_or_admin());

-- ---------------------------------------------------------------------------
-- items
-- ---------------------------------------------------------------------------
drop policy if exists items_read   on items;
drop policy if exists items_write  on items;
drop policy if exists items_update on items;
drop policy if exists items_delete on items;
create policy items_read on items for select to authenticated
  using (org_id = current_org());
create policy items_write on items for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy items_update on items for update to authenticated
  using (org_id = current_org() and is_manager_or_admin())
  with check (org_id = current_org() and is_manager_or_admin());
create policy items_delete on items for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- purchase_orders + lines + conversions
-- ---------------------------------------------------------------------------
drop policy if exists po_read   on purchase_orders;
drop policy if exists po_write  on purchase_orders;
drop policy if exists po_update on purchase_orders;
drop policy if exists po_delete on purchase_orders;
create policy po_read on purchase_orders for select to authenticated
  using (org_id = current_org());
create policy po_write on purchase_orders for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy po_update on purchase_orders for update to authenticated
  using (org_id = current_org() and is_manager_or_admin())
  with check (org_id = current_org() and is_manager_or_admin());
create policy po_delete on purchase_orders for delete to authenticated
  using (org_id = current_org() and is_admin());

drop policy if exists po_lines_read on po_lines;
create policy po_lines_read on po_lines for select to authenticated
  using (org_id = current_org());
-- (writes to po_lines remain RPC-only — no insert/update/delete policy)

drop policy if exists conv_read   on po_conversions;
drop policy if exists conv_write  on po_conversions;
drop policy if exists conv_delete on po_conversions;
create policy conv_read on po_conversions for select to authenticated
  using (org_id = current_org());
create policy conv_write on po_conversions for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy conv_delete on po_conversions for delete to authenticated
  using (org_id = current_org() and is_admin());

drop policy if exists plc_read   on po_line_conversions;
drop policy if exists plc_write  on po_line_conversions;
drop policy if exists plc_delete on po_line_conversions;
create policy plc_read on po_line_conversions for select to authenticated
  using (org_id = current_org());
create policy plc_write on po_line_conversions for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy plc_delete on po_line_conversions for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- sales_orders + lines
-- ---------------------------------------------------------------------------
-- The draft-status escape hatch (staff may create and edit their own drafts)
-- is preserved verbatim; org scoping is added alongside it.
-- ---------------------------------------------------------------------------
drop policy if exists so_read   on sales_orders;
drop policy if exists so_insert on sales_orders;
drop policy if exists so_update on sales_orders;
drop policy if exists so_delete on sales_orders;
create policy so_read on sales_orders for select to authenticated
  using (org_id = current_org());
create policy so_insert on sales_orders for insert to authenticated
  with check (org_id = current_org() and (is_manager_or_admin() or status = 'draft'::so_status));
create policy so_update on sales_orders for update to authenticated
  using (org_id = current_org() and (is_manager_or_admin() or status = 'draft'::so_status))
  with check (org_id = current_org() and (is_manager_or_admin() or status = 'draft'::so_status));
create policy so_delete on sales_orders for delete to authenticated
  using (org_id = current_org() and is_admin());

drop policy if exists sol_read   on sales_order_lines;
drop policy if exists sol_write  on sales_order_lines;
drop policy if exists sol_update on sales_order_lines;
drop policy if exists sol_delete on sales_order_lines;
create policy sol_read on sales_order_lines for select to authenticated
  using (org_id = current_org());
create policy sol_write on sales_order_lines for insert to authenticated
  with check (org_id = current_org() and (is_manager_or_admin() or exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status)));
create policy sol_update on sales_order_lines for update to authenticated
  using (org_id = current_org() and (is_manager_or_admin() or exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status)))
  with check (org_id = current_org() and (is_manager_or_admin() or exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status)));
create policy sol_delete on sales_order_lines for delete to authenticated
  using (org_id = current_org() and (is_manager_or_admin() or exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status)));

-- ---------------------------------------------------------------------------
-- payments
-- ---------------------------------------------------------------------------
-- payments_write was `with check (true)` — staff may record payments (rule 9).
-- That stays; it simply becomes org-bounded.
-- ---------------------------------------------------------------------------
drop policy if exists payments_read   on payments;
drop policy if exists payments_write  on payments;
drop policy if exists payments_delete on payments;
create policy payments_read on payments for select to authenticated
  using (org_id = current_org());
create policy payments_write on payments for insert to authenticated
  with check (org_id = current_org());
create policy payments_delete on payments for delete to authenticated
  using (org_id = current_org() and is_admin());

-- ---------------------------------------------------------------------------
-- Append-only ledgers — read + the one sanctioned manual insert each
-- ---------------------------------------------------------------------------
drop policy if exists sm_read  on stock_movements;
drop policy if exists sm_write on stock_movements;
create policy sm_read on stock_movements for select to authenticated
  using (org_id = current_org());
create policy sm_write on stock_movements for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin()
              and source_type = 'adjustment'::stock_source);

drop policy if exists cm_read  on cash_movements;
drop policy if exists cm_write on cash_movements;
create policy cm_read on cash_movements for select to authenticated
  using (org_id = current_org());
create policy cm_write on cash_movements for insert to authenticated
  with check (org_id = current_org() and is_admin()
              and source_type = 'manual'::cash_source);

drop policy if exists transfer_read  on stock_transfers;
drop policy if exists transfer_write on stock_transfers;
create policy transfer_read on stock_transfers for select to authenticated
  using (org_id = current_org());
create policy transfer_write on stock_transfers for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());

drop policy if exists client_credits_read on client_credits;
create policy client_credits_read on client_credits for select to authenticated
  using (org_id = current_org());

drop policy if exists vendor_credits_read on vendor_credits;
create policy vendor_credits_read on vendor_credits for select to authenticated
  using (org_id = current_org());

-- ---------------------------------------------------------------------------
-- order_attachments — table rows only; the bucket itself is 0028
-- ---------------------------------------------------------------------------
-- This closes DISCOVERY: without it, one tenant could list another's rows and
-- read their storage_path values. 0028 closes direct object fetch.
-- ---------------------------------------------------------------------------
drop policy if exists order_attachments_read   on order_attachments;
drop policy if exists order_attachments_insert on order_attachments;
drop policy if exists order_attachments_delete on order_attachments;
create policy order_attachments_read on order_attachments for select to authenticated
  using (org_id = current_org());
create policy order_attachments_insert on order_attachments for insert to authenticated
  with check (org_id = current_org() and is_manager_or_admin());
create policy order_attachments_delete on order_attachments for delete to authenticated
  using (org_id = current_org() and is_manager_or_admin());

-- ---------------------------------------------------------------------------
-- audit_log
-- ---------------------------------------------------------------------------
drop policy if exists audit_read on audit_log;
create policy audit_read on audit_log for select to authenticated
  using (org_id = current_org() and is_manager_or_admin());

-- ---------------------------------------------------------------------------
-- doc_counters keeps NO policies — it is written solely by the SECURITY
-- DEFINER next_doc_number(), and RLS with no policy denies everyone else.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- Post-flight: every policy on a tenant table must mention current_org().
-- Catches a policy silently missed in the rewrite above.
-- ---------------------------------------------------------------------------
do $$
declare
  bad text;
begin
  select string_agg(tablename || '.' || policyname, ', ')
    into bad
  from pg_policies
  where schemaname = 'public'
    and coalesce(qual, '') || coalesce(with_check, '') not like '%current_org()%';

  if bad is not null then
    raise exception 'ABORT: policies without org scoping: %', bad;
  end if;
end $$;
