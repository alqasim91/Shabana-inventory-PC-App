# 04 — Native architecture: .NET + WPF + SQLite (conversion-readiness)

Status: design document for the build agents. **No code is written here.** Read after `01`, `02`, `03` and the files in
`sqlite/`. Where this document says "03 §x" it means a section of `03-DB-PORTING-NOTES.md`; "02 §x" is
`02-FUNCTIONAL-SPEC.md`. Rules are **referenced, not copied**: if this file and 02/03 disagree about a business rule, 02/03 win and this
file is the bug. If they disagree about a **web defect**, this file (section 13) wins, because it records the decision to fix it.

Product rule that overrides everything: **the end user never runs a command, edits a file, starts a service, opens a port or touches
the database.** Every operational need (setup, backup, restore, upgrade, repair, password recovery, problem report) is a button
in the app or a step in the installer. Section 16 lists the audit of this rule.

---

## 1. Decisions summary

"Owner" = already decided by the owner. "Proposed" = this document's decision; reasoned below; the owner may veto (all owner questions are now resolved: see `00-README.md` decisions L-xx).

| # | Topic | Decision | Status |
|---|---|---|---|
| D1 | Stack | .NET + WPF (C#), RTL, ar-EG, IBM Plex Sans Arabic, EGP | Owner |
| D2 | Target framework | **`net10.0-windows` (.NET 10 LTS, supported to Nov 2028).** Decided 2026-10-18: .NET 8 LTS leaves support on 10 Nov 2026, so v1.0 must not ship on it. One property in `Directory.Build.props`; package pins in section 2 follow the 10.x line | Decided |
| D3 | Database | SQLite for everything incl. users/auth, one file, WAL | Owner |
| D4 | Access layer | Dapper over Microsoft.Data.Sqlite (no EF Core) | Proposed |
| D5 | Password hashing | Argon2id (Konscious) as written in `schema.sql`; PBKDF2-SHA256 fallback (built in). No bcrypt anywhere: there is no import (L-24) | Proposed |
| D6 | Printing | Code-built `FixedDocument` pages (A4, RTL) shown in a viewer and printed through WPF; PDF export = pages rasterised at 200 dpi into an in-house minimal PDF writer. No third-party PDF library | Proposed |
| D7 | Charts | No chart library in v1 (the spec has only horizontal bar lists and progress bars) | Proposed |
| D8 | Data location | `C:\ProgramData\ShabanaInventory\` (per machine, ACL `Users:Modify`), not `%LocalAppData%`, and not the old `C:\ProgramData\Shabana\`; backups default to `C:\ProgramData\ShabanaInventory\Backups` (changeable in settings) | Proposed |
| D9 | Installer | Keep Inno Setup 6 (not MSIX); self-contained `win-x64` publish; per-machine; Authenticode signing step that is optional and skipped when no certificate secret exists (the owner buys and holds the certificate, L-13) | Decided |
| D10 | Single machine | **One PC only**, one process, one DB file. LAN / multi-PC access and the public tunnel link are **dropped** (owner-confirmed, L-14) | Owner |
| D11 | Auth recovery | Recovery key printed at setup + a UAC-gated, logged "Windows administrator" reset, **enabled** (owner-confirmed, L-18; the owner can still switch it off). No master password, no vendor backdoor | Owner |
| D12 | Money / qty | `Money` = `long` piasters, `Qty` = `long` milli, `Int128` intermediates, never `double`/`decimal` in the domain | Per 03 §2 |
| D13 | Tenancy | Single business; no `org_id`, no `/platform` | Per 03 §1 |
| D14 | Time | UTC instants + business-zone calendar days, computed with NodaTime tzdb (not Windows tz) | Proposed |
| D15 | Hard deletes | None for orders, contacts, items, users, sites (reversal only, owner-accepted, L-21); payments deletable only with compensation, as in the web UI | Owner |

---

## 2. Platform, packages, pins

**Runtime/OS.** 64-bit Windows 10 version 1809 (build 17763) or later, and Windows 11; **x64 only** (owner-confirmed, L-29). No x86 or ARM64 build is produced. .NET 10 does not run on Windows 7 / 8 / 8.1. The app is **self-contained** (the .NET runtime is shipped inside the install), so the
end user installs nothing else. Publish flags: `-r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishTrimmed=false`
(WPF does not support trimming). Not single-file (WPF native libraries; start-up cost of self-extraction). Expected install size 150–200 MB unpacked,
roughly 60–70 MB as an installer.

**Pins.** Use Central Package Management (`Directory.Packages.props`), commit `packages.lock.json` for every project and restore with
`--locked-mode` in CI. The versions below were chosen from the author's knowledge of NuGet; this environment has no NuGet access, so the build agent
must **confirm each exists and is not vulnerable on day 1 (`dotnet list package --vulnerable --include-transitive`) and then freeze it**.
Packages that track the runtime (Microsoft.Data.Sqlite, Microsoft.Extensions.*, Npgsql) use the **10.0.x** line to match D2 (`net10.0-windows`); take the latest patch.

| Package | Used by | Version | Why |
|---|---|---|---|
| Microsoft.Data.Sqlite | Data | 10.0.x | Official ADO.NET provider; bundles `SQLitePCLRaw.bundle_e_sqlite3`. App must assert `sqlite_version() >= 3.38` at start (schema uses STRICT, generated columns, RETURNING; developed on 3.45.1) |
| Dapper | Core/Data | 2.1.35 | Thin mapper; see D4 |
| CommunityToolkit.Mvvm | App | 8.4.0 | Source-generated `[ObservableProperty]` / `[RelayCommand]`, messenger |
| Microsoft.Extensions.Hosting | App | 10.0.x | Generic Host: DI, logging, `BackgroundService` for the idle monitor and due-expense refresh |
| Serilog.Extensions.Hosting, Serilog.Sinks.File | App | latest stable (confirm day 1) / 5.0.0 | Rolling log files in `logs\`, 14 days |
| Konscious.Security.Cryptography.Argon2 | Data | 1.3.1 | Pure-managed Argon2id (D5) |
| NodaTime | Core | 3.2.x | Embedded tzdb: correct Cairo DST rules regardless of Windows updates (Egypt changed DST rules twice in ten years) |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Shouldly, coverlet.collector | Tests | 2.9.x / 2.8.x / 17.11.x / 4.2.x / 6.0.x | Shouldly, not FluentAssertions (v8+ is commercial) |
| FlaUI.UIA3 | Tests.UI | 4.0.0 | Windows UI Automation smoke tests in CI |
| Inno Setup | installer | 6.x (record the exact version in the workflow `env`) | D9 |

No other UI library: plain WPF + resource dictionaries (own `Theme.xaml`). Reason: third-party WPF kits have uneven RTL support, and the spec's UI is
forms, tables and print outputs. **Culture/NLS:** set `System.Globalization.UseNls=true` in `runtimeconfig` so sorting/casing behaves the same on
every Windows build (no ICU dependency); **do not rely on culture for digits** — see section 4.6.

---

## 3. Solution layout

The whole tree below lives in the **`native/`** folder at the root of this repository (`Shabana-inventory-PC-App`, decision L-28); paths are relative to `native/`. The old edition stays on a `legacy/pc-edition` branch for hot-fixes. CI workflows live in the repo's `.github/workflows/` with `working-directory: native`.

```
Shabana.sln
Directory.Build.props         TFM (D2), LangVersion, Nullable=enable, TreatWarningsAsErrors, analyzers
Directory.Packages.props      all pins (section 2)
global.json                   SDK pin
.editorconfig  nuget.config   (nuget.config: nuget.org only, package source mapping)
src/
  Shabana.Core/               net10.0, no WPF, no SQLite reference
    Domain/                   Money, Qty, Bp (basis points), enums, DTO records, Permission constants (38 keys, 02 §5.2)
    Abstractions/             I*Service interfaces, IDb / IUnitOfWork, IClock, ICurrentUser, IAuthorizer, IPasswordHasher
    Authorization/            Guard attributes, Authorizer, SiteScope
    Services/                 one class per service (section 7.5) — the business recipes R1-R13 live here
    Formatting/               ArabicNumber, MoneyFormat, QtyFormat, ArabicDate, SearchNormalizer
    Validation/               PhoneEg, Username, PasswordPolicy, DiscountRules, PoPricing (bidirectional)
    Calendar/                 BusinessCalendar (NodaTime), BusinessZones (IANA list from 02 §3)
    Errors/                   DomainException, ErrorCode (from 03 §9), ErrorMessages
    Resources/                labels.ar.json (copy of resources/labels.ar.json), labels.native.ar.json (new native-only labels)
  Shabana.Data/               net10.0
    Sqlite/                   ConnectionFactory (pragmas), SqliteDb : IDb, UnitOfWork (BEGIN IMMEDIATE + app_context)
    Migrations/Scripts/       0001_baseline.sql (= sqlite/schema.sql, both phases), 0002_*.sql ... EmbeddedResource
    Migrations/               MigrationRunner, BalanceFingerprint, BaselineSplitter (Phase 1 / Phase 2 split, used by salvage and repair)
    Security/                 PasswordHasher (Argon2id / PBKDF2), RecoveryKey, SessionTokens
    Maintenance/              BackupService, RestoreService, IntegrityService, ExportService, StartupBackupPolicy, Diagnostics
  Shabana.App/                net10.0-windows, WPF, OutputType WinExe
    App.xaml(.cs)  Startup/   single-instance, host builder, startup gate (section 4.3)
    Shell/                    MainWindow, Sidebar, Header (site switcher, bell, search, user menu), LockOverlay
    Views/ ViewModels/        one pair per screen (section 14)
    Controls/                 DataTable (filters/sort), ArabicDatePicker, NumberInput, MoneyText, SiteBadge, Toast host, Modal host
    Printing/                 PrintLayoutEngine, document builders, PdfPagesWriter, PrintPreviewWindow
    Services/                 NavigationService, DialogService, ClipboardService, ImageCompressor (attachments)
    Resources/                Theme.xaml, Fonts/IBMPlexSansArabic-*.ttf, Icons/, app.manifest (PerMonitorV2)
tests/
  Shabana.Tests/              xunit: Core + Data + services against a real temp SQLite file
  Shabana.Tests.UI/           FlaUI smoke (wizard, login, lock, print preview opens)
installer/
  Shabana.iss  lang/Arabic.isl  assets/      Inno Setup (section 11)
tools/
  LabelsGen/                  generates Labels.g.cs from labels.ar.json (+ native file) at build
  fixtures/                   N-1 database fixtures for migration tests
.github/workflows/            build-native.yml (section 11.6)
```

Dependency rule (enforced by an architecture test): `Core` references nothing of ours; `Data` → `Core`;
`App` → `Core`, `Data`; `Tests*` → everything. `Core` may reference `System.Data.Common` and Dapper but never
`Microsoft.Data.Sqlite`. Views never call `Data` types: view-models depend on `I*Service` interfaces only (CLAUDE.md "no inline queries in components").

**Why service implementations sit in `Core`, not `Data`.** The recipes (03 §9) are business logic whose steps are SQL statements in one transaction; splitting
each into repository interface + implementation doubles the code and hides the transaction boundary. `Core` holds the services and their SQL text;
`Data` supplies connections, transactions, migrations, security primitives and maintenance. Tests run the real services against a real SQLite file.

---

## 4. Application architecture

### 4.1 MVVM (CommunityToolkit.Mvvm)

- View-model-first: `NavigationService.GoTo<SalesOrderDetailViewModel>(id)` swaps `ContentControl.Content`; a `DataTemplate` per view-model type chooses the view.
  There are no URLs; the route table in 02 §5.5 becomes the screen table in section 14 and the `HomeRedirect` order is implemented once in `LandingResolver`.
- `ObservableObject` base with `IsBusy`, `Error`; commands are `AsyncRelayCommand` with `CanExecute` bound to **permission + state** (so a forbidden button is
  disabled **and** hidden: `Visibility="{Binding Can[sales.invoice], Converter=...}"` through `IPermissionView`, CLAUDE.md rule 9, the UI half. The service guard is the enforcing half).
- Messenger (`WeakReferenceMessenger`) for cross-screen events only: `SiteChanged`, `DataChanged(entity)` (refresh lists after a write), `SessionLocked`, `DueExpensesChanged`.
- Modals: `IDialogService.ShowAsync<TViewModel>(…)` renders a view-model inside a modal host layer (dimmed overlay, Esc closes, Enter submits). Helpers: `Confirm`, `ConfirmDanger`
  (reversals/deletes), `AskReason` (mandatory text: void expense, skip due, manual cash, adjustment). Toasts auto-dismiss at 3.5 s (02 §3).
- Threading: all service calls are `async`; each runs on the thread pool; the UI thread never touches a connection. One in-process write gate (`SemaphoreSlim(1)`) serialises write
  transactions so UI actions never busy-wait on each other; `BEGIN IMMEDIATE` + `busy_timeout` cover other processes.
- Lists load whole (like the web, 02 §6 "no paging"): the DataTable is virtualised (`VirtualizingPanel`, recycling) and filters/sorts in memory with Arabic collation. Budget: 10,000 rows < 200 ms to filter.

### 4.2 Dependency injection

Microsoft.Extensions.DependencyInjection through the Generic Host. Lifetimes: `Singleton` — `IDb`, `IClock`, `IBusinessCalendar`, `ISessionContext` (current user), `INavigationService`,
`IDialogService`, all `I*Service` (stateless; the current user comes from `ISessionContext`), `StartupBackupPolicy` (daily-at-start decision, no timer); `Transient` — view-models. Services are wrapped by a
`DispatchProxy` that enforces the `[Guard]` attribute of each interface method (section 6.6). `IClock` is injectable so tests control "now" and the business day.

### 4.3 Start-up sequence (deterministic; every failure path is a screen, never a stack trace)

1. **Single instance** (section 5.5). Second launch focuses the first and exits.
2. Resolve data dir (section 5.1; `--data-dir` / `SHABANA_DATA_DIR` override for tests only). Create sub-folders if missing. Set up logging.
3. Global handlers: `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` → Arabic error dialog with **«تصدير تقرير المشكلة»** (section 9.7); never shows exception text to staff.
4. **Recovery journals:** if `restore\swap.json` exists, finish or roll back the interrupted file swap deterministically (section 9.2) before opening the DB.
5. **Open DB** (create if absent): apply pragmas (5.2); assert SQLite version; run `MigrationRunner` (section 8). Outcomes: *created* → first-run; *upgraded* (a "database was updated" toast with the pre-migration backup name); *up to date*; *newer than the app* → blocking screen "هذه البيانات أنشئت بإصدار أحدث من البرنامج"; *failed* → rolled back automatically, app shows the cause and the safe-copy location, offers "تصدير تقرير المشكلة".
6. `PRAGMA quick_check` (always; skipped above 1 GB, then weekly). Failure → **Recovery screen** (section 9.5), not the login.
6b. **Daily backup:** if first run is complete and no successful `auto` backup exists for today's business date, take it now (section 9.2), before any login, so it precedes the user's work.
7. If `app_meta.first_run_completed = 0` → **First-run wizard** (section 9.1). Otherwise → **Login** (section 6).
8. After login: start background jobs (idle monitor, due-expense refresh), show the dashboard/landing page.

### 4.4 UI shell

Desktop only (the web's mobile layout, bottom tab bar and `MoreSheet` are dropped, section 14). Window: min 1100x680, designed for 1366x768 laptops and up; per-monitor DPI v2 manifest. Right-hand sidebar (248 px)
with the two nav sections and the business name under the app name (02 §6), header with site switcher («كل الفروع» + permitted active branches), search (`Ctrl+K`), due-expenses bell (`expenses.view`),
user menu (name, role, change password, lock, sign out). The selected site is remembered per Windows user in `%AppData%\ShabanaInventory\ui.json`
(purely cosmetic: selected site, window size, column widths; not business data, not in backups). If the stored branch no longer exists → "all".

### 4.5 RTL, language, fonts

Root `FlowDirection="RightToLeft"`, `Language="ar-EG"` (override `FrameworkElement.LanguageProperty` metadata at start), `TextOptions.TextFormattingMode="Ideal"`. Thread culture `ar-EG` only for **sorting/comparison**. Fonts:
IBM Plex Sans Arabic (SIL OFL — redistribution and embedding allowed; ship `Regular/Medium/SemiBold/Bold` TTFs as `Resource`, referenced `pack://application:,,,/Resources/Fonts/#IBM Plex Sans Arabic`). Embedded in printed XPS/FixedDocuments automatically.

### 4.6 Numbers, dates, money formatting (do not trust .NET culture)

`.NET` formats numbers with Latin digits even for `ar-EG`; the web got Arabic-Indic digits from `toLocaleString('ar-EG')`. Therefore **all display formatting goes through `Formatting/`** with fixed tables, identical on every PC:

- Digits `٠١٢٣٤٥٦٧٨٩`, thousands `٬`, decimal `٫`, minus `−` (U+2212), percent `٪` (02 §3).
- `MoneyFormat.Format(Money, currency)`: integer amounts without fraction, otherwise exactly 2 digits, then space + symbol (`١٢٬٣٤٥٫٥٠ ج.م`); the 12 currency symbols of 02 §3 live in a table.
- `QtyFormat.Format(Qty, unit?)`: integer as is, otherwise up to 3 decimals with trailing zeros trimmed, optional unit label (`كجم` / `وحدة`).
- `ArabicDate`: own month-name table (`يناير … ديسمبر`), short (`٥ سبتمبر`) and long (`٥ سبتمبر ٢٠٢٦`); calendar days are `YYYY-MM-DD` strings that are **never** time-zone shifted; instants are shown in the business zone.
- Input: `NumberInput` accepts Arabic-Indic/Persian digits, `٫ , ،` as the decimal point, drops `٬` and spaces, allows a leading `-` where a signed value is legal (02 §3); it parses to `Money`/`Qty` from the **text** (no `double`).
- `SearchNormalizer` and Arabic collation per 02 §3 (digits → ASCII, strip tashkeel/tatweel, `أإآٱ→ا`, `ة→ه`, `ىئ→ي`, `ؤ→و`; AND-of-terms). Unit tests use golden vectors lifted from the spec.
- Golden tests (Phase 1) pin every example in 02 §3, because a shop reads these numbers all day.

### 4.7 Labels

`resources/labels.ar.json` (62 KB, all groups of `labels.ts`) is the single source; `tools/LabelsGen` turns it at build time into `Labels.g.cs`: nested static classes with `const string`, and the 16 `_templates` as methods
(`Labels.Table.ShowingCount(int shown, int total)`) implemented with the Arabic formatter. **Native-only labels** (login lockout, recovery, wizard, backup, restore, integrity, lock screen, change password, update messages, error codes of 03 §9)
go in `labels.native.ar.json` with the same generator; groups to add: `LOCK`, `PWD`, `RECOVERY`, `WIZARD`, `BACKUP`, `RESTORE`, `INTEGRITY`, `ERR` (one entry per `ErrorCode`), `DIAG`. Existing keys that mention web concepts
(`AUTH.email`, `AUTH.orgCode*`, `USERS.createNote`, `ERROR_BOUNDARY` update-available state, `COMMON.offlineBanner`, `PLATFORM.*`) are not generated. **No Arabic string literal may appear outside the two JSON files** (analyzer check in CI).

---

## 5. Data location, file handling, concurrency

### 5.1 Where the data lives (decision D8)

`C:\ProgramData\ShabanaInventory\` (resolved with `Environment.SpecialFolder.CommonApplicationData`):

```
db\shabana.db  (+ shabana.db-wal, shabana.db-shm while open)
Backups\auto\  Backups\manual\  Backups\pre-migration\  Backups\pre-restore\   (default backup root `C:\ProgramData\ShabanaInventory\Backups`; changeable in Settings, 9.2)
restore\       staging/journal for restore
logs\          shabana-YYYYMMDD.log (14 days)
exports\       default target for CSV/PDF exports
config\machine.json   { backupRoot, offsiteRoot, backupKeyDpapi, firstInstalledVersion }  (machine-specific, non-secret except the DPAPI blob)
```

Why ProgramData, not LocalAppData: shop PCs have several Windows accounts (counter, owner, "admin"); `%LocalAppData%` is per account and would give each account its **own empty database**. The old edition also used ProgramData
for the same reason, and an ASCII path avoids Arabic Windows user names in paths. Why not the old folder `C:\ProgramData\Shabana\`: it holds the old edition's PostgreSQL data, config and backups, which the installer never touches or reads (no import, L-24); a new
sibling name cannot collide. Why not `Program Files`: not writable by standard users.

ACL: the installer creates the tree and grants `BUILTIN\Users:(OI)(CI)Modify` on it (Inno `[Dirs] Permissions: users-modify`). Consequence, stated plainly: the database is **not encrypted** and any Windows account on the PC can read the file; in-app roles protect against
casual access and mistakes, **not** against someone with Windows access. Optional later hardening: SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher`), which would also invalidate the Windows-administrator recovery path (D11) — decide before enabling.

