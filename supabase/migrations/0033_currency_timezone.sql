-- ============================================================================
-- 0033_currency_timezone.sql — each business picks its own currency and timezone
-- ----------------------------------------------------------------------------
-- The app was Egypt-only: ج.م was hardcoded in the UI labels and every date was
-- rendered in whatever timezone the browser happened to be in. A shop in
-- الرياض or دبي needs its own currency on its invoices, and its own day
-- boundary — the dashboard is per-DAY (rule 10), so "today" being computed in
-- the wrong zone silently files evening sales under the wrong date.
--
-- Both are DISPLAY settings. No conversion, no rates, no per-transaction
-- currency: one business, one currency, and the stored NUMERIC amounts are
-- untouched. That is the whole feature, and it is worth saying plainly because
-- "multi-currency" usually means something far larger.
--
-- WHY THIS CURRENCY LIST AND NOT A LONGER ONE
-- Every money column in this schema is NUMERIC(12,2) — two decimal places. The
-- Gulf currencies KWD, BHD, OMR and JOD are *three*-decimal (1 dinar = 1000
-- fils), so storing them here would silently truncate the last digit of every
-- amount. They are deliberately absent rather than half-supported; adding them
-- means migrating every money column to NUMERIC(12,3) first, which is a
-- separate and much larger change. The list below is exactly the currencies
-- this schema can represent exactly.
-- ============================================================================

alter table organization
  add column if not exists currency text not null default 'EGP',
  add column if not exists timezone text not null default 'Africa/Cairo';

alter table organization
  drop constraint if exists org_currency_supported,
  drop constraint if exists org_timezone_valid;

-- Two-decimal currencies only — see the note above.
alter table organization
  add constraint org_currency_supported check (
    currency in ('EGP','SAR','AED','QAR','USD','EUR','TRY','MAD','LBP','ILS','DZD','SYP')
  );

-- Validated against the server's own tz database rather than a hardcoded list,
-- so a legitimate zone is never rejected because this migration didn't know it.
-- A trigger, not a CHECK: Postgres forbids subqueries in check constraints, and
-- pg_timezone_names is a view.
create or replace function organization_validate_timezone()
returns trigger language plpgsql
set search_path = public as $$
begin
  if not exists (select 1 from pg_timezone_names where name = new.timezone) then
    raise exception 'المنطقة الزمنية غير معروفة: %', new.timezone;
  end if;
  return new;
end;
$$;

drop trigger if exists trg_organization_timezone on organization;
create trigger trg_organization_timezone
  before insert or update of timezone on organization
  for each row execute function organization_validate_timezone();

comment on column organization.currency is
  'ISO 4217 code shown with every amount. Display only — no conversion, and all money stays NUMERIC(12,2), which is why 3-decimal currencies (KWD/BHD/OMR/JOD) are not offered.';
comment on column organization.timezone is
  'IANA zone deciding this business''s day boundary — the dashboard and cash drawer are per-day.';
