-- Append-only audit trail: who did what. A single generic trigger function
-- (audit_row) snapshots every significant mutation into audit_log with the
-- acting user (auth.uid() — resolved from the request JWT, works inside the
-- SECURITY DEFINER context too). Readable by manager+; written ONLY by the
-- definer trigger (no client insert/update/delete policies), so the trail
-- can't be forged or erased from the app.
create table audit_log (
  id         uuid primary key default gen_random_uuid(),
  actor      uuid references auth.users(id) on delete set null,
  action     text        not null,   -- insert | update | delete
  entity     text        not null,   -- source table name
  entity_id  text,                    -- best-effort row id (id / user_id)
  data       jsonb       not null,   -- full row snapshot (NEW, or OLD on delete)
  created_at timestamptz not null default now()
);
create index audit_log_created_at_idx on audit_log (created_at desc);
create index audit_log_actor_idx      on audit_log (actor);
create index audit_log_entity_idx     on audit_log (entity);

alter table audit_log enable row level security;
create policy audit_read on audit_log for select to authenticated using (is_manager_or_admin());
-- No INSERT/UPDATE/DELETE policies: only the SECURITY DEFINER trigger writes.

create or replace function audit_row()
  returns trigger language plpgsql security definer set search_path = public as $$
declare
  rec jsonb;
  eid text;
begin
  if (tg_op = 'DELETE') then rec := to_jsonb(old); else rec := to_jsonb(new); end if;
  eid := coalesce(rec->>'id', rec->>'user_id');
  insert into audit_log(actor, action, entity, entity_id, data)
  values (auth.uid(), lower(tg_op), tg_table_name, eid, rec);
  if (tg_op = 'DELETE') then return old; else return new; end if;
end $$;
revoke execute on function audit_row() from public, anon, authenticated;

-- Money -----------------------------------------------------------------
create trigger trg_audit_payments_ins after insert on payments
  for each row execute function audit_row();
create trigger trg_audit_payments_del after delete on payments
  for each row execute function audit_row();

-- Orders (creation + status transitions only — skip the noisy line-edit
-- recompute updates that fire while a draft is being built) ---------------
create trigger trg_audit_so_ins after insert on sales_orders
  for each row execute function audit_row();
create trigger trg_audit_so_upd after update on sales_orders
  for each row when (old.status is distinct from new.status) execute function audit_row();
create trigger trg_audit_po_ins after insert on purchase_orders
  for each row execute function audit_row();
create trigger trg_audit_po_upd after update on purchase_orders
  for each row when (old.status is distinct from new.status) execute function audit_row();

-- Inventory movements that are human decisions (conversions, transfers,
-- manual adjustments) — derived sale/conversion movements are already
-- covered by their parent document's audit row ----------------------------
create trigger trg_audit_conv_ins after insert on po_conversions
  for each row execute function audit_row();
create trigger trg_audit_conv_del after delete on po_conversions
  for each row execute function audit_row();
create trigger trg_audit_transfer_ins after insert on stock_transfers
  for each row execute function audit_row();
create trigger trg_audit_stock_adj after insert on stock_movements
  for each row when (new.source_type = 'adjustment') execute function audit_row();
create trigger trg_audit_cash_manual after insert on cash_movements
  for each row when (new.source_type = 'manual') execute function audit_row();

-- Config & access control -------------------------------------------------
create trigger trg_audit_items    after insert or update or delete on items
  for each row execute function audit_row();
create trigger trg_audit_contacts after insert or update or delete on contacts
  for each row execute function audit_row();
create trigger trg_audit_profiles after insert or update or delete on profiles
  for each row execute function audit_row();
create trigger trg_audit_sites    after insert or update on sites
  for each row execute function audit_row();
create trigger trg_audit_org      after update on organization
  for each row execute function audit_row();