The installer provisions the **folders and permissions**; the **database file is created by the app on first launch** through the same `MigrationRunner` used for every upgrade (one code path; no stale pre-built DB inside the installer to keep in sync; first launch takes about a second). The end user never sees a difference.

### 5.2 Connection settings (`ConnectionFactory`, applied on **every** connection open — `foreign_keys` is per connection)

```
PRAGMA foreign_keys = ON;        PRAGMA journal_mode = WAL;        PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;      PRAGMA recursive_triggers = OFF;  PRAGMA wal_autocheckpoint = 1000;
PRAGMA journal_size_limit = 67108864;  PRAGMA temp_store = MEMORY;  PRAGMA trusted_schema = ON;   -- schema uses triggers/views
```

`synchronous=FULL` (03 allows NORMAL or FULL): shop PCs lose power, and a committed sale must survive it; the throughput cost is irrelevant at this scale. `PRAGMA optimize` on clean exit and after migrations; `wal_checkpoint(TRUNCATE)` after backup and on exit.
Connection string: `Data Source=…;Mode=ReadWriteCreate;Cache=Private;Pooling=True;Foreign Keys=True`. The DB must be on a **local fixed disk** (WAL does not work on network shares): at start, if the path's drive type is not `Fixed`, show a blocking explanation.

### 5.3 Transactions

Section 7.2. Every business operation is **one** `BEGIN IMMEDIATE` transaction; reads that must be consistent (dashboard, financial summary, reports) run in one deferred transaction (single snapshot in WAL).

### 5.4 File locking / other processes

The DB file is opened only by the app. Anti-virus scanners and Windows backup can hold brief locks: `busy_timeout` plus a retry wrapper (3 attempts, 250 ms) on `SQLITE_BUSY/LOCKED` for opens and checkpoints. Backups use the SQLite **online backup API** (`SqliteConnection.BackupDatabase`), never a file copy of a live WAL database.

### 5.5 Single instance

