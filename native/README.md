# Shabana Inventory - native Windows edition (`native/`)

.NET 10 LTS, WPF (RTL, ar-EG), SQLite (Dapper + Microsoft.Data.Sqlite), x64 only. Specs: `../native-conversion/` (`00-README.md` = decisions and phase plan, `04-NATIVE-ARCHITECTURE.md` = how it is built). This folder holds **P0 (Foundations)**: the skeleton, the build gates and the data baseline, **P1 (Core primitives)**: the pure domain building blocks, and **P2 (Data layer and migrations)**: connections, transactions, migrations, backup, restore and integrity; no screens yet.

## Build and test

```
cd native
dotnet restore --locked-mode      # lock files are committed; fails if they are stale
dotnet build -warnaserror
dotnet test
```

Needs the .NET 10 SDK (`global.json`). On Linux/macOS the WPF project compiles too (`EnableWindowsTargeting`), but it can only run on Windows. All tests target `net10.0` and run on any OS.
After changing a package version in `Directory.Packages.props`, run `dotnet restore --force-evaluate` and commit the changed `packages.lock.json` files.
Vulnerability check (also run by CI): `dotnet list Shabana.sln package --vulnerable --include-transitive`.

## Layout

```
Shabana.sln  Directory.Build.props  Directory.Packages.props  global.json  nuget.config  .editorconfig
src/Shabana.Core      net10.0          domain + services (later); Resources/labels.ar.json is the SOURCE OF TRUTH for all Arabic text
src/Shabana.Data      net10.0          SQLite; Migrations/Scripts/0001_baseline.sql (embedded) + Migrations/Reference/seed.sql
src/Shabana.App       net10.0-windows  WPF shell (MainWindow, RTL, title from Labels)
tests/Shabana.Tests   net10.0          xunit + Shouldly: baseline/smoke, generator, analyzer, architecture
tools/LabelsGen       netstandard2.0   incremental source generator: labels*.ar.json -> Labels.g.cs
tools/Shabana.Analyzers netstandard2.0 analyzer SHB0001: no Arabic literal outside the JSON files
```

Dependency rule (enforced by `ArchitectureTests`): Core references nothing of ours and never SQLite/WPF; Data -> Core; App -> Core, Data; tools reference nothing and reach src projects only as analyzers.

## Labels (`Shabana.Core.Resources.Labels`)

All Arabic strings live in `src/Shabana.Core/Resources/labels.ar.json` (+ `labels.native.ar.json` for native-only text). At build the generator turns every entry into a `const string`: `Labels.Common.Save`, `Labels.Contacts.Title`; keys are PascalCased (`PERM_LABEL` -> `PermLabel`, `sales.view` -> `SalesView`). `Labels.<Group>.ByKey` gives the original-key lookup for data-driven labels.
The 16 `_templates` become methods with Arabic-Indic digits and the `n == 1` variant: `Labels.Table.ShowingCount(12, 1234)`, `Labels.Expenses.DueBannerTitle(1)`.
A reference to a missing key is a compile error (CS0117); malformed JSON, duplicate keys, colliding names and bad templates are build errors `SHL0001`-`SHL0005`.
`native-conversion/resources/labels.ar.json` is the original hand-off copy; edit the one in `src/Shabana.Core/Resources/`.

## Rule: no Arabic literal in code

Analyzer `SHB0001` (error) rejects Arabic in string/char literals (also via escapes) in `.cs` and in `.xaml` attribute/text values of every project under `src/` (generated code and comments are exempt). Tests are exempt (golden vectors). Use `Labels`.

## Database baseline

`0001_baseline.sql` is `native-conversion/sqlite/schema.sql` byte for byte (a test pins it). Expected catalog: 36 tables, 13 views, 108 triggers, 61 explicit indexes (SQLite also keeps 33 automatic `sqlite_autoindex_*`). `smoke-test.sql` runs against the embedded script. The minimum SQLite engine is 3.38 (`SqliteRuntime.MinimumVersion`).

## Core primitives (P1, `Shabana.Core`)

