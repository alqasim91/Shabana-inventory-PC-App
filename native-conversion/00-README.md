# 00 — Native conversion: index, decisions, open questions, build plan

Goal: replace the Windows installer that bundles a web frontend + PostgreSQL/PostgREST/GoTrue/Caddy with a **native Windows desktop app**
(.NET + WPF, Arabic RTL, SQLite for everything). This folder is the hand-off package for the build agents. **Documentation only: no application
code exists yet.** Baseline for behaviour: web commit `39dc229` (see `01`).

---

## 1. Files and reading order

| Order | File | What it is | Read it when |
|---|---|---|---|
| 1 | `00-README.md` (this) | Index, decisions log, open questions, phased plan | first |
| 2 | `01-WEB-APP-SNAPSHOT.md` | The baseline commit, what changed recently, gap between web and the old PC edition, PC-only features | to understand what is being replaced |
| 3 | `02-FUNCTIONAL-SPEC.md` | Every screen, workflow, role, permission, formula, error text, audit sentence, and the 33 documented defects (DIV-n) | before any feature work; it is the behaviour contract |
| 4 | `03-DB-PORTING-NOTES.md` | PostgreSQL → SQLite: units, table map, catalog of 93 functions / 71 triggers / 82 policies and where each now lives, authorization matrix, 13 service recipes (R1-R13), query cookbook, defects B1-B3 / G1-G8 | before the data/service layer |
| 5 | `sqlite/schema.sql` | The target schema (36 tables, 13 views, 108 triggers); becomes `0001_baseline.sql` (with the two amendments of `04` §8.6) | Phase 0/2 |
| 6 | `sqlite/seed.sql`, `sqlite/smoke-test.sql` | First-run seed; throw-away behavioural proof | Phase 0/4 |
| 7 | `resources/labels.ar.json` | All Arabic UI strings (+16 .NET-style templates) | Phase 0 (generator) |
| 8 | `04-NATIVE-ARCHITECTURE.md` | **How to build it:** solution layout, packages, MVVM/DI, data location, auth/session/recovery, guard map, service list, migrations, zero-admin operations, printing, installer/CI, defect fixes, parity checklist | before Phase 0; refer constantly |
| 9 | `05-DATA-MIGRATION.md` | One-time import from the old PC (PostgreSQL) install into SQLite | Phase 12 (and Phase 13 for the installer side) |
| context | `../README.md`, `../BUILD_PLAN.md`, `../OPUS-HANDOFF.md`, `../supabase/PC-DIVERGENCE.md`, `../installer/` | The product being replaced | for the import and installer work |

Precedence when documents disagree: **02/03 (behaviour, schema) > 04/05 (architecture, import) for business rules**; **04 §13 overrides 02 §12** for defects (04 records the decision to fix them). Anything in the old web docs (`CLAUDE.md`, `SCHEMA.md`) that contradicts the code is stale (02 §12 DIV-19, DIV-28).

Three hard rules for every agent (from the project and the owner):
1. **No end-user operations.** No command, script, service, port, config file or database tool is ever required of the user (04 §16).
2. **Money and quantities are integers** (`Money` = piasters, `Qty` = milli); stock and cash are append-only ledgers; corrections are new rows.
3. **Authorization is enforced in the service layer and hidden in the UI**, and every service method has a guard attribute and a guard-matrix test (04 §6.6).

---

## 2. Decisions log

"Owner" = stated by the owner. "Doc" = already fixed in 03. "Proposed" = made in 04/05 and open to veto.