Named mutex `Global\ShabanaInventory.SingleInstance` (also used as Inno `AppMutex` so the installer waits for / closes the app). `Global\` so a second Windows session (fast user switching) cannot open the same database concurrently
and run a second start-up backup; that user sees «البرنامج مفتوح في جلسة مستخدم آخر على هذا الجهاز». Same-session second launch signals the first through a named `EventWaitHandle` to restore/focus its window.

### 5.6 Concurrency and LAN (decision D10)

v1 supports exactly one running instance on one PC. The old edition let other devices on the shop Wi-Fi reach the server PC through Caddy (01 §4); **that capability is dropped, confirmed by the owner: one PC only** (L-14). It is not emulated by
sharing the `.db` file over SMB (corruption risk — forbidden). Should the owner ever ask for LAN, the design hook is already in place: all UI code talks to `I*Service` interfaces, so a second implementation (`HttpServiceClient`) plus a host mode (a Windows service or the main app running Kestrel on the same
`Core` services, token auth over the existing `users`/`user_sessions` tables, TLS with a locally generated certificate) can be added without touching view-models. That is a separate phase after 1.0 (listed in `00-README.md`).
Within one instance: the write gate + `BEGIN IMMEDIATE` serialise writers, which is also what makes the read-modify-write recipes (opening stock R8, opening balances R9/R10) race-free (03 §6 row 10).

---

## 6. Authentication, session, authorization

Schema: `users`, `user_sessions`, `auth_events`, `user_permissions`, `user_sites`, `permission_keys`, `role_presets` (03 §1, §3; `schema.sql` section 2). Authorization rules are **not repeated here**: 02 §5 (what), 03 §5 (RLS → app layer matrix) are the specification; section 6.6 maps each to a guard.

### 6.1 Login screen

Fields: `AUTH.username`, `AUTH.password` (no business code, no email: 02 §4.1 differences). Single failure message `AUTH.invalidCredentials`. Business name (from `business_settings`, readable pre-login) shown under the app name; this is the only pre-login data access (`BusinessSettingsService.GetPublicName()`, marked `[Anonymous]`).
Steps in `AuthService.Login` (one `BEGIN IMMEDIATE` transaction): lowercase+trim username → load user → **always run a hash verification** (against a constant dummy hash when the user is unknown, to avoid timing/user enumeration) → refuse if `active = 0` (new label «الحساب موقوف، راجع المدير»; fixes DIV-8), `locked_until > now`, or `password_hash IS NULL` outside the wizard →
on success: reset `failed_attempts`, set `last_login_at`, insert `user_sessions` (random 32-byte token held in memory; only `sha256(token)` stored; absolute expiry 12 h), `auth_events('login_ok')`; **if the stored hash uses weaker parameters than the current ones (PBKDF2 fallback or lower Argon2 cost) → rehash with the current Argon2id parameters in the same transaction**; on failure: `failed_attempts+1`, 5th consecutive failure sets `locked_until = now + 15 min`, `auth_events('login_failed'/'locked')`.
Lockout is per account; there is no IP concept on a desktop app. Time source for lockout is the app clock; changing the Windows clock backwards is a known limitation (documented, low risk on a local single-user PC).

### 6.2 Password policy and change

New passwords: >= 8 characters, not equal to the username, not a single repeated character; Arabic letters allowed (UTF-8, NFC-normalised before hashing). Argon2id parameters `m=65536 KiB, t=3, p=1` (about 100-250 ms on a counter PC; tune in Phase 3 so login stays under 400 ms on the slowest target; if memory allocation fails, fall back to PBKDF2-SHA256 600,000 iterations via `Rfc2898DeriveBytes.Pbkdf2`). Encoded as the PHC-style strings the schema CHECK expects (`$argon2id$v=19$m=…,t=…,p=…$salt$hash`).
- **Must-change-password:** after login, if `must_change_password = 1`, the only reachable screen is *Change password* (cannot be dismissed; "sign out" is available). Used for admin-reset passwords and the new-user temporary password.
- **Self-service change** (user menu, fixes DIV-9): current password + new + confirm → `AuthService.ChangeOwnPassword`; revokes the user's other sessions; `auth_events('password_changed')`.
- **Admin reset** (`UserService.ResetPassword`, guard `users.manage`; a non-admin cannot reset an admin, 02 §4.3 rule 3): the admin types or generates a temporary password; `must_change_password = 1`; the target's sessions are revoked; `auth_events('password_reset', detail 'by:<admin>')`.
- Admin deactivation (`active = 0`) revokes sessions immediately (the next service call fails the session check and the app drops to the login screen).

### 6.3 Session and idle auto-lock

- One signed-in user at a time. `ISessionContext` holds `UserId` and the session token; **every service call** re-validates (session row not revoked/expired **and** `users.active = 1`) and re-reads the user's role, permissions and branch scope with three tiny indexed queries (`v_user_permissions`, `v_user_site_access`). No cache: a permission removed by an admin takes effect on the user's very next action. (This is the DB's RLS-per-statement behaviour, reproduced.)
- `last_seen_at` is touched at most every 60 s.
- **Idle auto-lock:** default 10 minutes of no keyboard/mouse input inside the app (setting `app_meta.idle_lock_minutes`, 1-60, admin may disable). The lock overlay hides all content (screens stay in memory so an unfinished form is not lost), shows the user's name and a password box; unlock requires the **same user's** password and uses the same failure counters; «تبديل المستخدم» signs out (prompts about unsaved forms). Printing and background backup are unaffected.
- Sign-out clears view-model caches and revokes the session row.

### 6.4 Recovery — when the only admin forgets the password (decision D11)

Two paths, **no master password, no vendor reset, nothing the vendor can compute**:

1. **Recovery key (primary).** Generated in the first-run wizard (mandatory before the app can be used): 160 random bits from `RandomNumberGenerator`, shown as 32 Base32 characters in groups of 4 (`ABCD-EFGH-…`), displayed **once**, with *Print* and *Save to file…* buttons and a confirmation step (retype the last group). Stored only as an Argon2id hash in `app_meta` (`recovery_key_hash`, `recovery_key_created_at`).
   Use: login screen → «نسيت كلمة المرور؟» → enter key → choose which **admin** account → set a new password → the key is **rotated** (a new key is shown and must be printed) → `auth_events('password_reset', detail 'recovery_key')` + audit entry. Attempts are rate-limited (5 failures → 15 min, doubling, state in `app_meta`).
   The printed sheet is generated by the app («ورقة الاسترداد»: business name, date, the key, and — if backup encryption is enabled — the backup key, section 9.3) and should be kept in the shop's safe.
2. **Windows-administrator reset (secondary; **enabled**, owner-confirmed L-18; the owner can still switch it off in Settings and is told so in the wizard).** The reason it exists: the old edition's `reset-admin.ps1` served exactly this purpose, and the database file is readable by any Windows administrator anyway, so this path adds **no** new exposure — it is the same trust boundary expressed through a UI. Flow: «نسيت كلمة المرور؟ → استخدام حساب ويندوز مسؤول» → the app relaunches itself elevated (UAC) → verifies the process is in the local Administrators group → asks the person to type the business name and a confirmation phrase → resets the chosen admin to a typed temporary password with `must_change_password = 1`. It writes `auth_events('password_reset', detail 'windows_admin:<DOMAIN\user>')` and an audit row, and **every admin sees a banner at next login** («تمت إعادة تعيين كلمة مرور المدير من ويندوز بتاريخ …»).
   With SQLCipher (future) this path would be removed.
3. If both are unavailable the only route is **restore a backup** (9.2) that predates the lost password; nothing else exists, by design.

### 6.5 Roles and permissions at runtime

`Permission` constants for the 38 keys (02 §5.2). `IAuthorizer` offers `Require(perm)`, `RequireAny(perms)`, `RequireAdminRole()`, `RequireSite(siteId?)` (NULL site passes, 03 §5), `AllowedSiteIds()` and `SiteFilterSql(alias)` (returns the `site_id IN (…)` predicate/CTE, so a repository cannot forget it: any SQL touching a branch-owned table is built through `SiteScope` helpers, and a Roslyn/regex test in CI fails on a `FROM stock_movements|cash_movements|payments|sales_orders|…` string without the helper). Admin role bypasses key checks (`has_perm` semantics) but **not** the rules in 6.6 that are role checks by design.

### 6.6 Guard enforcement and the RLS → guard map

Mechanism. Every method of every `I*Service` carries exactly one declarative attribute: `[Guard(Perm.X)]`, `[GuardAny(...)]`, `[GuardAdminRole]`, `[GuardSignedIn]`, or `[Anonymous]`. A `DispatchProxy` decorator evaluates it before the call (also checks session validity and `active`). **Data-dependent guards** (branch of the row, order status, "cannot grant a key you lack") are inline calls in the implementation and listed in the table's last column. A reflection test fails the build if a public interface method has no guard attribute, and the **guard matrix test** calls every method as a user holding nothing, as a user holding only the required key, and as a user lacking exactly that key, asserting `ForbiddenException` (Arabic «ليس لديك صلاحية…») for the first and third and success-or-domain-error for the second.

Coverage of 03 §5.2 (all **82 live policies**, named; PG-only `storage.objects` policies included) and 03 §5.4/§5.5 (RPC and trigger gates):

| Policy names (03 §5.2) | Native guard | Service.method |
|---|---|---|
| `org_read` | signed-in (name pre-login: anonymous) | `BusinessSettingsService.Get` / `GetPublicName` |
| `org_update` | `settings.manage` | `BusinessSettingsService.Update` |
| `sites_read` | signed-in + `SiteScope` (restricted users see only their branches) | `SiteService.ListVisible` |
| `sites_admin_write`, `sites_admin_update` | `sites.manage` | `SiteService.Create/Update` |
| `sites_admin_delete` | not exposed (sites are never deleted, 02 §2.1) | — |
| `profiles_read` | signed-in for the **id→name directory** (needed for «بواسطة»); the full Users screen needs `users.manage` | `UserService.Directory`, `UserService.List` |
| `profiles_admin_write`, `profiles_admin_update` | `users.manage`; **plus** role rules of 02 §4.3 (only an admin creates/edits an admin; cannot grant keys you lack; cannot change own role/active; last active admin protected — DB triggers also enforce the last two) | `UserService.Create`, `AccessService.SaveAccess` |
| `profiles_admin_delete` | not exposed (users are deactivated, never deleted) | — |
| `permission_keys_read` | signed-in | `AccessService.ListKeys` |
| `user_permissions_read`, `user_sites_read` | own rows: signed-in; others: `users.manage` | `AccessService.GetUserAccess`, `ISessionContext` |
| `user_permissions_write`, `user_permissions_delete`, `user_sites_write`, `user_sites_delete` | `users.manage` (+ rules above), replace-all in one tx (R13) | `AccessService.SaveAccess` |
| `contacts_read`, `phones_read`, `pm_read` | `contacts.view` | `ContactService.List/Get/…` |
| `contacts_write`, `contacts_update`, `phones_write`, `phones_update`, `phones_delete`, `pm_write`, `pm_update`, `pm_delete` | `contacts.manage` (a payment method referenced by a payment cannot be deleted — DIV-18) | `ContactService.Create/Update` |
| `contacts_delete` | not exposed (no UI in web; DIV-6) | — |
| `items_read` | `inventory.view` | `InventoryService.ListItems/GetItem` |
| `items_write`, `items_update` | `inventory.items` | `InventoryService.CreateItem/UpdateItem` |
| `items_delete` | not exposed | — |
| `po_read`, `po_lines_read`, `plc_read` | `purchases.view` | `PurchaseService.List/Get` |
| `po_write` | `purchases.create` | `PurchaseService.CreateGeneral/CreateItemized` |
| `po_update` | `purchases.edit` (+ R6/R7 checks) | `PurchaseService.AdminEditGeneral/UpdateItemized` |
| `po_delete` | not exposed (DIV-6) | — |
| `conv_read` | `purchases.view` + `RequireSite(row.site)` / `SiteScope` | `PurchaseService.Get` |
| `conv_write` | `purchases.convert` + `RequireSite(destination)` | `PurchaseService.AddConversion` |
| `conv_delete` | `purchases.delete` + `RequireSite(row.site)` | `PurchaseService.ReverseConversion` |
| `plc_write` | `purchases.convert` + `RequireSite(destination)` (**fixes DIV-29**: web had no branch check) | `PurchaseService.AddLineConversion` |
| `plc_delete` | `purchases.delete` + `RequireSite(row.site)` (fixes DIV-29) | `PurchaseService.ReverseLineConversion` |
| `so_read` | `sales.view` + `SiteScope` | `SalesService.List/Get` |
| `so_insert` | `sales.draft` + `RequireSite` + always `draft` | `SalesService.CreateDraft` |
| `so_update` (coarse) + trigger gates 03 §5.5 (`so_status_transition`, `so_write_guard`, `so_apply_discount`) | per operation: header/lines of a draft `sales.draft`; header after draft `sales.edit_locked`; draft→invoiced `sales.invoice`; invoiced→placed `sales.place`; placed→invoiced `sales.cancel_placement`; non-`none` discount `sales.discount`; all with `RequireSite(order.site)` | `SalesService.UpdateDraft/AdminEdit/Invoice/Place/CancelPlacement/SetDiscount` |
| `so_delete` | not exposed (DIV-6) | — |
| `sol_read` | `sales.view` | `SalesService.Get` |
| `sol_write`, `sol_update`, `sol_delete` | `sales.draft` and parent status = `draft` (DB trigger also refuses) | `SalesService.CreateDraft/UpdateDraft` |
| `payments_read` | `sales.view` (parent so) or `purchases.view` (parent po) + `SiteScope` | `PaymentService.ListForOrder` |
| `payments_write` | `payments.record` + `RequireSite(drawer)`; also gates `so_overpay`, `po_overpay`, `credit_deposit`, `vendor_credit_deposit` (**fixes DIV-11/G3**) and `settle_contact_opening` | `PaymentService.Add`, `CreditService.Deposit*`, `ContactOpeningService.Settle` |
| `payments_delete` | `payments.delete` + `RequireSite` | `PaymentService.Delete` |
| `sm_read` | `inventory.view` + `SiteScope` | `InventoryService.Movements/StockRows` |
| `sm_write` | `inventory.adjust` + `RequireSite`, `source_type='adjustment'` only | `InventoryService.Adjust` |
| `transfer_read` | `inventory.view` + (`site(from)` or `site(to)`) | `InventoryService.Transfers` |
| `transfer_write` | `inventory.transfer` + `RequireSite(from)` and `RequireSite(to)` | `InventoryService.Transfer` |
| `cm_read` | `cash.view` + `SiteScope` | `CashService.Balance/Movements` |
| `cm_write` | `cash.manual` + `RequireSite`, `source_type='manual'`, reason mandatory | `CashService.ManualMovement` |
| `client_credits_read`, `vendor_credits_read`, `contact_opening_balances_read` | `contacts.view` | `CreditService`, `VendorCreditService`, `ContactOpeningService` reads |
| (RPC gates 03 §5.4) `credit_apply/refund`, `vendor_credit_apply/refund` | `payments.credit` | `CreditService`, `VendorCreditService` |
| (RPC) `set_opening_stock`, `set_contact_opening_balance` | **admin role** (not a key, 02 §5.4) | `InventoryService.SetOpeningStock`, `ContactOpeningService.Set` |
| `order_attachments_read`, `order_docs_read` | `sales.view` for `sale`, `purchases.view` for `purchase` (type-specific; web allowed either) | `AttachmentService.List/Read` |
| `order_attachments_insert`, `order_attachments_delete`, `order_docs_insert`, `order_docs_delete` | `attachments.manage` (fixes G4: `pc_file_*` used a role) | `AttachmentService.Add/Remove` |
| `audit_read` | `audit.view` (route also by key — DIV-3) | `AuditService.List` |
| `expense_categories_read` | `expenses.view` | `ExpenseService.ListCategories` |
| `expense_categories_write`, `expense_categories_update` | `expenses.manage` | `ExpenseService.Create/UpdateCategory` |
| `expense_rules_read` | `expenses.view` + `SiteScope` | `ExpenseService.ListRules/Due` |
| `expense_rules_write`, `expense_rules_update`, `expense_rules_delete` | `expenses.manage` + `RequireSite` (delete refused when the rule has posted expenses/skips — G8; UI offers "deactivate") | `ExpenseService.CreateRule/UpdateRule/DeleteRule` |
| `expenses_read` | `expenses.view` + `SiteScope` | `ExpenseService.List` |
| `expenses_write` | `expenses.record` + `RequireSite` | `ExpenseService.Record` |
| (RPC) `void_expense` | `expenses.void` + `RequireSite`, reason | `ExpenseService.Void` |
| `expense_rule_skips_read`, `expense_rule_skips_write` | `expenses.view` / `expenses.record` + rule's branch permitted | `ExpenseService.Due/Skip` |
| (RPC) `get_financial_summary` | `financials.view` | `FinancialService.Summary` |
| (RPC) `get_expense_summary` | `financials.view` (org-wide) **or** `expenses.view` (confined to permitted branches) | `ExpenseService.Summary` |
| (INVOKER functions) `get_dashboard` etc. | `dashboard.view`, then each section filtered by its own key + `SiteScope` (fixes G5; accountant sees nothing here) | `DashboardService.ForDay` |
| (RPC) `create_itemized_po`, `update_itemized_po`, `admin_update_purchase_order`, `admin_update_sales_order` | `purchases.create` / `purchases.edit` / `purchases.edit` / `sales.edit_locked` | as above |
| (RPC) `pc_create_user`, `pc_first_run_bootstrap`, `pc_needs_setup` | `users.manage` (+ all four roles, fixes G6) / first-run only / anonymous | `UserService.Create`, `SetupService`, `AuthService.NeedsSetup` |

Check performed by the author: all 82 policy names of 03 §5.2 appear above (counted per table: organization 2, sites 4, profiles 4, permission_keys 1, user_permissions 3, user_sites 3, contacts 4, contact_phones 4, contact_payment_methods 4, items 4, purchase_orders 4, po_lines 1, po_conversions 3, po_line_conversions 3, sales_orders 4, sales_order_lines 4, payments 3, stock_movements 2, stock_transfers 2, cash_movements 2, three credit/opening reads, order_attachments 3, storage 3, audit 1, expense_categories 3, expense_rules 4, expenses 2, expense_rule_skips 2 = 82). The CI guard-matrix test enumerates the same list from a fixture so the table cannot rot.

---

## 7. Service layer

### 7.1 Conventions

- Every web service file becomes one C# service (`I<Name>Service` in `Abstractions`, class in `Core/Services`). Components/view-models never contain SQL (CLAUDE.md convention).
- All methods are `async Task<T>`, take a `CancellationToken`, take/return **DTO records** (never entity rows), and throw `DomainException(ErrorCode, message)` for rule violations; `ForbiddenException` for guards; unexpected exceptions are logged and surfaced as a generic Arabic error.
- All writes go through `IUnitOfWork` (7.2). SQL lives next to the service as `const string` or embedded `.sql` files named `<Service>.<Query>.sql`.
- Triggers do the data-integrity work (03 §4.3); services do authorization, validation that needs friendly messages, orchestration, and anything marked SVC in 03 §4.2. After each insert whose row is completed by an AFTER trigger (`order_seq`, `order_code`, `invoice_number`, `payments.site_id`, `po_line_conversions.item_id/po_id`), the service **re-SELECTs** the row (03 §6 row 4).
- Errors: schema raises `RAISE(ABORT,'CODE|text')`; `SqliteErrorMapper` splits at the first `|`, maps `CODE` to `ErrorCode`, and re-queries the figures the Postgres messages interpolated (available quantity, remaining, paid, wallet balance) to build the full Arabic sentence (exact wording per 02 §8). The 33 codes of 03 §9 are an enum; a test asserts every code has a label.

### 7.2 Transactions (`IUnitOfWork`)

```
await uow.RunAsync(user, async tx => { ... });      // write
  1. acquire in-process write gate
  2. open connection (pragmas), BEGIN IMMEDIATE
  3. UPDATE app_context SET actor_user_id = @uid, editing = 0     -- rolls back with the tx (schema.sql header protocol)
  4. run the recipe;  (R5-R7 only) UPDATE app_context SET editing = 1 ... editing = 0 before COMMIT
  5. COMMIT; on any exception ROLLBACK (app_context rolls back too); map SqliteException; release gate
