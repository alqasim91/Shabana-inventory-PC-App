# 01 — Web app snapshot and gap analysis (baseline for the native rewrite)

Produced 2026-10-18. Documentation only: nothing in either repo was modified or committed
other than adding this file and `02-FUNCTIONAL-SPEC.md` under `native-conversion/`.

---

## 1. The baseline (state this hash in everything downstream)

| | |
|---|---|
| Repo | `/home/user/Shabana-Inventory` (origin `https://github.com/alqasim91/Shabana-Inventory`) |
| **Baseline commit** | **`39dc229b82b7344b958cee116e6c986b6e65ff5f`** (short `39dc229`) |
| Date | 2026-09-05 17:35:58 +0300 |
| Message | `fix: the neutral login door no longer names a business` |
| Branch | local checkout is on `claude/great-johnson-bkoedw`; `main` points at the same commit; working tree clean |
| History size | 36 commits, 2026-07-21 (`bef9ab0` initial) to 2026-09-05 |
| Size at baseline | `src/` = 106 files / 17,565 lines TS+TSX; `supabase/migrations/` = 43 files / 8,695 lines SQL (numbers 0001..0044, **0038 intentionally absent**); 2 Edge Functions; 19 page files; 19 service files |

**The native rewrite must treat `39dc229` as the functional baseline.** The functional spec
(`02-FUNCTIONAL-SPEC.md`) was derived from the code at exactly this hash. If the web app moves
on, diff against this hash and amend the spec; do not re-derive from scratch.