```
Numerics/    Money (piasters), Qty (milli), Bp (basis points), MoneyMath (Int128, round half away from zero), PoPricing (bidirectional PO pricing + recency), Limits
Formatting/  ArabicNumber (fixed digit/separator tables), MoneyFormat, QtyFormat, PercentFormat, ArabicDate, OrderNumber, Currencies, NumberInput (text -> scaled integer)
Text/        SearchNormalizer (port of web lib/search.ts), PhoneEg (port of lib/phone.ts)
Auth/        Username, PasswordPolicy
Sales/       DiscountRules
Time/        IClock, BusinessCalendar (NodaTime tzdb, Africa/Cairo day bounds), BusinessZones, InstantText
Errors/      ErrorCode (the 34 codes of 03 section 9, equal to the codes the baseline schema raises), ErrorCodes (code <-> enum <-> Arabic message)
```
No `double`/`decimal` in Core arithmetic. Code points of Arabic characters are written numerically in code (SHB0001); the Arabic text (error messages, month names, currency symbols/names, time-zone names, password messages) is in `labels.native.ar.json` (groups `ERR`, `ERR_GENERIC`, `PWD`, `MONTH`, `CURRENCY_SYMBOL`, `CURRENCY_NAME`, `TIMEZONE_NAME`).
`tests/Shabana.Tests/Fixtures/web-crosscheck.json` holds outputs of the web app's real functions (search, phone, digits, dates, formatMoney/formatQty) for the cross-check tests; `gen-web-crosscheck.mjs` regenerates it (`node --experimental-strip-types`, Node 22 with full ICU).

## Data layer (P2, `Shabana.Data`; no WPF)

```
DataLocations                       C:\ProgramData\ShabanaInventory layout (db\, Backups\, restore\, ...), root injectable for tests
Sqlite/ConnectionFactory            the only place a connection is made; 04 section 5.2 pragmas on EVERY open; Open / OpenExisting / OpenUnpooled / OpenForInspection
Sqlite/UnitOfWork (IUnitOfWork)     RunAsync = gate + BEGIN IMMEDIATE + app_context actor + COMMIT/ROLLBACK; ReadAsync = one snapshot, query_only
Sqlite/SqliteErrorMapper            RAISE(ABORT,'CODE|..') -> Core DomainException(ErrorCode); other failures classified (DbErrorKind), never mistaken for a rule
Migrations/MigrationRunner          inspect -> verified pre-migration backup -> ONE transaction (FK off, scripts statement by statement, FK check, integrity check, version mirror, balance fingerprint) -> commit
Migrations/BaselineSplitter         phase 1 (tables, indexes, reference data) / phase 2 (triggers, views) + an idempotent phase 2 for repair
Backup/BackupService + Verifier     .shbk = ZIP(shabana.db from the online backup API, manifest.json); verified twice; final name only after verification
Backup/RestoreService               verify -> pre-restore backup -> checkpoint -> journal (restore\swap.json) -> swap -> validate -> commit; RecoverPendingSwap at start-up
Maintenance/LedgerInvariants        the 14 ledger invariants as SQL (the stress-test oracle AND the owner's integrity check)
Maintenance/DatabaseFingerprint     SHA-256 of schema / data / balances: how migrations and restores are proved
```

Rules that are easy to get wrong (each has a test):
- **Timeouts.** Microsoft.Data.Sqlite retries a locked command for `Default Timeout` seconds (30 by default, **0 = forever**) whatever `PRAGMA busy_timeout` says, so `ConnectionFactory` puts its 5 s there.
- **Multi-statement text.** `SqliteCommand.ExecuteNonQuery` silently stops at the first statement that returns rows. Migration scripts are therefore split (`SqlStatementSplitter`) and run one statement per command; a script may not `BEGIN`/`COMMIT`/`VACUUM`/`ATTACH`.
- **Table rebuilds in a migration** (03 section 6 row 9): the runner turns `foreign_keys` OFF; the script must run `PRAGMA legacy_alter_table = ON` for its own duration (the runner resets it after each script), otherwise `ALTER TABLE .. RENAME` fails on triggers that mention the dropped table. See `tests/.../TestMigrations.cs` (`V3Rebuild`).
- **Pooling.** A pooled connection keeps the file open after `Dispose()`. Code that moves, replaces or deletes a database file calls `ConnectionFactory.ReleaseFileHandles()` first; migration, backup and restore use unpooled connections.
- **Restore never deletes the old database before the new one is committed.** The old file set (`.db`, `-wal`, `-shm`, `-journal`) is renamed to `shabana.db.restore-previous*`; the journal says which side a crash recovers to (before commit: old; after: new).
- A migration is immutable once released: `tests/Shabana.Tests/Fixtures/migration-checksums.json` pins the SHA-256 (LF-normalised) of every script.

Test suites for P2 live in `tests/Shabana.Tests/Data/`. The 65-assertion schema suite and the 6,000-operation x 4-seed stress run are **re-created** (the scripts of 03 section 0 are not in the repository); `SHABANA_STRESS_OPS=500` shortens the stress run for a quick local loop (default and CI: the full 6,000).