await db.ReadAsync(async q => { ... });             // deferred tx = one consistent snapshot
```

`created_by` columns are set by the service (SQLite has no column default that reads `app_context`, schema header). Audit rows for non-row events (G: PO edits, DIV-23; security events) are inserted by the service with `AuditWriter`. Authorization is checked **before** `BEGIN` when it needs no row data, otherwise inside the transaction against current data.

### 7.3 Money and quantity types

```
readonly record struct Money(long Piasters)    // EGP x 100;  ops: + - negate, comparison; Parse(string) / FromDecimalText; no implicit double
readonly record struct Qty(long Milli)         // kg or units x 1000
readonly record struct Bp(int Value)           // percent x 100 (basis points): 12.5% = 1250
static class MoneyMath
  LineTotal(Qty q, Money unitPrice)  = (Int128)q.Milli * unitPrice.Piasters + 500) / 1000        // round-half-up, 03 §2; checked narrowing
  PercentDiscount(Money subtotal, Bp p) = ((Int128)subtotal.Piasters * p.Value + 5000) / 10000
  PoTotalFromKgAndPrice(Qty kg, Money perKg) = LineTotal(kg, perKg)
  PoPricePerKgFromTotal(Money total, Qty kg) = ((Int128)total.Piasters * 1000 + kg.Milli / 2) / kg.Milli       // kg > 0
  PoKgFromTotalAndPrice(Money total, Money perKg) = ((Int128)total.Piasters * 1000 + perKg.Piasters / 2) / perKg.Piasters   // perKg > 0
