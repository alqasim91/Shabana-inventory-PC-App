-- ============================================================================
-- 0032_permissions_enforce.sql — the policies start reading the permissions
-- ----------------------------------------------------------------------------
-- 0031 built the model and backfilled it so that every user's permission set is
-- exactly what their role grants them today. This migration is the switchover:
-- `is_manager_or_admin()` and `is_admin()` are replaced, throughout, by the
-- specific permission the action needs, plus `can_use_site()` wherever a row
-- belongs to a branch.
--
-- Because of that backfill, the morning after this lands nobody can do more or
-- less than they could the night before. What changes is that an admin can now
-- move any one of those abilities, per person, from the UI.
--
-- THREE KINDS OF ENFORCEMENT, because RLS alone cannot express all of it:
--
--  1. Policies — "may you touch this row at all", plus branch scoping.
--  2. Triggers — "may you make THIS transition". فوترة and تنفيذ are both
--     `update sales_orders set status = …`; only a trigger can see old.status
--     and new.status and tell them apart. Same for the discount.
--  3. RPC gates — the twelve SECURITY DEFINER functions bypass RLS by design,
--     so each carries its own check. Those are rewritten mechanically below.
--
-- Deliberately NOT changed: the write policies on `profiles`. User management
-- runs through admin_set_user_access(), which is SECURITY DEFINER and carries
-- the escalation guards. Leaving the table's own policies admin-only means a
-- users.manage holder cannot sidestep those guards with a direct UPDATE and
-- promote themselves to admin.
-- ============================================================================

do $$
begin
  if not exists (select 1 from pg_proc where proname = 'has_perm') then
    raise exception 'ABORT: has_perm() missing — apply 0031 first';
  end if;
  -- PC EDITION DIVERGENCE (see supabase/PC-DIVERGENCE.md) - the `exists ... from
  -- profiles` clause is added here and is NOT in the cloud copy of this file.
  -- 0031's backfill inserts one row per existing profile. On a fresh PC install
  -- the database is empty (the first profile is created at runtime by
  -- pc_first_run_bootstrap, long after migrations run), so the backfill
  -- correctly inserts nothing and this guard then fires on a database that is
  -- perfectly healthy - aborting provisioning before any service is registered.
  -- The guard is only meaningful when there was data to back-fill.
  if exists (select 1 from profiles) and not exists (select 1 from user_permissions) then
    raise exception 'ABORT: user_permissions is empty — 0031 backfill did not run';
  end if;
end $$;

-- ===========================================================================
-- 1. POLICIES
-- ===========================================================================

-- organization ---------------------------------------------------------------
drop policy if exists org_update on organization;
create policy org_update on organization for update to authenticated
  using (id = current_org() and has_perm('settings.manage'))
  with check (id = current_org() and has_perm('settings.manage'));

-- sites ----------------------------------------------------------------------
-- Reading is branch-scoped: this is what makes the site switcher show a
-- restricted user only their own فروع.
drop policy if exists sites_read         on sites;
drop policy if exists sites_admin_write  on sites;
drop policy if exists sites_admin_update on sites;
drop policy if exists sites_admin_delete on sites;
create policy sites_read on sites for select to authenticated
  using (org_id = current_org() and can_use_site(id));
create policy sites_admin_write on sites for insert to authenticated
  with check (org_id = current_org() and has_perm('sites.manage'));
create policy sites_admin_update on sites for update to authenticated
  using (org_id = current_org() and has_perm('sites.manage'))
  with check (org_id = current_org() and has_perm('sites.manage'));
create policy sites_admin_delete on sites for delete to authenticated
  using (org_id = current_org() and has_perm('sites.manage'));

-- contacts + children --------------------------------------------------------
drop policy if exists contacts_read   on contacts;
drop policy if exists contacts_write  on contacts;
drop policy if exists contacts_update on contacts;
drop policy if exists contacts_delete on contacts;
create policy contacts_read on contacts for select to authenticated
  using (org_id = current_org() and has_perm('contacts.view'));
