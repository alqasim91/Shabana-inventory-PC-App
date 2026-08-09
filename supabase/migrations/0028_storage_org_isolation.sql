-- ============================================================================
-- 0028_storage_org_isolation.sql — close the order-docs bucket
-- ----------------------------------------------------------------------------
-- Part 5 of 5 (see multi-tenant-plan.md).
--
-- THE LEAK. 0022 shipped this policy:
--
--   create policy order_docs_read on storage.objects
--     for select to authenticated using (bucket_id = 'order-docs');
--
-- `authenticated` is not "someone with Supabase access" — it is the role every
-- person who logs into the app receives. With one business that is correct and
-- intended (staff already read every order, so they may read its paper copy).
-- The moment a second business exists the SAME policy silently means "every
-- user of every client", and vendor invoices and scanned paper orders become
-- readable across tenants. The policy never changed; its blast radius did.
--
-- ----------------------------------------------------------------------------
-- APPROACH — no object migration
-- ----------------------------------------------------------------------------
-- The obvious fix is to re-prefix every path with the org id, but that means
-- physically moving objects in storage: storage.objects.name is the S3 key, so
-- rewriting it in SQL would leave rows pointing at objects that are not there.
--
-- Instead, ownership is resolved through order_attachments, which is already
-- org-scoped by 0025 and whose storage_path equals storage.objects.name exactly
-- (verified against production: 1 object, 1 row, matched). So:
--
--   SELECT / DELETE → the row must exist AND belong to your org
--   INSERT          → the path must start with your org id
--
-- Reads are checked against the table because the row is the source of truth
-- and this works for objects uploaded before this migration. Inserts cannot use
-- that check — the app uploads the file BEFORE inserting the attachment row, so
-- at insert time no row exists yet — hence the path-prefix rule for new
-- uploads. Existing objects keep their old paths and stay reachable via their
-- row; nothing has to be moved.
--
-- This is strictly tighter than a prefix rule alone: an orphaned object with no
-- attachment row becomes readable by nobody, which is the correct outcome.
--
-- FRONTEND COUPLING: src/services/attachments.ts must prefix new upload paths
-- with `<org_id>/`. Until it does, uploads fail the INSERT policy. Ship both
-- together.
-- ============================================================================

drop policy if exists order_docs_read   on storage.objects;
drop policy if exists order_docs_insert on storage.objects;
drop policy if exists order_docs_delete on storage.objects;

-- Read: only files whose attachment row belongs to your organization.
create policy order_docs_read on storage.objects
for select to authenticated
using (
  bucket_id = 'order-docs'
  and exists (
    select 1 from public.order_attachments a
     where a.storage_path = storage.objects.name
       and a.org_id = public.current_org()
  )
);

-- Insert: manager+ as before, and the path must be inside your org's folder.
-- storage.foldername() returns the path segments; [1] is the first folder.
create policy order_docs_insert on storage.objects
for insert to authenticated
with check (
  bucket_id = 'order-docs'
  and public.is_manager_or_admin()
  and (storage.foldername(name))[1] = public.current_org()::text
);

-- Delete: manager+ and the row must be yours.
create policy order_docs_delete on storage.objects
for delete to authenticated
using (
  bucket_id = 'order-docs'
  and public.is_manager_or_admin()
  and exists (
    select 1 from public.order_attachments a
     where a.storage_path = storage.objects.name
       and a.org_id = public.current_org()
  )
);

-- ---------------------------------------------------------------------------
-- Index supporting the policy lookup — it runs on every object read.
-- ---------------------------------------------------------------------------
create index if not exists order_attachments_path_idx
  on public.order_attachments (storage_path);