```

Rules: no `double`/`decimal` anywhere in Core arithmetic (DIV-12); parsing goes from **text** to scaled integers (inputs with more digits than the scale are rounded half away from zero at the input edge, with a visible rounding hint in the form); `Int128` intermediates, narrowing is `checked`; range limits of 03 §2 are validated before insert (`Qty <= 1,000,000,000 milli` on lines, `Money` unit price `<= 1,000,000,000` piasters, totals `<= 999,999,999,999`). Stock valuation rounds **per row** (`(qty_milli*cost+500)/1000`), never `SUM(q)*cost` (03 §2, C8). Bidirectional PO pricing (02 §7.6) is the pure class `PoPricing` (touch-recency list → derived field) with exhaustive unit tests.
Dapper `TypeHandler`s map `Money`/`Qty`/`Bp`/`DateOnly`(business date)/`Instant`(UTC ISO `YYYY-MM-DDTHH:MM:SS.sssZ`) to the schema's TEXT/INTEGER columns.

### 7.4 Time

`IClock.UtcNow` (Instant); `IBusinessCalendar` (NodaTime `DateTimeZone` from `business_settings.timezone`, a table maps the 18 IANA zones of 02 §3; unknown id → refuse to save): `Today()`, `DayBounds(date) → [startUtc, endUtc)`, `DateOf(instant)`. Every "day D" query binds the UTC bounds (03 C4/C5); defaults ("today" in every form, due-expense "today", dashboard default day) use `Today()`. This implements the 02 §13.6 recommendation and fixes DIV-26.

### 7.5 Services: web file → C# service → methods

"Recipe" refers to 03 §9 (R1-R13) and "C#" cookbook queries to 03 §10. Guard column = section 6.6 (key + site/state checks). Methods marked **NEW** have no web equivalent.

| Web file (02 §11) | C# service | Methods (source) | Primary guards |
|---|---|---|---|
| `access.ts` | `AccessService` | `ListKeys`, `GetUserAccess(userId)`, `GetRolePreset(role)` (reads `role_presets`), `SaveAccess(userId, name, role, active, allSites, siteIds, keys)` (RPC `admin_set_user_access`, recipe R13: one tx, replace-all, all rules of 02 §4.3) | `users.manage` + role rules |
| `admin.ts` | `UserService`, `SiteService` | `UserService.List`, `Directory`, `Create` (`pc_create_user`, all 4 roles, Argon2id, preset seeded by trigger), **NEW** `ResetPassword`, `Deactivate/Activate` (through `SaveAccess`); `SiteService.ListAll`, `ListVisible`, `Create`, `Update` (name/active) | `users.manage`, `sites.manage` |
| `attachments.ts` | `AttachmentService` | `List(orderType, orderId)`, `Add(orderType, orderId, file)` (metadata + `attachment_blobs` in one tx; image compression is done by `App.ImageCompressor` before the call: long edge <= 2000 px, JPEG q 0.82, only if smaller; PDF/images <= 10 MB; mime allow-list), `Read(path)` (incremental blob read), `Remove(id)` | `attachments.manage` / view keys |
| `audit.ts` | `AuditService` | `List(from, to, entity?, action?, limit 500)` + `Describe(row)` (sentence generator, 02 §10) | `audit.view` |
| `cash.ts` | `CashService` | `Balance(siteId?, asOfDate?)` (view / C1), `DrawerBalances()`, `Movements(range, siteId?)` (+ running balance when one branch), `ManualMovement(site, kind: deposit/withdraw/adjust, amount, reason)` | `cash.view`, `cash.manual` |
| `contactOpening.ts` | `ContactOpeningService` | `GetOutstanding(contactId)`, `Set(contactId, kind, amount, asOf, note)` (R9, admin), `Settle(contactId, kind, amount, method, siteId, note)` (R10), `ListEntries(contactId)` (now used by the statement) | admin / `payments.record` (fixes DIV-10) |
| `contacts.ts` | `ContactService` | `List(search?, type?)` (one set-based query on `v_contact_balance`, fixes DIV-32), `Get(id)`, `Create`, `Update` (diff-based update of phones and payment methods keeping row ids, fixes DIV-18), `Balance(id)` (`v_contact_balance`), `Ledger(id, from?, to?)` (section 13 DIV-17), `Statement(id, from, to)` | `contacts.view` / `contacts.manage` |
| `credit.ts` | `CreditService` | `Balance(contactId)`, `Movements(contactId)`, `Deposit` (R3), `Apply(orderId, amount)` (R2), `Refund` (R4). Overpay lives in `PaymentService` (R1) | `payments.record` / `payments.credit` |
| `dashboard.ts` | `DashboardService` | `ForDay(siteId?, date)` → typed DTO built from 03 §10 C1-C7 (cash opening/closing, sales, collections by method, payouts by method, **expenses**, low stock, top items, site drawers, **stock opening/closing movement table**) | `dashboard.view` + section keys |
| `expenses.ts` | `ExpenseService` | `ListCategories/CreateCategory/UpdateCategory`, `List(range, siteId?)`, `Record` (R12), `Void` (R11), `ListRules/CreateRule/UpdateRule/DeleteRule`, `DueList(siteId?)` (C9), `PostDue`, `Skip` (R12), `Summary(from, to, siteId?)` | see 6.6 |
| `financials.ts` | `FinancialService` | `Summary(from, to, siteId?)` (02 §9.3; returns the period, position and monthly blocks; expense column merged server-side) | `financials.view` |
| `inventory.ts` | `InventoryService` | `ListItems(activeOnly)`, `GetItem`, `CreateItem`, `UpdateItem`, `StockRows(siteId?)` (items x branches, `v_stock_balance`), `StockAsOf(site, item, date)`, `Movements(itemId)`, `Adjust` (mandatory reason), `Transfer`, `SetOpeningStock` (R8) | `inventory.*`, admin |
| `organization.ts` | `BusinessSettingsService` | `Get`, `GetPublicName` (anonymous), `Update` (name required, currency from the 12, timezone from the 18) | `settings.manage` |
| `payments.ts` | `PaymentService` | `ListForOrder`, `Add(order, amount, method, accountId?, drawerSiteId?, paidDate, note, allowOverpay)` (insert; when `allowOverpay` and amount > remaining runs R1 for `so_overpay`/`po_overpay`), `Delete` (cash compensation by trigger; **credit-method payments also restore the wallet**, fixes DIV-22/G1), `Sum`, `ListContactAccounts(contactId)` | `payments.record`, `payments.delete` |
| `platform.ts` | — | **None, deliberately.** `/platform` is dropped (02 §7.25, 03 §1) | — |
| `purchases.ts` | `PurchaseService` | `List` (with progress and paid via `v_po_kg_progress`/`v_po_balance`), `Get`, `CreateGeneral`, `CreateItemized` (R7), `AdminEditGeneral` (R6), `UpdateItemized` (R7 + new-total >= paid, fixes DIV-20), `AddConversion` / `ReverseConversion`, `AddLineConversion` / `ReverseLineConversion`; unit of `output_unit` must equal the item's unit (fixes DIV-31) | see 6.6 |
| `reports.ts` | `ReportService` | `Sales(range, siteId?)`, `Purchases(range)`, `Cash(range, siteId?)` (opening = balance at end of from-1), `Balances()` (single query), `ExportCsv(report)` | `reports.view` + underlying keys |
| `sales.ts` | `SalesService` | `List(siteId?)`, `Get`, `CreateDraft` (one tx: header + lines + discount last), `UpdateDraft` (one tx), `AdminEdit` (R5, with B2/B3 fixes), `Invoice`, `Place`, `CancelPlacement`, `SetDiscount`; **no `Delete`** (DIV-6) | see 6.6 |
| `vendorCredit.ts` | `VendorCreditService` | mirror of `CreditService` (R2-R4 vendor side) | as credit |
| (no web file) | `AuthService` **NEW** | `NeedsSetup`, `Login`, `Logout`, `ChangeOwnPassword`, `Unlock`, `RecoverWithKey`, `RecoverAsWindowsAdmin`, `RotateRecoveryKey` | anonymous / signed-in |
| (no web file) | `SetupService` **NEW** | `CompleteFirstRun(request)` — one transaction (replaces `pc_first_run_bootstrap`) | first-run only |
| (no web file) | `MaintenanceService` **NEW** | `BackupNow`, `ListBackups`, `Restore`, `RunIntegrity`, `RunFullIntegrity`, `ExportAll`, `Diagnostics`, `SaveBackupSettings` (section 9) | admin role (`[GuardAdminRole]`; no new permission key is introduced for maintenance) |
| (client side in web) | `SearchService` | `Query(text)` over four groups, 5 hits each, Arabic-normalised AND-of-terms (02 §6), built from the same cached lists | per group's view key |
| (print in web) | `PrintService` (App) | builds document models from the services above; no SQL | — |

Self-check: the 19 files of 02 §11 are all present in column 1 (`platform.ts` as an explicit "none").

### 7.6 Recipes that deserve explicit attention

Implementation follows 03 §9 verbatim (R1-R13), with the corrections of section 13. Two cross-cutting behaviours:
- **Order of writes inside a recipe matters** for triggers: discount last (after lines), `editing=1` window only for R5-R7, wallet compensation row **before** deleting a `credit` payment, cash rows for payments carry `created_at = paid_at` (schema amendment S-2, section 8.6).
- **Payments are dated with a real instant:** the form's date is combined with the current time of day in the business zone (future dates rejected); fixes DIV-24.

---

## 8. Migrations

### 8.1 Mechanism

- Scripts: `Shabana.Data/Migrations/Scripts/NNNN_<name>.sql`, **embedded resources**, strictly increasing, immutable once released. `0001_baseline.sql` = `native-conversion/sqlite/schema.sql` (tables, indexes, reference data, triggers, views). The marker `-- >>> PHASE 1 ENDS HERE` stays in the file: `BaselineSplitter` exposes Phase 1 (tables + reference data) and Phase 2 (triggers + views) for salvage and repair (9.4); there is no data import (L-24).
- Version: `PRAGMA user_version` is authoritative (`schema.sql` already sets 1); `app_meta.schema_version` mirrors it and is written by the same script. A unit test pins the SHA-256 of every released script (`migration-checksums.json`): a changed released script fails CI.
- Reference data that changes between versions (new `permission_keys`, `role_presets`) is added by a migration, never by app code.

### 8.2 Upgrade algorithm (`MigrationRunner`)

1. Read `user_version` = V; target T = highest embedded script. `V = 0` and the file is empty → create (all scripts, no backup needed). `V > T` → **refuse** (newer data; never downgrade).
2. `V = T` → done. `V < T` → **pre-migration backup** via the online backup API to `backups\pre-migration\pre-v{V}-to-v{T}-{timestamp}.shbk`, then verify the copy (open, `integrity_check`, compare row counts + SHA-256 in the manifest). **If the backup cannot be made or verified, the upgrade does not start** and the user is told why (disk space is the usual cause) with the offending folder.
3. Compute the **balance fingerprint** (SHA-256 over per-(site,item) stock, per-site cash, per-contact balance, wallet totals, order totals, from the views) — before.
4. `PRAGMA foreign_keys = OFF` (required by the table-rebuild recipe of 03 §6 row 9), `BEGIN IMMEDIATE`, apply **all pending scripts in order inside this one transaction** (SQLite DDL is transactional; `user_version` is part of the transaction), `PRAGMA foreign_key_check` must return nothing, `PRAGMA integrity_check` must return `ok`, recompute the fingerprint — it **must equal the "before" value unless the script declares `-- shabana:balances-may-change`** — then `COMMIT`; `PRAGMA foreign_keys = ON`.
5. **Any failure at any point → `ROLLBACK`** (the database is logically identical to before). The app then **does not open the business UI**; it shows "تعذر تحديث قاعدة البيانات" with options *retry* and *تصدير تقرير المشكلة*, and the pre-migration backup is left in place. A process crash mid-transaction is rolled back by SQLite's WAL recovery on next open.
6. After commit: `wal_checkpoint(TRUNCATE)`, `PRAGMA optimize`, log; keep the last 5 pre-migration backups (and any younger than 90 days).
7. If a bug is found *after* an upgrade, the documented path is **Restore** from the pre-migration backup (section 9.2) — a button, not a procedure.

### 8.3 Writing migrations (rules for build agents)

Additive changes (new table/column/index/trigger/permission key) are plain statements. Changing a CHECK list (new enum value) or a column type = the 12-step rebuild (create new, copy, drop, rename, recreate indexes/triggers/views, `foreign_key_check`), written once in a helper pattern (03 §6 row 9). Every migration ships with a test that builds a database at version N-1 **from a fixture containing real-looking ledger data** (stress-test generator from the porting notes, committed as a deterministic seed), upgrades it, and asserts invariants + fingerprint.

### 8.4 Reference data

`permission_keys` (38), `role_presets`, `app_meta.schema_version` come with the baseline (schema.sql section 10). The first-run wizard's seed (business settings row, first branch, 8 default expense categories, `doc_counters` zeros) is the content of `seed.sql` executed by `SetupService`; the placeholder admin of that file is **replaced by the wizard's admin** (the wizard updates that row, per `seed.sql` header), not left behind.

### 8.5 Version of the application vs the database

`app_meta`: `created_by_app_version`, `last_opened_by_app_version`, `last_migrated_from`. The Diagnostics report prints them.

### 8.6 Schema amendment S-2 (APPLIED in `sqlite/schema.sql`; carried unchanged into the embedded `0001_baseline.sql`, which is still unreleased)

Amendment S-1 (accept bcrypt hashes in `users.password_hash`) was **reverted**: there is no import (L-24), so the CHECK accepts only `$argon2id$*` and `$pbkdf2-sha256$*`; `smoke-test.sql` proves a garbage hash and a bcrypt hash are refused and an argon2id hash is accepted.

| ID | Amendment | Reason | Status |
|---|---|---|---|
| S-2 | `trg_payment_apply` (and the service-written cash rows in R1/R3 that belong to a payment) set `cash_movements.created_at = paid_at` of the payment | Single date basis for dashboards and the drawer (fixes DIV-27); service rejects future `paid_at`. In `schema.sql` the trigger-written row (`trg_payment_apply`) is dated this way; the compensating row of `trg_payment_delete_apply` keeps the deletion time (a reversal happens when it is posted). Service-written R1/R3 excess/advance rows must pass `paid_at` as `created_at` themselves | **Applied** (trigger part) in `schema.sql`; smoke-tested |

Also in `schema.sql` (decision L-22): the `manager` role preset has no `purchases.edit` (PO editing is admin only); smoke-tested. Everything else in section 13 is service logic or queries; no further DDL change is required. The build agent copies `schema.sql` as it stands into `0001_baseline.sql`. After release, any change is a numbered migration.

---

## 9. Zero-admin operations (everything is a button)

### 9.1 First-run wizard (replaces `FirstRunSetup`, `pc_first_run_bootstrap`, `provision.ps1` prompts)

Shown when `first_run_completed = 0`. Nothing is written until the last step; closing the window midway leaves a blank database and the wizard starts over. Steps (Arabic, one page each, back/next):
1. **Welcome.** Always a fresh start: there is no import (L-24).
2. **Business:** name (required), address, phone, currency (EGP default, 12 options), timezone (Africa/Cairo default, 18 options).
3. **First branch:** name (default «الفرع الرئيسي»). (More branches later in Settings.)
4. **Administrator:** full name, username (`^[a-z0-9._-]+$`), password + confirm (policy 6.2).
5. **Recovery sheet:** shows the recovery key (6.4); print / save; retype the last group to continue; explains the Windows-administrator option and lets the owner turn it off.
6. **Backups:** choose the backup folder (default `C:\ProgramData\ShabanaInventory\Backups` with an amber notice «هذا المجلد على نفس قرص البيانات؛ يُنصح بنسخة إضافية على قرص خارجي»; the wizard detects "same physical volume" and external/removable volumes and suggests them; changeable later in Settings), schedule fixed: one automatic backup per day at the first launch of the day (no time to choose), retention (default daily 14, weekly 8, monthly 12), optional encryption (9.3).
7. **Finish:** summary; one transaction runs `seed.sql` content + wizard values + admin Argon2id hash + `recovery_key_hash` + `first_run_completed = 1`; first backup («بعد الإعداد») is taken; auto sign-in as the admin; tips card: "set opening stock / opening cash / opening balances" with buttons (02 §8.5, 8.9, 8.10).

### 9.2 Backup, restore, auto-backup (Settings → «النسخ الاحتياطي والبيانات»)

*Backup file.* `.shbk` = ZIP containing `shabana.db` (online-backup copy, no WAL needed) and `manifest.json` (`appVersion`, `schemaVersion`, `createdAtUtc`, `reason` auto/manual/pre-migration/pre-restore, `sha256` of the db, row counts of key tables, `businessName`). Optional encryption: `.shbke` = AES-256-GCM (built in), key from the **backup key** (section 9.3). Every backup is verified right after creation (open + `integrity_check` + count comparison); an unverified backup is deleted and reported as a failure.

*UI.* A status card (last successful backup, whether today's backup is done, destination, free space) — the dashboard badge from the old edition is kept: «آخر نسخة احتياطية: …», amber when the last attempt failed or the last success is older than 2 days. Buttons: **نسخ احتياطي الآن**, **استعادة**, **نسخة إلى مكان آخر…**, **تغيير مجلد النسخ** (default `C:\ProgramData\ShabanaInventory\Backups`). A list of backups (date, size, reason, schema version, ✓ verified).

*Schedule (decision L-17).* **Once per day, at app start:** on the first launch of each business day (NodaTime business-zone calendar date, 02 §3) and **before the user works**, `BackupService` runs right after the database is opened, migrated and `quick_check`ed and before the login screen accepts a password (a short Arabic progress card «جارٍ أخذ نسخة احتياطية…», typically a few seconds; the decision is `StartupBackupPolicy`: enabled AND no successful `auto` backup dated today). If that backup fails the app still starts, shows the amber badge and retries at the next launch the same day. There is **no timer and no backup on exit**; a kept-open app does not back up again until its next launch on a later day (the manual button is always available). Kept: the **pre-migration** backup (taken by `MigrationRunner` before any upgrade, section 8.2), the **manual backup button**, and retention. Honest limitation (documented in the UI): a day on which the app is never opened gets no backup. (A headless scheduled-task mode is listed as post-1.0 in `00-README.md`.)

*Retention.* Per root: keep all of the last 14 daily, then one per week for 8 weeks, then one per month for 12 months (configurable); `pre-migration`/`pre-restore`: keep the latest 5 and anything younger than 90 days; **never delete the newest verified backup and never prune while the latest backup attempt failed**.

*Restore.* Pick a listed backup or **«اختيار ملف نسخة…»** (USB). Steps in `RestoreService`, all automatic: validate manifest + hash + `integrity_check` of the candidate in a temp location → show what will be replaced («هذه النسخة بتاريخ … وستفقد الحركات بعد ذلك التاريخ») and require the admin to type a confirmation word → take a **pre-restore backup** of the current database → write the swap journal `restore\swap.json` → close all connections → move candidate into place with `File.Replace`, delete stale `-wal/-shm` → reopen → `MigrationRunner` (a backup from an older version is upgraded; one from a newer app is refused) → `quick_check` → clear journal → force sign-in (users come from the restored data). If any step fails the pre-restore copy is swapped back automatically. A crash mid-swap is finished or rolled back at the next start from the journal (start-up step 4).

*Off-site copy.* Optional second root (a USB drive, a network share, or a folder synced by OneDrive/Google Drive); every new backup is copied there (30-day retention). This replaces rclone/`setup-offsite.ps1`. Encryption strongly suggested for cloud-synced folders.

### 9.3 Backup encryption key

Enabled by a checkbox. App generates a random 256-bit key, shows it once as Base32 groups on the printable recovery sheet, and stores it protected with **DPAPI, `LocalMachine` scope** (so the start-up backup works for any Windows account) inside `config\machine.json`. Restoring an encrypted backup on another PC asks for the key typed from the sheet. Backups made by the old edition (AES-256-CBC + HMAC, "SHBNAES1") are PostgreSQL dumps and cannot be restored by this app (no import, L-24).

### 9.4 Integrity check and repair (Settings → «فحص سلامة البيانات»)

- *Automatic:* `PRAGMA quick_check` at every start (section 4.3 step 6) and weekly while open; result stored in `app_meta` and shown as a green/amber badge.
- *Full check (button):* `PRAGMA integrity_check`, `PRAGMA foreign_key_check`, and the **ledger invariants** (the 14 from 03 §0's stress test: no negative stock or drawer, no overpaid order, converted <= total, closed => fully collected, `total = subtotal - discount`, `subtotal = sum(lines)`, cash ledger = cash payments, net sale movements = lines of placed/closed orders, numbering assigned, …) implemented as SQL in `IntegrityService`. The report is in plain Arabic with counts, and an *Export report* button.
- *Safe automatic repairs:* `REINDEX`, `wal_checkpoint(TRUNCATE)`, `ANALYZE`, `VACUUM` (after a backup), recreate missing views/triggers from the embedded baseline Phase 2 (idempotent `DROP IF EXISTS` + create) if `sqlite_master` lacks them. Nothing that edits ledger rows.
- *If corruption or invariant violations are found:* the screen offers (1) **Restore latest verified backup** (9.2), listing what would be lost, (2) **Salvage** (best effort, Phase 11): read every table row by row from the damaged file into a fresh database created from baseline Phase 1, then Phase 2 + invariants, presented as a *candidate* the admin may adopt after reviewing counts — the original file is never modified, (3) *Export problem report*.

### 9.5 Recovery screen

Start-up failures that cannot be repaired automatically (not a database, corrupt, newer than the app, migration failed) show one screen: what happened in one sentence, the safe-copy folder, buttons **استعادة من نسخة** / **محاولة إنقاذ** / **تصدير تقرير المشكلة** / **خروج**. No login is possible until the database opens, by design.

### 9.6 Export

*Reports and lists:* CSV (UTF-8 with BOM so Excel shows Arabic correctly) from every list and report; *full data export* (zip of one CSV per table, money as decimals with 2 digits, quantities with 3, UTC and local dates both) from Settings, so the owner's data is never locked in. Admin role.

### 9.7 Problem report (replaces `export-report.ps1`)

«تصدير تقرير المشكلة» (Settings and the crash dialog): zip on the Desktop with the last 14 log files, `diagnostics.txt` (app/OS/.NET versions, schema version, `quick_check` result, table row counts, settings **names**, migration history, backup list with dates, free disk), never database content, never password hashes, never keys. The owner sends it by WhatsApp.

### 9.8 Updates

No internet is assumed. An update is a new installer file delivered by USB/WhatsApp; running it upgrades the program in place and the app migrates the database at next start (section 8). An optional "check for updates" button (release feed) is post-1.0.

### 9.9 Business settings that live in `app_meta` (so backups carry them)

`idle_lock_minutes`, `backup.enabled` (daily-at-start), `backup.retention.*`, `backup.encrypt`, `windows_admin_recovery_enabled`, `recovery_key_hash`, `recovery_key_created_at`, `recovery_fail_count`, `recovery_locked_until`, `last_backup_*`, `last_quick_check_*`. Machine-specific paths (`backupRoot` (default `C:\ProgramData\ShabanaInventory\Backups`), `offsiteRoot`, DPAPI key) live in `config\machine.json` and are not restored from backups.

---

## 10. Printing and export

Needs (02 §6, §7): sales invoice, contact statement, الملخص المالي, reports (4 tabs), audit; A4, RTL, Arabic-Indic digits, business header block, signature boxes, footer with app name.

**Decision D6.** `PrintLayoutEngine` builds **`FixedDocument` pages in code** (A4 portrait 793.7 x 1122.5 DIPs, landscape option for wide reports): it measures each table row with WPF text measurement, flows rows across pages, **repeats the table header on every page**, keeps the totals box and signature boxes together at the end, and adds «صفحة ن من م». Shown in a `PrintPreviewWindow` (`DocumentViewer`, RTL) with *Print* (`PrintDialog.PrintDocument`) and *Save as PDF*.
Why not `FlowDocument`: it cannot repeat table headers across pages or keep a signature block with the totals reliably. Why not a PDF library: QuestPDF needs a commercial licence above a revenue threshold; PDFsharp has no Arabic shaping; the screen already renders Arabic correctly through WPF, so print = the same renderer = what the user previewed.
**PDF export** = render each page to a bitmap at 200 dpi (A4 = 1654 x 2339 px), encode JPEG (quality 85), and write a minimal PDF 1.4 (catalog, page tree, one image XObject `DCTDecode` per page, correct MediaBox) with an in-house `PdfPagesWriter` (about 100 lines, unit-tested by reading it back with a PDF parser in CI). Trade-off, stated: PDF text is not selectable/searchable; fine for invoices sent over WhatsApp. If the owner later needs real text PDFs, swap `PdfPagesWriter` for a library behind the same interface.
Fonts are embedded by WPF when printing/XPS; IBM Plex Sans Arabic includes Arabic-Indic digit glyphs (verify in Phase 10).
Document builders: `InvoiceDocument`, `StatementDocument`, `FinancialSummaryDocument`, `ReportDocument`, `AuditDocument`, `RecoverySheetDocument`. The invoice may be printed for **any non-draft** order (the web required an invoice number string; the string is internal, 02 §2.4).

---

## 11. Installer, release, CI

### 11.1 Decision D9: Inno Setup 6 (not MSIX)

MSIX needs the package signed by a certificate the PC trusts (sideloading friction on unmanaged counter PCs), restricts writes outside its container (we need ProgramData ACLs), and complicates detecting/handling the old Inno-installed edition. Inno Setup is small, scriptable, supports per-machine install, ACLs, mutex-aware upgrade, custom pages, RTL via an Arabic language file (`installer/lang/Arabic.isl`, community file, reviewed and committed), and it is what the current edition already uses.

### 11.2 What the installer does (no scripts for the user; one UAC prompt)

`PrivilegesRequired=admin`, `ArchitecturesAllowed=x64compatible`, `ArchitecturesInstallIn64BitMode=x64compatible` (64-bit Windows only; a 32-bit Windows is refused with an Arabic message), `MinVersion=10.0.17763`, publish RID `win-x64` (L-29), `AppMutex=Global\ShabanaInventory.SingleInstance` (closes/waits for the running app), `CloseApplications=yes`, new `AppId` GUID (never the old `{B6E9B6C1-…}`).
Installs to `{autopf}\Shabana Inventory` (ASCII, spaces are fine now: no PowerShell/NSSM argument problem remains); creates `C:\ProgramData\ShabanaInventory\` tree with `users-modify`; shortcuts «مخزون شبانه» on Desktop and Start menu; offers *launch now*. **No database, service, port, scheduled task or script is installed.** Uninstall removes program files only and asks (default **No**) whether to also delete the data folder, with a warning that backups would go too; the data is otherwise kept (same policy as the old edition).

### 11.3 Old PC edition (Postgres-based): offer to uninstall it, carry nothing over

**No data is imported or carried over** (fresh install, L-24). Detection (any one): uninstall registry key `{B6E9B6C1-6B7B-4C9F-9C1A-5D3A6A2E9F11}_is1` (HKLM, 32/64-bit views), service `ShabanaPostgres`, or file `C:\ProgramData\Shabana\config\db-password.key`. If found, an Arabic page says plainly: *«تم العثور على النسخة القديمة من البرنامج. لن يتم نقل أي بيانات منها إلى النسخة الجديدة. يمكنك إزالة النسخة القديمة الآن بأمان.»* with a checkbox **«إزالة النسخة القديمة»** (ticked by default). If ticked, in this order (the installer is already elevated):
1. Best-effort **disable the public link**: run the old `installer\scripts\setup-tunnel.ps1 -Disable` when it exists (removes the Tailscale Funnel exposure).
2. **Stop and remove its services**: `ShabanaCaddy`, `ShabanaGoTrue`, `ShabanaPostgREST`, then `ShabanaPostgres` (stop, wait, `sc delete`), so port 8000 and the Postgres process are freed.
3. Run the old edition's own silent uninstaller (`unins000.exe /VERYSILENT`). Per its design this removes the program files and shortcuts and **keeps** `C:\ProgramData\Shabana\` (`data\`, `backups\`, `config\`), which the installer never reads, copies, imports or deletes; the page says the old folder is left on disk and may be deleted by the owner whenever he wishes.
4. Remove leftover old desktop/start-menu shortcuts.
If unticked, nothing of the old edition is touched (the new app uses no ports or services, so the two cannot conflict). Failure of any step is logged and shown in Arabic; the new installation continues. No registry flag, no import kit, no PostgreSQL binaries are shipped.

### 11.4 Code signing (decision L-13)

**The owner buys and holds the certificate** (OV file or EV token, in his name; validation takes days, so he should start early). **Builds ship unsigned until then**: SmartScreen shows "Unknown publisher" and the owner clicks "More info -> Run anyway". Signing is therefore **optional and never a release gate**: the CI signing step runs only when the secrets `SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD` exist and is **skipped silently otherwise** (no failure, a log line «unsigned build»). When a certificate exists: sign `Shabana.App.exe` and the installer exe (other DLLs are not required) with Authenticode SHA-256 and an RFC-3161 timestamp (`signtool sign /fd sha256 /tr <tsa> /td sha256`), via Inno's `SignTool=` and a CI step for the app exe before packaging. An EV token cannot sit in GitHub runners (self-hosted runner or remote signing needed); an OV file certificate fits the secrets pattern.

### 11.5 Versioning

`MAJOR.MINOR.PATCH` from git tag `vX.Y.Z`; non-tag builds `0.<run>.<sha7>`. Version stamped into assembly, installer, `app_meta`, and Diagnostics. Database schema version is independent (section 8).

### 11.6 CI (`.github/workflows/build-native.yml`, replaces the concept of `build-installer.yml`)

| Job | Runner | Trigger | Steps |
|---|---|---|---|
| `test` | windows-latest | every push and PR | `actions/setup-dotnet` with `dotnet-version: 10.0.x` (same pin as `global.json`); `dotnet restore --locked-mode`; `dotnet build -warnaserror`; `dotnet test` with coverage (Core+Data+services against temp SQLite; guard-matrix; migration fixtures; golden formatting; schema smoke test executing `sqlite/smoke-test.sql` through the app's SQLite); vulnerability gate `dotnet list package --vulnerable`; label generator check; "no Arabic literal" analyzer |
| `ui-smoke` | windows-latest | push to main/tag | publish app, run FlaUI tests: first-run wizard in a temp `--data-dir`, login, idle lock, print preview opens, backup then restore round trip |
| `package` | windows-latest | push to main, tag `v*` | `dotnet publish` (self-contained, R2R); compile `installer/Shabana.iss` with `ISCC.exe` (preinstalled; pin recorded); sign **only if the signing secrets exist** (otherwise skipped, unsigned build); **installer test: silent install to a temp dir, run `Shabana.App.exe --selftest --data-dir <tmp>` (creates the DB, migrates, runs integrity, exits 0), silent upgrade over the previous release's installer using a fixture DB, silent uninstall**; upload artifact |
| `release` | windows-latest | tag `v*` | `softprops/action-gh-release` with installer + SHA-256 file (`contents: write` only for this job) |

Dependabot for NuGet and Actions. Secrets are never committed. The old workflow stays untouched until cut-over (section 15, Phase 14).

---

## 12. Testing strategy (summary; per-phase gates are in `00-README.md`)

1. **Schema:** `smoke-test.sql` + the porting notes' 65 behavioural assertions and the randomised 6,000-operation stress test are ported to xunit and run against the **embedded** baseline (so the shipped script is the tested script), with `recursive_triggers` OFF and ON.
2. **Services:** each recipe has scenario tests on the 02 §8 examples (100 kg PO split across two branches, overpay, credit apply, admin edit of a closed order, wallet compensation…).
3. **Authorization:** the guard-matrix test of 6.6 over every interface method; additional tests for deactivated user, revoked session, branch-restricted user, accountant (sees totals, zero rows).
4. **Formatting/validation:** golden vectors from 02 §3 (digits, money, qty, dates, search normalisation, phone, username, discount rounding).
5. **Migrations:** N-1 fixture upgrade with fingerprint equality; forced failure (a script throwing mid-way) leaves the database untouched; backup failure aborts the upgrade.
6. **Maintenance:** backup/restore round trip, corrupted-file detection (flip bytes in a copy), journal recovery after simulated crash at each swap step.
7. **Installer:** silent install / upgrade / uninstall; old-edition detection and safe removal against a simulated old install (services, registry key), no data touched.
8. **UI:** FlaUI smoke (section 11.6) + a manual Windows checklist per phase on a Windows 10 VM and one **real 64-bit (x64) PC** before 1.0 (the old plan's lesson: nothing ran end to end on Windows).
9. **Performance:** synthetic 3-year dataset (about 100k stock movements, 50k payments, 20k orders): list screens < 300 ms, dashboard < 500 ms, full integrity check < 10 s, backup < 5 s.

---

## 13. Known web defects: the native app implements the correct behaviour

Sources: 02 §12 (DIV-n), 03 §7 (B1-B3 reproduced bugs, G1-G8 gaps). "Where" names the enforcing artefact; each has a test named after the id.

**The six called out explicitly**

| Defect | Native behaviour | Where |
|---|---|---|
| **Invoicing fails (`next_doc_number`, DIV-1 / B1)** | Invoicing works: the `SO-YYYY-####` string is assigned by `trg_so_assign_invoice_number` from `doc_counters`; the UI never depends on it (invoice printable for every non-draft order) | schema trigger + `SalesService.Invoice` + test `B1_InvoiceAssignsNumber` |
| **Closed-SO edit double-deducts stock (B2)** | `trg_so_placement_apply` returns stock for `{placed,closed}→{invoiced,draft}`; R5 test: sell 10 of 40, admin-edit the closed order, stock afterwards is exactly 40 minus the new quantity | schema trigger + R5 |
| **Admin edit of an order with an amount discount fails (B3)** | `discount_amount` is clamped by the generated column; the over-discount triggers stand down while `app_context.editing = 1`; discount is rewritten after the lines | schema + R5 |
| **Unchecked money RPCs (DIV-11 / G3)** | `so_overpay`, `po_overpay`, `credit_deposit`, `vendor_credit_deposit` are gated by `payments.record` (+ branch of the drawer); guard-matrix test proves accountant and no-key users are refused | `PaymentService`, `CreditService`, `VendorCreditService` |
| **Deactivation not enforced (DIV-8)** | Login refuses inactive users; **every service call** re-checks `active`; deactivating revokes sessions at once; the last active admin cannot be deactivated | `AuthService`, `DispatchProxy`, `AccessService`, DB triggers |
| **No password change UI (DIV-9)** | Self-service change, admin reset, must-change flag, recovery key and Windows-admin reset | section 6.2/6.4 |