create policy contacts_write on contacts for insert to authenticated
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy contacts_update on contacts for update to authenticated
  using (org_id = current_org() and has_perm('contacts.manage'))
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy contacts_delete on contacts for delete to authenticated
  using (org_id = current_org() and has_perm('contacts.delete'));

drop policy if exists phones_read   on contact_phones;
drop policy if exists phones_write  on contact_phones;
drop policy if exists phones_update on contact_phones;
drop policy if exists phones_delete on contact_phones;
create policy phones_read on contact_phones for select to authenticated
  using (org_id = current_org() and has_perm('contacts.view'));
create policy phones_write on contact_phones for insert to authenticated
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy phones_update on contact_phones for update to authenticated
  using (org_id = current_org() and has_perm('contacts.manage'))
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy phones_delete on contact_phones for delete to authenticated
  using (org_id = current_org() and has_perm('contacts.manage'));

drop policy if exists pm_read   on contact_payment_methods;
drop policy if exists pm_write  on contact_payment_methods;
drop policy if exists pm_update on contact_payment_methods;
drop policy if exists pm_delete on contact_payment_methods;
create policy pm_read on contact_payment_methods for select to authenticated
  using (org_id = current_org() and has_perm('contacts.view'));
create policy pm_write on contact_payment_methods for insert to authenticated
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy pm_update on contact_payment_methods for update to authenticated
  using (org_id = current_org() and has_perm('contacts.manage'))
  with check (org_id = current_org() and has_perm('contacts.manage'));
create policy pm_delete on contact_payment_methods for delete to authenticated
  using (org_id = current_org() and has_perm('contacts.manage'));

-- items ----------------------------------------------------------------------
drop policy if exists items_read   on items;
drop policy if exists items_write  on items;
drop policy if exists items_update on items;
drop policy if exists items_delete on items;
create policy items_read on items for select to authenticated
  using (org_id = current_org() and has_perm('inventory.view'));
create policy items_write on items for insert to authenticated
  with check (org_id = current_org() and has_perm('inventory.items'));
create policy items_update on items for update to authenticated
  using (org_id = current_org() and has_perm('inventory.items'))
  with check (org_id = current_org() and has_perm('inventory.items'));
create policy items_delete on items for delete to authenticated
  using (org_id = current_org() and has_perm('inventory.item_delete'));

-- purchase orders ------------------------------------------------------------
-- POs are site-agnostic at creation (rule 2); it is the CONVERSION that lands
-- in a branch, so that is where can_use_site() belongs.
drop policy if exists po_read   on purchase_orders;
drop policy if exists po_write  on purchase_orders;
drop policy if exists po_update on purchase_orders;
drop policy if exists po_delete on purchase_orders;
create policy po_read on purchase_orders for select to authenticated
  using (org_id = current_org() and has_perm('purchases.view'));
create policy po_write on purchase_orders for insert to authenticated
  with check (org_id = current_org() and has_perm('purchases.create'));
create policy po_update on purchase_orders for update to authenticated
  using (org_id = current_org() and has_perm('purchases.edit'))
  with check (org_id = current_org() and has_perm('purchases.edit'));
create policy po_delete on purchase_orders for delete to authenticated
  using (org_id = current_org() and has_perm('purchases.delete'));

drop policy if exists po_lines_read on po_lines;
create policy po_lines_read on po_lines for select to authenticated
  using (org_id = current_org() and has_perm('purchases.view'));

drop policy if exists conv_read   on po_conversions;
drop policy if exists conv_write  on po_conversions;
drop policy if exists conv_delete on po_conversions;
create policy conv_read on po_conversions for select to authenticated
  using (org_id = current_org() and has_perm('purchases.view') and can_use_site(site_id));
create policy conv_write on po_conversions for insert to authenticated
  with check (org_id = current_org() and has_perm('purchases.convert') and can_use_site(site_id));
create policy conv_delete on po_conversions for delete to authenticated
  using (org_id = current_org() and has_perm('purchases.delete') and can_use_site(site_id));

