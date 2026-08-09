# مخزون شبانة — PC Edition

Windows installer that runs the same مخزون شبانة inventory app entirely on
a single shop PC — no internet required, no Docker. See
[BUILD_PLAN.md](BUILD_PLAN.md) for the full architecture, the design
decisions behind it, and current build status.

Same application code as
[alqasim91/Shabana-Inventory](https://github.com/alqasim91/Shabana-Inventory)
(cloud/Vercel/Supabase edition) — different deployment target.

See [OPUS-HANDOFF.md](OPUS-HANDOFF.md) for the security-critical pieces left
to build/review (the single-tenant first-run bootstrap and three flagged
scripts).

## Layout

- `frontend/` — vendored read-only copy of the cloud app's source, owned by
  this repo (re-synced manually; see BUILD_PLAN.md)
- `installer/setup.iss` — Inno Setup script
- `installer/scripts/` — PowerShell: provisioning, secrets, backup,
  restore, admin reset, migrations, service registration
- `installer/config/` — per-machine config templates
- `supabase/migrations/` — copied from the cloud repo, kept in sync
  manually (see BUILD_PLAN.md)
- `supabase/platform-bootstrap.sql` — vendored from Supabase's own
  postgres repo, not hand-written
- `.github/workflows/build-installer.yml` — builds the installer on a
  `windows-latest` runner, publishes it as a GitHub Release