**All remaining items**

| ID | Native behaviour |
|---|---|
| DIV-2 | Screens and nav are gated by the **permission key** (users.manage, settings.manage, sites.manage), not the admin role; the admin preset holds them |
| DIV-3 | `/audit` screen gated by `audit.view` only |
| DIV-4 | **Fixed, deliberately different from web (owner decision, L-22)**: purchase-order editing is **admin only**. `purchases.edit` is removed from the manager preset (`schema.sql` `role_presets`; `smoke-test.sql` asserts it); the admin can still grant the key to a named user. Matches the original `SCHEMA.md` intent ("admin can edit any order") |
| DIV-5 | "تسوية" gated by `inventory.adjust`; header "نقل بين الفروع" and the row icon by `inventory.transfer`; "إضافة صنف" by `inventory.items` |
| DIV-6 | No hard delete of sales orders, purchase orders, contacts, items, users, sites. Payments are deletable (`payments.delete`) with compensation. Draft orders are corrected by editing; there is no "cancel order" status in the schema, so a draft that is not wanted stays a draft or is emptied (documented) |
| DIV-7 | Add-payment form gated by `payments.record` |
| DIV-10 | Settling an opening receivable/payable allowed to `payments.record` holders; **setting** the opening figure stays admin-only |
| DIV-12 | Integer arithmetic everywhere (7.3); no `double` |
| DIV-13 | Dashboard gets an «حركة المخزون» table (per item: opening, in, out, closing for the chosen day and scope) so rule 10 ("both stock and cash") is satisfied; ledger query C1 with the D-1 bound. Owner-accepted enhancement (L-23) |
| DIV-14 | Dashboard shows **expenses** as their own row, plus a cash reconciliation panel built only from `cash_movements` (opening + cash in - cash out = closing, grouped by reason), which reconciles exactly; non-cash methods are labelled as not touching the drawer |
| DIV-15 | Attachments remain on purchase orders in v1 (parity); schema supports sales orders; enabling it later is UI-only |
| DIV-16 | Contact address and notes are shown on the contact page and on the statement header |
| DIV-17 | One balance definition (`v_contact_balance`) feeds list, reports and page; the ledger includes vendor-advance rows and wallet applications, shows a **carried-forward row** for ranges, per-row balance with له/عليه, final balance **as of the range end**, and sorts by instant then id |
| DIV-18 | Contact edit updates phones/payment methods in place (stable ids); a payment method referenced by any payment cannot be removed (message), so the "which account" trail is never wiped |
| DIV-19 | Stale texts are not carried over; new labels (`USERS`, `WIZARD`); `CLAUDE.md`/`SCHEMA.md` are updated at cut-over (Phase 14) |
| DIV-20 | `UpdateItemized` verifies new total >= already paid |
| DIV-21 | Admin edit of a sales order that already has **cash payments** cannot change the branch (message explains: reverse the payments first); other edits unaffected |
| DIV-22 / G1 | Deleting a `credit` payment posts a compensating wallet row (`adjustment`) in the same transaction |
| DIV-23 | Every PO edit (R6/R7) writes an explicit `audit_log` row |
| DIV-24 | Payment instant = chosen date + current business-zone time of day; future dates rejected; business date derived from the instant |
| DIV-25 | Branch view never silently drops rows: non-cash items with no branch are shown as a separate line «غير مرتبط بفرع», in both single-branch and all-branches views |
| DIV-26 | Every day boundary is the business-zone midnight → UTC bounds (7.4) |
| DIV-27 | One date basis: payments and their cash rows are dated by `paid_at` (S-2); all day buckets use it |
| DIV-28 | `CLAUDE.md` rules 6/9 corrected at cut-over: four roles, 38 keys, closing only from `placed` |
| DIV-29 | Branch check on line conversions (6.6) |
| DIV-30 | Balances are computed on full data; branch scope limits only which **rows** a restricted user sees |
| DIV-31 | Conversion output unit is the item's unit (read-only in the form; service rejects a mismatch) |
| DIV-32 | Set-based queries; no per-contact calls |
| DIV-33 | Not applicable (web-only UI nits); native "when" timestamps use the business zone |
| G2 | = DIV-20 |
| G3 | = DIV-11 |
| G4 | Attachment add/remove gated by `attachments.manage` |
| G5 | `dashboard.view` enforced in the service |
| G6 | All four roles creatable (accountant included) |
| G7 | Web seed not ported; first-run seed only |
| G8 | A rule with posted expenses/skips cannot be deleted; UI offers "إيقاف" (deactivate) with the reason |
| B1/B2/B3 | See the table above |

