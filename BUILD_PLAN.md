# Shabana Inventory — PC App (local-first Windows build)

## What this is

A Windows installer that stands up **the same مخزون شبانة app** (same React
frontend, same 32 Postgres migrations, same RLS/permissions model) on a
single shop PC with **no internet dependency**. One PC acts as the server;
other devices on the shop's Wi-Fi (or, optionally, a public tunnel URL) reach
it as clients. When the PC or its internet drops, the shop keeps selling on
the LAN — only remote/tunnel access is lost. That's by design, not a bug.

This is a **separate product** from the cloud version
(`alqasim91/Shabana-Inventory`, live at shabana-inventory.vercel.app on
Supabase Cloud). Same application code lineage, different deployment target.
It replaces the earlier Electron-style `PC App/` folder inside the main
repo — that approach is abandoned in favor of what's described here. Do not
resurrect it without reason.

## Why not Docker

Considered and rejected: self-hosting the Supabase Docker stack. Docker
Desktop requires WSL2 + BIOS virtualization + several GB RAM, and this
installer is run **unattended by the shop owner**, not by us. A failed
Docker install on a five-year-old counter PC with no internet and no one to
call is the worst-case support scenario for this product. Native binaries
avoid the entire dependency.

## Why not "self-contained HTML + local DB in the browser"

Considered and rejected: PGlite/WASM-Postgres running entirely inside the
browser. It breaks the permission system (RLS is enforced *by the database*
process, not by client-side code — a browser-local DB has no real boundary
between users) and doesn't support multiple devices reading one shop's data
simultaneously, which the multi-user/per-permission work in the cloud app
already assumes.

## Architecture

Four native Windows binaries, no virtualization, running as Windows
services, installed under `C:\ProgramData\Shabana\` (never a user profile —
Windows usernames are often Arabic on these machines, and Postgres on
Windows does not handle non-ASCII data-directory paths well):

| Component | Binary | Role |
|---|---|---|
| Database | PostgreSQL (EDB Windows binaries) | Same schema/migrations as cloud. `initdb` with `--encoding=UTF8 --locale=C`, `timezone = 'Africa/Cairo'` set explicitly (IANA name handles Egypt's DST correctly — never hardcode a UTC offset). |
| REST API | PostgREST | Turns tables + RPCs into the same REST surface `supabase-js` already speaks. No app-layer rewrite needed. |
| Auth | GoTrue | Issues the same JWTs cloud Supabase does, so `auth.uid()` and every RLS policy work unmodified. No SMTP configured — email confirmation disabled, password reset is a **local admin script**, not an email flow. |
| Frontend + reverse proxy | Caddy | Serves the built React app and reverse-proxies `/rest/v1` → PostgREST, `/auth/v1` → GoTrue, all on one port (`:8000` by default). |

The React app itself is **unchanged application code** — same
`src/services`, same components. Only `VITE_SUPABASE_URL` /
`VITE_SUPABASE_ANON_KEY` point at the local Caddy instance instead of
`*.supabase.co`.

Optional: `cloudflared` (Cloudflare Tunnel) as a fifth service, for
customers who also want a public HTTPS URL. Not installed by default — see
"Editions" below.

## Editions

Same installer, same code, different install-time choice:

- **Local-only** — LAN access only. What every customer gets by default.
- **Local + tunnel** — adds `cloudflared`, routes a public HTTPS URL
  (customer subdomain under our domain, or Tailscale Funnel with no domain
  purchase) to the same local database. No sync, no replication — one
  database, two ways to reach it. Remote access is lost during an outage;
  the shop itself keeps working. This tradeoff was explicitly chosen by the
  user and is not a defect to "fix."

## The 8 hardening items (and how each is being solved)

These were identified as the things that will actually bite a customer if
skipped. Status reflects what's built vs. still open — keep this section
current as work lands.

1. **Per-install secrets** — installer generates a fresh JWT secret + DB
   password per machine (`installer/scripts/generate-secrets.ps1`). Shipping
   one shared secret to every customer would let one customer forge a
   `service_role` token against another customer's tunnel URL — this is the
   single highest-severity item in the whole project. **Needs a security
   review pass (Opus) before any real customer install** — the pattern
   follows Supabase's own self-hosting docs (HS256 shared secret, standard
   anon/service_role JWT claims) but has not yet been adversarially checked.
2. **Backups + restore** — nightly `pg_dump` via Windows Task Scheduler,
   14-day retention, written to a folder the owner picks at setup (nudge
   toward a second drive / USB). Dashboard shows "آخر نسخة احتياطية: …".
   `restore.bat` is a tested, one-click restore path.
3. **Timezone** — `Africa/Cairo` set at `initdb` time. Solved by config, not
   code.
4. **Admin lockout** — `reset-admin.bat` runs locally on the server PC,
   updates `auth.users` via `psql` directly. No email path exists, so this
   is not optional — must ship in v1.
5. **Code signing** — sign the installer `.exe` only (not each bundled
   binary). Deferred until the installer actually works end-to-end; start
   the cert purchase early since OV certs take time to build reputation.
6. **Upgrades** — `schema_migrations` tracking table; upgrade runner applies
   only new `.sql` files, takes a `pg_dump` before starting, restores
   automatically on any failure. Touches live customer data unattended —
   **needs a security/correctness review pass before shipping**, same
   reason as item 1.
7. **Install path + encoding** — `C:\ProgramData\Shabana\`,
   `initdb --encoding=UTF8 --locale=C`. Solved by installer convention.
8. **Blind support** — "تصدير تقرير المشكلة" button zips service logs +
   recent DB errors to the Desktop for the owner to send over WhatsApp.

## First-run flow

Installer does **only mechanical setup** — lay down binaries, `initdb`, run
migrations, generate secrets, register services, desktop shortcut, open
browser to `http://localhost:8000`. No credentials are collected in the
Windows installer UI (no rollback story if that fails mid-wizard, and no
risk of a password landing in an install log).