drop policy if exists plc_read   on po_line_conversions;
drop policy if exists plc_write  on po_line_conversions;
drop policy if exists plc_delete on po_line_conversions;
create policy plc_read on po_line_conversions for select to authenticated
  using (org_id = current_org() and has_perm('purchases.view'));
create policy plc_write on po_line_conversions for insert to authenticated
  with check (org_id = current_org() and has_perm('purchases.convert'));
create policy plc_delete on po_line_conversions for delete to authenticated
  using (org_id = current_org() and has_perm('purchases.delete'));

-- sales orders ---------------------------------------------------------------
-- The UPDATE policy is the coarse "may you touch a sales order at all" gate;
-- which specific change is allowed is decided by the two triggers in part 2.
-- INSERT is now pinned to drafts for everyone: an order created directly as
-- 'placed' would never run the placement trigger and would sell stock that was
-- never deducted.
drop policy if exists so_read   on sales_orders;
drop policy if exists so_insert on sales_orders;
drop policy if exists so_update on sales_orders;
drop policy if exists so_delete on sales_orders;
create policy so_read on sales_orders for select to authenticated
  using (org_id = current_org() and has_perm('sales.view') and can_use_site(site_id));
create policy so_insert on sales_orders for insert to authenticated
  with check (org_id = current_org() and has_perm('sales.draft') and can_use_site(site_id)
              and status = 'draft'::so_status);
create policy so_update on sales_orders for update to authenticated
  using (org_id = current_org() and can_use_site(site_id) and (
           has_perm('sales.draft') or has_perm('sales.invoice') or has_perm('sales.place')
           or has_perm('sales.cancel_placement') or has_perm('sales.edit_locked')))
  with check (org_id = current_org() and can_use_site(site_id) and (
           has_perm('sales.draft') or has_perm('sales.invoice') or has_perm('sales.place')
           or has_perm('sales.cancel_placement') or has_perm('sales.edit_locked')));
create policy so_delete on sales_orders for delete to authenticated
  using (org_id = current_org() and has_perm('sales.delete') and can_use_site(site_id));

drop policy if exists sol_read   on sales_order_lines;
drop policy if exists sol_write  on sales_order_lines;
drop policy if exists sol_update on sales_order_lines;
drop policy if exists sol_delete on sales_order_lines;
create policy sol_read on sales_order_lines for select to authenticated
  using (org_id = current_org() and has_perm('sales.view'));
create policy sol_write on sales_order_lines for insert to authenticated
  with check (org_id = current_org() and has_perm('sales.draft') and exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status));
create policy sol_update on sales_order_lines for update to authenticated
  using (org_id = current_org() and has_perm('sales.draft') and exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status))
  with check (org_id = current_org() and has_perm('sales.draft') and exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status));
create policy sol_delete on sales_order_lines for delete to authenticated
  using (org_id = current_org() and has_perm('sales.draft') and exists (
    select 1 from sales_orders so
     where so.id = sales_order_lines.so_id and so.status = 'draft'::so_status));

-- payments -------------------------------------------------------------------
-- Reading is branch-scoped rather than permission-scoped: both a sales person
-- and a purchasing person need the ledger, but neither should see the other
-- branch's money. site_id is null on non-cash PO payments, which can_use_site()
-- treats as "not branch-specific".
drop policy if exists payments_read   on payments;
drop policy if exists payments_write  on payments;
drop policy if exists payments_delete on payments;
create policy payments_read on payments for select to authenticated
  using (org_id = current_org() and can_use_site(site_id));
create policy payments_write on payments for insert to authenticated
  with check (org_id = current_org() and has_perm('payments.record') and can_use_site(site_id));
create policy payments_delete on payments for delete to authenticated
  using (org_id = current_org() and has_perm('payments.delete') and can_use_site(site_id));

-- append-only ledgers --------------------------------------------------------
drop policy if exists sm_read  on stock_movements;
drop policy if exists sm_write on stock_movements;
create policy sm_read on stock_movements for select to authenticated
  using (org_id = current_org() and has_perm('inventory.view') and can_use_site(site_id));
