# 02 — Functional specification of مخزون شبانه (implementation-neutral)

Baseline: web app commit **`39dc229b82b7344b958cee116e6c986b6e65ff5f`** (2026-09-05). See `01-WEB-APP-SNAPSHOT.md`.

Purpose: capture everything the owner's new native Windows app (SQLite, no server) must do so that the rewrite loses nothing.
Nothing here prescribes a technology. Where the web app enforces a rule inside Postgres (RLS, trigger, RPC), the rule is
described as a **behaviour the new app must enforce itself** (there will be no RLS in SQLite).

## 1. How to read this document

- **Code wins.** The spec was derived from the source at the baseline hash. `CLAUDE.md`, `SCHEMA.md`, `makhzoon-shabana-build-plan.md`
  and `multi-tenant-plan.md` were read too; every place where they disagree with the code is marked **[DIV-n]** and collected in section 12.
- Citations are `path:line` relative to `/home/user/Shabana-Inventory` unless prefixed `PC:` (the PC repo).
  `labels.ts` = `src/labels.ts`. Label keys are written `GROUP.key` exactly as the `labels.ts` export name and property
  (e.g. `PURCHASES.colCode`). The full Arabic strings are in `src/labels.ts` and in
  `native-conversion/resources/labels.ar.json`; the most important ones are quoted inline.
- "Server-enforced" below means: enforced by a trigger, RLS policy, or RPC in `supabase/migrations/*.sql`. In the native app every such rule
  is the app's responsibility, ideally inside one transaction per user action.
- Multi-tenancy (`organization` rows, `org_id` on every table, `/platform` console, `delete_organization`, per-org slug in login emails) is
  **not** part of the native product (one shop, one database). Section 4.5 says exactly what to keep from it (the business identity record).

## 2. Domain model and invariants

### 2.1 Entities (tables at baseline; 31 public tables in the full chain)

Money columns are `NUMERIC(12,2)`; quantities `NUMERIC(12,3)`. Every row also carries `org_id` (drop), `created_at`, and usually `created_by` (keep: it feeds the audit trail and "بواسطة" labels).

| Entity | Columns that matter | Constraints / notes | Source |
|---|---|---|---|
| **sites** (فروع) | `name_ar`, `active` | never deleted; deactivated sites vanish from pickers but keep history | `0002_tables.sql:13` |
| **profiles** | `user_id`, `full_name`, `username` (unique), `role` (admin\|manager\|staff\|accountant), `active`, `all_sites` | `active=false` is **not enforced anywhere** [DIV-8] | `0002:23`, `0010`, `0031`, `0037` |
| **permission_keys** | `key`, `area`, `sort`, `note` | global catalog of 38 keys; read by the permission editor | `0031:40` |
| **user_permissions** | `(user_id, perm)` | one row per granted key; copied from a preset at user creation, then independent | `0031:104` |
| **user_sites** | `(user_id, site_id)` | used only when `profiles.all_sites=false`; empty set means **no** branch | `0031:122` |
| **organization** | `business_name`, `address_line`, `phone_line`, `currency` (12 codes), `timezone` (IANA), `slug` | one record = the business identity printed on invoices/statements/financials and shown in the sidebar | `0009`, `0024`, `0033` |
| **contacts** | `type` vendor\|client\|both, `name`, `address`, `notes` | name + primary phone mandatory (UI) | `0002:34` |
| **contact_phones** | `phone`, `is_primary` | exactly one primary per contact (partial unique index) | `0002:46` |
| **contact_payment_methods** | `method`, `instapay_number`, `bank_name`, `account_number` | shape check: instapay needs number; bank_transfer needs bank + account; cash allowed bare | `0002:58` |
| **items** | `name_ar`, `unit_type` kg\|unit, `low_stock_threshold` (12,3), `sale_price`, `cost_price`, `active` | `cost_price` display/valuation only, never feeds an order total | `0002:77`, `0040:49` |
| **purchase_orders** | `order_seq`, `order_code`, `po_type` general\|itemized, `vendor_id`, `order_date`, `total_kg`, `price_per_kg`, `total_amount`, `status` open\|fully_converted\|closed, `product_name`, `notes` | general: `total_kg`,`price_per_kg` NOT NULL; itemized: both NULL; `total_kg>0`. `status='closed'` is never written by any code path | `0002:90`, `0019:28` |
| **po_conversions** (general PO) | `po_id`, `site_id`, `item_id`, `kg_consumed`, `output_qty`, `output_unit`, `conversion_date` | each row = one `+stock` movement | `0002:109` |
| **po_lines** (itemized PO) | `po_id`, `item_id`, `qty`, `unit_price`, `line_total` (stored, user-drivable) | site-agnostic; `purchase_orders.total_amount = Σ line_total` | `0019`, `0020` |
| **po_line_conversions** | `po_line_id`, `site_id`, `qty`, `conversion_date`, denormalised `item_id`,`po_id` | receives part/all of a line into a branch; splittable | `0020`, `0021` |
| **sales_orders** | `order_seq`, `invoice_number`, `site_id`, `client_id`, `status` draft\|invoiced\|placed\|closed, `order_date`, `subtotal`, `discount_type` none\|amount\|percent, `discount_value`, `discount_amount`, `total_amount` | `total_amount = subtotal − discount_amount` (trigger) | `0002:128`, `0030` |
| **sales_order_lines** | `so_id`, `item_id`, `qty`, `unit_price`, `line_total = round(qty*unit_price,2)` | editable only while order is draft | `0002:144` |
| **payments** | `parent_type` po\|so, `parent_id` (polymorphic, no FK), `amount>0`, `method` cash\|instapay\|bank_transfer\|**credit**, `site_id` (drawer), `paid_at`, `note`, `contact_payment_method_id` | `credit` = internal method for applying a stored wallet; never user-selectable | `0002:159`, `0013`, `0016` |
| **stock_movements** | `site_id`, `item_id`, `qty_delta` (signed), `source_type` po_conversion\|sale\|adjustment\|transfer\|opening, `source_id`, `note` | **append-only** | `0002:177`, `0034` |
| **stock_transfers** | `from_site`,`to_site`,`item_id`,`qty`,`note` | writes two stock movements; `from≠to` | `0002:195` |
| **cash_movements** | `site_id`, `amount_delta` (signed), `source_type` payment\|manual\|expense, `source_id`, `reason` (NOT NULL) | **append-only** | `0002:211`, `0041` |
| **client_credits** | `contact_id`, `amount_delta`, `source_type` overpayment\|deposit\|applied\|refund\|adjustment\|opening, `source_id`, `method` (cash\|instapay), `site_id`, `occurred_on`, `note` | append-only; balance never < 0 | `0014`, `0039` |
| **vendor_credits** | same shape as client_credits | vendor advance (عربون) wallet | `0023`, `0039` |
| **contact_opening_balances** | `contact_id`, `kind` receivable\|payable, `amount_delta`, `as_of`, `note` | append-only | `0039` |
| **expense_categories** | `name_ar` (unique), `sort`, `active` | 8 seeded: وجبات 10, أجور 20, إيجار 30, كهرباء ومياه 40, صيانة 50, مواصلات 60, مصروفات نثرية 70, أخرى 99; archived never deleted | `0042:90` |
| **expense_rules** | `name_ar`, `category_id`, `site_id`, `amount>0`, `method`≠credit, `description`, `frequency` daily\|weekly\|monthly, `day_of_week` 1..7 ISO, `day_of_month` 1..31, `start_date`, `end_date`, `active` | frequency/day shape constraint | `0042:128` |
| **expenses** | `category_id`, `site_id`, `amount>0`, `method`≠credit, `expense_date`, `description`, `rule_id`, `due_date`, `voided_at/by`, `void_reason` | never edited, only voided; one live row per `(rule_id,due_date)` | `0042:169` |
| **expense_rule_skips** | `rule_id`, `due_date`, `reason` (NOT NULL) | append-only | `0042:303` |
| **order_attachments** | `order_type` purchase\|sale, `order_id`, `storage_path`, `file_name`, `mime_type`, `byte_size` | file bytes elsewhere; images + PDF, ≤10 MB | `0022` |
| **audit_log** | `actor`, `action` insert\|update\|delete, `entity` (table), `entity_id`, `data` (row JSON) | append-only, no write path from the app | `0011` |
| **doc_counters** | `(scope, year, last_value)` | gap-free counters; scopes `po`,`so` (yearly codes) and `po_seq`,`so_seq` (year 0, running numbers) | `0003`, `0024:298` |

### 2.2 Ledger principle (CLAUDE.md "Conventions", verified)
Stock on hand = `Σ stock_movements.qty_delta` per (site, item). Drawer balance = `Σ cash_movements.amount_delta` per site.
Client wallet = `Σ client_credits.amount_delta`; vendor wallet likewise; opening receivable/payable = `Σ contact_opening_balances`.
No balance is stored or editable. Corrections are new compensating rows. `stock_movements`, `cash_movements`, `client_credits`,
`vendor_credits`, `contact_opening_balances`, `expense_rule_skips`, `audit_log` reject UPDATE/DELETE
(`block_mutation()` → «هذا السجل للقراءة فقط ولا يمكن تعديله أو حذفه (سجل حركة)», `0004_triggers.sql:354`).
Balances "as of date D" are sums over rows created before the end of day D (`created_at < D+1`), so history is reproducible.

### 2.3 Hard floors (server-enforced; the new app must refuse the same)
- **No negative stock** per (site, item): any movement with `qty_delta < 0` is refused when `balance + qty_delta < −0.0005`
  → «لا يمكن تنفيذ الحركة: الرصيد الحالي للصنف في الفرع (X) لا يكفي» (`0008:263`). Applies to sales placement, transfers, adjustments, reversals.
- **No negative drawer** per site: any cash movement with `amount_delta < 0` is refused when `balance + delta < −0.005`
  → «رصيد الخزينة في الفرع (X) لا يكفي لهذه الحركة» (`0008:280`). Applies to cash payments to vendors, manual سحب/تسوية, client refunds, vendor advances, cash expenses, reversal of a collected cash payment.
- **No negative wallets**: «لا يمكن أن يصبح رصيد العميل سالبًا» (`0014:45`); vendor wallet analog (`0023:49`).
- Tolerances: quantities 0.0005, money 0.005.

### 2.4 Document numbering
Human-facing identifier = running number per type per business: **«أمر شراء N»** / **«أمر بيع N»** (`src/lib/orderNo.ts:1`),
`N` printed with Arabic-Indic digits. Assigned at insert (a sales order has its number while still a draft), gap-free (`next_org_seq`,
`0024:298`). Internal strings `order_code = PO-YYYY-####` (at insert) and `invoice_number = SO-YYYY-####` (at invoicing) exist but the UI never
shows them (only global search includes `invoiceNumber` in its haystack, `GlobalSearch.tsx`). **Defect:** the invoice-number assignment is broken on a database built from the migration files [DIV-1]; the native app can omit both strings or generate them without ill effect.

## 3. Formatting and locale rules

All user-visible text is Arabic, RTL. Locale `ar-EG` everywhere; currency EGP by default (`ج.م`).

| Rule | Definition | Source |
|---|---|---|
| Digits | Arabic-Indic ٠١٢٣٤٥٦٧٨٩ for every displayed number (via `toLocaleString('ar-EG')`), thousands separator `٬`, decimal mark `٫` | `MoneyDisplay.tsx:3` |
| `formatMoney(x)` | round to 2 dp (`Math.round(x*100)/100`); fraction digits: 0 if the rounded value is an integer, otherwise exactly 2; then a space and the currency symbol: `١٢٬٣٤٥٫٥٠ ج.م`, `٥٠٠ ج.م` | `MoneyDisplay.tsx:3-13` |
| `formatQty(x, unit?)` | integer → as is; else round to 3 dp; then optional space + unit label (`كجم` / `وحدة`) | `MoneyDisplay.tsx:16` |
| Percent | number in Arabic digits followed by `٪` (e.g. `١٠٪`) | `SalesOrderDetail.tsx`, `SalesOrderFormModal.tsx` |
| Signed amounts | `+` or `−` (U+2212) prefix then `formatMoney(abs)`; used in cash report, credit ledgers, audit | `Reports.tsx`, `CreditCard.tsx` |
| Business currency | one currency per business, display only: EGP ج.م, SAR ر.س, AED د.إ, QAR ر.ق, USD $, EUR €, TRY ₺, MAD د.م, LBP ل.ل, ILS ₪, DZD د.ج, SYP ل.س. No conversion; amounts stay 2-decimal, so 3-decimal dinars are deliberately excluded | `lib/locale.ts:27`, `0033` |
| Business timezone | IANA zone, default `Africa/Cairo`; list of 18 Arab-region zones | `lib/locale.ts:44` |
| Calendar dates | stored/passed as `YYYY-MM-DD`; short = day numeric + month short (`٥ سبتمبر`), long = day + month long + year (`٥ سبتمبر ٢٠٢٦`). A bare date is a calendar date and is **not** time-zone shifted; a real timestamp is rendered in the business timezone | `lib/date.ts:5-30` |
| "Today" | `todayISODate()` = current date **in the business timezone** (`Intl en-CA`), not the device's. Used for every date default, the dashboard default day, report ranges | `lib/date.ts:42` |
| Date picker | button showing the long Arabic date; native date input underneath | `ArabicDatePicker.tsx` |
| Numeric input | accepts Arabic-Indic and Persian digits; `٫` `,` `،` all mean the decimal point; `٬` and spaces dropped; a leading `-` allowed (needed for تسوية); only one dot; text field with decimal keypad | `lib/digits.ts`, `NumberInput.tsx` |
| Rounding | money 2 dp, quantities 3 dp; the database rounds half away from zero (`round(numeric,2)`); the web UI uses JS `Math.round` on doubles for derived pricing fields (see [DIV-12]). The native app should do all money maths in exact decimal / scaled integers | `PurchaseOrderFormModal.tsx:59` |
| Phone validation | normalise: strip spaces, `-`, `()`; `+20`/`0020`/`20` prefix → `0`. Mobile `^01[0125]\d{8}$`; landline `^0[2-9]\d{6,8}$`. Primary phone required and must pass; extra phones validated only when non-empty. InstaPay numbers are *not* validated in the contact form | `lib/phone.ts` |
| Username | `^[a-z0-9._-]+$`, lowercased/trimmed; business code (slug) `^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$` | `lib/username.ts:30` |
| Arabic search | both sides normalised: Arabic-Indic and Persian digits → ASCII; strip tashkeel/tatweel; أإآٱ→ا; ة→ه; ىئ→ي; ؤ→و; collapse whitespace; lowercase; query is split on spaces and **every term must appear** (order independent) | `lib/search.ts` |
| Sorting | Arabic collation (`localeCompare(…,'ar')`) for text; numeric for numbers | `DataTable.tsx` |
| ISO weekday | 1=Monday … 7=Sunday, labels الاثنين … الأحد (`WEEKDAY_LABEL`, `labels.ts:1244`) | `labels.ts` |
| Order number | `toLocaleString('ar-EG')` of the sequence after the noun | `lib/orderNo.ts` |
| Percent discount rounding | `round(subtotal * pct / 100, 2)` once, stored as `discount_amount`, so the printed invoice cannot drift from the ledger | `0030:112` |
| Toast | success/error/info, auto-dismiss 3.5 s | `Toast.tsx` |
| Audit timestamps | `toLocaleString('ar-EG',{day,month:'short',hour,minute})` using the **device** timezone (not the business one) | `Audit.tsx:106` |

