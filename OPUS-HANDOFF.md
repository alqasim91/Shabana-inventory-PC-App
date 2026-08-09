# Opus handoff — the security-critical pieces left for review/build

This document is the boundary between the Sonnet scaffolding pass and the
Opus pass. Everything below was **deliberately not written by Sonnet**,
because a plausible-looking-but-wrong implementation here is expensive: the
failure modes are silent, and the cost is a shop's financial data or a
cross-customer security hole.

Read alongside [BUILD_PLAN.md](BUILD_PLAN.md), whose items #1 and #6 and the
"model division of labor" section flag the same work.

---

## 1. Review the three flagged files (already written, need a second pair of eyes)

These were written in the Sonnet pass and *work in theory*, but touch
secrets / money / auth and were flagged for adversarial review before any
real customer install:

- **`installer/scripts/generate-secrets.ps1`** — per-install JWT secret, DB
  password, and derived anon/service_role JWTs. Verify: HS256 signing is
  correct and matches what PostgREST 16 and GoTrue v2.195 actually expect;
  the JWT claims (`role`, `iss`, `iat`, `exp`) are sufficient (does GoTrue
  need `aud`? does PostgREST care about `iss`?); randomness source is
  sound; nothing is logged or committed. The 10-year expiry is a
  deliberate choice — challenge it.
- **`installer/scripts/migrate.ps1`** — unattended migration runner with
  backup-then-rollback-on-failure. Verify the rollback actually restores
  cleanly (needs a real test, not a read-through), that `ON_ERROR_STOP=1`
  behaves as assumed across the psql calls, and that a partial failure
  can't leave `shabana_migrations` claiming a migration applied when it
  didn't.
- **`supabase/platform-bootstrap.sql`** — vendored from supabase/postgres
  (provenance in its header). The key thing to verify is the `auth.uid()`
  definition (reads `request.jwt.claim.sub`) against the **pinned
  PostgREST version** — older/newer PostgREST has used
  `request.jwt.claims` (plural JSON) instead of per-claim GUCs. If that's
  wrong, EVERY RLS policy silently sees a null uid and the whole
  permission system fails open or closed. This is the single most
  important line to check in the whole repo.

---

## 2. Build the single-tenant first-run bootstrap (the real new work)

### The problem

The cloud app is **multi-tenant**. Onboarding a business is done by
`supabase/functions-cloud-reference/create-organization/index.ts`: a
*platform admin* (row in `platform_admins`) calls it, it mints the owner's
auth user via the GoTrue admin API using the **service_role key**, then
calls the `provision_organization()` SQL RPC (org + first admin profile +
first site + document counters, one transaction). Team users are added
later by `admin-create-user/index.ts`, gated on being a *tenant admin*,
same service-role auth-minting pattern.

Neither works on a PC install, for three independent reasons:

1. **No Deno edge runtime.** These are `Deno.serve` functions; nothing runs
   them locally.
2. **No platform admin, and a chicken-and-egg.** First run has an empty
   database — no org, no admin, no one authorized to create either.
3. **The service_role key cannot live in a browser.** The cloud pattern
   keeps it server-side in the edge function. A localhost setup page has no
   equivalent server tier to hide it in.

### What "done" looks like

- **First run:** a localhost-only page (Caddy already gates `/setup*` to
  `127.0.0.1` — see `installer/config/Caddyfile.template`) that, when no
  organization exists, collects business name + admin username/password +
  first site name, and creates all of it. This is the PC equivalent of
  `create-organization`, minus the platform-admin gate (replaced by
  "localhost + no org yet exists").
- **Ongoing:** the existing in-app user-creation flow
  (`src/services/admin.ts` → `createUser()` →
  `supabase.functions.invoke('admin-create-user')`) needs a local
  equivalent, since `functions.invoke` will 404 with no edge runtime. The
  frontend is now **vendored and owned** (`frontend/`), so `admin.ts` can
  be pointed at whatever local mechanism you choose — editing it is fine
  and expected now.

### The core decision (yours to make)

Both flows must mint `auth.users` rows that GoTrue will accept at login.
Two approaches:

- **(A) SECURITY DEFINER SQL functions** that write `auth.users` directly,
  hashing the password with `pgcrypto` `crypt(pw, gen_salt('bf'))`. No edge
  runtime, no service key in the browser — the browser calls an RPC over
  PostgREST like any other. `installer/scripts/reset-admin.ps1` **already
  uses exactly this pattern** for password reset, so there's precedent in
  the repo to be consistent with. The thing to verify: whether GoTrue
  v2.195 needs a matching `auth.identities` row (and/or specific
  `aud`/`role`/`confirmed_at` columns) for a hand-inserted user to log in
  — this has changed across GoTrue versions and MUST be checked against the
  pinned version, not assumed. The reset-admin script only *updates* an
  existing row, so it hasn't had to solve the identities question — a fresh
  insert does.
  - Gating for the first-run function: it must refuse to run if any org
    already exists (so it's a true one-shot), and ideally only be callable
    by the `anon` role in that empty-DB window. For ongoing user creation,
    the SECURITY DEFINER function must re-check the caller is a tenant
    admin server-side (the cloud edge function does this against
    `profiles` — replicate it, do NOT trust the client).

- **(B) A tiny local sidecar** holding the service key, exposing a
  localhost-only endpoint that calls the GoTrue admin API — closer to the
  cloud code, but adds a fifth service to install/supervise and a place the
  service key lives on disk in a process. Probably not worth it vs. (A),
  but your call.

### Single-tenant simplifications you may want

- **Slug.** Login derives the email as `username@<slug>.local` and routing
  has `/:orgSlug/login` (see `frontend/src/App.tsx`,
  `frontend/src/pages/Login.tsx`). For one shop, a fixed slug (e.g.
  `shop`) is simplest — the setup function can hardcode it, and the desktop
  shortcut can point straight at `/shop/login` or you can simplify routing
  to `/login`. Minimal-change (keep the machinery, fix the slug) is the
  safer default.
- **`platform_admins` / `create-organization` / `provision_organization`**
  — decide whether the first-run function reuses `provision_organization()`
  (it already does org+profile+site+counters atomically — reusing it means
  less new SQL to get right) or whether a purpose-built single-tenant
  function is cleaner. Reuse is probably right.

### Where everything lives

- Cloud originals to mirror: `supabase/functions-cloud-reference/*`
- The SQL RPC they call: `provision_organization()` — defined in
  `supabase/migrations/0027_platform_provisioning.sql`
- Frontend entry points to rewire: `frontend/src/services/admin.ts`,
  `frontend/src/pages/Login.tsx`, `frontend/src/App.tsx`
- Localhost gate already in place: `installer/config/Caddyfile.template`
  (`/setup*` block)
- Password-hashing precedent: `installer/scripts/reset-admin.ps1`

---

## 3. Smaller, non-critical leftovers (fine for Sonnet, listed for completeness)

- **Task #11** — last-backup indicator (read `backups/last-backup-status.json`,
  show timestamp on the dashboard) + problem-report export button (zip
  `logs/` + recent errors to Desktop). No security surface; just needs the
  frontend to be building first.
- **Icons** — `installer/setup.iss` references `payload\icon.ico`, not yet
  created. The app already has `frontend/public/icons/*.png`; convert one.
- **Code signing** (BUILD_PLAN.md item #5) — deferred until the installer
  works unsigned end-to-end.