The PC repo (`/home/user/Shabana-inventory-PC-App`) is at `1b45e56` (2026-10-18, "Arabic labels
exported to JSON for the native rewrite"; before that `eb4eb28` on 2026-08-16). Its vendored
frontend was last synced to cloud **`2c2c92a`** (2026-08-11, commit `5739f77`
"sync: bring the PC edition up to cloud 2c2c92a"). The web app has moved **8 commits / 6,190 inserted
lines in 47 files** since that sync (`git diff --shortstat 2c2c92a HEAD`).

A verified fact you should know before relying on the migrations as a source of truth (found while
building the whole 0001..0044 chain in a scratch Postgres 16 during this analysis):
**migration `0032_permissions_enforce.sql` re-introduced a call to the dropped two-argument
`next_doc_number('so', y)` inside `so_status_transition()`**, so on any database built from the
migration files, invoicing a sales order (draft to invoiced) fails with
`function next_doc_number(unknown, integer) does not exist`. Production evidently has a working
version (the app has kept shipping), but the repo, and therefore every fresh PC install, does not.
The internal `invoice_number` / `order_code` strings it generates are never shown in the UI (the
UI shows `order_seq`), so the native rewrite can drop them or regenerate them freely. See spec section 12.

---

## 2. What changed recently: the last 20 commits, grouped by feature

`git -C /home/user/Shabana-Inventory log` (newest first). The first 15 are the ones summarised
in the brief; the last five are context (multi-tenancy and discounts, which everything after builds on).

### A. Expenses (مصروفات أخرى) — newest feature
- **`cc5c0cf` 2026-09-05** `feat: مصروفات أخرى — categorised expenses with due-list recurring rules` (22 files, +3,454).
  Migrations `0041` (adds `'expense'` to `cash_source`, alone in its file), `0042` (tables
  `expense_categories`, `expense_rules`, `expenses`, `expense_rule_skips`; functions
  `expense_due_list`, `get_expense_summary`, `void_expense`; four permission keys), `0043`
  (branch-scopes the skips policies), `0044` (branch-scopes `get_expense_summary` for non-accountants).
  UI: `/expenses` page (list / recurring / categories tabs), five modals, header bell with due count,
  due banner on the dashboard, audit sentences, expense lines on الملخص المالي. Key design: recurring
  rules create **dues, never automatic deductions**; corrections are voids with a compensating cash row.

### B. Accountant role and الملخص المالي
- **`85b0586` 2026-09-02** `feat: المحاسب — a fourth role that sees totals and never a row`
  (18 files, +1,338). Migrations `0037` (enum value, alone) and `0040` (adds `items.cost_price`,
  permission keys `dashboard.view` and `financials.view`, new presets, `get_financial_summary()`,
  and adds the missing permission clause to `payments_read`). UI: `/financials` page, route-level
  `RequirePerm` guards, `HomeRedirect` landing order, mobile nav fallback for accounts that hold
  only one key, `ItemFormModal` gets سعر التكلفة.
- **`b7b818f` 2026-09-05** `feat: الملخص المالي reports four figures, not eight` (presentation only).
- **`ee7c24f` 2026-09-05** `fix: say why an account could not be created` (Arabic reason per failure code on user creation).

### C. Opening balances (migration paths for a shop going live mid-life)
- **`34fd7f2` 2026-09-01** `feat: opening balances for clients and vendors (رصيد افتتاحي)` — migration `0039`
  (`contact_opening_balances`, `get_contact_opening`, `set_contact_opening_balance`, `settle_contact_opening`;
  `get_contact_balance` extended), `OpeningBalanceCard` on the contact page, ledger shows opening lines.
- **`2104c6d` 2026-08-11** `feat: admin sets opening stock (رصيد افتتاحي) for migration` — migrations `0034`, `0035`
  (`set_opening_stock`), `OpeningStockModal`.
- **`2c2c92a` 2026-08-11** `fix: opening balances silently refused to save` and
  **`f71ccfd`** `fix: Arabic numerals in the opening-balance counts` (polish on the above).

### D. Per-user permissions (صلاحيات) — the model everything is now gated on
- **`d4e9f4b` 2026-08-06** `feat: per-user صلاحيات — admin-managed access, enforced in the database`
  (27 files, +1,882/−231). Migrations `0031` (catalog `permission_keys`, `user_permissions`, `user_sites`,
  `profiles.all_sites`, `has_perm()`, `can_use_site()`, `admin_set_user_access()`) and `0032` (59 policies,
  triggers and RPC gates switched from role to permission). `RoleGate` deleted, replaced by `PermGate`;
  `UserAccessModal`. (`0032` carries the `next_doc_number` regression described above.)

### E. Locale and presentation
- **`753d132` 2026-08-10** `feat: Arabic numerals, per-business currency and timezone` — migration `0033`
  (`organization.currency`, `organization.timezone`), `lib/locale.ts`, `lib/digits.ts`, `NumberInput`
  (accepts Arabic-Indic digits), `OrgIdentityCard` gets currency + timezone.
- **`83213b4` 2026-08-06** `feat: show branch stock while writing a sales order` (live available qty per line).
- **`2d83c63` 2026-08-11** `fix: sidebar footer and two subtitles named one tenant's business`.
- **`39dc229` 2026-09-05** (baseline) `fix: the neutral login door no longer names a business`.

### F. Platform / multi-tenant plumbing (irrelevant to a single-shop native app, but present in the schema)
- **`7ce8401` 2026-08-20** `feat: delete_organization — the other half of provisioning` (migration `0036`, SQL only, no UI).
- **`4232895` 2026-09-01** `fix: a deploy no longer breaks every tab that was already open` (`lib/chunkReload.ts`,
  `lib/lazyPage.ts`: web-deploy hygiene, no native equivalent needed).

### Context (older, still part of the baseline)
- `36965de` 2026-07-31 discount on a sales order (migration `0030`)
- `9d6ab17` 2026-08-01 accept Egyptian landlines (`lib/phone.ts`)
- `7ceb23d` 2026-07-25 login page shows the client's business name (`0029`, `org_public_name`)
- `ea78db7` 2026-07-25 delete the storage object before its attachment row
- `32b3fdc` 2026-07-25 multi-tenant SaaS foundation (`0024`..`0028`)

Older history (all inside the baseline): vendor advances `0023`, paper-order attachments `0022`, itemized POs
`0019`..`0021`, order numbers `0017`, client credit wallet `0014`..`0016`, audit trail `0011`, sales lifecycle
hardening `0008`, global search and column filters, username login, business identity `0009`.

---

## 3. Gap analysis: web `39dc229` versus the PC repo's vendored copy

Method: `diff -rq Shabana-Inventory/src Shabana-inventory-PC-App/frontend/src`; `diff -rq` on the two
`migrations/` directories; `diff -rq` on Edge Functions; `diff` on `package.json`, `vite.config.ts`, `index.html`,
`tailwind.config.js`, `public/`. Counts: web `src/` 106 files / 17,565 lines; PC `frontend/src/` 94 files / 14,490 lines.

### 3.1 Migrations: web has 43 files; PC has 37 (35 vendored + 2 PC-only)

| Status | Files |
|---|---|
| Identical, present in both | `0001`..`0031`, `0033`..`0035` |
| **Differs (PC edited on purpose)** | `0032_permissions_enforce.sql`: guard changed to `if exists (select 1 from profiles) and not exists (select 1 from user_permissions)` (documented in `PC-DIVERGENCE.md`). Without it a fresh database aborts at 0032. The web repo still has the unguarded version. |
| **Missing in PC (web only)** | `0036_delete_organization.sql` (173 lines, platform tooling, not needed), `0037_app_role_accountant.sql` (16), `0039_contact_opening_balances.sql` (442), `0040_accountant_financials.sql` (611), `0041_cash_source_expense.sql` (18), `0042_expenses.sql` (875), `0043_expense_skip_site_scope.sql` (87), `0044_expense_summary_site_scope.sql` (184) — 2,406 lines of SQL |
| PC only | `0100_pc_local_auth.sql` (`auth.uid()` fix, `pc_needs_setup`, `pc_first_run_bootstrap`, `pc_create_user`), `0101_pc_local_storage.sql` (attachment bytes in `pc_file_bytes` via `pc_file_put/get/delete`) |
| Not a gap | **There is no `0038`.** It was deliberately skipped: `0040` is numbered 0040 because it reads the table `0039` creates. |

Ordering constraints that any migration runner (or any port) must preserve: `0037` and `0041` each add an
enum value and **must run in their own transaction before** `0040` / `0042` use it. All 44 files were applied
cleanly in order in this analysis (using the PC-edited `0032`), yielding **31 public tables, 79 RLS policies,
104 triggers, 38 permission keys**.

Verified end-state numbers (scratch DB, web 0001..0044): `role_preset_permissions` returns admin 38, manager 26,
staff 12, accountant 1.

### 3.2 Edge Functions
`supabase/functions/{admin-create-user,create-organization}` vs PC `functions-cloud-reference/`: `create-organization`
identical; **`admin-create-user/index.ts` differs** (PC reference copy predates the `accountant` role in its allowed-role list).
On PC both functions are replaced by SQL (`pc_create_user`, `pc_first_run_bootstrap`) and the reference copy is documentation only.
The same gap exists in PC's `pc_create_user` (`0100_pc_local_auth.sql:250` whitelists only `'admin','manager','staff'`), so even after merging the web migrations the PC edition could not create an accountant.

### 3.3 Frontend files only in the web app (the PC build lacks these features entirely)

| File | Feature |
|---|---|
| `src/pages/Expenses.tsx` (503) | مصروفات أخرى page |
| `src/pages/Financials.tsx` (379) | الملخص المالي page (accountant landing) |
| `src/components/expenses/{CategoryModal,DueExpensesList,DueExpensesPanel,ExpenseFormModal,ExpenseRuleModal,ReasonModal}.tsx` | expense dialogs, due list, skip/void reason dialog |
| `src/hooks/useDueExpenses.ts` | due-expense query shared by header bell, dashboard banner, expenses page |
| `src/services/expenses.ts` (218) | expense/category/rule/due/skip/void/summary API |
| `src/services/financials.ts` (21) | `get_financial_summary` wrapper |
| `src/services/contactOpening.ts` (74) | contact opening-balance API |
| `src/components/contacts/OpeningBalanceCard.tsx` (391) | opening receivable/payable/advance set + settle UI |
| `src/lib/chunkReload.ts`, `src/lib/lazyPage.ts` | web-deploy recovery (no native equivalent) |

### 3.4 Frontend files in both, but different
Split into (a) differences that are **PC adaptations** (must be reproduced or consciously replaced in the native app)
and (b) differences that are simply **PC being stale** (the web version is the target).

(a) PC adaptations (PC-only code, absent from web):
- `src/lib/supabase.ts`: API base = `window.location.origin` instead of `VITE_SUPABASE_URL`.
- `src/pages/FirstRunSetup.tsx` + `src/services/setup.ts` (PC only files): one-time setup screen (business name, admin full name, username, password + confirm, first فرع name). Labels `SETUP.*`, `AUTH.serviceUnreachable`.
- `src/pages/Login.tsx`: fresh-install detection (`pc-needs-setup` query, 5 s stale), redirect to `/setup`, "API unreachable" banner, redirect target `/dashboard`.
- `src/components/ProtectedRoute.tsx`: unauthenticated redirect to `/shabana/login` (fixed slug), `RequireRole` redirects to `/dashboard`.
- `src/App.tsx`: `/setup` route; plain `React.lazy`; always redirects `/` to `/dashboard`.
- `src/services/admin.ts`: `createUser()` calls RPC `pc_create_user` instead of the Edge Function.
- `src/services/attachments.ts` (141-line diff): bytes through `pc_file_put/get/delete`, previews as object URLs.
- `src/components/pc/PcBackupStatus.tsx` (PC only) + `PC_BACKUP.*` labels: reads `/pc/last-backup.json`, warns when failed or older than 2 days.

(b) Stale on PC (web `39dc229` is ahead; for reference, `git diff --stat 2c2c92a HEAD -- src` touches 37 files, +3,703/−122 lines, including the new files of 3.3):
`App.tsx` (no `RequirePerm`, no `HomeRedirect` landing order, no `PersistQueryClientProvider`, no `/expenses` `/financials`),
`components/contacts/CreditCard.tsx` and `VendorCreditCard.tsx` (1 line), `components/inventory/ItemFormModal.tsx` (cost price),
`components/layout/{BottomTabBar,Header,MoreSheet,navConfig}.tsx` (accountant-aware mobile nav, due-expenses bell, expenses and
financials nav items), `components/shared/ErrorBoundary.tsx` (update-available state), `components/users/UserAccessModal.tsx`
(accountant preset), `labels.ts` (323 differing lines: EXPENSES, FINANCIALS, CONTACT_OPENING, new PERM_LABEL entries, new user-creation error strings),
`main.tsx` (chunk reload), `pages/Audit.tsx` (expense sentences), `pages/ContactDetail.tsx` (OpeningBalanceCard),
`pages/Dashboard.tsx` (due banner), `pages/Inventory.tsx` (cost column; `minWidth` 930 vs 820), `pages/Users.tsx` (accountant role, error mapping),
`services/contacts.ts` (opening lines in the ledger), `services/inventory.ts` (cost price), `types/database.ts` (234 lines: expense, financial, opening types).

Everything else in `src/` (including `OpeningStockModal`, `useOrganization`, `lib/locale.ts`, `lib/digits.ts`, `NumberInput`, `OrgIdentityCard`,
`PermGate`, `access.ts`) is byte-identical, i.e. already at the `2c2c92a` level.

### 3.5 Other files
- `package.json`, `vite.config.ts`, `index.html`, `tailwind.config.js`, `public/icons/*`: identical.
- Web-only: `CLAUDE.md`, `SCHEMA.md`, `makhzoon-shabana-build-plan.md`, `multi-tenant-plan.md`, `vercel.json`, `ui-mockup/`, a design-handoff zip, `supabase/seed.sql`.
- PC-only: `installer/` (Inno Setup + PowerShell), `supabase/{pc-prelude.sql,platform-bootstrap.sql,PC-DIVERGENCE.md}`, `tests/ui-smoke` (Playwright), `.github/workflows/build-installer.yml`.

### 3.6 Feature-level gap summary (what a user of the PC edition cannot do today but a web user can)
1. Record or manage **expenses**, recurring-expense dues, categories, voids (whole module).
2. **المحاسب** role, **الملخص المالي** page, `dashboard.view` / `financials.view` / `expenses.*` permission keys (the PC permission editor shows only the 32 keys of 0031).
3. **Opening balances for clients/vendors** (`رصيد افتتاحي` on the contact card) and settlement of an opening receivable/payable.
4. **Item cost price** (`سعر التكلفة`) and inventory valuation. (Note: `0040` adds `items.cost_price`; on PC the column does not exist, so the cost-price UI cannot be merged without the migration.)
5. **Route-level permission guards** (typing `/contacts` as a user without `contacts.view` is bounced on web; on PC only RLS stops the data).
6. Cleaner login/landing logic, update-recovery.
7. `payments_read` row policy: on PC any member of the org can read every payment row (the missing permission clause was fixed in `0040`).

### 3.7 Sync hazards if the PC repo were to be re-synced (not the plan, but record them)
- Do not overwrite files the PC repo changed (`supabase.ts`, `Login.tsx`, `ProtectedRoute.tsx`, `App.tsx`, `admin.ts`, `attachments.ts`, `Dashboard.tsx`, `labels.ts`, migration `0032`).
- Re-apply the `0032` guard edit, the `0100`/`0101` numbering rule (PC-only migrations at `0100+`), and ensure `provision.ps1`/`migrate.ps1` apply `0037`/`0041` each as a separate psql run (they already invoke `psql -f` per file).
- The `0032` `next_doc_number` regression affects the PC installer too (invoicing); `tests/ui-smoke` does not exercise it.

---

## 4. PC-edition operational features that exist only in the PC repo (must be carried over or consciously dropped)

These are not part of the web app's functional behaviour but are part of the shipped PC product and the owner will expect them from a native Windows app.

| Feature | Where | Behaviour to preserve |
|---|---|---|
| Single machine, no internet needed | `BUILD_PLAN.md` | all data local; LAN clients via Caddy; optional public link |
| Install path | `installer/setup.iss` | `C:\ProgramData\Shabana\` (ASCII path on purpose; Windows usernames are often Arabic) |
| First-run setup | `FirstRunSetup.tsx`, `0100` | one-shot, collects business name, admin name/username/password, first فرع; locked to loopback; slug fixed `shabana` |
| Nightly backup | `installer/scripts/backup.ps1` | 03:00 daily scheduled task, `pg_dump` custom format `shabana-*.dump`, 14-day retention, status JSON for the dashboard badge; also one at install time (`bf7482f`) |
| Off-site encrypted backup | `setup-offsite.ps1`, `lib-crypto.ps1`, `decrypt-backup.ps1` (`68b460b`) | AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC, PBKDF2-SHA256 200k iterations, 32-byte salt, magic `SHBNAES1`), to a folder or an rclone remote, 30-day retention, passphrase shown once, decrypt tool has no install dependency |
| Restore | `restore.ps1` | lists dumps, confirms, `-Force` for automated rollback |
| Admin password reset | `reset-admin.ps1` | local-only (no email path exists) |
| Upgrades | `migrate.ps1` | `shabana_migrations` table, backup before, auto-restore on failure |
| Problem report | `export-report.ps1` | zip of logs + config filenames + recent DB errors to Desktop; never secrets |
| Public link (opt-in) | `setup-tunnel.ps1` (`5ff1afa`) | Tailscale Funnel, TLS terminates on the shop PC; setup and bootstrap RPC refused on the public site |
| Own window | `559c379` | app opens as an app window, not a browser tab |
| Backup badge | `PcBackupStatus.tsx` | "آخر نسخة احتياطية: …", amber if failed or older than 2 days |

Open items recorded by the PC repo itself (`OPUS-HANDOFF.md`, `BUILD_PLAN.md`): code signing and icon deferred; the whole chain had not been run end to end on Windows when written; `migrate.ps1` rollback never exercised on real data.

---

## 5. Docs in the web repo that are stale relative to the code at `39dc229`

(Details with citations are in spec section 12.) In short: `CLAUDE.md` still says three roles and "Phase 0 delivers the database only";
`SCHEMA.md`'s migration table stops at `0032` and its section 7b still describes `organization` as a singleton (superseded by `0024`);
`multi-tenant-plan.md` is marked "PLAN — not yet implemented" though it has been; `Users.tsx` still tells the admin the first
account must be created in the Supabase dashboard. `SCHEMA.md`'s "RLS role model" table predates `0032` and no longer describes
who can do what. **For the rewrite, code wins: use `02-FUNCTIONAL-SPEC.md`.**

Companion resource already committed by another pass: `native-conversion/resources/labels.ar.json` (full Arabic label export of `labels.ts`;
the spec cites label keys by their `labels.ts` export name and key).