## 4. Authentication, identity, first run

### 4.1 Web login (baseline)
- Two doors: `/:orgSlug/login` (a business's own link, shows `APP_NAME` and, below it, the business's public name looked up with the RPC `org_public_name(slug)`; falls back to `AUTH.loginSubtitle` «إدارة المخزون والمبيعات») and `/login` (generic, shows no subtitle and asks for `AUTH.orgCode` «كود المنشأة» prefilled from `localStorage['shabana:orgSlug']`). `Login.tsx:30-130`.
- Fields: [business code on `/login`], `AUTH.username` «اسم المستخدم», `AUTH.password` «كلمة المرور». The username is mapped to the auth email `username@<slug>.local` unless it already contains `@` (then used as typed, lowercased). `lib/username.ts:48`.
- Any failure shows one message `AUTH.invalidCredentials` «اسم المستخدم أو كلمة المرور غير صحيحة» (a wrong password and an unknown business are indistinguishable on purpose). Submit label toggles `AUTH.submit` «دخول» / `AUTH.submitting`.
- On success the slug is remembered on the device; the app routes to `/` which `HomeRedirect` resolves (section 5.5).
- Sign-out (`AUTH.logout` «تسجيل الخروج», in the header user menu) clears the cached query data (`localStorage['shabana:queryCache']`) because counter devices are shared. `AuthContext.tsx:107-115`.
- After login the app loads: the user's `profiles` row; their permission set (`user_permissions` rows, own rows always readable); their branch list (`user_sites` when `all_sites=false`). If any of these fail the permission set is **empty** (fail closed): no buttons, `NoLandingPage` («لا توجد صفحة متاحة لهذا الحساب»). `AuthContext.tsx:29-80`.
- **There is no UI to change your own password or reset another user's.** (Cloud: via Supabase dashboard; PC: `reset-admin.ps1`.) The native app will need its own recovery path [DIV-9].
- Session duration/refresh is whatever Supabase gives; nothing app-specific.

### 4.2 Account creation (المستخدمون → إضافة مستخدم)
Admin-only (route role gate **and** the server function). Fields: `USERS.fullName`, `USERS.username` (login handle), `USERS.password` (initial, shown in clear text, ≥ 6 chars), `USERS.role` (admin|manager|staff|accountant labels `ROLE_LABEL`).
Validation: username required and `^[a-z0-9._-]+$` (`USERS.usernameRequired`, `usernameInvalid`), password ≥ 6 (`passwordRequired`), name required.
Server rules (Edge Function `admin-create-user`, `supabase/functions/admin-create-user/index.ts`): caller must have `profiles.role='admin'` (not merely `users.manage`) and an org; email derived from the **caller's** business slug (client cannot choose); creates the auth account (email pre-confirmed), inserts the profile (`active=true`); if the profile insert fails the auth account is deleted. A trigger then seeds the new user's `user_permissions` from the role preset (`profile_seed_permissions`, `0031:306`).
Error codes mapped to Arabic: `email_exists` «اسم المستخدم مستخدم بالفعل», `forbidden`, `invalid_input`, `unauthorized`, `no_org`, `create_failed`, `profile_failed`, `server_error`, with raw detail appended after «التفاصيل: ».

### 4.3 Editing a user and their access (المستخدمون → row click → `UserAccessModal`)
One atomic save via RPC `admin_set_user_access` (`0032:525`). Editable: full name, role, active flag, the set of permission toggles, `all_sites` or a chosen branch list. Tools: role buttons apply a **preset** (copied, not linked; presets are fetched from `role_preset_permissions()`), "copy from another user", per-area select-all / clear-all, a preset-match label (`ACCESS.presetAdmin|Manager|Staff|Accountant|Custom`).
Rules enforced server-side:
1. caller needs `users.manage`; target must be in the same business and exist («المستخدم غير موجود»); name non-blank («الاسم مطلوب»);
2. unknown permission keys rejected («صلاحيات غير معروفة: …»);
3. a non-admin caller may not create or edit an admin («إنشاء أو تعديل مدير نظام متاح لمدير النظام فقط») and may not grant a permission they do not hold themselves («لا يمكنك منح صلاحيات لا تملكها»);
4. **you cannot change your own role or active flag** («لا يمكنك تغيير دورك أو تعطيل حسابك بنفسك»); your own role/active are silently kept;
5. the last active admin cannot be demoted or deactivated («لا يمكن إزالة آخر مدير نظام في المنشأة»);
6. `all_sites=false` with no branches is rejected («اختر فرعًا واحدًا على الأقل أو فعّل «كل الفروع»»);
7. permissions and branches are *replaced*, not merged. Saving an admin stores the full key list (admins bypass the rows anyway).
UI notes: when role = admin the toggle grid is replaced by `ACCESS.adminAllAccess`; self-edit shows `ACCESS.selfWarning`; `user.active` toggle disabled for self.

### 4.4 First run (PC edition, `PC:frontend/src/pages/FirstRunSetup.tsx`, `PC:0100_pc_local_auth.sql`)
Fresh install = "no profile exists" (`pc_needs_setup`). One-time screen `SETUP.*`: business name, admin full name, admin username (`^[a-z0-9._-]+$`), password ≥ 6 + confirmation, first فرع name (default «الفرع الرئيسي»). `pc_first_run_bootstrap` is strictly one-shot (refuses once any organization is claimed; advisory lock against double submit), creates organization (slug fixed `shabana`), admin account, first site, counters. Reachable from the loopback only. The native app needs the same first-run claim and nothing to log in to before it.

### 4.5 What to keep from `organization`
A single "business identity" record: `business_name` (required; default «شبانة لتجارة الألوميتال»), `address_line` (default «دمياط الجديدة، طريق دمياط الجديدة – كفر البطيخ»), `phone_line` (default «هاتف: 057-2401180»), `currency`, `timezone` (defaults EGP / Africa/Cairo). The defaults are used when the record is missing or still loading (`useOrganization.ts`). Edited in الإعدادات → `ORG_SETTINGS` by `settings.manage` (admin). Printed on invoices, statements, الملخص المالي; shown under the app name in the sidebar. `timezone` also decides the business day (see section 9.1).

## 5. Roles and permissions

