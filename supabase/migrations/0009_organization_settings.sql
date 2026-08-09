-- Singleton table holding the business identity printed on invoices/statements
-- and shown in the sidebar. One row only (id boolean = true), admin-writable,
-- everyone reads. Replaces the hardcoded ORG_NAME / ORG_INFO constants.
create table organization (
  id            boolean     primary key default true,
  business_name text        not null,
  address_line  text,
  phone_line    text,
  updated_at    timestamptz not null default now(),
  constraint organization_singleton check (id)
);

alter table organization enable row level security;

create policy org_read   on organization for select to authenticated using (true);
create policy org_insert on organization for insert to authenticated with check (is_admin());
create policy org_update on organization for update to authenticated
  using (is_admin()) with check (is_admin());

-- Keep updated_at server-authoritative.
create or replace function organization_touch()
  returns trigger language plpgsql security definer set search_path = public as $$
begin
  new.updated_at := now();
  return new;
end $$;
revoke execute on function organization_touch() from public, anon, authenticated;

create trigger trg_organization_touch before update on organization
  for each row execute function organization_touch();

-- Seed the single row with the values previously hardcoded in labels.ts, so
-- existing invoices/statements keep printing identically until the admin edits.
insert into organization (id, business_name, address_line, phone_line) values
  (true,
   'شبانة لتجارة الألوميتال',
   'دمياط الجديدة، طريق دمياط الجديدة – كفر البطيخ',
   'هاتف: 057-2401180');