The **app itself** detects "no admin exists yet" and shows a one-time setup
screen (Arabic, same UI as the rest of the app) collecting business name,
admin account, first فرع. This screen must be **gated to `127.0.0.1` only**
in Caddy (`remote_ip 127.0.0.1`) — otherwise there's a window where an
unclaimed admin account sits behind a public tunnel URL and whoever reaches
it first owns the shop's books.

## Build pipeline

Everything is buildable on macOS; only final verification needs real x86-64
Windows (see "Testing" below).

- **GitHub Actions** (`.github/workflows/build-installer.yml`), `windows-latest`
  runner: downloads pinned versions of the four binaries, builds the
  frontend against local-target env vars, compiles the Inno Setup script,
  attaches the resulting `.exe` to a GitHub Release. Every push to `main`
  (or a tag) produces a new downloadable version.
- **Inno Setup** (`installer/setup.iss`) — compiled by the Actions runner
  (real Windows), so no Wine/cross-compile fragility to worry about.
- Secrets are **never** committed to this repo. The repo holds the code that
  *generates* a secret on the customer's machine; it never holds one itself.

## Testing

A GitHub Actions Windows runner builds the installer but cannot verify it
*installs* correctly (initdb succeeds, services start, first-run screen
appears) — it's a clean ephemeral VM, not a stand-in for a shop PC.
Recommended loop:
1. Windows VM on this Mac (VMware Fusion free tier, or UTM) for the
   fast iterate loop — build → install → find a Windows-specific bug (path,
   service, script quoting) → fix → reinstall.
2. One final pass on **real x86-64 Windows hardware** before any paying
   customer sees it — Apple Silicon VMs run Windows-on-ARM, which emulates
   x86-64 well but isn't identical to what customers actually have.