create policy sm_write on stock_movements for insert to authenticated
  with check (org_id = current_org() and has_perm('inventory.adjust') and can_use_site(site_id)
              and source_type = 'adjustment'::stock_source);

drop policy if exists cm_read  on cash_movements;
drop policy if exists cm_write on cash_movements;
create policy cm_read on cash_movements for select to authenticated
  using (org_id = current_org() and has_perm('cash.view') and can_use_site(site_id));
create policy cm_write on cash_movements for insert to authenticated
  with check (org_id = current_org() and has_perm('cash.manual') and can_use_site(site_id)
              and source_type = 'manual'::cash_source);

-- A transfer is visible from either end, but moving stock needs rights at BOTH
-- ends — otherwise a branch-scoped user could pull stock out of a branch they
-- have no business touching.
drop policy if exists transfer_read  on stock_transfers;
drop policy if exists transfer_write on stock_transfers;
create policy transfer_read on stock_transfers for select to authenticated
  using (org_id = current_org() and has_perm('inventory.view')
         and (can_use_site(from_site) or can_use_site(to_site)));
create policy transfer_write on stock_transfers for insert to authenticated
  with check (org_id = current_org() and has_perm('inventory.transfer')
              and can_use_site(from_site) and can_use_site(to_site));

drop policy if exists client_credits_read on client_credits;
create policy client_credits_read on client_credits for select to authenticated
  using (org_id = current_org() and has_perm('contacts.view'));

drop policy if exists vendor_credits_read on vendor_credits;
create policy vendor_credits_read on vendor_credits for select to authenticated
  using (org_id = current_org() and has_perm('contacts.view'));

-- order attachments ----------------------------------------------------------
drop policy if exists order_attachments_read   on order_attachments;
drop policy if exists order_attachments_insert on order_attachments;
drop policy if exists order_attachments_delete on order_attachments;
create policy order_attachments_read on order_attachments for select to authenticated
  using (org_id = current_org() and (has_perm('sales.view') or has_perm('purchases.view')));
create policy order_attachments_insert on order_attachments for insert to authenticated
  with check (org_id = current_org() and has_perm('attachments.manage'));
create policy order_attachments_delete on order_attachments for delete to authenticated
  using (org_id = current_org() and has_perm('attachments.manage'));

-- audit log ------------------------------------------------------------------
drop policy if exists audit_read on audit_log;
create policy audit_read on audit_log for select to authenticated
  using (org_id = current_org() and has_perm('audit.view'));

-- the permission tables themselves ------------------------------------------
-- Relaxed from is_admin() now that has_perm exists; the escalation guards in
-- admin_set_user_access() are what actually keep this safe.
drop policy if exists user_permissions_read   on user_permissions;
drop policy if exists user_permissions_write  on user_permissions;
drop policy if exists user_permissions_delete on user_permissions;
create policy user_permissions_read on user_permissions for select to authenticated
  using (org_id = current_org() and (user_id = auth.uid() or has_perm('users.manage')));
create policy user_permissions_write on user_permissions for insert to authenticated
  with check (org_id = current_org() and has_perm('users.manage'));
create policy user_permissions_delete on user_permissions for delete to authenticated
  using (org_id = current_org() and has_perm('users.manage'));

drop policy if exists user_sites_read   on user_sites;
drop policy if exists user_sites_write  on user_sites;
drop policy if exists user_sites_delete on user_sites;
create policy user_sites_read on user_sites for select to authenticated
  using (org_id = current_org() and (user_id = auth.uid() or has_perm('users.manage')));
create policy user_sites_write on user_sites for insert to authenticated
  with check (org_id = current_org() and has_perm('users.manage'));
create policy user_sites_delete on user_sites for delete to authenticated
  using (org_id = current_org() and has_perm('users.manage'));

-- storage objects (bucket order-docs, from 0028) -----------------------------
drop policy if exists order_docs_insert on storage.objects;
drop policy if exists order_docs_delete on storage.objects;
create policy order_docs_insert on storage.objects for insert to authenticated
  with check (
    bucket_id = 'order-docs'
    and public.has_perm('attachments.manage')
    and (storage.foldername(name))[1] = public.current_org()::text
  );
