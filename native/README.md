# Shabana Inventory - native Windows edition (`native/`)

.NET 10 LTS, WPF (RTL, ar-EG), SQLite (Dapper + Microsoft.Data.Sqlite), x64 only. Specs: `../native-conversion/` (`00-README.md` = decisions and phase plan, `04-NATIVE-ARCHITECTURE.md` = how it is built). This folder is **phase P0 (Foundations)**: the skeleton, the build gates and the data baseline; no features yet.

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