Claude Code can run directly on the Windows VM/machine against this same
repo (`git clone` there too) — useful specifically for the Windows-only
failures (service won't start, PowerShell quoting, path-with-space) that
can only be diagnosed by reading real Windows service logs.

## Model division of labor (for this build)

- **Sonnet 5** — the bulk: Actions workflow, Inno Setup script, backup/
  restore/reset-admin scripts, timezone/encoding/path config, frontend
  first-run screen, backup indicator, problem-report button, general
  scaffolding and iteration against Windows failures.
- **Opus** — a security/correctness review pass specifically on: secret
  generation (#1 above), the migration/upgrade runner (#6 above), and any
  change reshaping auth/RLS for local admin creation (the no-SMTP
  SECURITY DEFINER function replacing the cloud Edge Function). These are
  the three places where a plausible-looking-but-wrong implementation is
  expensive — not because Sonnet can't write correct code, but because the
  failure mode is silent and the cost of being wrong is a shop's financial
  data or a cross-customer security hole.

## Build log — what's done, what's verified, what's still open

Written 2026-08-09, Sonnet 5 pass. Status snapshot — update as work lands,
don't treat as current without checking git log.

**Done and reasonably solid:**
- Repo scaffolded, 32 migrations copied from `alqasim91/Shabana-Inventory`.
- `supabase/platform-bootstrap.sql` — vendored verbatim from
  `github.com/supabase/postgres` at commit
  `ff09b101523c6479f37fe1e0d02c5f7e3845104c` (2026-08-09), not written from
  memory. Two real bugs found and fixed while sourcing it, worth knowing
  about since they're the kind of thing that'd otherwise fail silently:
  1. The vendored scripts `ALTER` a `supabase_admin` role they assume
     already exists (Supabase's own Docker image creates it implicitly via
     `initdb -U supabase_admin`). Our `initdb` runs as `postgres` instead,
     so a one-line shim (`create role supabase_admin;`) was added before
     the vendored content — see that file's header for the full
     reasoning.
  2. Nothing in the vendored scripts sets a password on `authenticator` or
     `supabase_auth_admin` — PostgREST and GoTrue would have had no way to
     log in. `provision.ps1` now sets both (and `supabase_storage_admin`)
     to the one generated DB password after bootstrap runs.
- `installer/scripts/provision.ps1` — reordered so secrets generate
  *before* `initdb` (the generated password becomes the `postgres`
  superuser's own initial password via `--pwfile`, which also fixed a
  latent bug: the original draft pointed `--pwfile` at an empty temp
  file).
- Binary sources verified against live release APIs, not assumed:
  Postgres via `theseus-rs/postgresql-binaries` (portable msvc build),
  PostgREST ships an official `windows-x86-64.zip`, Caddy ships an
  official `windows_amd64.zip`. **GoTrue does not ship a Windows binary at
  all** (checked `supabase/auth`'s release assets — linux/darwin/arm64
  only) — the CI workflow cross-compiles it from source instead
  (`GOOS=windows go build`), which is the correct approach for a Go
  binary, not a workaround.
- `backup.ps1` / `restore.ps1` / `reset-admin.ps1` / `migrate.ps1` — the
  automated rollback path in `migrate.ps1` calls `restore.ps1 -Force` to
  skip its interactive confirmation prompt; without that flag an
  unattended migration failure would hang forever waiting for a "YES" that
  never comes. Caught and fixed during this pass.
- **Per-machine frontend config, without touching
  `alqasim91/Shabana-Inventory`.** Original plan needed a small edit to
  that repo's Supabase client bootstrap (read a runtime-fetched config
  file). Explicitly ruled out — that repo stays untouched. Solved instead
  entirely within this repo: CI builds the frontend with placeholder
  tokens (`__SHABANA_RUNTIME_SUPABASE_URL__` /
  `__SHABANA_RUNTIME_ANON_KEY__`) standing in for
  `VITE_SUPABASE_URL`/`VITE_SUPABASE_ANON_KEY` — Vite bakes these in as
  literal strings at build time regardless of what the values are, so no
  source change was needed, only different env var *values* passed to a
  build the cloud repo already supports. `patch-frontend-config.ps1` then
  does a plain text find-and-replace across the built `.js` files at
  install time, once per machine, substituting the real local URL and
  generated anon key. The token strings are duplicated in two places
  (`.github/workflows/build-installer.yml`'s build step and
  `patch-frontend-config.ps1`'s header) and must stay byte-for-byte
  identical — the patch script refuses to continue if it finds zero
  matches, specifically to catch drift here loudly instead of silently
  shipping an unpatched frontend.

**Explicitly not done — needs your decision, not just more building:**
- **`FRONTEND_REPO_PAT` secret** — the CI workflow clones
  `alqasim91/Shabana-Inventory` (private) to build the frontend; needs a
  PAT with read access added as a repo secret on
  `Shabana-inventory-PC-App`. Not set — CI will fail on that step until it
  is.
- **Code signing** — deliberately deferred per BUILD_PLAN.md item #5,
  start once the installer works end-to-end unsigned.
- **Nothing in this repo has been run.** Not on a Windows VM, not on real
  hardware, not even `ISCC.exe` locally. First real signal will be the CI
  workflow's own build log once the two items above are resolved.

## Constraints carried over from the main project

- Arabic-only UI, RTL, `ar-EG` locale, EGP currency — unchanged.
- Money as `NUMERIC(12,2)`, never float — unchanged.
- Stock/cash remain append-only ledgers (`stock_movements`,
  `cash_movements`) — unchanged; this also happens to make the ledgers the
  easiest part of the schema to eventually sync/replicate if that's ever
  wanted.
- `profiles.role` (admin/manager/staff) + the `has_perm()`/`can_use_site()`
  permission system from migrations 0031/0032 — reused as-is.