---

## 14. Feature parity checklist

Phase numbers refer to `00-README.md`. Tick a box only when the screen exists, enforces its permissions in the service **and** hides them in the UI, passes its tests, and matches the cited spec section (labels from `labels.ar.json`).

### 14.1 Routes of the web app (02 §5.5) — every route listed

- [ ] **R-01** `/login` — login screen, lockout, inactive refusal (02 §4.1, section 6.1) — P4
- [ ] **R-02** `/:orgSlug/login` — **DROPPED**: single business, no org code or slug (02 §1, 03 §1); the one login screen shows the business name
- [ ] **R-03** `/` and any unknown path (`HomeRedirect`, `NoLandingPage`) — `LandingResolver` with the order dashboard -> financials -> sales -> inventory -> purchases -> contacts -> expenses -> reports, never a loop (02 §5.5) — P4
- [ ] **R-04** `/setup` (PC-only first run) — becomes the first-run wizard (section 9.1) — P4
- [ ] **R-05** `/dashboard` — 02 §7.3, §9.1, with DIV-13/14 enhancements — P10
- [ ] **R-06** `/purchases` — 02 §7.4 — P7
- [ ] **R-07** `/purchases/:id` — 02 §7.5, §8.1 — P7
- [ ] **R-08** `/inventory` — 02 §7.8 — P5
- [ ] **R-09** `/inventory/:id` — 02 §7.9 — P5
- [ ] **R-10** `/sales` — 02 §7.11 — P8
- [ ] **R-11** `/sales/:id` — 02 §7.12, §8.2 — P8
- [ ] **R-12** `/sales/:id/invoice` — print, 02 §7.14 — P10
- [ ] **R-13** `/contacts` — 02 §7.15 — P5
- [ ] **R-14** `/contacts/:id` — 02 §7.16 (+DIV-16) — P5 (ledger list P10)
- [ ] **R-15** `/contacts/:id/statement` — print «كشف حساب», 02 §7.18 (+DIV-17) — P10
- [ ] **R-16** `/expenses` — 02 §7.19, §8.11 — P9
- [ ] **R-17** `/reports` — 4 tabs, 02 §7.20 — P10
- [ ] **R-18** `/financials` — الملخص المالي, 02 §7.21, §9.3 — P10
- [ ] **R-19** `/audit` — 02 §7.22, §10 — P10
- [ ] **R-20** `/settings` — branches + business identity (+ the new maintenance area), 02 §7.23 — P5 / P11
- [ ] **R-21** `/users` — 02 §7.24, §4.2-4.3 — P5
- [ ] **R-22** `/platform` — **DROPPED**: multi-tenant console (02 §7.25)

