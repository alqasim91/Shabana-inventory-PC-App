# 05 — One-time import from the existing PC edition (PostgreSQL) into SQLite

Audience: the build agent for `Shabana.LegacyImport` (Phase 12) and the installer (Phase 13). Companion of `04-NATIVE-ARCHITECTURE.md`
(sections 5, 6, 8, 9, 11.3) and `03-DB-PORTING-NOTES.md` (§2 units, §3 table map, §11 outline of this procedure, §9 recipes). Target schema:
`sqlite/schema.sql`. The old product is described in `BUILD_PLAN.md`, `OPUS-HANDOFF.md`, `supabase/PC-DIVERGENCE.md`, `installer/`.

The user-facing contract, in one paragraph: *on first launch, if the old program is found, the app offers «استيراد بياناتي من النسخة القديمة». One button. It reads
the old database without changing it, builds the new database on the side, proves with numbers that every balance matches, shows that proof, and only then switches over. If anything fails,
nothing has changed and the old program's data is untouched. The user never types a path, a password, a port or a command.*

---

## 1. Guarantees

| # | Guarantee | How |
|---|---|---|
| GU-1 | **The old install is never modified** | Read-only transaction; the importer never writes to the old PostgreSQL, its data folder, its config, or its backups (the only old-side actions are starting/stopping the old `ShabanaPostgres` service when needed and, at the user's explicit request after success, the retirement step in §11) |
| GU-2 | **The live native database is never half-imported** | All work happens in a *staging* SQLite file; the live file is replaced only by an atomic, journalled swap after verification passes (§10) |
| GU-3 | **No silent data loss or alteration** | Every table is loaded with counts compared; ledger tables are additionally compared by SHA-256 digest of canonical rows; balances recomputed by independent SQL on both sides must match to the piaster/milli (§9) |
| GU-4 | **Re-runnable** | Each attempt builds a new staging file; a failed or cancelled attempt leaves no state except a log |
| GU-5 | **User sees the evidence** | The verification report is shown (and saved as an exportable file) before the user confirms the switch |

Out of scope for v1: *merging* old data into a native database that already holds business data (the import runs only into an empty/first-run database; see §2), importing from the cloud/Supabase edition (a different source; the same mapping applies but the reader differs), importing from a `.dump` without a PostgreSQL server (§3 mode D uses the legacy kit).

## 2. Preconditions and entry points

- **Entry A (normal):** first-run wizard step 1 detects the old edition (§3.1) and offers import instead of "start fresh".
- **Entry B (later):** Settings → «صيانة البيانات» → «استيراد من النسخة القديمة», offered **only when the native database has no business data** (no orders, payments, stock or cash movements, contacts or items beyond the seed) — e.g. the owner started fresh, changed their mind the same day. The import then replaces the (empty) database after a `pre-import` backup. If the native database already contains business data, the button is replaced by an explanation; merging is not supported.
- Requires: Windows administrator elevation **only if** the old files/services cannot be read or started by the current user (§3.3). The app relaunches itself elevated with `--resume-import`; the wizard state is passed on the command line.
- Supported source: PC edition databases built by this repo's installer: migrations `0001`…`0035` plus PC-only `0100`/`0101` (the task states the shipped installs are at 0035). **Floor: 0032** (permission model present). Below that the importer stops with «نسخة البيانات القديمة قديمة جدًا». Everything introduced after 0035 is created empty/defaulted (§6, §8).
- Exactly **one organization** is expected (`PC-DIVERGENCE.md`). If several exist, the wizard lists them with their row counts and asks which one to import; the others are ignored (and reported).

## 3. Locating and reading the old install

### 3.1 Detection (`LegacyDetector`, runs in the wizard, read-only, no elevation)

Evidence, in order; "found" = at least the first two of (a)-(c) agree, or the user picks the folder:
(a) registry uninstall key `{B6E9B6C1-6B7B-4C9F-9C1A-5D3A6A2E9F11}_is1` (HKLM 64/32-bit) → `InstallLocation`; (b) default folder `C:\ProgramData\Shabana\` (the old installer fixed this path: `DisableDirPage=yes`, `DefaultDirName`) with `config\db-password.key`, `config\pg-port.txt`, `data\pg\PG_VERSION`; (c) services `ShabanaPostgres`, `ShabanaPostgREST`, `ShabanaGoTrue`, `ShabanaCaddy` (via `ServiceController`, status only);
(d) the installer's own marker `HKLM\Software\ShabanaInventory\LegacyDetected` (written by the new installer when it found the old edition, 04 §11.3); (e) manual: «اختيار مجلد النسخة القديمة…». The detector also records: whether `bin\pg\bin\postgres.exe` still exists, the service states, the number of `backups\shabana-*.dump` files and the newest date, the highest `supabase\migrations\*.sql` shipped, and free disk space.

### 3.2 Access modes (`LegacyReader` chooses automatically; the wizard shows plain Arabic status, never a technical term)

| Mode | When | Procedure |
|---|---|---|
| **A — live read (preferred)** | `ShabanaPostgres` service is running (the new installer deliberately leaves it running, 04 §11.3) | Connect with Npgsql to `127.0.0.1:<port from config\pg-port.txt, default 5432>`, database `postgres`, user `postgres`, password from `config\db-password.key` (the old scripts all use exactly this: `provision.ps1`, `backup.ps1`, `reset-admin.ps1`). One connection, `BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY` → a single consistent snapshot for the whole import; also `SET default_transaction_read_only = on`. |
| **B — start the old service** | Service installed but stopped | Elevated helper starts **only** `ShabanaPostgres` (not Caddy/GoTrue/PostgREST), waits until it accepts connections, then proceeds as mode A; afterwards stops it again and restores its previous start type. One UAC prompt. |
| **C — private instance on a copy** | The service is gone or will not start, but `data\pg` and the old `bin\pg\bin` binaries exist | Elevated helper copies `data\pg` to `import\pgcopy\` (only if the old server is not running — otherwise a live copy is unsafe → use A/B), writes a loopback-only `pg_hba.conf` (`trust`) and `postgresql.conf` (`port` = a free port, `listen_addresses='127.0.0.1'`) **in the copy**, starts `pg_ctl` from the old `bin\pg\bin` against the copy, imports as in A, then stops and deletes the copy. The original data folder is never started or written. |
| **D — from a backup file (last resort)** | Old program files are gone (the old uninstaller removes `bin\`) or the data directory is unusable, but a `backups\shabana-*.dump` (custom format) exists | Uses the **legacy kit** (PostgreSQL 18.x server binaries shipped as an optional installer component, 04 §11.3): `initdb` a temporary cluster under `import\pgtemp\`, `pg_restore` the newest (or user-chosen) dump into it, import as in A, delete the cluster. Needs roughly 3x the dump size free. If the dump is one of the old **encrypted off-site** files (`SHBNAES1`: AES-256-CBC + HMAC-SHA256, PBKDF2-SHA256 200,000 iterations, 32-byte salt), the wizard asks for the passphrase and decrypts it first (algorithm documented in `installer/scripts/lib-crypto.ps1`; the decrypt routine is re-implemented in C# and tested against a file made by the old script). |

Fallback ladder shown to the user when a mode fails: A fails → try B → C → D → final message «تعذّر قراءة البيانات القديمة. لم يتغير شيء. تواصل مع الدعم وأرسل تقرير المشكلة» with the report button. Each failed attempt's reason is logged (never the password).

### 3.3 Permissions and secrets

- `config\db-password.key` lives under `C:\ProgramData\Shabana\config`. Folders created by an elevated installer inherit read access for local users by default, so mode A usually needs no elevation; if reading throws `UnauthorizedAccessException`, the app asks once for Windows administrator approval and continues elevated (§2).
- The password is read into memory, used for the Npgsql connection, and discarded. It is never logged, never copied into the new database, never written to `machine.json`, never shown. The import log redacts connection strings.
- The PostgreSQL port is read from `config\pg-port.txt` (the old installer may have moved off 5432 when it was reserved); fall back to 5432 then 5433.
- (Observation for the owner, not a native-app issue: on the old install that password file may be readable by every local account. It protects a loopback-only database. Retirement in §11 removes the services; the owner may then delete the old folder.)

### 3.4 Introspection (the importer adapts to the old schema; it never assumes a migration level)

At the start the reader reads `shabana_migrations` (filenames applied) and, per table, `information_schema.columns`. The mapping tables of §6 declare for every target column a **source expression** and an **if-missing default**; a missing source column/table yields the default (and a note in the report), never an error. This is how "old PC install is at 0035 so newer features get defaults" is implemented. Unknown extra source columns/tables are ignored and listed in the report (e.g. `platform_admins`, GoTrue's `auth.*` internals, `storage.*` shim, `shabana_migrations`).

## 4. Numeric and time conversion rules (exact)

All conversions are done **in the SELECT on the PostgreSQL side** so that no floating-point or `decimal` library ever touches a value (03 §11 step 2):

| Source | Target | Expression | Rounding rule |
|---|---|---|---|
| `numeric(12,2)` money | `*_piasters INTEGER` | `(round(col * 100))::bigint` | PostgreSQL `round(numeric)` = **half away from zero**. A `numeric(12,2)` column can only hold 2 decimals, so the multiplication is exact and `round` is a no-op; the importer *proves* it: `count(*) filter (where col*100 <> round(col*100))` must be 0 for every money column, else the run stops and lists the rows (this would indicate a non-standard column) |
| `numeric(12,3)` qty / kg | `*_milli INTEGER` | `(round(col * 1000))::bigint` | same proof with `*1000` |
| `sales_orders.discount_value numeric(12,2)` | `discount_value_x100` | `(round(discount_value * 100))::bigint` | one rule for both kinds: for `amount` the result is piasters, for `percent` it is basis points (12.5 % = 1250, exact) |
| generated/derived columns (`sales_orders.discount_amount`, `total_amount`; `sales_order_lines.line_total`) | **not inserted** (generated columns in the target) | read from PG as `*_piasters` only for **verification** | The target formulas (`(qty_milli*price+500)/1000`, `(subtotal*bp+5000)/10000`) equal PostgreSQL's `round(...,2)` for non-negative values (03 §2); every order and line total is compared one by one (§9) |
| `po_lines.line_total` (stored, user-drivable; NULL possible after 0020) | `line_total_piasters NOT NULL` | `coalesce(round(line_total*100), round(qty*unit_price*100))::bigint` | NULL is replaced by the same half-away-from-zero product and reported |
| `uuid` | `TEXT` | `lower(id::text)` | unchanged (all FKs and `source_id`s stay valid) |
| `timestamptz` | `TEXT` UTC | `to_char(col AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')` | PostgreSQL microseconds are truncated to milliseconds (`.MS`); no ordering ties are created (checked: duplicates of `(created_at,id)` are impossible, rows keep their uuid) |
| `date` | `TEXT` `YYYY-MM-DD` | `to_char(col,'YYYY-MM-DD')` | no time-zone conversion: a date is a business calendar day |
| `boolean` | `INTEGER` | `col::int` | |
| `jsonb` (`audit_log.data`) | `TEXT` | `data::text`, then the importer adds `"_legacy":true` (§6) | |
| text enums | `TEXT` | `col::text` | validated against the target CHECK lists before insert; an unknown value stops the run |

Old `paid_at` values are midnight in the PC's database time zone (the installer set `Africa/Cairo`); converting to the absolute UTC instant keeps the Cairo calendar day correct when the app later converts back with the business zone (04 §7.4).

**Range guards** (03 §2, schema CHECKs): line `qty_milli <= 1,000,000,000`, unit prices `<= 1,000,000,000` piasters, stored totals `<= 999,999,999,999`. A violation aborts with the row list. Practically impossible for a shop, but it must be an explicit message, not a CHECK failure.

## 5. Order of work

```
 0. detect + open source (mode A-D), take the read-only snapshot, read migration level, choose organization
 1. pre-flight scan on the source (counts, rounding proofs, enum checks, orphan checks, range guards)  → may block
 2. create staging file  import\staging-<ts>.db   from baseline PHASE 1 ONLY (tables, indexes, reference data; NO triggers, NO views, NO seed.sql admin)
 3. load tables in FK order inside ONE staging transaction (§6)
 4. post-load normalisation (§8)
 5. apply baseline PHASE 2 (triggers + views) to the loaded data — triggers do not fire retroactively
 6. verification (§9)  → show the report; user confirms
 7. atomic swap into place (§10); first native backup "after-import"; mark app_meta.imported_from
 8. offer retirement of the old edition (§11)
```

Step 2 detail: the placeholder `admin` row of `seed.sql` is **not** created in import mode (it would collide with imported usernames). `app_meta.first_run_completed` is set to `1` at step 7 and the new database has real users from the start. Reference data (`permission_keys`, `role_presets`, `schema_version`) comes from Phase 1. The seed content that is *not* user data — 8 default expense categories and `doc_counters` rows — is applied by the importer (below).

## 6. Table-by-table mapping (load order = FK order of 03 §11)

Conventions: `org_id` is dropped everywhere; every query filters `where org_id = :org` on tables that have it; "if missing" = the default used when the old install does not have that column/table; `created_by`/actor columns that reference a user that no longer exists become NULL and are counted (the target FKs would otherwise fail) — for `audit_log` the dead actor id is preserved inside the JSON (`"_legacy_actor"`).

| # | Source (PG) | Target (SQLite) | Column mapping and notes | If missing / default |
|---|---|---|---|---|
| 1 | `sites` | `sites` | `id`, `name_ar`, `active::int`, `created_at` | — |
| 2 | `organization` (+ currency/tz) | `business_settings` (id=1) | `business_name`, `address_line`, `phone_line`, `currency`, `timezone`; `slug`, `active`, `created_at` dropped | currency `EGP`, timezone `Africa/Cairo` (pre-0033). Timezone must be in the app's 18-zone list, else `Africa/Cairo` + warning |
| 3 | `profiles` ⋈ `auth.users` | `users` | §7. `id`=`profiles.user_id`; `username`; `email`; `full_name`; `role`; `active`; `all_sites`; `password_hash`; `must_change_password`; `password_changed_at`; `last_login_at`; `created_at` | `all_sites` true if column missing |
| 4 | `user_permissions` | `user_permissions` | `(user_id, perm)`; perms not in the 38-key catalog are dropped and reported | table missing (pre-0031) → derive from `role_presets` |
| 5 | `user_sites` | `user_sites` | `(user_id, site_id)` | empty |
| 6 | `contacts` | `contacts` | `id,type,name,address,notes,created_by,created_at` | — |
| 7 | `contact_phones` | `contact_phones` | `is_primary::int`; if a contact has no primary phone the first becomes primary (reported); if several, keep the earliest | — |
| 8 | `contact_payment_methods` | `contact_payment_methods` | shape CHECK must hold; rows with `method='credit'` or malformed rows are dropped and listed | — |
| 9 | `items` | `items` | `low_stock_threshold`→`_milli`, `sale_price`→`_piasters`, `cost_price`→`_piasters`, `unit_type`, `active` | `cost_price` = 0 (column added by web 0040, absent at 0035) |
| 10 | `purchase_orders` | `purchase_orders` | `id,order_code,order_seq,po_type,vendor_id,order_date,total_kg→_milli,price_per_kg→_piasters,total_amount→_piasters,status,product_name,notes,created_by,created_at` | `po_type`='general' (pre-0019); `order_seq` NULL/0 → assigned 1..n by `created_at,id` (reported) |
| 11 | `po_conversions` | `po_conversions` | `kg_consumed`,`output_qty`→milli, `output_unit`, `conversion_date`, `site_id`, `item_id`, `po_id` | — |
| 12 | `po_lines` | `po_lines` | `qty`,`unit_price`,`line_total` per §4; legacy `site_id` kept | — |
| 13 | `po_line_conversions` | `po_line_conversions` | `qty`→milli; `item_id`/`po_id` denormalised: if NULL, filled from `po_lines` | — |
| 14 | `sales_orders` | `sales_orders` | `id,order_seq,invoice_number,site_id,client_id,status,order_date,subtotal→_piasters,discount_type,discount_value→_x100,created_by,created_at`; **`discount_amount`/`total_amount` not inserted** | `discount_type`='none', value 0 (pre-0030); `invoice_number` NULL for non-draft rows is back-filled in §8 (on the PC edition invoicing was broken by defect DIV-1, so such rows are expected) |
| 15 | `sales_order_lines` | `sales_order_lines` | `qty`→milli, `unit_price`→piasters; `line_total` generated | — |
| 16 | `payments` | `payments` | `id,parent_type,parent_id,amount→_piasters,method,site_id,paid_at (UTC ISO),note,contact_payment_method_id,created_by,created_at`; polymorphic `parent_id`: rows whose parent no longer exists are **kept** and reported (the target has no FK either; deleting history is not the importer's job) | `contact_payment_method_id` NULL (pre-0016) |
| 17 | `stock_movements` | `stock_movements` | `qty_delta→_milli`, `source_type`, `source_id`, `note`, `created_at` | — |
| 18 | `stock_transfers` | `stock_transfers` | `qty→_milli` | — |
| 19 | `cash_movements` | `cash_movements` | `amount_delta→_piasters`, `reason` (a blank reason becomes `'(بدون بيان)'` and is reported: the target CHECK forbids blank); `created_at` is copied as is (legacy back-dated payments keep their old drawer dates; S-2 dating applies to payments recorded after the import) | — |
| 20 | `client_credits`, `vendor_credits` | same names | `amount_delta→_piasters`, `source_type`, `method`, `site_id`, `occurred_on`, `note` | tables missing (pre-0014/0023) → empty |
| 21 | — (`contact_opening_balances`) | same name | nothing to import | **empty** (web 0039, absent at 0035) |
| 22 | `doc_counters` | `doc_counters` | `(scope, year, last_value)`; after load `po_seq`/`so_seq` (year 0) = `max(last_value, max(order_seq))`; yearly `po`/`so` = `max(last_value, highest numeric suffix of codes that year)` | rows inserted with 0 if absent |
| 23 | `order_attachments` + `pc_file_bytes` | `order_attachments` + `attachment_blobs` | `storage_path`: strip the leading `<org uuid>/` segment in **both** tables (must stay equal); `mime_type` normalised (`image/jpg`→`image/jpeg`; unknown → derive from the file extension; still unknown → row skipped and listed); `byte_size` := actual `length(bytes)`; bytes streamed (<= 10 MB each) | metadata whose bytes are absent is imported anyway (UI shows «الملف غير متوفر») and counted |
| 24 | `audit_log` | `audit_log` | `data::text` with `"_legacy":true` added (money in legacy snapshots is in pounds; the viewer reads legacy shape by that flag); `actor` NULL if the user is gone, original id kept as `"_legacy_actor"` | — |
| 25 | — (`expense_categories`) | `expense_categories` | the 8 seeded defaults (03 §8) | (web 0042 absent) |
| 26 | — (`expense_rules`, `expenses`, `expense_rule_skips`) | same | **empty** | (web 0041-0044 absent) |
| — | `permission_keys` | (reference data) | not imported: the target has the 38-key catalog; extra old keys can't exist | — |
| — | `auth.*`, `storage.*`, `platform_admins`, `shabana_migrations`, `pc_*` functions | — | **not imported** (GoTrue sessions/refresh tokens, mailer state, storage shim, multi-tenant console, installer bookkeeping) | — |

Everything is inserted with explicit ids; `INSERT` statements are batched (about 2,000 rows) inside the single staging transaction; Phase 1 has no triggers, so no row is posted twice (03 §11 step 1).

## 7. Users, passwords and permissions

**Decision: keep the users' passwords.** GoTrue stores bcrypt (`$2a$10$…`; the old `reset-admin.ps1` wrote `crypt(…, gen_salt('bf'))` which is `$2a$06$…`). Both are verifiable by `BCrypt.Net-Next` (04 §2). The hasher therefore supports **verify-only bcrypt** (`$2a$`, `$2b$`, `$2y$`):
- Relies on schema amendment **S-1** (04 §8.6, **applied** in `sqlite/schema.sql`): the `users.password_hash` CHECK accepts `$2[aby]$*` (bcrypt, verify-only) besides `$argon2id$`/`$pbkdf2-sha256$`, so the importer keeps the hashes instead of nulling them (03 §11 step 3 is aligned).
- On the first successful login with a bcrypt hash, the same transaction **rehashes with Argon2id** and overwrites the column (`auth_events('login_ok', detail 'rehash')`). After that user's first login no bcrypt string remains for them. New and changed passwords are never bcrypt.
- Known-answer tests (CI job `legacy-import`): hashes produced by PostgreSQL `crypt()` for ASCII and **Arabic** passwords, cost 6 and 10, verify correctly; a password longer than 72 bytes follows bcrypt's truncation rule exactly as the old stack did (documented; the importer does not alter it).
- Why this is safe: it hands no new capability to anyone; the hashes were already on the same PC. Why it is better than resetting: no user has to be told a temporary password; no admin work; the owner keeps working on day one.
- Risk accepted: old minimum password length was 6; imported users keep a 6-character password until they change it. The wizard's final screen offers a checkbox «إلزام جميع المستخدمين بتغيير كلمة المرور عند أول دخول» (default off). If the owner prefers a clean slate, the same checkbox sets `must_change_password=1` for all.

Field mapping for `users`:

| Target | Source | Rule |
|---|---|---|
| `id` | `profiles.user_id` | same uuid (audit `actor`, `created_by`, `user_permissions` keep working) |
| `username` | `profiles.username` | If NULL (accounts created before 0010 on a cloud lineage): `split_part(auth.users.email,'@',1)`, lowercased, stripped to `[a-z0-9._-]`; on collision append `-2`, `-3`…; if still empty `user-<first 8 of id>`. Every derived name is listed in the report |
| `email` | `auth.users.email` | NULL when it is the synthetic `<username>@shabana.local` (login is by username; native `email` is optional) |
| `full_name` | `profiles.full_name` | blank → username |
| `role` | `profiles.role` | `admin/manager/staff` (no accountant exists at 0035) |
| `active` | `profiles.active::int` | |
| `all_sites` | `profiles.all_sites::int` | true if missing |
| `password_hash` | `auth.users.encrypted_password` | kept if it matches `^\$2[aby]\$\d\d\$`; NULL/empty/other → NULL and `must_change_password=1` (such a user cannot log in until an admin resets); the importer lists them |
| `must_change_password` | — | 0 when a valid hash was kept; 1 otherwise |
| `password_changed_at` | `auth.users.updated_at` (UTC ISO) | best available |
| `last_login_at` | `auth.users.last_sign_in_at` | |
| `failed_attempts`, `locked_until` | — | 0 / NULL |
| `created_at` | `profiles.created_at` | |

Orphans: a `profiles` row with no `auth.users` row is imported **inactive** with `password_hash NULL, must_change_password 1`. An `auth.users` row with no profile is ignored (reported). If after import **no active admin** exists, the importer promotes nothing automatically; it blocks with «لا يوجد مدير نشط في البيانات القديمة» and the verification screen offers *create an administrator now* (the same dialog as wizard step 4) before the swap.

**Permissions.** Imported `user_permissions` are kept as the owner configured them (32 keys at 0031). The 6 keys introduced after 0035 are granted **exactly the way the web migrations backfilled them**, so nobody gains or loses access the web app would not have given them:
- `dashboard.view` → every non-accountant user (web 0040 §3 backfill),
- `expenses.view`, `expenses.record`, `expenses.manage`, `expenses.void` → each user gets those that his **role preset** contains (web 0042 §8 backfill; staff: view+record, manager: view+record+manage, admin: all),
- `financials.view` → only if the role preset has it (none of admin/manager/staff do; admins bypass anyway).
For `admin` users the full 38-key set is stored (what `admin_set_user_access` does). The report lists, per user, the keys added, so the owner can review them in Users → access.

## 8. Post-load normalisation (still inside the staging transaction, before Phase 2)

1. **Invoice numbers:** for every sales order with `status <> 'draft'` and NULL `invoice_number`, assign `SO-<YYYY>-<####>` from the year of `order_date`, in `(order_date, order_seq)` order, advancing the `so` yearly counter (the numbers are internal; the UI shows `order_seq`).
2. **Counters:** apply the `doc_counters` rules of §6 row 22.
3. **Permission backfill:** §7.
4. **Default categories** (the 8) and `business_settings` singleton present.
5. **Statistics:** `ANALYZE`; `PRAGMA optimize` after Phase 2.
6. **Lock-in flags** in `app_meta`: `first_run_completed=1`, `imported_from` = JSON `{source:"pc-edition", pgVersion, migrationLevel:"0035", importedAtUtc, counts, warnings}`.
After Phase 2: `PRAGMA foreign_key_check` (must be empty) and `PRAGMA integrity_check` (`ok`).

## 9. Verification (what must match, and what the user sees)

The verification runs on the **staging database** against the **source snapshot** (same transaction in modes A/B/C/D), with SQL written independently for each side so a mapping mistake cannot cancel itself out. The user is shown a screen «تقرير الاستيراد» with a green/red mark per line; **any red line blocks the switch** (the user can only go back, export the report, or cancel).

| Check | Source side | Staging side | Tolerance |
|---|---|---|---|
| **Row counts** for every imported table (+ skipped/dropped counts with reasons) | `count(*)` per table (org-filtered) | `count(*)` per table | exact; difference must equal the listed skipped rows |
| **Stock** per (site,item) | `sum(qty_delta)` | `v_stock_balance` | 0 differences; also total units per site |
| **Cash drawer** per site | `sum(amount_delta)` and PG `get_cash_balance(site,null)` | `v_cash_drawer_balance` | exact |
| **Contact balances** | `get_contact_balance(contact)` for every contact and the explicit formula `receivable − payable − client_credit + vendor_credit` | `v_contact_balance` | 0 differences; shown: number of contacts with a balance, Σ «عليه», Σ «له» |
| **Wallets** | `sum` of `client_credits` / `vendor_credits` per contact | `v_client_credit_balance` / `v_vendor_credit_balance` | exact |
| **Orders** | per SO: `total_amount`, `subtotal`, `discount_amount`, each line `line_total`; per PO: `total_amount`, converted kg | the generated columns / views | exact per row (this proves the integer rounding formulas against PostgreSQL's `round`) |
| **Paid per order** | `sum(payments)` per parent | `v_so_balance`, `v_po_balance` | exact |
| **Ledger digests** | `sha256` over canonical text of `stock_movements`, `cash_movements`, `payments`, `client_credits`, `vendor_credits` ordered by id, e.g. `id\|site\|item\|delta_scaled\|created_at_iso\n` (computed in PG with `sha256(convert_to(string_agg(...),'UTF8'))`) | the same canonical text computed from the staging rows | byte equal |
| **Dashboard spot check** | for 3 sample days (today, the busiest day, a day mid-history): PG `get_dashboard(null, D)` cash opening/closing and collections | `DashboardService` figures | cash identical; collections identical except for the documented day-boundary rule (UTC vs Cairo; shown, not blocking, listed as «فروق متوقعة») |
| **Invariants** | — | `IntegrityService.RunFull()` (04 §9.4) | all pass |
| **Users** | profile count; # with kept bcrypt; # needing reset | same | exact |

Screen content in order: summary banner («تم التحقق من ٢٤ جدولًا و … رصيدًا. لا فروق»), the table of counts, the balance totals (per branch drawer, total stock lines, contacts «له/عليه» totals, orders total, payments total), warnings (derived usernames, orphan actors, attachments without bytes, back-filled invoice numbers, keys added per user), the legacy-data location, buttons **تأكيد الانتقال** / **إلغاء** / **حفظ التقرير** (CSV + PDF in `exports\`). The same report is stored in `app_meta.imported_from.warnings` and as a file in `backups\pre-import\`.

## 10. Failure, cancel and rollback behaviour

**Before the swap (everything above):** failure or *Cancel* → close the source snapshot (nothing was written to it), delete `import\staging-*.db` (and `import\pgcopy`/`pgtemp` for modes C/D), stop anything the helper started (mode B returns the service to its previous state), keep a `logs\import-<ts>.log`. Result for the user: «لم يتغير شيء. بياناتك القديمة كما هي.» The live native database was never opened for writing.

**The swap (the only step that touches the live file):**
1. Take a `backups\pre-import\` backup of the current native database (it is the empty first-run DB or a nearly empty one; cheap, and keeps the rule "every destructive step has a backup").
2. Write the journal `import\swap.json` `{step:0, live:"db\\shabana.db", staging:"import\\staging-…db", backup:"…"}`.
3. Close every connection (checkpoint `TRUNCATE`).
4. `step:1` rename live → `db\shabana.db.pre-import`; `step:2` move staging → `db\shabana.db` (same volume, atomic rename); delete stale `-wal/-shm`; `step:3`.
5. Reopen, `MigrationRunner` (no-op; staging was built from the current baseline), `quick_check`, `first_run_completed` = 1.
6. Delete the journal; the pre-import copy remains in `backups\pre-import\`.
7. First native backup tagged `after-import` is taken immediately.

**Failure after step 4 starts (cannot open, check fails):** automatically `rename pre-import → live`, journal cleared, error shown, import offered again. **Crash/power loss at any step:** at the next start, the start-up routine (04 §4.3 step 4) reads `swap.json` and decides deterministically: step 0 → delete staging, done; step 1 with live missing → move `shabana.db.pre-import` back; step 2 complete (new live present) → verify with `quick_check`, keep it if good else roll back; then remove the journal. Unit-tested by simulating a crash after each step.

**Disk space:** before starting, require free space >= 2x (size of source data + attachment bytes) on the data volume, and >= 3x dump size in mode D; otherwise a clear Arabic message with the numbers.

**Time:** typical shop data (tens of thousands of rows) takes seconds to a few minutes; the screen shows per-table progress and can be cancelled until the swap.

## 11. After a successful import: retiring the old edition (one button, optional, never automatic)

The wizard's last page and Settings → «النسخة القديمة»: «تم نقل بياناتك. يمكنك الآن إيقاف النسخة القديمة.» Button **إيقاف وإزالة النسخة القديمة** (one UAC prompt, elevated helper):
1. Make sure Caddy/GoTrue/PostgREST are stopped and disabled (the installer already did this).
2. Stop `ShabanaPostgres`, set it disabled.
3. Run the old program's own silent uninstaller (`C:\ProgramData\Shabana\unins000.exe /VERYSILENT /SUPPRESSMSGBOXES`): it stops/removes the services via NSSM and `pg_ctl`, deletes `bin\`, `www`, `www-src`, `logs`, `Shabana.url`, and — by its own design — **keeps `data\`, `backups\`, `config\`** (see `setup.iss` `[UninstallDelete]` comment). If the old uninstaller is missing, the helper removes the four services itself (`sc delete`) and leaves all files.
4. Rename the leftover data folder to `C:\ProgramData\Shabana-legacy-archive\` (a single rename; the content is untouched) and write `legacy-archive.json` `{archivedAt, importedAt, sizeBytes}` into the new data folder.
5. The app shows a quiet banner in Settings after 60 days: «حذف بيانات النسخة القديمة نهائيًا؟ (X ج.ب)». Deletion is a button with a typed confirmation; it is never automatic. The old `.dump` backups inside the archive remain restorable only through mode D (legacy kit) — stated in the banner.
If the user declines retirement, the old services stay disabled (not deleted); import can be re-run.

## 12. Test plan and fixtures

1. **Fixture generator** (`tools/fixtures`): a script (run once by a developer or in CI on Linux) that spins up PostgreSQL 18, applies `platform-bootstrap.sql`, `pc-prelude.sql`, migrations `0001`…`0035` (with the PC-edited `0032`) and `0100`/`0101`, then runs a deterministic scenario script (POs with conversions across two branches, partial/over-payments, wallet applications, discounts, cash movements, an opening-stock adjustment, attachments with bytes, 3 users incl. an Arabic-password user created through `pc_create_user`) and a randomised volume pass. Output: a `pg_dump -Fc` fixture committed under `tools/fixtures/` (small) plus the generator.
2. **Importer integration test** (`legacy-import` CI job, Postgres 18 service container): restore the fixture, run `LegacyImportService` end to end headless, assert §9 passes with zero red lines.
3. **Negative tests:** (a) mutate one `qty_delta` in the source after reading counts (digest mismatch must fail), (b) an order total changed by 1 piaster, (c) a user without `auth.users`, (d) duplicate usernames derived, (e) attachment missing bytes, (f) source at migration `0030` (below floor), (g) cancelled at each stage, (h) crash simulated at each swap step, (i) mode B (service stopped) and mode D (dump only) on a CI container with the legacy kit, (j) disk full simulated.
4. **bcrypt known answers** (§7), including 6- and 10-cost, Arabic passwords, empty/NULL hash.
5. **Rounding property test:** random `numeric(12,2)` x `numeric(12,3)` pairs: PostgreSQL `round(q*p,2)*100` equals the schema formula `(qty_milli*price+500)/1000`; random discount percentages equal `(subtotal*bp+5000)/10000`.
6. **First real test** is the owner's pilot copy of the actual shop PC (Phase 15): the build agents do not have a real old install; fixtures are an approximation.

## 13. Edge cases

| Situation | Behaviour |
|---|---|
| Old app still open in a browser / old services running | Installer already stopped web-facing services; a read-only snapshot is consistent regardless; wizard warns to close the old app and records the snapshot time |
| Two organizations in the old DB | chooser (§2); others reported |
| `profiles` empty (setup never completed) | stop: «لا توجد بيانات لاستيرادها» → offer fresh start |
| Order without lines / PO without conversions | imported as is (valid states) |
| Sales order `placed/closed` but stock movements missing (hand-edited DB) | digest/stock checks use ledgers as truth; the invariant "net sale movements = lines of placed/closed orders" fails → red line, import blocked, details shown; no auto-repair of history |
| Negative stock or drawer in the source (floors were bypassed) | invariants fail → blocked with the offending rows (the target triggers would reject the ledger on later writes) |
| Payment parent missing | kept, reported |
| Time zone setting unusual | default to Cairo + warning |
| Huge attachments table | streamed; memory flat; progress by bytes |
| Source password file unreadable even after elevation | mode D if a dump exists, else message |
| User starts the import twice | second attempt builds a new staging file; only one `swap.json` may exist |
| Native app already imported once | Entry B hidden (business data exists); legacy data page offers "open archive folder" only |
| Old edition not found | wizard silently shows only "start fresh"; Settings entry C lets the user choose a folder or a `.dump` manually |

## 14. Required changes elsewhere (summary)

- `04 §8.6` **S-1** (bcrypt prefix in the `users.password_hash` CHECK) and **S-2** (payment-born cash rows dated by `paid_at`) are already applied in `sqlite/schema.sql` and must be carried unchanged into the embedded baseline before Phase 12.
- `04 §11.3` installer behaviour (leave `ShabanaPostgres` running, disable the rest, keep old folders, legacy kit component, `LegacyDetected` marker).
- `AuditService.Describe` must understand rows with `"_legacy":true` (money in pounds, no `_piasters` keys, `entity` = old table names, same set).
- A label group `IMPORT`/`LEGACY` (wizard texts, report lines, retirement banner) in `labels.native.ar.json`.