### 5.1 Model (what is real in the code)
- `profiles.role` is one of four values with Arabic labels `ROLE_LABEL`: **admin = «مدير عام»**, **manager = «مدير مخازن»**, **staff = «موظف»**, **accountant = «محاسب»** (`labels.ts:51`).
  (The permission editor's preset chips use shorter names: «مدير النظام», «مدير», «موظف», «محاسب» — `ACCESS.preset*`.)
- Since migrations `0031`/`0032` **what a user may do is decided by the 38 permission keys they hold, not by the role.** The role is only (a) the preset the account was seeded from,
  (b) the recovery path: `has_perm()` returns true for every key when the caller's role is admin, so an admin can never be locked out (`0031:251`, `AuthContext.tsx:119`), and (c)
  a handful of hard role checks listed in 5.4.
- Presets are **copied** at creation (trigger on profile insert) or when an admin clicks a role chip in the editor; editing a preset later changes nobody. Presets live in the SQL function `role_preset_permissions(role)` (final definition `0042_expenses.sql:390`).
- Second dimension: **branch scope**. `profiles.all_sites=true` (default) or an explicit set in `user_sites`. `can_use_site(site)` is true for admins, for `all_sites` users, for a NULL site (not branch-specific, e.g. non-cash PO payments) and for listed sites (`0031:266`). A branch-restricted user sees only their branches in the site switcher (`sites_read` filters), and the "كل الفروع" aggregate is the sum **over the branches they may see**.

### 5.2 The 38 permission keys, presets and enforcement

Legend: A admin (always everything) · M manager preset (25 keys in native; web had 26) · S staff preset (12) · C accountant preset (1). ✔ = in preset. Labels are `PERM_LABEL` (`labels.ts:791`), area names `PERM_AREA` (`labels.ts:778`); editor groups by area ordered by `sort`.

| Key (area) | Arabic label | M | S | C | Enforced in the database (policy / trigger / RPC) | UI guard in the web app |
|---|---|:-:|:-:|:-:|---|---|
| `dashboard.view` (reports) | لوحة التحكم | ✔ | ✔ | – | none (route only; the data RPC is also RLS-limited by the cash/stock keys) | route `/dashboard`; nav; `App.tsx:133` |
| `sales.view` | عرض أوامر البيع | ✔ | ✔ | – | `so_read` (+branch), `sol_read`, `payments_read` (SO payments), attachment read | route `/sales*`; nav |
| `sales.draft` | إنشاء وتعديل مسودات البيع | ✔ | ✔ | – | `so_insert` (must be `status='draft'`, +branch), `sol_write/update/delete` (only while parent is draft), coarse `so_update`, trigger `so_write_guard` for header edits of a draft | «إضافة أمر بيع» `Sales.tsx:127`; «تعديل المسودة» `SalesOrderDetail.tsx` |
| `sales.discount` | عمل خصم على أمر البيع | ✔ | ✔ | – | trigger `so_apply_discount`: needed to *set or change* a non-`none` discount («غير مصرح لك بعمل خصم على أوامر البيع») `0032:415` | **no guard**: the discount tabs show for everybody; failure arrives as a database error |
| `sales.invoice` | فوترة أوامر البيع | ✔ | – | – | trigger `so_status_transition` (draft→invoiced) «غير مصرح لك بفوترة أوامر البيع» `0032:333` | «فوترة» button |
| `sales.place` | تنفيذ الأمر وخصم المخزون | ✔ | – | – | same trigger (invoiced→placed) «غير مصرح لك بتنفيذ أوامر البيع» | «تنفيذ» button |
| `sales.cancel_placement` | إلغاء التنفيذ وإرجاع المخزون | ✔ | – | – | same trigger (placed→invoiced) «غير مصرح لك بإلغاء تنفيذ أوامر البيع» | «إلغاء التنفيذ» button |
| `sales.edit_locked` | تعديل أمر بيع بعد فوترته | – | – | – | `so_write_guard` (header edit when status≠draft) «غير مصرح لك بتعديل أمر بيع بعد فوترته»; RPC `admin_update_sales_order` gate | «تعديل» button on non-draft orders |
| `sales.delete` | حذف أمر بيع | – | – | – | `so_delete` policy | **no UI** (service `deleteSalesOrder` exists, unused) — see [DIV-6] |
| `purchases.view` | عرض أوامر الشراء | ✔ | ✔ | – | `po_read`, `po_lines_read`, `conv_read` (+branch), `plc_read`, attachment read, `payments_read` (PO payments) | route `/purchases*`; nav |
| `purchases.create` | إنشاء أمر شراء | ✔ | – | – | `po_write` insert; RPC `create_itemized_po` gate | «إضافة أمر شراء» `Purchases.tsx:158` |
| `purchases.edit` | تعديل أمر شراء | **–** (admin only, L-22) | – | – | `po_update`; RPCs `admin_update_purchase_order`, `update_itemized_po` gates | «تعديل» `PurchaseOrderDetail.tsx:116` — web: managers held it by preset [DIV-4]; **native: admin only, deliberate fix (L-22)** |
| `purchases.convert` | التحويل إلى المخزون | ✔ | – | – | `conv_write` (+branch), `plc_write` (**no branch check**) | conversion buttons `:178`, `:256` |
| `purchases.delete` | حذف أمر شراء أو عكس تحويل | – | – | – | `po_delete`, `conv_delete` (+branch), `plc_delete` | reverse-conversion icons `:233`, `:286`; PO delete has no UI |
| `inventory.view` | عرض الأصناف والأرصدة | ✔ | ✔ | – | `items_read`, `sm_read` (+branch), `transfer_read` (either end) | route `/inventory*`; nav |
| `inventory.items` | إضافة وتعديل الأصناف والأسعار | ✔ | – | – | `items_write`, `items_update` | «إضافة صنف» + «نقل بين الفروع» header buttons `Inventory.tsx:158`; «تسوية»+«تعديل» `ItemDetail.tsx:77` |
| `inventory.adjust` | تسوية المخزون | ✔ | – | – | `sm_write`: insert only with `source_type='adjustment'` (+branch) | **UI uses `inventory.items`** to show «تسوية» [DIV-5] |
| `inventory.transfer` | النقل بين الفروع | ✔ | – | – | `transfer_write`: needs `can_use_site` for BOTH ends | per-row transfer icon `Inventory.tsx:120`; header button uses `inventory.items` [DIV-5] |
| `inventory.item_delete` | حذف صنف | – | – | – | `items_delete` | no UI |
| `payments.record` | تسجيل دفعة | ✔ | ✔ | – | `payments_write` (+branch of `site_id`); RPC `settle_contact_opening` gate | add-payment form is **not** gated in the UI [DIV-7]; settle button (but card admin-only) |
| `payments.credit` | استخدام أو رد رصيد العميل/المورد | ✔ | – | – | RPCs `credit_apply`, `credit_refund`, `vendor_credit_apply`, `vendor_credit_refund` | «استخدام رصيد العميل» and refund buttons |
| `payments.delete` | حذف دفعة | – | – | – | `payments_delete` (+branch) | trash icon `PaymentsPanel.tsx:302` |
| `cash.view` | عرض رصيد الخزينة وحركاتها | ✔ | ✔ | – | `cm_read` (+branch); also silently zeroes dashboard cash figures if absent | none |
| `cash.manual` | إيداع / سحب / تسوية بالخزينة | – | – | – | `cm_write`: insert only with `source_type='manual'` (+branch) | «+ حركة يدوية» `Reports.tsx:250` |
| `contacts.view` | عرض جهات الاتصال وكشوف الحساب | ✔ | ✔ | – | `contacts_read`, `phones_read`, `pm_read`, `client_credits_read`, `vendor_credits_read`, `contact_opening_balances_read` | route `/contacts*`; nav |
| `contacts.manage` | إضافة وتعديل جهات الاتصال | ✔ | – | – | insert/update on contacts, phones, payment methods (+ phone/method deletes) | add/edit buttons |
| `contacts.delete` | حذف جهة اتصال | – | – | – | `contacts_delete` | no UI |
| `attachments.manage` | إرفاق وحذف مستندات الأوامر | ✔ | – | – | `order_attachments_insert/delete` + storage policies | camera / file / delete controls |
| `expenses.view` | عرض المصروفات والفئات | ✔ | ✔ | **never** | read policies on categories, rules, expenses, skips (rules/expenses/skips +branch); RPCs `expense_due_list`, `get_expense_summary` (branch-confined) | route `/expenses`; nav; header bell |
| `expenses.record` | تسجيل مصروف وتسوية المستحق | ✔ | ✔ | – | `expenses_write` (+branch), `expense_rule_skips_write` | add button; due «تسجيل/تخطي» |
| `expenses.manage` | إدارة الفئات والمصروفات المتكررة | ✔ | – | – | categories/rules insert, update (rules also delete) (+branch for rules) | add/edit rule & category |
| `expenses.void` | إلغاء مصروف مُسجَّل | – | – | – | RPC `void_expense` | «إلغاء المصروف» per row |
| `reports.view` | فتح صفحة التقارير | ✔ | ✔ | – | none — the report data is the other keys' RLS | route `/reports`; nav |
| `audit.view` | قراءة سجل التدقيق | ✔ | – | – | `audit_read` | nav item uses it, but the **route** is role-gated admin\|manager [DIV-3] |
| `financials.view` | الملخص المالي | – | – | ✔ | RPC `get_financial_summary` gate; also lets `get_expense_summary` run **org-wide** (0044) | route `/financials`; nav |
| `users.manage` | إدارة المستخدمين والصلاحيات | – | – | – | `user_permissions_*`, `user_sites_*` policies; RPC `admin_set_user_access` | nav item uses it, but the **route** is role-gated admin only [DIV-2] |
| `sites.manage` | إدارة الفروع | – | – | – | `sites_admin_write/update/delete` | `/settings` route is admin-role only [DIV-2] |
| `settings.manage` | تعديل بيانات المنشأة | – | – | – | `org_update` | same |

Counts verified by building the web migrations: admin 38 · manager 26 (native: 25) · staff 12 · accountant 1.
**Manager preset = the ✔ in column M (25 in native; web had 26 because it also held `purchases.edit`, removed by L-22)** (everything except: `sales.edit_locked`, `sales.delete`, `purchases.delete`, `inventory.item_delete`, `payments.delete`, `cash.manual`, `contacts.delete`, `expenses.void`, `financials.view`, `users.manage`, `sites.manage`, `settings.manage`).
**Staff preset = 12:** `sales.view`, `sales.draft`, `sales.discount`, `purchases.view`, `inventory.view`, `payments.record`, `cash.view`, `contacts.view`, `expenses.view`, `expenses.record`, `dashboard.view`, `reports.view`.
**Accountant preset = 1:** `financials.view`. Isolation is "holds nothing else": no table is readable without a key; the accountant sees aggregate totals only. A completeness fix was needed for `payments_read` (it had no key check) — `0040:535`.

### 5.3 Role × action matrix (default presets; an admin may customise any non-admin per user)

| Action | admin | manager | staff | accountant | Where enforced |
|---|:-:|:-:|:-:|:-:|---|
| Open dashboard | ✔ | ✔ | ✔ | – | route + key `dashboard.view` |
| View sales orders | ✔ | ✔ | ✔ | – | `sales.view` |
| Create / edit **draft** sales order (incl. discount) | ✔ | ✔ | ✔ | – | `sales.draft` (+`sales.discount`) |
| Invoice (فوترة) | ✔ | ✔ | – | – | trigger |
| Place (تنفيذ, deducts stock) | ✔ | ✔ | – | – | trigger |
| Cancel placement (إرجاع المخزون) | ✔ | ✔ | – | – | trigger |
| Edit invoiced/placed/closed order | ✔ | – | – | – | `sales.edit_locked` |
| Delete sales order (DB-permitted, no UI) | ✔ | – | – | – | `sales.delete` |
| View purchase orders | ✔ | ✔ | ✔ | – | `purchases.view` |
| Create PO (general or itemized) | ✔ | ✔ | – | – | `purchases.create` |
| Edit PO (any stage) | ✔ | – (**admin only in native; web managers could: deliberate fix, L-22**) | – | – | `purchases.edit` |
| Convert PO into branch stock | ✔ | ✔ | – | – | `purchases.convert` |
| Reverse a conversion | ✔ | – | – | – | `purchases.delete` |
| View inventory / item movements | ✔ | ✔ | ✔ | – | `inventory.view` |
| Add / edit item, sale price, cost price | ✔ | ✔ | – | – | `inventory.items` |
| Stock adjustment (تسوية) | ✔ | ✔ | – | – | `inventory.adjust` |
| Transfer stock between branches | ✔ | ✔ | – | – | `inventory.transfer` |
| **Set opening stock (رصيد افتتاحي)** | ✔ | – | – | – | **role** admin (`set_opening_stock`: `is_admin()`) — deliberately not a key |
| Record a payment on any order | ✔ | ✔ | ✔ | – | `payments.record` |
| Apply / refund client credit, apply / refund vendor advance | ✔ | ✔ | – | – | `payments.credit` |
| Delete a payment (auto-reverses cash) | ✔ | – | – | – | `payments.delete` |
| View drawer balances / cash movements | ✔ | ✔ | ✔ | – | `cash.view` |
| Manual drawer إيداع / سحب / تسوية | ✔ | – | – | – | `cash.manual` |
| View contacts, balances, statements | ✔ | ✔ | ✔ | – | `contacts.view` |
| Add / edit contacts | ✔ | ✔ | – | – | `contacts.manage` |
| **Set a contact's opening balance** | ✔ | – | – | – | **role** admin (`set_contact_opening_balance`: `is_admin()`) |
| Settle an opening receivable/payable | ✔ (UI shows card only to admin) | key-holders have DB right but no UI | – | – | `payments.record` in RPC; UI admin only [DIV-10] |
| Attach / remove order documents | ✔ | ✔ | – | – | `attachments.manage` |
| View expenses, categories, rules, dues | ✔ | ✔ | ✔ | – (by design) | `expenses.view` |
| Record expense, post/skip a due | ✔ | ✔ | ✔ | – | `expenses.record` |
| Manage categories and recurring rules | ✔ | ✔ | – | – | `expenses.manage` |
| Void an expense | ✔ | – | – | – | `expenses.void` |
| Reports (sales, purchases, cash, balances) | ✔ | ✔ | ✔ | – | `reports.view` + underlying keys |
| الملخص المالي | ✔ | – | – | ✔ | `financials.view` |
| Audit log | ✔ | ✔ | – | – | route role + `audit.view` |
| Users & permissions | ✔ | – | – | – | route role + `users.manage` + role check in create function |
| Sites (فروع) and business identity | ✔ | – | – | – | route role + `sites.manage` / `settings.manage` |
| Platform console (create businesses) | n/a | n/a | n/a | n/a | `platform_admins` (not a role) — drop |

### 5.4 Hard role checks that are *not* permission keys
1. `set_opening_stock` — `is_admin()` (`0035:59`); UI button only if `profile.role==='admin'` (`Inventory.tsx:150`).
2. `set_contact_opening_balance` — `is_admin()` (`0039:128`); `OpeningBalanceCard` renders nothing for non-admins (`:115`).
3. Account creation — function checks `role='admin'` (`admin-create-user`; PC `pc_create_user`); routes `/users`, `/settings` use `RequireRole(['admin'])` (`App.tsx:175`); `/audit` uses `RequireRole(['admin','manager'])` (`App.tsx:171`).
4. `/platform` — `platform_admins` membership; the route is not role-gated.
5. Lockout rules in `admin_set_user_access` (4.3).
6. Admin short-circuit in `has_perm` and in the UI `can()`.

### 5.5 Route and landing rules
| Route | Component | Guard |
|---|---|---|
| `/login`, `/:orgSlug/login` | `Login` | public; signed-in visitors are redirected to `/` |
| `/` and any unknown path | `HomeRedirect` | first match in order **dashboard.view → `/dashboard`, financials.view → `/financials`, sales.view → `/sales`, inventory.view → `/inventory`, purchases.view → `/purchases`, contacts.view → `/contacts`, expenses.view → `/expenses`, reports.view → `/reports`**; nothing matches → `NoLandingPage` (never a redirect loop) `App.tsx:65-100` |
| `/dashboard` | `Dashboard` | key `dashboard.view` |
| `/purchases`, `/purchases/:id` | `Purchases`, `PurchaseOrderDetail` | `purchases.view` |
| `/inventory`, `/inventory/:id` | `Inventory`, `ItemDetail` | `inventory.view` |
| `/sales`, `/sales/:id`, `/sales/:id/invoice` | `Sales`, `SalesOrderDetail`, `SalesOrderInvoice` | `sales.view` |
| `/contacts`, `/contacts/:id`, `/contacts/:id/statement` | `Contacts`, `ContactDetail`, `ContactStatement` | `contacts.view` |
| `/expenses` | `Expenses` | `expenses.view` |
| `/reports` | `Reports` | `reports.view` |
| `/financials` | `Financials` | `financials.view` |
| `/audit` | `Audit` | role admin or manager |
| `/settings`, `/users` | `Settings`, `Users` | role admin |
| `/platform` | `Platform` | none in router; page asks `is_platform_admin()` |

Navigation entries use the same keys (`navConfig.tsx`): sidebar «القوائم الرئيسية» = لوحة التحكم, أوامر الشراء, المخزون, أوامر البيع, جهات الاتصال, مصروفات أخرى; «أخرى» = التقارير, الملخص المالي, سجل النشاط (`audit.view`), المستخدمون (`users.manage`), الإعدادات (`settings.manage`). An item is simply not listed when the user lacks its key.

### 5.6 Actions with no permission check at all (any signed-in member can call them)
`so_overpay`, `po_overpay`, `credit_deposit`, `vendor_credit_deposit` are SECURITY DEFINER with no `has_perm` call (migration `0032` re-gated only eight functions) — they write payments, cash and wallet rows on behalf of anyone. The intended policy per `SCHEMA.md` is "staff and up" (`payments.record`). **The native app should gate all four with `payments.record`** [DIV-11].
Also readable by every member: `organization` (own business identity), `permission_keys`, one's own `user_permissions`/`user_sites`.

## 6. Application shell and cross-cutting behaviour

**Layout.** RTL root. Desktop (≥ `lg`): right-hand sidebar 248 px (logo tile «ش», `APP_NAME`, business name under it, nav sections `NAV.mainSection` «القوائم الرئيسية» and `NAV.otherSection` «أخرى», footer «مخزون شبانه · الإصدار 0.1.0») + header + content. Mobile: header + content + bottom tab bar with up to 4 tabs and a `NAV.more` «المزيد» sheet. Default tabs: dashboard, inventory, sales, contacts (only those the user can open). If the user can open none of those four (e.g. accountant) the bar falls back to the first four pages they *can* open (`splitMobileNav`, `navConfig.tsx`); the sheet lists everything else reachable, without repeating tabs. `AppShell.tsx`, `Sidebar.tsx`, `BottomTabBar.tsx`, `MoreSheet.tsx`.

**Header** (`Header.tsx`):
1. Site switcher pill (current name or `SITE.allSites` «كل الفروع») → menu: «كل الفروع» then active branches the user may use. Selection persisted per device (`localStorage['shabana:selectedSite']`); if the stored branch no longer exists the app falls back to "all". Passing "all" to a query means site = NULL ("aggregate over every branch I may see").
2. Search button / `Ctrl+K`/`⌘K` → `GlobalSearch`.
3. Bell (only with `expenses.view`): badge = number of due recurring-expense occurrences; popover `EXPENSES.dueTitle` «مصروفات مستحقة» with `DueExpensesPanel` and button `EXPENSES.dueOpenPage`.
4. User menu: initials avatar, `full_name`, `ROLE_LABEL[role]`, action `AUTH.logout`.

**GlobalSearch** (`GlobalSearch.tsx`): one box over four groups — `SEARCH.groupPurchases` (haystack: PO label + vendor + product name), `groupSales` (SO label + client + invoice number), `groupItems` (name), `groupContacts` (name + primary phone). Max 5 hits per group with a «و N نتيجة أخرى» tail; `↑↓` navigate, `Enter` opens, `Esc` closes; Arabic-normalised AND-of-terms matching (section 3). Uses the same cached lists as the list pages (sales list respects the selected branch). Hit opens the detail route. Searches only data already readable by the user.

**DataTable** (all lists): optional per-column multi-select filter (options derived from the full unfiltered data; auto search box when > 8 options; mobile = bottom sheet), per-column sort toggle (ascending → descending → off), active-filter chips with × and «مسح كل الفلاتر», empty message vs `TABLE.noMatch` «لا توجد نتائج مطابقة للفلاتر», row click navigation, min-width scroll. All filtering/sorting is in memory over the complete list (no paging anywhere).

**Other shared pieces:** `Modal` (scrolling body, footer buttons), `ConfirmDialog` (danger style for reversals/deletes), `ReasonModal` (mandatory reason), `Toast`, `PageHeader` (title, subtitle, actions), `MoneyDisplay` (bold amount, tones default/positive green/negative amber), `SiteBadge`, `OfflineBanner` (`COMMON.offlineBanner` «أنت غير متصل بالإنترنت»), `ErrorBoundary` (`ERROR_BOUNDARY.*`, with a special "new version released" state), `PlaceholderPage` (unused).

**Printing.** Print outputs: sales invoice, contact statement, الملخص المالي (all via `react-to-print`, A4, business header block, signature lines), Reports and Audit (browser print of the page). Everything wrapped in `print:hidden` chrome (sidebar, header, tabs, filters). Print layout: business name (large, teal), address and phone lines, document title and metadata on the opposite side, body, signature boxes, tiny footer `APP_NAME`.

**Offline/caching (web only):** query cache persisted in localStorage for 24 h so read-only views render with no connection; writes need a live session. Not relevant to a local-database native app except as a reminder that the new app has no offline mode problem.

**Which screens follow the header's selected branch:** dashboard, sales list, inventory list, reports (sales, cash), expenses, الملخص المالي, due expenses (list + bell). Purchases are branch-agnostic. Ledger-based pickers (payment drawer, conversion destination) list the user's allowed branches.

## 7. Screens

For each route: purpose, who, data, fields/columns, filters, actions, validations, prints, label keys. "KPI" = summary card.

### 7.1 `/login`, `/:orgSlug/login` — `Login.tsx` (covered in 4.1)
Labels: `AUTH.*`, `APP_NAME`.

### 7.2 `/setup` (PC only) — first run (4.4). Labels `SETUP.*`.

### 7.3 `/dashboard` — لوحة التحكم (`Dashboard.tsx`, service `dashboard.ts`, RPC `get_dashboard`)
- Header: `DASHBOARD.title`, `DASHBOARD.subtitle`; right: pill with branch name or «كل الفروع», `ArabicDatePicker` (default: today in business timezone).
- Banner (when any recurring expense is due): `EXPENSES.dueBannerTitle(n)` + `dueBannerBody` + `DueExpensesPanel` (post/skip in place; refreshes dashboard, expenses, expense-summary).
- PC only: `PcBackupStatus` badge.
- KPI row: `openingCash` «رصيد أول اليوم» (hint `openingHint`) · `salesToday` «مبيعات اليوم» · `collectionsToday` «تحصيلات اليوم» · `closingCash` «رصيد الخزينة آخر اليوم».
- Panels: `topItemsTitle` «الأصناف الأكثر مبيعًا» (top 5 by quantity sold that day, horizontal bars, empty `noTopItems`); `collectionsTitle` «التحصيلات حسب الطريقة» (rows كاش/انستاباي/تحويل بنكي, only non-zero, plus `collectionsTotalTag`; empty `noCollections`); `payoutsTitle` «المدفوعات للموردين حسب الطريقة» (+ `payoutsTotalTag`; empty `noPayouts`); **all-branches view only:** `drawersTitle` «خزائن الفروع آخر اليوم» (one tile per active branch + `combinedTotal` «الإجمالي المجمّع»); `lowStockTitle` «تنبيه انخفاض المخزون» table (`lowStockCol` الصنف, `lowStockSite` الفرع, `lowStockQty` المتاح amber pill, `lowStockThreshold` الحد الأدنى; empty `noLowStock`).
- Exact figure definitions: section 9.1. Notable: **no stock opening/closing is shown** even though CLAUDE.md rule 10 promises it [DIV-13]; expenses are not in "المدفوعات للموردين" [DIV-14].

### 7.4 `/purchases` — أوامر الشراء (`Purchases.tsx`, service `purchases.ts`)
- Action: `+ PURCHASES.addPo` «إضافة أمر شراء» (key `purchases.create`) → `PurchaseOrderFormModal`; on save navigates to the new order.
- Columns (`DataTable`, min width 900): `colCode` (formatPoNo, sort by seq) · `colVendor` (filter, sort) · `colProduct` (itemized: badge «مصنّف» + «N صنف»; general: product name or —; filter groups itemized under the badge) · `colDate` (sort) · `colConversion` «التحويل إلى المخزون» (`done / total` + % + bar; general in كجم, itemized in units; filter `convNotStarted|convPartial|convDone` = «لم يبدأ|جزئي|مكتمل»; sort by %) · `colTotal` · `colPayStatus` (chip «مدفوع بالكامل» when remaining ≤ 0.005, else «X متبقي»; filter paid|partial; sort by remaining).
- Data: every PO newest first (`order_date` desc, `created_at` desc) with vendor name, converted kg (Σ `kg_consumed`), line counts/ordered/received, and `paid` = Σ **all** payments on it (including internally applied wallet credit).
- Empty: `PURCHASES.noPos`.

### 7.5 `/purchases/:id` — `PurchaseOrderDetail.tsx`
- Header: title `formatPoNo`, badges «مصنّف» (itemized), «مدفوع بالكامل» (`remaining ≤ 0.005 && total > 0`); `PURCHASES.edit` (key `purchases.edit`); vendor · date · product name; notes box (`PO_FORM.notes`).
- KPI cards: general: `totalKg` الكمية الكلية · `convertedKg` المحول للمخزون (+ progress bar) · `total` الإجمالي · `paidRemaining` المدفوع / المتبقي. Itemized: `linesTitle` (count) · total · paid/remaining.
- General order — panel `conversionsTitle`: `remainingKg` المتبقي للتحويل; button `+ addConversion` (key `purchases.convert`, hidden when remaining ≤ 0.0005); rows = date, destination branch pill, item, `kg → output qty unit`, «بواسطة» creator; reverse icon (key `purchases.delete`) → confirm `reverseConfirmTitle/Msg` («سيتم خصم الكمية الناتجة من مخزون الفرع. لا يمكن التراجع إذا كان الرصيد غير كافٍ…»).
- Itemized order — panel `linesTitle`: per line item name, line total, `colOrdered`, `colReceived`, `colRemaining` (button `receiveAction · remaining` with key `purchases.convert`, or tag `receivedTag` «مكتمل»), and below each line its receipts (branch pill, qty, date, reverse icon `reverseLineConv`/`reverseLineConvMsg` with key `purchases.delete`). Header button `+ conversionsTitle` opens the receiving dialog with the first open line.
- Right/below: `PaymentsPanel` (parent `po`, branch picker for cash drawer, contact = vendor so vendor wallet applies), `AttachmentsPanel` (paper order), print-hidden.
- Modals: `ConversionModal`, `ItemizedConversionModal`, `PurchaseOrderFormModal` (edit mode), confirm dialogs.
- Edit warnings: `PURCHASES.editConvertedWarning` when general with conversions; `editItemizedWarning` when itemized (editing the lines **cancels all receipts and returns their stock**, user must re-receive).

### 7.6 PO form (`PurchaseOrderFormModal.tsx`) — create (key `purchases.create`) and edit (key `purchases.edit`)
- Fields: vendor (contacts of type vendor|both; required `PO_FORM.vendorRequired`), order type select (`typeGeneral` «أمر عام (بالوزن)» / `typeItemized` «أمر مصنّف (أصناف)»; **locked when editing**), product name (general only), date (default today), notes.
- General: three linked fields `totalKg` (3 dp), `pricePerKg` (2 dp), `total` (2 dp). **Bidirectional pricing rule** (`editPrice`, `PurchaseOrderFormModal.tsx:147`): keep a recency list of which field the user touched last; the least-recently-touched of the three is the *derived* field, recomputed after each edit from the other two: derived `total` = kg × ppk (2 dp); derived `ppk` = total ÷ kg (2 dp, only if kg>0); derived `kg` = total ÷ ppk (3 dp, only if ppk>0). Initial recency makes `total` the derived field. The derived field is badged `PO_FORM.computed`. Validation: kg > 0 (`kgRequired`), ppk and total must be finite numbers (`amountsRequired`).
- Itemized: line grid (add 1 / 5 / 10 lines). Each line: item select (active items + «＋ صنف جديد» which asks for a new name and unit kg|وحدة — the new item is created in the same transaction with sale price 0, threshold 0), `qty`, `unit_price` (label «سعر الكيلو» for kg items else «سعر الوحدة»), `line_total`. Per line the user drives either unit price or line total; qty is the anchor: unit mode → total = qty×price (2 dp); total mode → price = total ÷ qty (2 dp). A line is "started" if anything was touched; started lines must be valid (item or new name, qty > 0, price ≥ 0) else `PO_FORM.lineInvalid`; at least one valid line (`linesRequired`). Order total = Σ line totals (rounded per line to 2 dp).
- Save paths: create general → insert PO; create itemized → atomic RPC `create_itemized_po` (order + new items + lines); edit general → RPC `admin_update_purchase_order`; edit itemized → RPC `update_itemized_po`. Toasts `PO_FORM.saved|savedItemized|savedEdit|saveError`.

### 7.7 Conversion dialogs
- `ConversionModal` (general PO): item (default first), destination branch (default first), `kgConsumed` (≤ remaining; `exceedsRemaining`), `outputQty` (> 0), `outputUnit` kg|وحدة (defaults to the item's unit, overridable). UI check then server check (below). Success `CONVERSION_FORM.success`.
- `ItemizedConversionModal`: pick open line (shows remaining), destination branch, qty (> 0, ≤ line remaining `ITEMIZED_CONVERT.exceeds`). Success `ITEMIZED_CONVERT.success`.
- Rules in 8.1.

### 7.8 `/inventory` — المخزون (`Inventory.tsx`)
- Header buttons: `OPENING.button` «رصيد افتتاحي» (role admin), `+ INVENTORY.addItem` and `INVENTORY.transferBetweenSites` (key `inventory.items`).
- Table rows = every **active** item × every branch in scope (selected branch, or all branches the user can see): columns `colItem` (filter, sort) · `colSite` (only in all-branches view; filter) · `colQty` (live stock via `get_stock`; sort) · `colMin` (threshold or —) · `colStatus` (منخفض amber when threshold>0 and qty ≤ threshold, else متوفر green; filter) · `colSalePrice` · `colCostPrice` (shown to everyone who can see inventory) · transfer icon per row (key `inventory.transfer`, preset from the row). Empty `INVENTORY.noItems`.
- Row click → `/inventory/:id`.

### 7.9 `/inventory/:id` — `ItemDetail.tsx`
Card: name, unit label, `salePrice`; buttons `INVENTORY.adjust` + `COMMON.edit` (key `inventory.items`); `perSiteStock` tiles (stock at every branch); `movementHistory` list: date, branch, type (`STOCK_SOURCE_LABEL`: تحويل من أمر شراء | بيع | تسوية | نقل | رصيد افتتاحي), signed change with unit, note. Empty `noMovements`. (Cost price is not displayed here.)

### 7.10 Item dialogs
- `ItemFormModal`: name (required `nameRequired`), `unitType` كجم|وحدة, `lowStockThreshold` (alert level; 0 = no alert), `salePrice` (prefilled into SO lines), `costPrice`, `active` (edit only). Numbers parsed with `Number(x) || 0` (invalid → 0, never rejected). No delete.
- `AdjustmentModal` (تسوية مخزون): branch (default selected), signed amount (non-zero `amountRequired`; positive adds, negative removes, `changeHint`), reason (**mandatory** `reasonRequired`). Writes a `stock_movements` row `source_type='adjustment'` with the reason as note. Server refuses going below zero.
- `TransferModal` (نقل بين الفروع): item, from-branch, to-branch (must differ `sameSiteError`), qty > 0, optional note; shows available at source; server writes two movements (out «نقل صادر», in «نقل وارد») after re-checking availability (`transfer_apply`).
- `OpeningStockModal` (admin): pick one branch; table of all active items with current stock, typed *counted* quantity (blank = no change) and computed difference; note; two-step confirm button («تأكيد الحفظ») ; calls `set_opening_stock` once per changed item sequentially, naming the failing item on error. A negative count is rejected. Posts `source_type='opening'` movement for the difference (see 8.9).

### 7.11 `/sales` — أوامر البيع (`Sales.tsx`)
- Button `+ SALES.addSo` (key `sales.draft`) → `SalesOrderFormModal`; navigates to the new draft.
- Columns: `colCode` (formatSoNo) · `colClient` (filter, sort) · `colSite` (filter) · `colDate` · `colTotal` · `colStatus` (chip مسودة | مفوترة | منفذة | مغلقة; filter in lifecycle order) · `colPayStatus` («مدفوع بالكامل» when total>0 and remaining ≤ 0.005 else «X متبقي»; filter). Scoped to the selected branch. Empty `noSos`.

### 7.12 `/sales/:id` — `SalesOrderDetail.tsx`
- Header: formatSoNo, status chip, «مدفوع بالكامل» chip; `client · branch · date · «بواسطة» creator`.
- Action buttons (each permission-gated, status-conditioned): draft with no payments → `editDraft` (key `sales.draft`); any non-draft → `edit` (key `sales.edit_locked`); draft → `invoice` فوترة (key `sales.invoice`); invoiced → `place` تنفيذ (key `sales.place`); placed → `cancelPlacement` (key `sales.cancel_placement`); any order that has an invoice number → `printInvoice` (opens `/sales/:id/invoice`). Each lifecycle action asks confirmation (`invoiceConfirmMsg` «سيتم تثبيت البنود وإصدار رقم فاتورة. لا يمكن تعديل البنود بعد الفوترة…», `placeConfirmMsg` «سيتم خصم الكميات من مخزون الفرع…», `cancelConfirmMsg` «سيتم إرجاع الكميات إلى مخزون الفرع…») and surfaces the server's Arabic error verbatim.
- KPIs: total (with `subtotal` and `discount` lines when a discount exists, percent shown as `N٪`), paid/remaining, status. Lines table: item, qty+unit, unit price, line total (`noLines` when empty).
- `PaymentsPanel` (parent `so`; cash always hits the order's own branch drawer; form hidden on a draft with note `draftNoPayments`; contact = client so client wallet applies).
- No attachments panel on sales orders (the table supports them; UI only on POs) [DIV-15].

### 7.13 SO form (`SalesOrderFormModal.tsx`)
- Fields: branch (default selected branch else first; required), client (clients: type client|both; required), order date, lines, discount.
- Lines: item (prefills `unit_price` from the item's `sale_price`, always editable), qty, unit price (label «سعر الكيلو» for kg items), live line total. Live "available" quantity at the order's branch under each line and `notEnoughStock` warning when qty > available (only for drafts/invoiced; not for placed/closed because their stock is already out). Warning only; the server decides at placement.
- Discount: tabs `discountNone` بدون | `discountAmount` مبلغ | `discountPercent` نسبة, value input. Live checks: percent > 100 → `discountPercentTooBig`; amount > subtotal → `discountTooBig`; type chosen but value ≤ 0 → `discountRequired`. Shows `subtotal`, `discountLine`, `netTotal`.
- Validation on save: branch, client, ≥ 1 complete line (item, qty > 0, price ≥ 0); a half-filled row is an error `lineInvalid`.
- Save paths: create → insert draft, insert lines, then set discount last; edit draft → clear discount, replace lines, update header + discount (sequence chosen so the discount is validated against the final subtotal); edit locked order → RPC `admin_update_sales_order` (8.2). The two client-side sequences are **not atomic** in the web app; the native app should do each save in one transaction.

### 7.14 `/sales/:id/invoice` — `SalesOrderInvoice.tsx` (print)
Back button, print button `INVOICE.print` (only when an invoice number exists; otherwise a card `INVOICE.notInvoiced`). Printed page: business header (name, address, phone) | «فاتورة بيع», `invoiceNumber` = formatSoNo, `issueDate` (today), `orderDate`; client and branch; table `م`, الصنف, الكمية, سعر الوحدة, الإجمالي; totals box: `subtotal` and `discount (N٪)` if any, `grandTotal` «الإجمالي الكلي», `paid`, `remaining`; two signature boxes (`clientSignature`, `sellerSignature`); footer app name on paper.

### 7.15 `/contacts` — جهات الاتصال (`Contacts.tsx`)
Add button (key `contacts.manage`). Search box (name or phone, plain substring, case-insensitive; **not** Arabic-normalised) + type chips الكل | عميل | مورد | عميل ومورد. Columns: name + type badge · primary phone (or first) · balance chip. Balance = `get_contact_balance` per contact (one call each) shown via `contactBalanceInfo`: |b| < 0.005 → «متسوي»; b > 0 → «X عليه» (they owe us, amber); b < 0 → «X له» (we owe them, green). Empty `noContacts`.

### 7.16 `/contacts/:id` — `ContactDetail.tsx`
Header: name, type badge, edit (key `contacts.manage`), `printStatement` button. Phones list, payment-method badges (`كاش`, `انستاباي — number`, `تحويل بنكي — bank`). **Address and notes are editable but displayed nowhere in the app** (not here, not on statements) [DIV-16]. Big balance box: sub-label (`مستحق من الطرف` / `مستحق للطرف` / `لا يوجد رصيد مستحق`) + amount; this number is the *ledger's final running balance*, not the RPC [DIV-17].
Below: `OpeningBalanceCard` (admin only; 8.10), `CreditCard` (client|both; 8.6), `VendorCreditCard` (vendor|both; 8.7), and `CONTACTS.transactions` «الحركات» list (date, description, debit, credit, running balance abs; rows linking to the SO/PO).

### 7.17 Contact form (`ContactFormModal.tsx`)
Fields: type (client default | vendor | both), name (`nameRequired`), primary phone (`primaryPhoneRequired`, `invalidPhone` «رقم هاتف مصري غير صحيح — موبايل (01012345678) أو أرضي (0572401180)»), extra phones (repeatable, optional, validated when filled), address, notes, payment methods (repeatable: كاش | انستاباي → number required `instapayNumberRequired` | تحويل بنكي → bank name + account number required `bankDetailsRequired`). Edit **replaces** all phones and methods (delete + reinsert), which orphans any payment's `contact_payment_method_id` (FK is `ON DELETE SET NULL`) — the "which account did this payment hit" history is lost on every contact edit [DIV-18].

### 7.18 `/contacts/:id/statement` — `ContactStatement.tsx` (print «كشف حساب»)
Date range (default last 365 days → today) with two pickers + print. Page: business header; title, issue date, period; account name and type; table `colDate, colDesc (البيان), colDebit مدين, colCredit دائن, colBalance الرصيد` (balance shown as absolute value, no له/عليه per row); final balance box `STATEMENT.finalBalance` with له/عليه tag; signature boxes (`clientSignature` «توقيع العميل / المورد», `accountantSignature`). Ledger construction in 9.4. Known defects: final balance ignores the chosen "to" date and there is no carried-forward row [DIV-17].

### 7.19 `/expenses` — مصروفات أخرى (`Expenses.tsx`; detail in 8.11)
Title `EXPENSES.title`; header pill (branch) + «تسجيل مصروف» (key `expenses.record`); due banner; tabs `tabList` المصروفات | `tabRecurring` المتكررة | `tabCategories` الفئات.
- List tab: date range (default 1st of month → today), KPIs `periodTotal` إجمالي الفترة and `periodCount` عدد المصروفات (from `get_expense_summary`, **voided rows excluded**), category chips with totals, table `colDate`, `colCategory` (+ tag «متكرر» when from a rule), `colNote` (description or rule name), `colSite`, `colMethod`, `colAmount`, `colStatus` (مُسجَّل | مُلغى with reason tooltip | «إلغاء المصروف» button for `expenses.void`).
- Recurring tab: rules table (`colRuleName`, `colCategory`, `colFrequency` summary «كل يوم | كل الاثنين | يوم N من كل شهر», `colSite`, `colAmount`; inactive tag «موقوف»); add/edit (key `expenses.manage`). Subtitle explains "no automatic deduction".
- Categories tab: table name/status/period total; add/edit (key `expenses.manage`); archive by unticking active.

### 7.20 `/reports` — التقارير (`Reports.tsx`)
Tabs: `tabSales` المبيعات · `tabPurchases` المشتريات · `tabCash` حركة الخزينة · `tabBalances` الأرصدة. Print button (browser print). Date range (default last 30 days → today) except on الأرصدة.
- Sales: non-draft orders in range for the selected branch; 4 summary cards (`ordersCount`, `totalsTag`, `paidTag` المحصل, `remainingTag`), table doc / client / date / total / paid / status; row → order.
- Purchases: POs in range (not branch-scoped); same cards; table doc / vendor / date / total / paid / status label.
- Cash: summary `inflowTag` وارد, `outflowTag` صادر, `closingTag` رصيد آخر الفترة, branch (`cashSite`); table date, [branch when "all"], `colReason`, signed amount, [`colBalanceAfter` running balance only when one branch]; button «+ حركة يدوية» (key `cash.manual`) → `ManualCashModal`. Opening = cash balance at end of (from−1); closing = opening + inflow − outflow. Rows newest first.
- Balances: every contact with |balance| ≥ 0.005, sorted by |balance| desc; cards `owedToUs` (Σ positive) / `owedByUs` (Σ negative); table contact / type / amount with له/عليه; row → contact. Empty `noBalances`.
- `ManualCashModal`: branch, kind إيداع (always +|x|) | سحب (always −|x|) | تسوية (signed as typed), amount non-zero, reason mandatory (`manualReasonRequired`). Server refuses a draw below zero.

### 7.21 `/financials` — الملخص المالي (`Financials.tsx`; figures in 9.3)
Date range (default Jan 1 of this year → today), branch from the header. Print button (A4 with business header, period, branch). Sections:
- `periodSection` «فترة التقرير» — six cards: `salesTotal` صافي المبيعات (detail «قبل الخصم X · خصومات Y»), `collectionsTotal` التحصيلات من العملاء, `purchasesTotal` إجمالي المشتريات, `payoutsTotal` المدفوع للموردين, `expensesTotal` المصروفات الأخرى, `netCash` صافي النقد (= collections − payouts − expenses, computed in the UI; hint when a branch is selected that the three terms have different scopes). Notes `collectionsNote`; `siteScopedHint` / `orgWideHint` per card.
- Method breakdowns: `collectionsByMethod`, `payoutsByMethod` (cash/instapay/bank only), `expensesByCategory`.
- `positionSection` «المركز المالي»: `inventoryValue` (+ per-branch list), `cashBalance` (+ per branch), `receivables`, `payables`, `clientCredits`, `vendorCredits`; `zeroCostWarning` when stocked items lack a cost price.
- `monthlySection`: month / sales / purchases / collections / payouts / expenses.
- Footnote `purchasesOrgWideNote` when a branch is selected.

### 7.22 `/audit` — سجل النشاط (`Audit.tsx`; 10)
Date range default last 30 days; filters `entity` (list = `AUDIT_ENTITY_LABEL`) and `action` (إضافة|تعديل|حذف); print. Table: `colWhen`, `colUser` (name; «النظام» when no actor; «مستخدم محذوف» when actor id not found), `colAction` (pill "verb · entity"), `colDetails` (generated sentence). Max 500 rows, newest first.

### 7.23 `/settings` — الإعدادات — الفروع (`Settings.tsx`)
`OrgIdentityCard` (`ORG_SETTINGS.*`: business name required, address, phone, currency list, timezone list; save) above the branch table: `colName`, `colStatus` (نشط | موقوف), `colDrawer` (live drawer balance, all-time), `colSince`. Add/edit modal: `SITES_ADMIN.name` (required `nameRequired`), `activeLabel` checkbox with hint `activeHint` («الفرع الموقوف يختفي من القوائم لكن يبقى سجله محفوظًا»). No delete.

### 7.24 `/users` — المستخدمون (`Users.tsx`; 4.2–4.3)
Table `colName` (avatar initial), `colUsername`, `colRole` badge, `colStatus`, `colSince`. Button `+ addUser`. Row → `UserAccessModal`. An info box `USERS.createNote` (stale text, [DIV-19]).

### 7.25 `/platform` — لوحة المشغّل (`Platform.tsx`) — **drop in native** (multi-tenant onboarding: create a business with owner, list businesses).
Listed only for completeness: `PLATFORM.*`.

### 7.26 Shared panels (used on several screens)
- **`PaymentsPanel`** (PO and SO): list of payments (newest first: amount, method label, stored account when linked, date, note, delete icon with key `payments.delete`); remaining = total − Σ payments; add form shown only if remaining > 0.005 and `canAdd` (false on draft SO): amount, method (كاش | انستاباي | تحويل بنكي), stored-account picker for instapay/bank (must pick one of the contact's saved accounts of that method, else `noAccountForMethod`), drawer picker for cash on POs (SO cash always uses the order's branch), date (default today), note. Logic in 8.4. Wallet section when the order has a contact: "الرصيد المتاح"/"الدفعة المقدّمة المتاحة" with a button to apply it (key `payments.credit`).
- **`AttachmentsPanel`** (POs): see 8.12.
- **`DueExpensesPanel`/`DueExpensesList`**: see 8.11.

## 8. Business workflows and rules

Quoted Arabic strings are the exact server error messages the web UI shows verbatim; the native app should reuse them.

### 8.1 Purchase orders and their conversion into branch stock (rules 2 and 3)

**Two kinds.** *General* (`po_type='general'`): bought **by weight (KG)**, one product, site-agnostic at creation. *Itemized* (`po_type='itemized'`): a list of named inventory items with quantities and unit prices, also site-agnostic at creation. Neither kind puts any stock anywhere until it is *converted/received into a specific branch*. A PO is never tied to a branch.

**General PO**
- Stored: `total_kg` (>0, 3 dp), `price_per_kg` (≥0), `total_amount` (≥0), optional `product_name`, `notes`. Pricing is entered with the bidirectional rule of 7.6; the database stores all three and does not cross-check them. `total_amount` is authoritative for payments.
- Progress is **derived**, never stored: converted = Σ `kg_consumed`; remaining = `total_kg − converted`.
- **Conversion** (key `purchases.convert`) is *free entry*, any number of times: (item, destination branch, kg consumed, output qty, output unit kg|unit, date default today). No ratios. Example from the rules: 100 kg → 50 kg→item1 as 40 units @A, 25 kg→item2 as 25 kg @A, 25 kg→item3 as 50 units @B.
  - Reject if `kg_consumed > remaining + 0.0005`: «الكمية المستهلكة (X كجم) تتجاوز المتبقي من أمر الشراء (Y كجم)».
  - Effect (same transaction): `+output_qty` stock movement at the destination branch, `source_type='po_conversion'`, `source_id` = the conversion row, note «تحويل من <order_code>». The stock quantity is posted as typed in the item's own unit; there is **no check that `output_unit` equals the item's `unit_type`** (a data-entry risk the native app may want to guard).
  - When `remaining ≤ 0.0005` the PO status becomes `fully_converted`.
- **Reverse a conversion** (key `purchases.delete`): allowed only if the branch still holds the quantity (`stock − output_qty ≥ −0.0005`) else «لا يمكن التراجع عن التحويل: المخزون الحالي في الفرع لا يكفي»; writes `−output_qty` movement (`source_type='adjustment'`, note «إلغاء تحويل من أمر الشراء»); a `fully_converted` PO goes back to `open`. General-PO conversions and their reversals are audit-logged; itemized receipts (`po_line_conversions`) and lines (`po_lines`) have no audit trigger.
- `total_kg` cannot be edited once any conversion exists («لا يمكن تعديل إجمالي الكيلوهات بعد بدء التحويل إلى المخزون») — except through the edit function below.
- **Edit a PO at any stage** (key `purchases.edit`; **admin only in native**, web managers held it by default [DIV-4], deliberate fix L-22): `total_kg > 0`; new `total_kg ≥ converted` («إجمالي الكيلوهات (X) أقل من المحوّل بالفعل (Y)»); new `total ≥ already paid` («الإجمالي الجديد (X) أقل من المدفوع بالفعل (Y)»); sets vendor, product, notes, date, kg, price/kg, total; recomputes status (`fully_converted` iff converted ≥ total_kg − 0.0005, else `open`). The edit is **not** audited (the PO audit trigger fires only on status change) [DIV-23].
- The PO's `status` value `closed` exists in the enum but nothing sets it. A fully paid, fully converted PO simply stays `fully_converted`.

**Itemized PO**
- Created atomically (`create_itemized_po`, key `purchases.create`): the PO, any brand-new items named on lines (name trimmed; chosen unit; sale price 0; threshold 0; active; **no duplicate-name check**), and the lines. At least one line («أضف صنفًا واحدًا على الأقل»); vendor required («المورد مطلوب»); new item needs a name («اسم الصنف الجديد مطلوب»).
- `total_amount = Σ line_total` (maintained by trigger); `total_kg`/`price_per_kg` NULL. `line_total` is stored as entered (the UI back-computes unit price from it when the user drives the total).
- **Receiving** (key `purchases.convert`): (line, destination branch, qty > 0, date) any number of times per line, so one line can be split across branches. Reject if qty exceeds the line's remaining: «الكمية المحوّلة (X) تتجاوز المتبقي من البند (Y)». Effect: `+qty` stock at the branch (`po_conversion`, note «استلام من <order_code>»); when every line is fully received the PO becomes `fully_converted`. Reverse (key `purchases.delete`) is guarded the same way and reopens the PO (note «إلغاء تحويل بند مصنّف»).
- **Edit** (key `purchases.edit`, `update_itemized_po`): rewrites header (vendor, date, notes) and all lines; deleting the old lines cascades away their receipts and reverses their stock (guarded — if any branch no longer holds the stock, the whole edit is rejected); status is reset to `open`; the user must re-receive. Unlike general POs, **no check that the new total is ≥ amount already paid** [DIV-20].
- Per-order extras in both kinds: payments ledger (8.4), vendor wallet application (8.7), attachments (8.12), the vendor's statement (9.4).

### 8.2 Sales orders

**Fields:** branch (stock and cash belong to it), client, order date, lines (item, qty > 0, unit price ≥ 0; `line_total = round(qty × unit_price, 2)`), discount. `subtotal = Σ line_total`; `total_amount = subtotal − discount_amount` and **every downstream figure (payments, closing, statements, credits, dashboard, reports) uses `total_amount`**.

**Discount (key `sales.discount`):** `none` | `amount` (ج.م) | `percent` (0–100). Derived `discount_amount` = amount, or `round(subtotal × pct ÷ 100, 2)`, stored once so the invoice cannot drift. Rejections: percent > 100 «نسبة الخصم لا يمكن أن تتجاوز ١٠٠٪»; discount > subtotal «الخصم (X) يتجاوز إجمالي البنود (Y)»; and, when a discount/line change lowers the total, if collected > new total «المحصّل من العميل (X) يتجاوز إجمالي الأمر بعد الخصم (Y)» (this last check stands down while an admin edit is in progress). A discount change is audit-logged.

**Lifecycle** (`draft → invoiced → placed → closed`):

| Transition | Who | Preconditions | Effects |
|---|---|---|---|
| create | `sales.draft` | **must be created as draft** | gets running number; branch must be allowed |
| draft: edit lines/header | `sales.draft` | lines locked afterwards: «لا يمكن تعديل بنود أمر بيع بعد فوترته» | `subtotal`/`total` recomputed |
| draft → invoiced (فوترة) | `sales.invoice` | ≥ 1 line «لا يمكن فوترة أمر بيع بدون بنود» | assigns the invoice number; lines frozen; now appears in client balance/statement/reports/dashboard sales |
| invoiced → placed (تنفيذ) | `sales.place` | every line available **at the order's branch**: «الكمية غير كافية في المخزون للصنف المطلوب (المتاح X / المطلوب Y)» | one `−qty` movement per line (`source_type='sale'`, `source_id` = line id, note «بيع <invoice>»); the stock floor guard also stops two lines of the same item from jointly overdrawing; if already fully collected → continues to `closed` |
| placed → closed | automatic | on payment: `total > 0` and `Σ payments ≥ total − 0.005`, **only from `placed`** | none |
| closed → placed | automatic | a payment is deleted and the order is no longer fully collected | none |
| placed → invoiced (إلغاء التنفيذ) | `sales.cancel_placement` | not allowed from `closed` (delete a payment first to reopen) | `+qty` compensating movements (`adjustment`, note «إلغاء تنفيذ <invoice>») |

Any other transition raises «انتقال حالة غير مسموح به لأمر البيع (X ← Y)» (e.g. draft→placed, invoiced→draft, closed→invoiced). An order that is fully paid while only `invoiced` stays `invoiced`; placing it then lands directly on `closed` (verified by running the migrations).

**Edit after invoicing** (key `sales.edit_locked`, admin by default; RPC `admin_update_sales_order`, one transaction): requires ≥ 1 line («يجب إدخال بند واحد على الأقل»). Algorithm: remember status; if placed/closed step back to invoiced (stock returns), then to draft; rewrite branch, client, date, lines, discount; verify payments ≤ new total («المدفوع (X) يتجاوز إجمالي الأمر بعد التعديل (Y)»); replay forward to invoiced, then placed if it was placed/closed (**may fail with the stock-shortage message, rolling the whole edit back**), then closed if it was closed and still fully paid. The invoice number is kept. **Changing the branch does not move already-posted cash**: existing cash payments stay on the old branch's drawer [DIV-21].

**Delete** exists in the database (`sales.delete`) but not in the UI; it deletes lines (cascade) but **does not reverse stock, cash or payments** [DIV-6]. The native app should offer cancel/void instead of delete, or implement reversal.

### 8.3 Stock ledger: adjustments, transfers, low stock
- Stock for (branch, item) = Σ `qty_delta`; "all branches" = Σ over branches the user may see; as-of D = movements created before the end of D.
- **Adjustment** (`inventory.adjust`): signed non-zero quantity, **mandatory reason**, free sign; may not push the branch below zero.
- **Transfer** (`inventory.transfer`, rights on both branches): from ≠ to, qty > 0; pre-check «الكمية غير كافية في الفرع المصدر للنقل (المتاح X / المطلوب Y)»; two movements (source `−`, note «نقل صادر»; destination `+`, note «نقل وارد»), `source_type='transfer'`, `source_id` = transfer row. No reversal exists (a transfer back is a new transfer).
- **Sources of stock movement** (the only ones): PO conversion/receipt `+`, sale `−`, sale cancel `+` (as adjustment), conversion reversal `−` (as adjustment), adjustment `±`, transfer `±`, opening `±`.
- **Low-stock rule:** an active item at an active branch is "low" iff `low_stock_threshold > 0` and stock ≤ threshold. Shown on the inventory list and (as of the chosen day) on the dashboard ordered by `stock ÷ threshold` ascending.

### 8.4 Payments ledger (rule 4) — shared by purchase and sales orders
- A payment: amount > 0, method (كاش | انستاباي | تحويل بنكي; `credit` is internal), date, note, optional stored-account reference (`contact_payment_method_id`), drawer branch (`site_id`, cash only).
- Insert checks, in order: parent exists; **sales order is not a draft** («لا يمكن تسجيل دفعة على مسودة أمر بيع — قم بالفوترة أولاً»); an SO cash payment defaults to the order's branch; **a cash payment must name a drawer** («يجب تحديد الفرع (الخزينة) للدفعة النقدية»); a referenced stored account must belong to this order's contact (client for SO, vendor for PO) and match the method («طريقة الدفع المحددة لا تخص جهة الاتصال»); **overpayment block:** `already_paid + amount > total + 0.005` → «المبلغ يتجاوز المتبقي على الأمر (الإجمالي X / المدفوع مسبقًا Y)».
- After insert: cash → one cash-movement row on the drawer branch: SO `+amount` (reason «تحصيل نقدي من بيع»), PO `−amount` (reason «سداد نقدي لأمر شراء»; blocked by the drawer floor if the drawer lacks cash). SO: auto-close as in 8.2. instapay/bank never touch a drawer.
- Balance on an order = `total − Σ payments` (wallet applications count as payments).
- **Delete a payment** (`payments.delete`, confirm dialog `PAYMENTS.deleteConfirmMsg`): cash payment → compensating drawer row (reason «إلغاء تحصيل نقدي (حذف دفعة)» / «إلغاء سداد نقدي (حذف دفعة)»; refused if the drawer would go negative); a closed SO that is no longer fully collected goes back to `placed`. **Deleting a `credit`-method payment (a wallet application) does not give the wallet its money back** [DIV-22]. The payment's audit row records the deletion.
- **Overpaying in the UI** (only while remaining > 0.005): if the typed amount exceeds the remaining, the panel asks to confirm (`PAYMENTS.overpayTitle/overpayConfirm`) and calls the overpay procedure instead of failing: SO → `so_overpay`, PO → `po_overpay`:
  1. pay the remaining as a normal payment (if any);
  2. the excess goes to the contact's wallet as an `overpayment` row (`method` = the tender, branch = drawer for cash, date = payment date);
  3. cash tender: the **whole** tendered amount moves the drawer — the payment trigger posts the remaining, and one extra row posts the excess (SO `+`, reason «رصيد زائد من دفعة بيع»; PO `−`, reason «دفعة مقدّمة زائدة لأمر شراء», PO requires a drawer branch);
  4. result toasts `PAYMENTS.overpaid(excess)` / `overpaidVendor(excess)`.
- **Apply wallet to an order** (`payments.credit`): see 8.6/8.7. UI offers it only when wallet > 0.005 and remaining > 0.005, prefilled with `min(wallet, remaining)`.
- Instapay/bank payments require the contact to have a saved account of that method; if none, `PAYMENTS.noAccountForMethod` («أضِفه من صفحة جهة الاتصال أولاً»). Payment `paid_at` is sent as the chosen calendar date (midnight, database time zone), so time of day is not captured [DIV-24].

### 8.5 Cash drawer (rule 5)
- One drawer per branch; balance = Σ `cash_movements.amount_delta` (all-time), as-of D = through end of D. Append-only; mistakes are fixed by new rows. Floor: never below zero (2.3).
- **Every writer of a cash row** (each with a human-readable `reason`, NOT NULL):
  | Writer | Sign | Reason text |
  |---|---|---|
  | cash payment on SO | + | «تحصيل نقدي من بيع» |
  | cash payment on PO | − | «سداد نقدي لأمر شراء» |
  | delete of those payments | reverse | «إلغاء تحصيل نقدي (حذف دفعة)» / «إلغاء سداد نقدي (حذف دفعة)» |
  | SO overpayment excess (cash) | + | «رصيد زائد من دفعة بيع» |
  | PO overpayment excess (cash) | − | «دفعة مقدّمة زائدة لأمر شراء» |
  | client wallet deposit (cash) | + | note or «إيداع رصيد للعميل» (source `manual`) |
  | client wallet refund (cash) | − | note or «استرداد رصيد للعميل» |
  | vendor advance deposit (cash) | − | note or «دفعة مقدّمة لمورّد» |
  | vendor advance refund (cash) | + | note or «استرداد دفعة مقدّمة من مورّد» |
  | cash expense | − | «مصروف: <فئة> — <بيان>» (source `expense`) |
  | expense void | + | «إلغاء مصروف: <فئة> — <سبب>» |
  | opening receivable settled in cash | + | note or «تسوية رصيد افتتاحي» |
  | opening payable settled in cash | − | same |
  | **manual** إيداع / سحب / تسوية (`cash.manual`) | ± | mandatory reason typed by the user |
- Wallet/opening-settlement cash rows are written with `source_type='manual'`, `source_id` = contact id, so they show as manual drawer moves in the cash report and trigger the manual-cash audit entry.
- The drawer opening figure at go-live is set with a manual إيداع (there is no dedicated "opening cash" function; opening *contact* balances deliberately never touch cash).
- Reads need `cash.view` and branch rights. Per-branch drawer balances are shown in الإعدادات and on the dashboard (all-branches view) and in الملخص المالي.

### 8.6 Client credit wallet (رصيد العميل)
Money held for a client. Append-only signed ledger `client_credits`; balance = Σ delta, never < 0. Sources and who may do them:

| Operation | Key | Rule | Effects |
|---|---|---|---|
| Overpayment of a SO | (payment form) | 8.4 | `+excess`, source `overpayment`, `source_id` = SO |
| **Deposit** (+ إضافة رصيد) | any member (no check) [DIV-11] | tender cash or instapay only; cash needs a branch; contact must exist | `+amount` `deposit`; cash → drawer `+` |
| **Apply to a SO** | `payments.credit` | amount ≤ order remaining «المبلغ يتجاوز المتبقي على الأمر (المتبقي X)»; amount ≤ wallet «رصيد العميل غير كافٍ (المتاح X)» | payment with method `credit` (no drawer, no site, note «دفع من رصيد العميل») + wallet `−amount` `applied`; order may auto-close |
| **Refund** (استرداد رصيد) | `payments.credit` | amount ≤ wallet; tender cash or instapay; cash needs branch and the drawer must cover it | wallet `−amount` `refund`; cash → drawer `−` |
| Opening advance | admin (8.10) | — | `opening` rows, no cash |

Wallet movements list on the contact card (`CREDIT.*`): label by source (`source_overpayment` دفعة زائدة, `source_deposit` إيداع رصيد, `source_applied` استخدام في أمر, `source_refund` استرداد, `source_adjustment` تسوية, `source_opening` رصيد افتتاحي), method, branch, «بواسطة» actor, note, signed amount. Balance shown `CREDIT.balance`; empty states `noCredit`/`noMovements`. A refund above the balance is blocked in the UI (`refundExceeds`) and the database. Money-in from overpayment/deposit counts as a **collection** on the dashboard/financial summary at its date (see 9).

### 8.7 Vendor advance wallet (دفعة مقدّمة للمورّد / عربون)
Mirror image for money **we prepaid a vendor**, allowed before any PO exists. Balance = Σ `vendor_credits`, never < 0 («لا يمكن أن يصبح رصيد الدفعة المقدّمة سالبًا»). Direction of cash is flipped:
- Overpay a PO (`po_overpay`): remaining paid, excess becomes an advance; cash → drawer `−` for the whole tender (requires a drawer branch).
- **Deposit** (+ دفعة مقدّمة): no permission check [DIV-11]; cash or instapay; cash → drawer `−` (floor guard blocks an uncovered prepayment).
- **Apply to a PO** (`payments.credit`): amount ≤ remaining and ≤ advance («الدفعة المقدّمة غير كافية (المتاح X)»); payment method `credit` + `−` row `applied`.
- **Refund** (`payments.credit`): vendor returns money: cash → drawer `+`; instapay no drawer.
- Contact balance **adds** the advance (it is an asset: pushes toward «عليه»). `VENDOR_CREDIT.*` labels (`source_applied` «خصم من أمر», `source_overpayment` «دفعة زائدة على أمر», `source_deposit` «دفعة مقدّمة»…).

### 8.8 Contact balances and statements — rules summarised (formulae in 9.4)
Convention: **positive = they owe us («عليه»), negative = we owe them («له»)**. Contacts of type `both` net the client and vendor sides. Balance covers non-draft sales, all purchase orders, every payment against them (including wallet applications as payments), the two wallets (client wallet *subtracts*, vendor advance *adds*) and opening receivable/payable.

### 8.9 Opening stock (admin) — `set_opening_stock`
For migration of an existing shop. Admin states the **counted** quantity per (branch, item); the system computes `delta = round(counted, 3) − current` against the real ledger and posts one `source_type='opening'` movement (note default «رصيد افتتاحي»); delta 0 writes nothing. Counted ≥ 0. Serialised per (branch, item) to avoid concurrent double counting. Audited. Non-admins cannot post `opening` rows directly. UI: 7.10.

### 8.10 Contact opening balances (admin) — `set_contact_opening_balance`, `settle_contact_opening`
Four kinds (UI chooses by contact type: client → receivable + client_advance; vendor → payable + vendor_advance; both → all four; labels `CONTACT_OPENING.kind_receivable` «مستحق على العميل», `kind_payable` «مستحق للمورّد», `kind_client_advance` «دفعة مقدّمة من العميل», `kind_vendor_advance` «دفعة مقدّمة لمورّد»).
- **Set** (role admin; value ≥ 0; "set to", not "add"): posts the difference between the stated figure and what is already recorded. `receivable`/`payable` → `contact_opening_balances` (dated `as_of`, note default «رصيد افتتاحي»); `client_advance`/`vendor_advance` → the wallet with `source_type='opening'` (only the opening rows are compared, real deposits are untouched). Contact must be a client (resp. vendor) of the right type («جهة الاتصال ليست عميلًا/مورّدًا»). **Never writes cash.** UI requires an in-modal two-step confirmation (`confirmSet`).
- **Settle** later (receivable/payable only; `payments.record` in the database, admin-only card in the UI [DIV-10]): amount ≤ outstanding («المبلغ يتجاوز الرصيد الافتتاحي المتاح (المتاح X)»); method cash or instapay; cash needs a branch and posts a drawer row (receivable `+`, payable `−`); posts a negative opening row dated today.
- Appears in the contact ledger/statement as «مستحق على العميل» / «مستحق للمورّد» / advance lines (`as_of` date).

### 8.11 Expenses — مصروفات أخرى (0041–0044)
- **Record an expense** (`expenses.record`): category (active, required), branch (required, allowed), amount > 0, method cash|instapay|bank (never `credit`), date (default today), free-text بيان (optional; the category groups reports, the بيان explains). **Cash** expense: one drawer row `−amount` (floor guard refuses if the drawer cannot cover it — the Arabic message is shown); instapay/bank count as spending but never touch the drawer. A category archived after the fact cannot be used on a *new* expense but old expenses stay readable/voidable.
- **Never edited, only voided** (`expenses.void`, admin): requires a written reason («سبب الإلغاء مطلوب»); posts the compensating drawer row (cash) and stamps `voided_at/by/reason`; the original stays visible with status مُلغى and reason tooltip; voiding frees the rule occurrence so it can be paid again. Already-voided → «هذا المصروف مُلغى بالفعل». Branch must be allowed.
- **Categories** (`expenses.manage`): add/rename/archive (never deleted); name unique per business («توجد فئة بهذا الاسم بالفعل»). 8 seeded.
- **Recurring rules** (`expenses.manage`): name, category, branch, expected amount (prefill only), method, بيان, frequency daily|weekly (ISO weekday)|monthly (day 1–31), start date, optional end date, active. A rule **never posts or deducts anything.**
- **Dues** (derived on demand by `expense_due_list`, requires `expenses.view`): for each active rule and each day from `max(start_date, today−180)` to `min(end_date, today)` that matches the rule (daily every day; weekly on the weekday; monthly on `min(day_of_month, last day of that month)`), the occurrence is *due* unless a live (non-voided) expense carries `(rule_id, due_date)` or a skip exists. Ordered by due date then name; each carries `days_late = today − due_date`. "Today" is the business-timezone date. A forgotten wage therefore accumulates as «متأخر N يوم» rather than vanishing; lookback capped at 180 days.
- **Post a due** (`تسجيل`): opens the expense form prefilled from the rule (category if still active, branch, amount, method, بيان); `expense_date` = today (when money left the drawer), `due_date` = the occurrence. **Skip** (`تخطي`): mandatory reason («سبب التخطي مطلوب»), stored permanently, cannot be undone (if a skip was a mistake, post the expense anyway — a posted expense also clears the due).
- Surfaces: header bell badge + popover, dashboard banner (`dueBannerTitle(n)`: «مصروف متكرر مستحق» / «N مصروفات متكررة مستحقة», body «لن يُخصم أي مبلغ من الخزينة قبل أن تؤكّده بنفسك.»), expenses page banner. Due list respects branch scope and the header's selected branch.
- Expense summary (9.5) feeds the expenses page KPIs and الملخص المالي.

### 8.12 Attachments (المرفقات) on purchase orders
Purpose: file the vendor's paper order. Controls (key `attachments.manage`): `ATTACHMENTS.camera` «تصوير» (device camera), `pickFile` «إرفاق ملف». Multiple files, uploaded one after another. Accepts images and PDF only («نوع الملف غير مدعوم — استخدم صورة أو PDF»); a PDF larger than 10 MB is rejected locally («حجم الملف أكبر من ١٠ ميجابايت»; the server limit is 10 MB for any file). Images are downscaled before storing: long edge ≤ 2000 px, JPEG quality 0.82, kept only if smaller; undecodable formats (HEIC) are stored as-is. Metadata row: order type (`purchase`; `sale` supported by the schema but no UI), order id, storage path (`<org>/<type>/<order>/<uuid>.<ext>`), original file name, mime, size. View: thumbnail grid; image → full-size lightbox with download; PDF → opens externally. Delete: confirmation (`deleteConfirm`), removes the file then the row. Deleting an order removes its attachment rows (cascade trigger). The metadata is audit-logged. Everyone who can see sales or purchases can read attachments.

### 8.13 Branches (فروع) and branch scoping
Branches are created/renamed/deactivated by `sites.manage` (admin only in the UI). Deactivated: hidden from switcher, pickers, dashboard drawers and low-stock list; ledgers keep history. A user's branch set limits: switcher entries, reads/writes of stock, cash, sales orders, payments, conversions, transfers, expenses, rules and dues. Non-branch records (items, contacts, POs, wallets, opening balances) are visible regardless.

## 9. Derived figures: exact definitions

"Branch filter" = `site_id = selected` when a branch is selected in the header, otherwise all branches the user may see. "Through end of D" = movements created before the start of D+1.

### 9.1 Dashboard (`get_dashboard(site, D)`, final definition `0023_vendor_credits.sql:120`; a SECURITY INVOKER function, so it only counts rows the caller may read)
| Figure | Definition |
|---|---|
| `opening_cash` | drawer balance through end of **D−1** (branch filter) — rule 10: opening of D = closing of D−1 |
| `closing_cash` | drawer balance through end of D |
| `sales_total` | Σ `total_amount` of sales orders with `order_date = D` and `status ≠ draft` (branch filter) — dated by the *order date*, regardless of when invoiced/placed |
| `collections_total` | Σ amount of SO payments with `paid_at::date = D` and `method ≠ credit` (branch via the **order's** branch) **plus** Σ positive client-wallet rows (`overpayment`,`deposit`) with `occurred_on = D` (branch via the wallet row's own `site_id`, which is NULL for non-cash tenders) |
| `collections_by_method` | same two sources grouped by method |
| `payments_out_by_method` | PO payments with `paid_at::date = D`, `method ≠ credit` (branch via `payments.site_id`, NULL for non-cash) **plus** positive vendor-wallet rows (`overpayment`,`deposit`) on D with a method (branch via row `site_id`) |
| `low_stock` | active items × active branches (branch filter) with `threshold > 0` and stock-through-end-of-D ≤ threshold; fields item, branch, qty, threshold; ordered by qty÷threshold ascending |
| `top_items` | Σ(−qty_delta) of `sale` movements created on D (branch filter), top 5 by quantity (cancellations are `adjustment` rows and are *not* netted) |
| `site_drawers` | per **active** branch closing balance through end of D (UI shows it only in the all-branches view with a combined total) |
Defects to design around: when a single branch is selected, non-cash collections that arrive through the wallet (instapay deposit/overpayment) and every non-cash vendor payout drop out because their `site_id` is NULL [DIV-25]; the day boundary uses the *database* session time zone, not the business time zone [DIV-26]; collections use `paid_at` while the drawer uses movement creation time [DIV-27]; expenses are absent from payouts [DIV-14]; no stock opening/closing [DIV-13].

### 9.2 Stock and drawer balances
`get_stock(site|NULL, item, as_of|NULL)` = Σ `qty_delta` (as_of ⇒ through end of that day). `get_cash_balance(site|NULL, as_of|NULL)` likewise. Both respect the caller's read rights (branch scope, `inventory.view`/`cash.view`).

### 9.3 الملخص المالي (`get_financial_summary(from, to, site)`, `0040:113`; totals only; needs `financials.view`)
Errors: caller lacks key «الملخص المالي غير متاح لهذا المستخدم»; `from > to` «تاريخ البداية يجب أن يسبق تاريخ النهاية أو يساويه»; unknown branch «الفرع غير موجود». Not row-level-secured: org filter is the only tenant boundary.
- **Period block** (dates inclusive):
  - `sales_subtotal`, `sales_discount`, `sales_total` (= net), `sales_count`: non-draft SOs with `order_date` in range (branch filter).
  - `purchases_total`, `purchases_count`: all POs with `order_date` in range — **branch filter does not apply** (POs are branch-agnostic).
  - `collections_total` = SO payments (`≠ credit`, `paid_at::date` in range, branch via the order) + positive client-wallet `overpayment`/`deposit` rows (`occurred_on` in range, branch via row `site_id`). `collections_by_method` same split.
  - `payouts_total` = PO payments (`≠ credit`, `paid_at::date` in range) + positive vendor-wallet `overpayment`/`deposit` rows; **org-wide regardless of branch**. `payouts_by_method`.
  - Rationale printed on the page (`collectionsNote`): applying a stored wallet to an order is not money moving; the money was counted the day it was deposited.
- **Position block** (as of end of `to`):
  - `inventory_value` = Σ(stock qty × `items.cost_price`) over **active** items at **active** branches (branch filter); `inventory_by_site`; `items_missing_cost` = count of active in-scope items with non-zero stock and cost_price 0 (drives `zeroCostWarning`).
  - `cash_balance` (branch filter; sums all cash rows including inactive branches) and `cash_by_site` (active branches).
  - `receivables` = Σ non-draft SO totals (`order_date ≤ to`) − Σ SO payments on those orders (`paid_at::date ≤ to`, includes wallet applications) + Σ opening receivable rows (`as_of ≤ to`). `payables` analogous with POs and opening payable. Both org-wide and **gross** (wallets are not netted in).
  - `client_credits` = Σ all client-wallet rows with `occurred_on ≤ to` (net of applications/refunds, includes opening); `vendor_credits` likewise. Org-wide.
- **Monthly block:** one row per calendar month from `from`'s month to `to`'s month, ranges clipped to `[from,to]`: `sales_total`, `purchases_total`, `collections_total`, `payouts_total` with the period definitions. The expenses column is merged client-side from the expense summary.
- UI-only arithmetic: `netCash = collections_total − payouts_total − expenses.total` (all payment methods, not just cash).
- Only gross-vs-net note for implementers: `receivables − payables` does **not** equal Σ contact balances because contact balance also nets/adds the wallets.

### 9.4 Contact balance and statement
`get_contact_balance(contact)` (final: `0039_contact_opening_balances.sql:342`; positive = owes us):
```
  (Σ total_amount of the contact's non-draft sales orders  − Σ ALL payments on those orders)
− (Σ total_amount of the contact's purchase orders         − Σ ALL payments on those orders)
− client_wallet_balance
+ vendor_advance_balance
+ Σ opening rows ( receivable: +delta ; payable: −delta )
```
(payments of every method including internal `credit`). Result shown to the piaster; "settled" iff |balance| < 0.005. Balance depends on which payment rows the viewer may read (branch-scoped users see partial totals) [DIV-30].

`getContactLedger(contact, range?)` (client-side assembly, `services/contacts.ts:198-332`): entries
1. each non-draft SO of the contact: date `order_date`, desc «أمر بيع N», **debit** = `total_amount`;
2. each PO: date `order_date`, desc «أمر شراء N», **credit** = `total_amount`;
3. each payment except method `credit`: date `paid_at` (timestamp), desc «دفعة تحصيل» (SO, credit) or «دفعة سداد» (PO, debit);
4. client-wallet rows except `applied`: date `occurred_on`; desc «دفعة زائدة (رصيد)» / «إيداع رصيد» / «استرداد رصيد» / «تسوية رصيد» / «دفعة مقدّمة من العميل» (opening); positive ⇒ credit, negative ⇒ debit;
5. opening rows: date `as_of`; receivable + ⇒ debit, payable + ⇒ credit, negatives flip; desc «مستحق على العميل» / «مستحق للمورّد».
Sorted ascending by the raw date strings (mixed `YYYY-MM-DD` and ISO timestamps, so same-day order is arbitrary-ish); running balance = Σ(debit − credit) computed over the **entire** history; the date range only filters which rows are displayed. Each order row links to its order. **Vendor-advance wallet rows are not included at all** (so an advance paid to a vendor never appears and its application is hidden because credit payments are skipped), and `finalBalance` is the all-history balance even when the printed range ends earlier [DIV-17].

### 9.5 Expense summary and dues
`get_expense_summary(from,to,site)` (`0044:38`): needs `financials.view` (org-wide) or `expenses.view` (confined to the caller's allowed branches). Over non-voided expenses with `expense_date` in range: `total`, `count`, `by_category` (descending by total), `by_method`, `by_site`, `monthly` (`YYYY-MM`). `expense_due_list(site, as_of=today in business tz)` per 8.11.

### 9.6 List-row status helpers
Order pay chip: remaining = total − Σ payments; PO "paid" iff remaining ≤ 0.005; SO "paid" iff total > 0 and remaining ≤ 0.005. Conversion state: none (done ≤ 0.0005), done (done ≥ total − 0.0005), else partial.

## 10. Audit trail (سجل النشاط)

Append-only `audit_log`; the application has no way to write or erase it. One row per significant mutation written by triggers: payments (insert, delete); sales orders (insert, **status change**, discount change); purchase orders (insert, **status change only**); general-PO conversions (insert, delete); stock transfers; stock movements of type adjustment/opening; cash movements of type manual; items, contacts, profiles (insert/update/delete); sites (insert/update); organization (update); order attachments (insert/delete); client credits, vendor credits, contact opening balances (insert); expenses (insert + void update), expense rules/categories (any change), expense skips (insert). Each row stores actor user id, action, table name, row id and a JSON snapshot. **Not audited:** order lines, PO edits that do not change status [DIV-23], itemized receipts, contact phones/methods, user permission and branch changes (only the profile row change is), sales-order header edits other than status/discount.
Readable by `audit.view` (manager and admin by default); UI route additionally requires role admin or manager [DIV-3].
Sentence generator (`Audit.tsx:describe`): `payments` → «تسجيل دفعة X» / «حذف دفعة X»; `sales_orders` → «إنشاء أمر بيع N» or «أمر البيع N ← <حالة>»; `purchase_orders` analog; `po_conversions` → «تحويل مشتريات X كجم» / «عكس تحويل…»; `stock_transfers` → «نقل N بين الفروع»; `stock_movements` → «تسوية مخزون ±N — ملاحظة»; `cash_movements` → «حركة خزينة X — سبب»; `client_credits` → «<مصدر>: ±X» (sources named: دفعة زائدة، إيداع رصيد، استخدام رصيد، استرداد رصيد، تسوية رصيد); `items` → «<فعل> صنف: اسم»; `contacts` → «<فعل> جهة اتصال: اسم»; `profiles` → «إنشاء|تعديل|حذف مستخدم: الاسم (الدور[، موقوف])»; `sites` → «<فعل> فرع: اسم»; `organization` → «تعديل بيانات النشاط: الاسم»; `expenses` → «تسجيل مصروف X — بيان» / «إلغاء مصروف X — بيان»; `expense_rules` → «<فعل> مصروف متكرر: اسم»; `expense_categories` → «<فعل> فئة مصروف: اسم»; `expense_rule_skips` → «تخطي مصروف مستحق — سبب»; anything else (vendor credits, opening balances, attachments) → generic «<فعل> <كيان>» with the raw table name. Verbs: إضافة | تعديل | حذف. Filters offer 17 entities (`AUDIT_ENTITY_LABEL`).

## 11. Service layer coverage (`src/services/*`) — the 19 files

| File | Operations | Used by |
|---|---|---|
| `access.ts` | list permission keys; get a user's perms+branches; save access (RPC `admin_set_user_access`); read preset (RPC `role_preset_permissions`) | `UserAccessModal` |
| `admin.ts` | list profiles; `updateProfile` (**unused**); create user (Edge function / PC RPC); list all sites; create/update site | `Users`, `Settings` |
| `attachments.ts` | list (with 1-hour signed URLs), upload (compress, org-prefixed path), delete (object then row) | `AttachmentsPanel` |
| `audit.ts` | list audit rows in date range with entity/action filter, resolve actor names; limit 500 | `Audit` |
| `cash.ts` | `getCashBalance(site, asOf)` | `Settings`, `reports.ts` |
| `contactOpening.ts` | get outstanding; set (RPC); settle (RPC); `listContactOpeningEntries` (**unused**) | `OpeningBalanceCard` |
| `contacts.ts` | balance; list (+N balance calls); get with phones/methods; create; update (delete-and-reinsert children); ledger | `Contacts`, `ContactDetail`, `ContactStatement`, `Reports`, `GlobalSearch` |
| `credit.ts` | client wallet balance + movements; overpay SO; apply credit; deposit; refund | `PaymentsPanel`, `CreditCard`, `OpeningBalanceCard` |
| `dashboard.ts` | `get_dashboard` | `Dashboard` |
| `expenses.ts` | categories list/create/update; expenses list/create/void; rules list/create/update; due list; skip; expense summary | `Expenses`, expense dialogs, `useDueExpenses`, `Financials` |
| `financials.ts` | `get_financial_summary` | `Financials` |
| `inventory.ts` | items list (active only)/get/create/update; stock lookup; inventory rows (items × branches); movements per item; adjustment; opening stock; transfer | `Inventory`, `ItemDetail`, forms, `SalesOrderFormModal` |
| `organization.ts` | read identity; public name by slug (pre-login); update identity | `useOrganization`, `Login`, `OrgIdentityCard` |
| `payments.ts` | list for an order (with stored-account detail); add; delete; sum; list a contact's saved accounts | `PaymentsPanel` and detail pages |
| `platform.ts` | is platform admin; list orgs; create org (Edge function) | `Platform` (drop) |
| `purchases.ts` | list (+progress, paid); get with conversions and lines; create general; create/update itemized (RPC); add/delete conversion; add/delete line conversion; admin update | `Purchases`, `PurchaseOrderDetail`, forms |
| `reports.ts` | sales report; purchases report; cash report; manual cash movement; `addDaysISO` | `Reports`, `Audit`, `ManualCashModal` |
| `sales.ts` | list (branch); get with lines; create draft; update draft; admin update (RPC); invoice / place / cancel (status updates); `deleteSalesOrder` (**unused**) | `Sales`, `SalesOrderDetail`, `SalesOrderInvoice`, form |
| `vendorCredit.ts` | vendor advance balance + movements; overpay PO; apply; deposit; refund | `PaymentsPanel`, `VendorCreditCard` |

Convention preserved from CLAUDE.md: all writes go through this layer; components never query directly **except** `PurchaseOrderFormModal.fetchVendors` and `SalesOrderFormModal.fetchClients`, which query `contacts` inline.

## 12. Disagreements between code and documentation, and defects found

Numbered for reference. "Docs" = CLAUDE.md, SCHEMA.md, build/multi-tenant plans. Items marked **[verified]** were reproduced on a scratch Postgres 16 built from migrations 0001–0044.

| # | Finding | Evidence | Recommendation for the native app |
|---|---|---|---|
| DIV-1 | **Invoicing is broken on any DB built from the migration files.** `0032:372` calls `next_doc_number('so', y)`, a two-argument function that `0024:230` dropped; error «function next_doc_number(unknown, integer) does not exist». **[verified]** Production presumably differs from the repo. | `0024_org_schema.sql:230-243`, `0032_permissions_enforce.sql:372` | Invoice/order-code strings are never displayed; omit them or generate from the running number. |
| DIV-2 | Nav items for users/settings use keys `users.manage` / `settings.manage`, but routes `/users` and `/settings` are `RequireRole(['admin'])`; a non-admin holding the key sees a link that bounces. `sites.manage` has no UI key at all. | `App.tsx:175`, `navConfig.tsx` | Gate screens by the key; keep admin as the preset holder. |
| DIV-3 | `/audit` route = role admin or manager, nav = `audit.view`: a manager without the key reaches the page (empty by RLS); a staff user granted the key cannot. | `App.tsx:171` | Use the key. |
| DIV-4 | Docs (`SCHEMA.md` 0018: "admin can edit any order"; RLS table "Edit: admin") vs code: after `0032` the manager preset holds `purchases.edit`, so **managers can edit any PO at any stage** (the RPCs were re-gated from `is_admin()` to `has_perm('purchases.edit')`). `0031`'s "nobody's access changes" claim is therefore not true for this action. Sales-order edit after invoicing (`sales.edit_locked`) remains admin only. | `0032:481-490`, `0042:390` | **Owner decision: native is admin-only** (`purchases.edit` removed from the manager preset, L-22; 04 §13). |
| DIV-5 | UI gates "تسوية" and the header "نقل بين الفروع" on `inventory.items`, but the database needs `inventory.adjust` / `inventory.transfer`. A user with only `inventory.adjust` cannot reach the button; one with `inventory.items` but not `.adjust` sees a button that fails. | `ItemDetail.tsx:77`, `Inventory.tsx:158` | Gate each button by its own key. |
| DIV-6 | `sales.delete`, `purchases.delete` (PO), `contacts.delete`, `inventory.item_delete` exist in the database; **no UI offers them**, and deleting a placed/closed SO **does not reverse stock, cash or payments** (payments are polymorphic, no FK) **[verified]**; deleting a PO reverses its conversions but leaves payments and cash. | `0032`, `0004:99`, `0008` | Do not implement hard deletes of financial documents; use cancel/void with compensating rows. |
| DIV-7 | Add-payment form is not behind `payments.record` in the UI (only delete and wallet use are gated). | `PaymentsPanel.tsx:302` | Gate. |
| DIV-8 | `profiles.active = false` and `organization.active = false` are **not enforced**: a deactivated user keeps full access after signing in. | grep of policies/functions: none read `active` for users | Enforce at login and on every action. |
| DIV-9 | No UI to change one's own password or reset someone else's. | no `updateUser`/reset code in `src/` | Provide admin reset + self-service change. |
| DIV-10 | Settling an opening receivable/payable: database allows `payments.record`; UI hides the whole card from non-admins. | `OpeningBalanceCard.tsx:115` | Allow settlement for `payments.record` holders; keep *setting* admin-only. |
| DIV-11 | Eight money RPCs were re-gated by `0032` (the header says twelve). `so_overpay`, `po_overpay`, `credit_deposit`, `vendor_credit_deposit` still have **no permission check** (they were meant to be "staff and up"). | `0032:481-490` (the eight-entry mapping), `0014`, `0023` | Gate with `payments.record`. |
| DIV-12 | `CLAUDE.md`: "Money NUMERIC(12,2). Never float." The UI derives prices with JS doubles (`Math.round(x*100)/100`) for PO totals/unit prices, discount preview, and sums; the database re-rounds. | `PurchaseOrderFormModal.tsx:59-84,147-172`, `SalesOrderFormModal.tsx:63-70` | Use exact decimal everywhere. |
| DIV-13 | Docs rule 10: "opening balance of day D = closing of D−1 for **both stock and cash**". The dashboard shows only cash opening/closing; stock appears only as the low-stock list. | `get_dashboard`, `Dashboard.tsx` | **Decided:** native dashboard shows stock opening/closing per item per site (owner decision L-23). |
| DIV-14 | Dashboard "المدفوعات للموردين" excludes expenses, although `closing_cash` includes cash expenses (they are cash rows). So opening + collections − payouts ≠ closing whenever expenses exist. `get_dashboard` was not touched by 0041-0044. | `0023:120`, `0042` | Add an expenses line. |
| DIV-15 | Schema supports attachments on sales orders (`order_type='sale'`); no UI. | `0022`, `SalesOrderDetail.tsx` | **Decided:** attachments on purchase orders only (owner decision L-30). |
| DIV-16 | Contact `address` and `notes` can be saved but are displayed nowhere (not on the contact page, not on statements). | grep | Display them. |
| DIV-17 | Contact statement/ledger defects: (a) vendor-advance wallet rows missing and their applications hidden, so a vendor with an advance has ledger balance ≠ `get_contact_balance` (the contact list/reports use the RPC, the contact page and statement use the ledger); (b) `finalBalance` is the all-history balance even when the chosen end date is in the past; (c) no "balance brought forward" line for a filtered range; (d) row balance shows an absolute value with no له/عليه; (e) mixed date/timestamp string sorting. | `services/contacts.ts:198-332`, `ContactStatement.tsx` | Single source of truth for balance; include vendor wallet; carry-forward row. |
| DIV-18 | Editing a contact deletes and re-inserts all phones and payment methods; payments' `contact_payment_method_id` is `ON DELETE SET NULL`, so the saved-account trail on past payments is wiped by any edit. | `services/contacts.ts:103-160`, `0016` | Update in place / soft-delete methods. |
| DIV-19 | `Users.tsx` text `USERS.createNote` still says the first admin is created in the Supabase dashboard; `SCHEMA.md` 7b describes `organization` as a singleton and the migration table stops at `0032`; `multi-tenant-plan.md` says "PLAN — not yet implemented"; `CLAUDE.md` lists three roles and says "Phase 0 delivers the database only"; `SCHEMA.md` "RLS role model" table predates permissions. PC docs: `BUILD_PLAN.md` says "32 migrations" while the vendored set is 35 + 2. | files named | Ignore the stale docs; this spec supersedes. |
| DIV-20 | Itemized PO edit does not verify the new total ≥ amount already paid (general PO edit does). | `0020_itemized_po_conversions.sql:201-264` vs `0018:194` | Add the check. |
| DIV-21 | Editing a locked sales order's branch leaves existing cash payments (and drawer rows) on the old branch. | `admin_update_sales_order` | Either forbid branch change after payments or move the cash rows with compensating pairs. |
| DIV-22 | Deleting a wallet-application (`credit`) payment does not return the amount to the wallet (no reversal for the paired `applied` row). | `payment_delete_apply` (`0008:214`) | Block deletion of credit payments or reverse the wallet row. |
| DIV-23 | Admin edits of PO header/amounts that do not change status are not in the audit log. | `0011` PO trigger is status-only | Audit all edits. |
| DIV-24 | `paid_at` is sent as a date (midnight); time of day lost; day assignment depends on the DB time zone. | `PaymentsPanel.tsx:161-172` | Store full timestamp + business date. |
| DIV-25 | With a branch selected, dashboard and financial summary drop wallet-borne non-cash collections (wallet row `site_id` NULL) and (dashboard) all non-cash vendor payouts (`payments.site_id` NULL). `get_financial_summary` documents the SO-payment variant of this problem and fixes it only for payments. | `0023:120`, `0040:113` | Attribute non-cash rows to a branch (order's branch) or define "org-wide only". |
| DIV-26 | `organization.timezone` is documented as "decides the business day", but `get_dashboard`, `get_cash_balance`, `get_stock`, `get_financial_summary` and the cash report all slice days with `::date` / `< D+1` in the **database session** time zone (UTC on Supabase; Cairo only because the PC installer sets it). Only the UI's "today" and `expense_due_list` use the business zone. | `0033`, `0003`, `0023` | Define the business day in the business zone once and use it everywhere. |
| DIV-27 | Dashboard collections are dated by `paid_at` (user-chosen date) but the drawer by movement creation time, so a back-dated cash payment appears in an earlier day's collections but moves the drawer today. Cash report ranges use `created_at`. | `get_dashboard`, `reports.ts:cashReport` | Use one date basis. |
| DIV-28 | Docs rule 9 describes three roles and a fixed role matrix; code has four roles, 38 per-user keys, branch scoping. CLAUDE.md rule 6 "Closed is automatic when collected == total" — code closes only from `placed`. | `0037`, `0008` | Follow this spec. |
| DIV-29 | `po_line_conversions` policies (`plc_*`) check the permission but **not** `can_use_site`, unlike `po_conversions`; a branch-restricted user may receive into another branch. | `0032:159-164` | Check branch rights on both. |
| DIV-30 | `get_contact_balance` and the list pages are computed through RLS, so a branch-restricted user sees different balances (they cannot read other branches' payments). | design | Define balances on full data; scope only the *display* of rows. |
| DIV-31 | Conversion `output_unit` is not validated against the item's `unit_type`; stock quantity is posted as typed. | `0002:109`, `ConversionModal` | Validate or convert. |
| DIV-32 | Dashboard and Reports "balances" tab compute one RPC call per contact; contact list likewise (N+1). Search and all lists load whole tables (no paging). Fine for a shop, relevant to a native port's query design. | services | Single aggregate query. |
| DIV-33 | Minor UI: `Expenses.refreshAll` invalidates `['cash-report']` but the report's key is `['report-cash']`; `ManualCashModal` comment says "Admin only (RLS-enforced)" while the gate is `cash.manual`; Audit "when" uses the device time zone. | `Expenses.tsx`, `ManualCashModal.tsx`, `Audit.tsx:106` | — |

## 13. Porting hazards for a SQLite, serverless, single-process app (implementation-neutral guidance)

1. **No RLS.** Every `has_perm` / `can_use_site` check in section 5 must become explicit authorization in the application layer, applied to **reads as well as writes** (read gating is what isolates the accountant and branch-restricted users).
2. **Triggers become transactions.** Each user action in section 8 must be one atomic transaction that performs all ledger writes and status changes (payment ⇒ cash row ⇒ possible auto-close; placement ⇒ stock rows ⇒ possible close; edit-locked-order rewind/replay with rollback on stock shortage).
3. **Append-only tables:** enforce by never exposing UPDATE/DELETE (SQLite triggers `RAISE(ABORT)` are available as a belt-and-braces measure).
4. **Floor guards** (stock, cash, wallets) must run inside the same transaction as the insert, against the *current* balance including rows already inserted in that transaction (multi-line orders).
5. **Exact decimals:** store money as integer piastres and quantities as integer grams (or decimal strings); never binary floating point (SQLite REAL). Replicate `round()` half-away-from-zero and the 0.005 / 0.0005 tolerances.
6. **Time:** store UTC instants plus an explicit business date; derive "day D" in the business timezone (fixes DIV-26/27); "today" for defaults and due expenses is the business-zone date.
7. **Numbering:** gap-free per-type counter updated in the same transaction as the insert.
8. **Concurrency:** web had multiple devices; a single-PC native app (or one with a LAN mode) still needs serialisation per (branch,item) for opening stock and per contact for opening balances.
9. **Attachments** are binary blobs ≤ 10 MB, images downscaled client-side; PC edition stored them inside the database (included in backups).
10. **Backups/recovery** (see snapshot §4): the PC edition's nightly + encrypted off-site backups, restore, admin password reset and problem-report export are product features the owner will expect.
11. **Auth:** local username/password accounts, roles + per-user keys + branch scope, first-run claim, no cloud email flows; password change/reset must be provided (DIV-9) and inactive accounts must be refused (DIV-8).
12. **Printing:** invoice, statement, financial summary, reports, audit → A4 RTL printable views with business header and signature boxes; the Arabic-Indic digit formatting must be preserved in print.
13. **Search/filter/sort** semantics (Arabic normalisation, AND-of-terms, Arabic collation) are part of perceived behaviour; keep them.
14. **Seed/demo data** (`supabase/seed.sql`) predates multi-tenancy and the permission model (no `org_id`, only 3 users, passwords `shabana123`); do not treat it as a spec. It documents the demo scenario only: 2 branches, 15 items, 10 contacts, 8 POs, 6 SOs.

## 14. Self-check: coverage

**Pages (`src/pages/*.tsx`, 19):** Audit 7.22 · ContactDetail 7.16 · ContactStatement 7.18 · Contacts 7.15 · Dashboard 7.3 · Expenses 7.19 · Financials 7.21 · Inventory 7.8 · ItemDetail 7.9 · Login 4.1/7.1 · Platform 7.25 · PurchaseOrderDetail 7.5 · Purchases 7.4 · Reports 7.20 · Sales 7.11 · SalesOrderDetail 7.12 · SalesOrderInvoice 7.14 · Settings 7.23 · Users 7.24 (plus the PC-only `FirstRunSetup` 4.4/7.2).

**Services (`src/services/*.ts`, 19):** access, admin, attachments, audit, cash, contactOpening, contacts, credit, dashboard, expenses, financials, inventory, organization, payments, platform, purchases, reports, sales, vendorCredit — all in section 11.

**Components covered by name:** layout (AppShell, Sidebar, Header, BottomTabBar, MoreSheet, navConfig) §6; shared (ArabicDatePicker, AttachmentsPanel, ColumnFilter, ConfirmDialog, DataTable, ErrorBoundary, GlobalSearch, Modal, MoneyDisplay, NumberInput, OfflineBanner, PageHeader, PaymentsPanel, PermGate, PlaceholderPage, SiteBadge, Toast) §3/§6/§7.26; contacts (ContactFormModal 7.17, CreditCard 8.6, VendorCreditCard 8.7, OpeningBalanceCard 8.10); expenses (all six) 8.11/7.19; inventory (ItemFormModal, AdjustmentModal, TransferModal, OpeningStockModal) 7.10; purchases (PurchaseOrderFormModal 7.6, ConversionModal, ItemizedConversionModal 7.7); sales (SalesOrderFormModal 7.13); reports (ManualCashModal 7.20); settings (OrgIdentityCard 7.23); users (UserAccessModal 4.3). Contexts: Auth §4/§5, Site §6. Hooks: useOrganization 4.5, useDueExpenses 8.11, useOnlineStatus §6. Lib: date/locale/digits/phone/search/username/orderNo/contactBalance/image §3 (chunkReload/lazyPage: web-deploy only).

**Roles in the matrix (5.2, 5.3):** admin, manager, staff, accountant — all four present, with per-key presets and the role-only checks in 5.4.

**Open questions:** all resolved by the owner; see the decisions log L-xx in `00-README.md` (DIV-4 admin-only PO edit L-22, DIV-6 reversal only L-21, DIV-13 accepted L-23, one PC only so no LAN L-14, attachments on purchase orders only L-30).