| ID | Decision | Rationale (short) | Where | Status |
|---|---|---|---|---|
| L-01 | Full native Windows app, .NET + WPF (C#), Arabic-only RTL, ar-EG, IBM Plex Sans Arabic, EGP | Owner | 04 §1 | Owner |
| L-02 | SQLite for everything including users/auth; no services/ports/scripts for the user; installer + first-run wizard + auto-migrations with pre-migration backup + backup/restore UI + scheduled backups + one-time import wizard + integrity check/repair UI | Owner: "so the user doesn't get hassled with database management" | 04 §5, §8, §9; 05 | Owner |
| L-03 | Single business per database file (no `org_id`, no `/platform`) | PC edition always had one organization | 03 §1 | Doc |
| L-04 | Money `INTEGER` piasters, quantities `INTEGER` milli, `Int128` intermediates, no floating point | Exactness; `NUMERIC(12,2/3)` fits | 03 §2; 04 §7.3 | Doc |
| L-05 | Stock/cash/credits are append-only ledgers enforced by triggers; balances are views | CLAUDE.md rule; hardening | 03 §7.3 | Doc |
| L-06 | Authorization in the app layer (no RLS), per-user 38 permission keys + branch scope, admin bypass | SQLite has no RLS | 03 §5; 04 §6 | Doc |
| L-07 | Native follows **web migration 0044** (accountant role, expenses, الملخص المالي, opening balances, cost price) although the old PC install stopped at 0035 | Owner wants parity with the current web app | 03 §0; 01 | Doc |
| L-08 | Data in `C:\ProgramData\ShabanaInventory\` with `Users:Modify` (not LocalAppData, not the old `Shabana` folder) | Multi-account PCs; keep the old data intact for import | 04 §5.1 | Proposed |
| L-09 | Dapper + Microsoft.Data.Sqlite (no EF Core) | Schema already has triggers/views/STRICT/generated columns and recipes are raw SQL; EF migrations would fight `schema.sql` | 04 §1 D4 | Proposed |
| L-10 | Password hashing Argon2id (Konscious) per schema; PBKDF2-SHA256 fallback; bcrypt verify-only for imported users, rehash on first login | Keeps users' passwords; schema amendment S-1 | 04 §6.2; 05 §7 | Proposed |
| L-11 | Printing: code-built `FixedDocument` pages (header repeat, signature block), PDF = rasterised pages via in-house writer; no third-party PDF/charts library | Deterministic RTL pagination, zero licence risk | 04 §10 | Proposed |
| L-12 | No chart library in v1 | Spec has only bar lists and progress bars | 04 §1 D7 | Proposed |
| L-13 | Inno Setup 6 installer (not MSIX), self-contained win-x64, per-machine, Authenticode signing as a release gate | MSIX sideload/signing friction and container limits | 04 §11 | Proposed |
| L-14 | One PC, one process; LAN / multi-PC / public link dropped from v1 | A local file cannot be shared safely; flagged to owner | 04 §5.6, §14.3 | Proposed (flagged) |
| L-15 | DB created by the app on first launch through the migration runner (the installer prepares folders + permissions) | One code path; no stale prebuilt DB | 04 §5.1 | Proposed (interprets owner's "installer creates/places the DB") |
| L-16 | Migrations: embedded versioned scripts, `PRAGMA user_version`, whole chain in one transaction with FK check + integrity check + balance fingerprint, backup first and abort if it fails | Safe unattended upgrades | 04 §8 | Proposed |
| L-17 | Backups: online-backup API, verified `.shbk` zip, in-app scheduler (21:00 default + on exit + catch-up), GFS retention, optional second folder, optional AES-256-GCM | Replaces nightly Task Scheduler + rclone | 04 §9.2-9.3 | Proposed |
| L-18 | Recovery: printed recovery key (hash only stored) + UAC-gated, logged, switchable Windows-administrator reset; no master password | Old `reset-admin.ps1` equivalent, no new exposure because the file is readable by Windows admins anyway | 04 §6.4 | Proposed (flagged) |
| L-19 | Idle auto-lock 10 min (configurable), lockout 5 failures / 15 min, must-change-password, per-call session + `active` re-check | Fixes DIV-8/9 | 04 §6 | Proposed |
| L-20 | Business day = business-zone midnight via NodaTime tzdb; all day buckets use it; payments dated by `paid_at` (cash rows too, S-2) | Fixes DIV-24/26/27 | 04 §7.4, §8.6 | Proposed |
| L-21 | No hard deletes of orders/contacts/items/users/sites; payments deletable with compensation (incl. credit payments) | Fixes DIV-6, DIV-22 | 04 §13 | Proposed |
| L-22 | Keep managers able to edit POs (current behaviour, DIV-4) | Preserve behaviour; owner can revoke per user | 04 §13 | Proposed |
| L-23 | Dashboard gains stock opening/closing table and an expenses row (DIV-13/14) | CLAUDE.md rule 10 literal compliance; reconciliation | 04 §13 | Proposed |
| L-24 | Import: staging DB + verification report + journalled atomic swap; read old Postgres live (A), start old service (B), private instance on a copy (C), or from a dump with an optional legacy kit (D) | Never touches the old data, never half-imports | 05 | Proposed |
| L-25 | Old edition retired only by an explicit button after a verified import; its data folder is archived, never auto-deleted | Safety | 05 §11 | Proposed |
| L-26 | Target framework: **.NET 10 LTS, `net10.0-windows`** (supported to Nov 2028). .NET 8 support ends 10 Nov 2026, so it is not used | Avoid shipping a v1.0 on an unsupported runtime | 04 §1 D2 | Decided |
| L-27 | Schema amendments before first release: S-1 (bcrypt prefix allowed in `password_hash` CHECK), S-2 (payment cash rows `created_at = paid_at`); **both already applied** in `sqlite/schema.sql` and covered by `smoke-test.sql` | Needed by L-10 and L-20 | 04 §8.6 | Applied in `sqlite/schema.sql` |
| L-28 | Build inside this repository (`Shabana-inventory-PC-App`) under a **`native/`** folder, not a new repository; old edition kept on a `legacy/pc-edition` branch for hot-fixes | Single history, installer/old-edition assets alongside; decided 2026-10-18 | 04 §3 | Decided |

---

## 3. Open questions for the owner (short)

1. **Other devices.** Did the shop use the old edition from phones / second PCs on the Wi-Fi, or the public link? v1 drops LAN and the public link. If you rely on them, we add a host mode after 1.0.
2. **Password recovery.** OK with the Windows-administrator reset being on by default (UAC-protected, logged, banner to all admins, can be switched off), in addition to the printed recovery key?
3. **Oldest Windows.** Is every shop PC Windows 10 (1809 or newer) or Windows 11, 64-bit? .NET 10 does not run on Windows 7/8.
4. **Keep existing passwords on import?** Recommended yes (old hashes verified once, then upgraded). Or force everyone to set a new password?
5. **Behaviour tweaks from the defect list.** Accept: payments dated with real time and counted on their own date (a payment dated yesterday changes yesterday's closing); dashboard stock opening/closing table; no deletion of orders/contacts/items (only voids/reversals); managers keep the right to edit purchase orders?
6. **Backups.** Where should backups go by default (external drive / second folder)? The PC must be on (app open) at the schedule time; default is 21:00 plus on exit. Acceptable?
7. **Code signing.** Who buys and holds the certificate (OV file vs EV token), and in which name? Unsigned builds show "Unknown publisher".
8. **Sales-order attachments and PDF.** Keep attachments on purchase orders only (as today)? Is an image-based PDF (not text-searchable) fine for sending invoices by WhatsApp?

---

## 4. Phased build plan

Conventions. "Spec" = sections to read. A phase is **done** only when every acceptance criterion passes in CI and its checklist items (04 §14) are ticked. Phases marked ‖ can run in parallel with others in the same row once their dependencies are met. "Review" = where a second, more careful pass is advised (data/security-critical, per the old project's Sonnet/Opus split).

```
P0 → P1 → P2 → P3 → P4 → ┬ P5 → P6 → P7 → P8 → P9 → P10 ┬→ P14 → P15
                          ├ P11 (needs P2,P3; UI part after P4) ┤
                          └ P12 (needs P2,P3,P11) → P13 ────────┘
```

| Phase | Name | Depends on | Delivers | Acceptance criteria (all must hold) | Spec | Review |
|---|---|---|---|---|---|---|
| **P0** | Foundations | — | Repo/solution layout (under `native/` of this repo), `Directory.*.props`, lock files, `.editorconfig`, CI `test` job, labels generator, embedded baseline script, SQLite version assertion | (1) `dotnet build -warnaserror` and `dotnet test` green on windows-latest; (2) `labels.ar.json` generates `Labels.g.cs` incl. the 16 templates and the build fails on a missing key; (3) the embedded `0001_baseline.sql` creates a database whose `sqlite_master` equals `schema.sql` (tables 36, views 13, triggers 108, indexes 61 — counts asserted) and `smoke-test.sql` passes; (4) vulnerable-package gate on; (5) architecture test enforces project references; (6) "no Arabic literal outside the JSON files" analyzer active | 04 §2-3, §4.7 | — |
| **P1** | Core primitives | P0 | `Money`, `Qty`, `Bp`, `MoneyMath`, `PoPricing`, Arabic formatters, `SearchNormalizer`, `PhoneEg`, `Username`, `PasswordPolicy`, `DiscountRules`, `BusinessCalendar` (NodaTime), `ErrorCode` + messages | (1) Golden tests reproduce every example in 02 §3; (2) property tests: `LineTotal`/`PercentDiscount` equal the reference `round-half-up` for 1M random values and never overflow (Int128); (3) bidirectional PO pricing: any two fields determine the third per 02 §7.6 incl. recency rule; (4) business-day bounds correct across Cairo DST transitions 2014-2026; (5) every `ErrorCode` of 03 §9 has an Arabic message | 02 §3, §7.6; 03 §2; 04 §4.6, §7.3-7.4 | — |
| **P2** | Data layer & migrations | P1 | `ConnectionFactory` (pragmas), `IUnitOfWork` (BEGIN IMMEDIATE + `app_context`), SQLite error mapper, `MigrationRunner` (backup first, single-tx chain, fingerprint, rollback), `BaselineSplitter`, backup/restore/integrity **libraries** | (1) Recipes' transaction protocol tested: error inside a tx leaves `app_context` and data unchanged; (2) migration test: N-1 fixture upgrades with equal fingerprint; a deliberately failing script rolls back to the exact prior state; a failing backup aborts the upgrade; newer-than-app DB refused; (3) backup via online API verified (integrity + counts + sha); restore round trip equals original; (4) ported schema behavioural suite (65 assertions) + 6,000-op stress run with 14 invariants, 4 seeds, green with `recursive_triggers` OFF and ON; (5) swap-journal crash simulation at each step recovers deterministically | 03 §9, §0; 04 §5, §8, §9.2 | **Yes** (data safety) |
| **P3** | Security & access services | P2 | `PasswordHasher` (Argon2id/PBKDF2/bcrypt-verify), `RecoveryKey`, `AuthService` (login/lockout/sessions/change/recover), `IAuthorizer` + `[Guard]` proxy + `SiteScope`, `AccessService`, `UserService`, `SiteService`, `BusinessSettingsService`, `SetupService` | (1) Hash tests: Argon2id/PBKDF2 round trip, parameters in PHC string, bcrypt known answers incl. Arabic passwords, rehash-on-login; login < 400 ms on the reference slow PC; (2) lockout 5/15 min, inactive refused, unknown user timing-equalised, session revoked on deactivate; (3) **guard-matrix test covers 100 % of `I*Service` methods** (none without a guard attribute) and the policy table of 04 §6.6 (82 names) is verified from a fixture; (4) admin rules of 02 §4.3 (last admin, self change, grant-what-you-lack, only admin edits admin) each have a test; (5) recovery: key accepted once, rotated, rate-limited; Windows-admin path refuses a non-elevated caller | 02 §4-5; 03 §5; 04 §6 | **Yes** (security) |
| **P4** | App shell, login, first-run | P3 | Single-instance start-up gate, theme/fonts/RTL, navigation + dialogs + toasts, login, change-password, **lock overlay**, first-run wizard (incl. recovery sheet printing), landing resolver, recovery/start-up error screens | (1) Fresh machine: app starts, wizard completes, admin signed in, `first_run_completed=1`, one wizard transaction (kill mid-wizard leaves a blank DB); (2) idle lock hides content and unlock needs the password; (3) landing order of 02 §5.5 for each preset incl. accountant and a no-permission user (`NoLandingPage`, no loop); (4) corrupt-file and newer-version start-up paths show the recovery screen; (5) FlaUI smoke passes; (6) RTL review: tab order, alignment, Arabic-Indic digits in every control | 02 §4, §6; 04 §4, §6, §9.1, §9.5 | UI review |
| **P5** | Master data & shared UI | P4 | `DataTable`, `ArabicDatePicker`, `NumberInput`, `MoneyText`, global search, site switcher; screens: Contacts (+ form), Inventory (+ item screens, adjust, transfer, **opening stock**), Settings (branches, business identity), Users (+ access editor) | Checklist R-08, R-09, R-13, R-14 (without ledger list), R-20, R-21, C-03, C-05, C-11, C-12, C-14 (switcher/search), C-15, C-16; plus: contact edit keeps method ids and refuses removal of a used method (DIV-18); address/notes displayed (DIV-16); phone validation per 02 §3; every button hidden **and** service-refused for users lacking the key (DIV-5) | 02 §7.8-7.10, §7.15-7.17, §7.23-7.24, §8.3, §8.9 | — |
| **P6** | Payments, drawer & wallets | P5 | `PaymentService` (incl. R1 overpay, delete with credit compensation), `CashService`, `CreditService`, `VendorCreditService`, `PaymentsPanel`, credit cards, `ManualCashModal`, cash movements list | (1) Scenarios of 02 §8.4-8.7 pass (overpay SO/PO, apply, refund, deposit; floors; auto-close/reopen); (2) DIV-11/G3 guard tests; (3) deleting a `credit` payment restores the wallet (DIV-22); (4) payment instant/`paid_at` rules, future date refused, cash row `created_at = paid_at` (S-2, DIV-24/27); (5) cash drawer never negative under random operations | 02 §8.4-8.7, §8.5; 03 R1-R4 | **Yes** (money) |
| **P7** | Purchasing | P6 | `PurchaseService` (general + itemized, R6/R7, conversions), PO screens and modals, `AttachmentService` + panel, `PoPricing` UI | R-06, R-07, C-01, C-02, C-10, C-20 ticked; 100 kg → three conversions across two branches example reproduces exact stock; conversion over remaining refused with the exact Arabic message; reversal guarded by branch stock; itemized edit verifies new total >= paid (DIV-20); unit = item unit (DIV-31); branch check on line conversions (DIV-29); PO edit audit rows (DIV-23); attachments limits (10 MB, mime allow-list, image downscale) | 02 §7.4-7.7, §8.1, §8.12 | — |
| **P8** | Sales | P6 | `SalesService` (draft, discount, lifecycle, **R5 admin edit**), SO screens, form with live stock hint | R-10, R-11, C-04 ticked; lifecycle table of 02 §8.2 incl. illegal-transition message; **B1/B2/B3 regression tests** (invoice works; closed-order edit leaves stock correct; amount-discount order editable); draft payment refused; cash payment branch rule; no branch change on an order with cash payments (DIV-21); discount rules and rounding; order numbers gap-free and assigned at insert | 02 §7.11-7.13, §8.2; 03 R5 | **Yes** (R5) |
| **P9** | Expenses & opening balances | P6 | `ExpenseService` (record, void, rules, dues, skips, summary), expense screens + dialogs + bell + dashboard banner; `ContactOpeningService` + card | R-16, C-08, C-13 ticked; due-list query equals the PostgreSQL function on the 180-day fixture; void posts compensating cash; skip needs reason; rule with posted expenses cannot be deleted (G8); opening set is delta-based and never writes cash; settle allowed to `payments.record` (DIV-10) | 02 §8.10-8.11; 03 R9-R12, C9 | — |
| **P10** | Insight & print | P7, P8, P9 | `DashboardService`, `ReportService`, `FinancialService`, `AuditService`, contact ledger/statement, `PrintLayoutEngine` + document builders + preview + PDF writer | R-05, R-12, R-14 ledger list, R-15, R-17, R-18, R-19, C-18, C-19, N-09 ticked; every figure of 02 §9 equals a hand-computed oracle on a generated dataset; dashboard identity: cash panel reconciles exactly from `cash_movements`; branch view shows «غير مرتبط بفرع» (DIV-25); statement carries a carried-forward row and includes vendor advances (DIV-17); printed pages: header repeats, signature box kept with totals, page numbers, Arabic-Indic digits and Plex glyphs verified, PDF opens in two viewers; performance targets of 04 §12.9 | 02 §7.3, §7.14, §7.18, §7.20-7.22, §9, §10 | Print review |
| **P11** | Data-safety operations | P2, P3 (UI after P4) | `MaintenanceService` + Settings area: backup now, scheduler, retention, off-site copy, optional encryption key, restore, integrity check/repair, salvage, full export, problem report, backup badge | N-04, N-05, N-06 ticked; schedule + catch-up + on-exit behave with a fake clock; retention never deletes the newest verified backup and does nothing when the latest backup failed; restore with a failing step swaps back; restore of an older-version backup migrates; corrupted-copy detection; encrypted backup round trip; problem report contains no secrets (scan test); free-space and same-volume warnings | 04 §9 | **Yes** |
| **P12** | Legacy import | P2, P3, P11 | `Shabana.LegacyImport` + wizard UI + retirement helper | 05 §12 test plan green in the `legacy-import` job: zero red lines on the fixture, all negative tests produce the documented outcome, bcrypt known answers, swap crash recovery, modes A-D; verification screen texts reviewed in Arabic; schema amendments S-1/S-2 in baseline | 05 | **Yes** |
| **P13** | Installer & release pipeline | P4 (P12 for the legacy kit) | Inno script + Arabic wizard, ACLs, upgrade-over-old logic, legacy kit component, signing step, `build-native.yml` (test, ui-smoke, legacy-import, package, release) | Silent install → `--selftest` exit 0 → silent upgrade over previous release with fixture DB preserved → silent uninstall keeps data; old-edition fixture VM (or container-free simulation of its services/registry) detected, public link disabled, three services disabled, Postgres left running, nothing deleted; unsigned and signed paths both build; installer shows Arabic RTL text; SHA-256 published | 04 §11 | **Yes** |
| **P14** | Hardening & parity sign-off | P10, P12, P13 | Defect regression suite, performance run, accessibility/RTL pass, manual Windows VM pass, **real x86-64 PC pass** | 04 §14 checklist 100 % ticked (dropped items justified); 04 §13: every id has a green named test; performance targets met on the 3-year dataset; no unhandled exception path leaves a stack trace on screen; zero known data-loss bugs; vulnerability scan clean; threat-model notes reviewed (unencrypted file, recovery paths) | all | Full review |
| **P15** | Pilot & cut-over | P14 | Pilot on a copy of the shop's real PC, then production cut-over; docs updated; old edition archived | Import of the **real** old install succeeds with zero red lines on the shop PC; two weeks of parallel read-only comparison (owner checks daily closing against the old data) without discrepancy; a restore drill performed by the owner using only the UI; updated `README.md`, `CLAUDE.md` (four roles, 38 keys, native stack, closing rule), `SCHEMA.md`; old edition tagged `pc-edition-final`; v1.0 released signed | 01-05 | Owner sign-off |

**Post-1.0 candidates (not planned, listed so no one builds them by accident):** headless scheduled-task backup mode; LAN host mode + remote client (04 §5.6); sales-order attachments UI; webcam capture; real-text PDF; SQLCipher encryption; update-check feed; chart dashboards.

### Suggested agent hand-off pattern
One agent per phase (P5-P10 may be split by screen after P4 lands the shared controls); each agent receives: this file, `04`, the relevant `02`/`03` sections, the checklist items, and the previous phase's merged code. Every phase ends with a short `docs/phase-N-report.md` listing deviations from the spec; deviations that change behaviour must be raised to the owner, not silently decided.