### 14.2 Dialogs, panels and shared components (02 §6, §7.6-7.17, §7.26)

- [ ] **C-01** `PurchaseOrderFormModal` (general + itemized, bidirectional pricing, new item on the fly) — P7
- [ ] **C-02** `ConversionModal` and `ItemizedConversionModal` — P7
- [ ] **C-03** `ItemFormModal`, `AdjustmentModal`, `TransferModal`, `OpeningStockModal` — P5
- [ ] **C-04** `SalesOrderFormModal` (live stock hint, discount tabs, validations) — P8
- [ ] **C-05** `ContactFormModal` (phones, payment methods, Egyptian phone validation) — P5
- [ ] **C-06** `PaymentsPanel` incl. overpay confirmation, stored-account picker, wallet application — P6
- [ ] **C-07** `CreditCard`, `VendorCreditCard` (deposit/apply/refund) — P6
- [ ] **C-08** `OpeningBalanceCard` (set with two-step confirm; settle) — P9
- [ ] **C-09** `ManualCashModal` (إيداع/سحب/تسوية, mandatory reason) — P6
- [ ] **C-10** `AttachmentsPanel` (camera replaced by *file/scan import*: pick file; webcam capture optional post-1.0) — P7
- [ ] **C-11** `UserAccessModal`, add-user dialog, role presets, copy-from-user — P5
- [ ] **C-12** Site add/edit modal; `OrgIdentityCard` — P5
- [ ] **C-13** Expense dialogs (record, rule, category, reason/skip/void) and `DueExpensesPanel` — P9
- [ ] **C-14** Header: site switcher, global search (`Ctrl+K`, four groups, keyboard navigation), due-expenses bell, user menu — P4/P5/P9
- [ ] **C-15** `DataTable`: per-column multi-select filters, sort tri-state, filter chips, empty states — P5
- [ ] **C-16** `ArabicDatePicker`, `NumberInput`, `MoneyDisplay`, `SiteBadge`, `Toast`, `ConfirmDialog`, `ReasonModal` — P5
- [ ] **C-17** Formatting rules of 02 §3 (digits, money, qty, dates, search, collation) — P1
- [ ] **C-18** Print outputs: invoice, statement, financial summary, reports, audit, + PDF export — P10
- [ ] **C-19** Audit-trail sentence generator and the 17 entity filters — P10
- [ ] **C-20** Document numbering «أمر شراء N» / «أمر بيع N» — P7/P8

### 14.3 PC-only features of the old edition (01 §4): keep / change / drop

| Old feature | Decision | Justification |
|---|---|---|
| Single machine, no internet | **Keep** | The whole point; SQLite file, no network code at all |
| Install path `C:\ProgramData\Shabana\` | **Change** to `C:\ProgramData\ShabanaInventory\` | Old folder holds the old PostgreSQL data, which the installer leaves untouched (D8, 11.3); fresh start, no import (L-24) |
| First-run setup (loopback-gated) | **Keep as wizard** | No network surface any more, so no loopback gate needed (section 9.1) |
| Nightly 03:00 backup, 14-day retention, status badge | **Keep, reworked** | One automatic backup per day at the first app launch of the day, before the user works; default folder `C:\ProgramData\ShabanaInventory\Backups`; GFS retention; manual button; same amber badge rule (9.2, L-17). Limitation (needs the app to be opened that day) is disclosed in the UI |
| Off-site encrypted backup (rclone, AES-CBC+HMAC) | **Keep, simplified** | Second folder (USB/share/synced folder) with optional AES-256-GCM; rclone dropped (an external tool the user would have to configure) |
| Restore (`restore.ps1`) | **Keep as UI** | 9.2, with pre-restore safety copy and automatic rollback |
| Admin password reset (`reset-admin.ps1`) | **Keep as recovery key + Windows-admin path** | 6.4; no script, no psql |
| Upgrades (`migrate.ps1`, backup, auto-restore) | **Keep as `MigrationRunner`** | Section 8; single-transaction rollback + fingerprint is stronger than restore-on-failure |
| Problem report (`export-report.ps1`) | **Keep as UI** | 9.7 |
| Import of old data | **DROP (owner decision, L-24)** | Existing data is dummy; fresh install; installer only offers to uninstall the old edition (11.3) |
| Invoice PDF | **Keep as image-based PDF (owner-confirmed, L-30)** | Not text-searchable; fine for WhatsApp (D6) |
| Public tunnel link (Tailscale Funnel) | **DROP (owner-confirmed, L-14)** | Exposes the shop's books to the internet; needs a server process; contradicts "no services/ports"; the installer's old-edition removal disables a previously enabled link (11.3) |
| LAN clients via Caddy (phones/other PCs) | **DROP (owner-confirmed: one PC only, L-14)** | A desktop app with a local file cannot serve other devices safely; design hook noted in 5.6 for a hypothetical future |
| Own window (app mode) | **n/a** | It is a native window |
| Backup badge `PcBackupStatus` | **Keep** | Dashboard + header indicator |
| Attachments (PC-shim bytes in DB) | **Keep, purchase orders only (owner-confirmed, L-30)** | `attachment_blobs` in the same file and backups; no sales-order attachments |
| Web-only features missing on PC (expenses, الملخص المالي, accountant role, opening balances, cost price, route guards) | **Keep: all included** | Native follows web migration 0044 (03 §0) |
| Offline banner, query cache, chunk-reload, mobile bottom tabs/MoreSheet | **DROP** | Web-only |
| Caddy, GoTrue, PostgREST, NSSM, PostgreSQL services | **DROP** | Replaced by the in-process SQLite engine |

### 14.4 New native features

- [ ] **N-01** Idle auto-lock + lock screen — P4
- [ ] **N-02** Change password / forced change / admin reset — P3/P4/P5
- [ ] **N-03** Recovery key sheet + recovery flows — P4
- [ ] **N-04** Backup / restore / daily start-up backup / retention / off-site / encryption UI — P11
- [ ] **N-05** Integrity check, repair, recovery screen, salvage — P11
- [ ] **N-06** Full data export, problem report — P11
- [ ] **N-07** Installer offers safe removal of the old edition (no data carried over) — P12
- [ ] **N-08** Installer with CI-tested install/upgrade/uninstall, x64 only — P12
- [ ] **N-09** Stock opening/closing panel on the dashboard (DIV-13) — P10

---

## 15. Open technical risks (carried to `00-README.md`)

1. **.NET 8 end of support (D2): resolved by targeting .NET 10 LTS.** 2. **Argon2 cost on weak PCs** — measured in Phase 3, PBKDF2 fallback exists. 3. **No Windows run yet** — Phase 13 includes a real-hardware pass (the old project never ran end to end on Windows). 4. **Backups depend on the app being opened that day** (one backup at the first launch of each day; no headless task in v1). 5. **Unencrypted DB file** readable by any Windows account. 6. **IBM Plex Sans Arabic digit glyphs in print** — verify in Phase 10. 7. **Old install state unknown** (partially removed, services stopped, bin removed) — the removal step of 11.3 is idempotent and logs-and-continues; tested against a simulated old install until a real one is available; the pilot in Phase 14 is the first real test.

---

## 16. Self-check against the task's rules (author's audit)

- **Every route in 02 appears in the parity checklist:** 02 §5.5 lists `/login`, `/:orgSlug/login`, `/`, `/dashboard`, `/purchases`, `/purchases/:id`, `/inventory`, `/inventory/:id`, `/sales`, `/sales/:id`, `/sales/:id/invoice`, `/contacts`, `/contacts/:id`, `/contacts/:id/statement`, `/expenses`, `/reports`, `/financials`, `/audit`, `/settings`, `/users`, `/platform`, plus PC `/setup` — R-01 … R-22 (22 items) cover all of them (two dropped with reasons).
- **Every service in 02 §11 has a C# service:** table 7.5 (19 files; `platform.ts` explicitly "none").
- **Every open defect has a stated fix:** section 13 covers DIV-1…33, B1-B3, G1-G8 (verified mechanically: a grep for each id appears in this file).
- **No instruction requires the end user to run a command or touch the DB:** installer (one UAC prompt), wizard, Settings buttons, recovery via UI. The one place a Windows concept appears (UAC elevation, choosing a folder, Windows-administrator recovery) is a standard OS dialog, never a console.
- **Referenced, not duplicated:** authorization rules are mapped to guards by policy name (6.6); business rules cite 02/03 sections.
