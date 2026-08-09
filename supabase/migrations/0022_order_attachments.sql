-- ============================================================================
-- 0022_order_attachments.sql — attach the vendor's paper order to a PO
-- ----------------------------------------------------------------------------
-- The staff receive a handwritten/printed order from the vendor and need it
-- filed against the digital PO. Files live in a PRIVATE Storage bucket; the
-- app never exposes a public URL, it mints a short-lived signed URL per view.
--
-- The table is deliberately generic (order_type purchase|sale) even though only
-- purchase orders get the UI today — adding sales-order attachments later is
-- then a UI change with no migration. There is no FK to purchase_orders/
-- sales_orders because the column is polymorphic; the delete trigger below
-- keeps rows from outliving their order.
-- ============================================================================

create type order_doc_type as enum ('purchase', 'sale');

create table order_attachments (
  id           uuid primary key default gen_random_uuid(),
  order_type   order_doc_type not null,
  order_id     uuid not null,
  -- Path inside the bucket: '<order_type>/<order_id>/<uuid>.<ext>'. Unique so a
  -- retried upload can never leave two rows pointing at one object.
  storage_path text not null unique,
  file_name    text not null,
  mime_type    text not null,
  byte_size    bigint not null check (byte_size > 0),
  created_by   uuid references auth.users(id) default auth.uid(),
  created_at   timestamptz not null default now()
);

create index order_attachments_order_idx on order_attachments (order_type, order_id, created_at);

alter table order_attachments enable row level security;

-- Everyone signed in can see what's filed (staff included — they already read
-- every order); only manager+ may attach or remove.
create policy order_attachments_read on order_attachments
  for select to authenticated using (true);
create policy order_attachments_insert on order_attachments
  for insert to authenticated with check (is_manager_or_admin());
create policy order_attachments_delete on order_attachments
  for delete to authenticated using (is_manager_or_admin());

-- Deleting a PO/SO must not strand its attachment rows. These are plain
-- documents, not ledger entries, so a real delete is correct here (the audit
-- trigger below still records that it happened).
create or replace function order_attachments_cascade()
returns trigger language plpgsql security definer set search_path = public as $$
begin
  delete from order_attachments
   where order_type = case tg_table_name when 'purchase_orders' then 'purchase'::order_doc_type
                                         else 'sale'::order_doc_type end
     and order_id = old.id;
  return old;
end $$;
revoke execute on function order_attachments_cascade() from public, anon, authenticated;

create trigger trg_po_attachments_cascade before delete on purchase_orders
  for each row execute function order_attachments_cascade();
create trigger trg_so_attachments_cascade before delete on sales_orders
  for each row execute function order_attachments_cascade();

-- Attaching and removing a source document is a business action worth tracing.
create trigger trg_audit_attachment_ins after insert on order_attachments
  for each row execute function audit_row();
create trigger trg_audit_attachment_del after delete on order_attachments
  for each row execute function audit_row();

-- ---------------------------------------------------------------------------
-- Storage: private bucket + policies mirroring the table's role rules.
-- 10 MB ceiling — the client downscales photos well below this, so hitting it
-- means someone attached a genuinely large scan/PDF, which is fine to reject.
-- ---------------------------------------------------------------------------
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values (
  'order-docs', 'order-docs', false, 10485760,
  array['image/jpeg', 'image/png', 'image/webp', 'image/heic', 'application/pdf']
)
on conflict (id) do nothing;

create policy order_docs_read on storage.objects
  for select to authenticated using (bucket_id = 'order-docs');
create policy order_docs_insert on storage.objects
  for insert to authenticated with check (bucket_id = 'order-docs' and public.is_manager_or_admin());
create policy order_docs_delete on storage.objects
  for delete to authenticated using (bucket_id = 'order-docs' and public.is_manager_or_admin());