create policy order_docs_delete on storage.objects for delete to authenticated
  using (
    bucket_id = 'order-docs'
    and public.has_perm('attachments.manage')
    and exists (
      select 1 from public.order_attachments a
       where a.storage_path = storage.objects.name
         and a.org_id = public.current_org()
    )
  );

-- ===========================================================================
-- 2. TRIGGERS — the checks RLS cannot express
-- ===========================================================================

-- Lifecycle. فوترة / تنفيذ / إلغاء التنفيذ are three separate permissions even
-- though all three are `update sales_orders set status = …`.
-- The automatic invoiced|placed -> closed on full collection is NOT gated: it
-- is written by payment_apply on behalf of whoever recorded the payment, and
-- they already needed payments.record to get there.
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

  if coalesce(current_setting('app.editing', true), '') <> 'on' then
    if not (
         (old.status = 'draft'    and new.status = 'invoiced')
      or (old.status = 'invoiced' and new.status = 'placed')
      or (old.status = 'placed'   and new.status = 'invoiced')
      or (old.status = 'placed'   and new.status = 'closed')
      or (old.status = 'closed'   and new.status = 'placed')
    ) then
      raise exception 'انتقال حالة غير مسموح به لأمر البيع (% ← %)', old.status, new.status;
    end if;

    if old.status = 'draft' and new.status = 'invoiced' and not has_perm('sales.invoice') then
      raise exception 'غير مصرح لك بفوترة أوامر البيع';
    end if;
    if old.status = 'invoiced' and new.status = 'placed' and not has_perm('sales.place') then
      raise exception 'غير مصرح لك بتنفيذ أوامر البيع';
    end if;
    if old.status = 'placed' and new.status = 'invoiced' and not has_perm('sales.cancel_placement') then
      raise exception 'غير مصرح لك بإلغاء تنفيذ أوامر البيع';
    end if;
  end if;

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

-- Header edits that are NOT status changes: editing a draft needs sales.draft,
-- touching an order that is already invoiced needs sales.edit_locked. Without
-- this, anyone holding any one of the five sales write permissions could edit
-- any order directly through the API — the UPDATE policy is deliberately coarse.
create or replace function so_write_guard()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  -- The admin editor sets app.editing and did its own check; status changes
  -- belong to so_status_transition above.
  if coalesce(current_setting('app.editing', true), '') = 'on' then return new; end if;
  if new.status is distinct from old.status then return new; end if;

  if old.status = 'draft' then
    if not has_perm('sales.draft') then
      raise exception 'غير مصرح لك بتعديل مسودات أوامر البيع';
    end if;
  else
    if not has_perm('sales.edit_locked') then
      raise exception 'غير مصرح لك بتعديل أمر بيع بعد فوترته';
    end if;
  end if;
  return new;
end;
$$;

drop trigger if exists trg_so_write_guard on sales_orders;
create trigger trg_so_write_guard
  before update on sales_orders
  for each row execute function so_write_guard();

-- Discount is its own permission — it is the one edit that moves money without
-- touching a line, and the classic way value walks out of a shop.
create or replace function so_apply_discount()
returns trigger language plpgsql
set search_path = public as $$
declare
  v_disc numeric(12,2);
  v_paid numeric(12,2);
  v_editing boolean := coalesce(current_setting('app.editing', true), '') = 'on';
begin
  if new.discount_type = 'none' then
    new.discount_value := 0;
  end if;

  if not v_editing
     and (tg_op = 'INSERT' or new.discount_type is distinct from old.discount_type
                           or new.discount_value is distinct from old.discount_value)
     and new.discount_type <> 'none'
     and not has_perm('sales.discount')
  then
    raise exception 'غير مصرح لك بعمل خصم على أوامر البيع';
  end if;

  if new.discount_type = 'percent' and new.discount_value > 100 then
    raise exception 'نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪';
  end if;

  v_disc := case new.discount_type
              when 'percent' then round(new.subtotal * new.discount_value / 100, 2)
              when 'amount'  then new.discount_value
              else 0
            end;

  if v_disc > new.subtotal + 0.005 then
    raise exception 'الخصم (%) يتجاوز إجمالي البنود (%)', v_disc, new.subtotal;
  end if;
  v_disc := least(v_disc, new.subtotal);

  new.discount_amount := v_disc;
  new.total_amount    := new.subtotal - v_disc;

  if tg_op = 'UPDATE' and new.total_amount < old.total_amount and not v_editing then
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

