# Opus pass — status of the security-critical pieces

This was the boundary between the Sonnet scaffolding pass and the Opus pass.
The Opus pass is now **done**; this file records what was decided, what was
verified, and — importantly — what still can only be confirmed by running the
installer on real Windows. Read alongside [BUILD_PLAN.md](BUILD_PLAN.md).

---

## 1. The three flagged files — resolved

### `auth.uid()` claim format — was broken, now fixed (the big one)
The vendored `platform-bootstrap.sql` defines `auth.uid()` reading
`current_setting('request.jwt.claim.sub')` — a per-claim GUC PostgREST
**removed in v9.0 (2021)**. Our pinned PostgREST 16 exposes only the single
JSON GUC `request.jwt.claims`. Confirmed against the PostgREST docs. Left
unfixed this makes `auth.uid()` return NULL on every request, so
`is_admin()`/`current_org()`/`has_perm()` all fail and **every RLS policy
denies the user their own data** with no error.

**Fix:** migration `0033_pc_local_auth.sql` redefines `auth.uid()`,
`auth.role()`, `auth.email()` to Supabase's current canonical form (coalesce
the legacy per-claim GUC with the JSON claims object). **Verified** against a
local Postgres: with `request.jwt.claims` set the way PostgREST sets it,
`auth.uid()` returns the correct uid (harness test T12).

### `jwt-aud` mismatch — found and removed
`postgrest.conf.template` set `jwt-aud = "authenticated"`, but the anon and
service_role keys `generate-secrets.ps1` mints carry no `aud` claim (as
Supabase's own keys don't). PostgREST would reject the anon key and the app
couldn't even load. **Fix:** removed `jwt-aud` — every token in this stack
targets this one PostgREST, so auditing audience buys nothing. (Comment in
the template explains it.)

### `generate-secrets.ps1` — reviewed, sound
HS256, `role` claim present, secret shared with GoTrue and PostgREST
(same `{{JWT_SECRET}}` written to both). No `aud`/`iss` checks are enforced,
so their absence/value is harmless. 10-year expiry is a deliberate
appliance-lifetime choice. randomness via `RandomNumberGenerator`. Nothing
logged or committed (and `.gitignore` guards `config/*.key` etc.). One item
that still wants a **real GoTrue login** to confirm end-to-end: that a token
GoTrue *issues* verifies against this secret in PostgREST — true by
construction (same secret) but unproven until the stack runs on Windows.

### `migrate.ps1` — logic reviewed, one fix, still needs a real rollback test
Fixed during this pass: the automated rollback now calls `restore.ps1 -Force`
(the interactive `YES` prompt would hang an unattended run forever). All psql
calls use `ON_ERROR_STOP=1`; `shabana_migrations` is only written after a
migration's psql exits 0, so a failure can't record a phantom apply. The
backup-then-rollback path is logically sound but **not yet exercised on real
data** — that's a required Windows test before trusting it on a customer.

---

## 2. Single-tenant first-run bootstrap — built and tested

Chosen **approach (A)**: SECURITY DEFINER SQL functions that write
`auth.users` directly, no edge runtime, no service key in the browser.
All in `supabase/migrations/0033_pc_local_auth.sql`.

Verified against the pinned versions by reading the source, not assuming:
- **GoTrue v2.195 self-migrates on startup** (`cmd/root_cmd.go`: the no-arg
  default runs `migrate` then `serve`) — so the auth schema fills out to
  v2.195 when the service starts. Our `register-services.ps1` runs it no-arg.
- **Password login needs no `auth.identities` row** (`internal/api/token.go`
  + `models.FindUserByEmailAndAudience`): the lookup is
  `instance_id = all-zeros AND email AND aud AND is_sso_user = false`, then a
  bcrypt check on `encrypted_password`. So a hand-inserted user with
  `aud='authenticated'`, `role='authenticated'`, `email_confirmed_at` set,
  and a `crypt(pw, gen_salt('bf'))` hash is sufficient — which is exactly
  what 0033 inserts (via dynamic SQL, because GoTrue adds `email_confirmed_at`
  / `is_sso_user` only at its first migration, after 0033 is *created*).

**Functions:**
- `pc_needs_setup()` — anon, returns "no org exists yet".
- `pc_first_run_bootstrap(...)` — anon, **one-shot** (refuses once any org
  exists; advisory lock closes the double-submit race), mints the admin auth
  user + calls `provision_organization()` with fixed slug `shabana`.
- `pc_create_user(...)` — authenticated, re-checks `role='admin'`
  server-side, derives email from the caller's org slug, mints auth user +
  profile; the `profile_seed_permissions` trigger seeds permissions as in
  cloud. Replaces `admin-create-user`.

**Tested** against a local Postgres with a GoTrue-shaped `auth.users`
(harness in scratchpad): fresh-run creates org/profile/site/counters;
bcrypt password verifies; one-shot guard blocks a second run; admin can
create staff; duplicate username rejected with no orphan auth row; a
non-admin caller is `forbidden`; `auth.uid()`/`auth.role()` read the JSON
claims correctly. (Full transcript was T1–T12 in the build session.)

**Frontend wired** (vendored, so editing is expected):
- `services/setup.ts` (new), `pages/FirstRunSetup.tsx` (new) — the localhost
  setup screen; `App.tsx` route `/setup`; `Login.tsx` forwards to `/setup`
  on a fresh install; `ProtectedRoute` sends unauth users to `/shabana/login`
  so the business-code field never shows (single tenant).
- `services/admin.ts` `createUser()` now calls `pc_create_user` RPC instead
  of `functions.invoke('admin-create-user')`.
- `Caddyfile.template` — `/setup` serves the SPA shell loopback-only, AND the
  `pc_first_run_bootstrap` RPC path itself is loopback-only (gating only the
  UI would leave the claim RPC remotely reachable on a tunnelled install).

Frontend **builds clean** (`npm run build`) with the placeholder tokens, and
both tokens land in the bundle as clean literals (so install-time patching
will find them).

---

## 3. What genuinely remains

- **A real Windows run.** Nothing here has run on Windows. The whole chain —
  initdb → bootstrap → 33 migrations → GoTrue self-migrate → services up →
  `/setup` → create admin → **log in** → use the app — has been verified
  piece by piece but never end-to-end on the actual stack. First real signal
  is the CI build log, then an install on a Windows VM.
- **`migrate.ps1` rollback** exercised on real data (see §1).
- **GoTrue-issued-token ↔ PostgREST** verified by an actual login (see §1).
- **Icon + code signing** — deferred (BUILD_PLAN.md item #5); `setup.iss`
  now uses Inno's default icon so it compiles.
- **`storage` / attachments** — the Storage server isn't installed; the
  attachments feature won't work on PC until addressed (BUILD_PLAN.md).
- **Platform/operator console** (`create-organization`, `/platform`) — multi-
  tenant tooling irrelevant to a single shop; left in, unreachable, harmless.