-- ===========================================================================
-- 3. RPC GATES
-- ===========================================================================
-- The twelve SECURITY DEFINER functions run as the owner and bypass RLS by
-- design, so each carries its own role check. Rather than re-type bodies that
-- move money and stock — where a transcription slip would be silent and
-- expensive — each function is regenerated from its own live definition with
-- exactly one token replaced, and the migration aborts if that token was not
-- found where it was expected.
do $$
declare
  m        record;
  v_new    text;
  v_hits   int;
  mappings constant text[][] := array[
    -- function name                 old check                 new check
    ['credit_apply',                 'is_manager_or_admin()',  'has_perm(''payments.credit'')'],
    ['credit_refund',                'is_manager_or_admin()',  'has_perm(''payments.credit'')'],
    ['vendor_credit_apply',          'is_manager_or_admin()',  'has_perm(''payments.credit'')'],
    ['vendor_credit_refund',         'is_manager_or_admin()',  'has_perm(''payments.credit'')'],
    ['create_itemized_po',           'is_manager_or_admin()',  'has_perm(''purchases.create'')'],
    ['update_itemized_po',           'is_admin()',             'has_perm(''purchases.edit'')'],
    ['admin_update_purchase_order',  'is_admin()',             'has_perm(''purchases.edit'')'],
    ['admin_update_sales_order',     'is_admin()',             'has_perm(''sales.edit_locked'')']
  ];
  i int;
begin
  for i in 1 .. array_length(mappings, 1) loop
    v_hits := 0;
    for m in
      select pg_get_functiondef(oid) as def, oid::regprocedure::text as sig
        from pg_proc
       where proname = mappings[i][1]
         and pronamespace = 'public'::regnamespace
    loop
      -- A gate that is not where we expect it means the function drifted from
      -- what this migration was written against. Stop rather than guess.
      if position(mappings[i][2] in m.def) = 0 then
        raise exception 'ABORT: % does not contain % — refusing to guess', m.sig, mappings[i][2];
      end if;
      v_new := replace(m.def, mappings[i][2], mappings[i][3]);
      execute v_new;
      v_hits := v_hits + 1;
      raise notice 'regated % : % -> %', m.sig, mappings[i][2], mappings[i][3];
    end loop;

    -- The dangerous failure is the quiet one: a misspelled name would leave a
    -- money RPC still gated on the old role and nobody would notice.
    if v_hits = 0 then
      raise exception 'ABORT: no function named %() found to re-gate', mappings[i][1];
    end if;
  end loop;
end $$;

-- admin_set_user_access now answers to users.manage rather than is_admin(), so
-- it needs the escalation guards that assumption used to provide: someone who
-- is not an admin may not mint an admin, may not edit one, and may not hand out
-- a permission they do not themselves hold.
create or replace function admin_set_user_access(
  p_user_id   uuid,
  p_full_name text,
  p_role      app_role,
  p_active    boolean,
  p_perms     text[],
  p_all_sites boolean,
  p_site_ids  uuid[]
) returns void
language plpgsql
security definer set search_path = public
as $$
declare
  v_org      uuid := current_org();
  v_target   profiles%rowtype;
  v_admins   int;
  v_unknown  text;
  v_over     text;
begin
  if not has_perm('users.manage') then
    raise exception 'غير مصرح لك بإدارة المستخدمين';
  end if;

  select * into v_target from profiles where user_id = p_user_id for update;
  if v_target.user_id is null or v_target.org_id is distinct from v_org then
    raise exception 'المستخدم غير موجود';
  end if;

  if trim(coalesce(p_full_name, '')) = '' then
    raise exception 'الاسم مطلوب';
  end if;

  select string_agg(k, ', ') into v_unknown
  from unnest(coalesce(p_perms, '{}')) k
  where k not in (select key from permission_keys);
  if v_unknown is not null then
    raise exception 'صلاحيات غير معروفة: %', v_unknown;
  end if;

  -- No climbing: only an admin may create or edit an admin, and a delegate can
  -- never grant more than they hold themselves.
  if not is_admin() then
    if p_role = 'admin' or v_target.role = 'admin' then
      raise exception 'إنشاء أو تعديل مدير نظام متاح لمدير النظام فقط';
    end if;
    select string_agg(k, ', ') into v_over
    from unnest(coalesce(p_perms, '{}')) k
    where not has_perm(k);
    if v_over is not null then
      raise exception 'لا يمكنك منح صلاحيات لا تملكها: %', v_over;
    end if;
  end if;

  if p_user_id = auth.uid()
     and (p_role is distinct from v_target.role or p_active is distinct from v_target.active)
  then
    raise exception 'لا يمكنك تغيير دورك أو تعطيل حسابك بنفسك';
  end if;

  if (v_target.role = 'admin' and v_target.active)
     and (p_role <> 'admin' or not p_active)
  then
    select count(*) into v_admins
      from profiles
     where org_id = v_org and role = 'admin' and active and user_id <> p_user_id;
    if v_admins = 0 then
      raise exception 'لا يمكن إزالة آخر مدير نظام في المنشأة';
    end if;
  end if;

  if not p_all_sites and coalesce(array_length(p_site_ids, 1), 0) = 0 then
    raise exception 'اختر فرعًا واحدًا على الأقل أو فعّل «كل الفروع»';
  end if;

  update profiles
     set full_name = trim(p_full_name),
         role      = case when p_user_id = auth.uid() then v_target.role   else p_role   end,
         active    = case when p_user_id = auth.uid() then v_target.active else p_active end,
         all_sites = p_all_sites
   where user_id = p_user_id;

  delete from user_permissions where user_id = p_user_id;
  insert into user_permissions (org_id, user_id, perm)
  select v_org, p_user_id, k from unnest(coalesce(p_perms, '{}')) k
  on conflict (user_id, perm) do nothing;

  delete from user_sites where user_id = p_user_id;
  if not p_all_sites then
    insert into user_sites (org_id, user_id, site_id)
    select v_org, p_user_id, s from unnest(p_site_ids) s
    on conflict (user_id, site_id) do nothing;
  end if;
end;
$$;

drop function if exists user_access_escalation_guard();

revoke execute on function admin_set_user_access(uuid, text, app_role, boolean, text[], boolean, uuid[])
  from public, anon;
grant execute on function admin_set_user_access(uuid, text, app_role, boolean, text[], boolean, uuid[])
  to authenticated;

-- ===========================================================================
-- 4. POST-FLIGHT
-- ===========================================================================
do $$
declare bad text;
begin
  -- 0025's rule still holds: every tenant policy must be org-scoped.
  select string_agg(tablename || '.' || policyname, ', ') into bad
  from pg_policies
  where schemaname = 'public'
    and tablename <> 'permission_keys'
    and coalesce(qual, '') || coalesce(with_check, '') not like '%current_org()%';
  if bad is not null then
    raise exception 'ABORT: policies without org scoping: %', bad;
  end if;

  -- Nothing in the app should still be deciding access by role. is_admin() is
  -- allowed to survive inside has_perm() and the escalation guard, and on the
  -- profiles policies, which stay admin-only on purpose.
  select string_agg(tablename || '.' || policyname, ', ') into bad
  from pg_policies
  where schemaname = 'public'
    and tablename not in ('profiles')
    and coalesce(qual, '') || coalesce(with_check, '') like '%is_manager_or_admin()%';
  if bad is not null then
    raise exception 'ABORT: policies still gated on the manager role: %', bad;
  end if;
end $$;
