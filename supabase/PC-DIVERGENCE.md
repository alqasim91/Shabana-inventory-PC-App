# Divergence from the cloud migrations

`supabase/migrations/` is vendored from the cloud app (`alqasim91/Shabana-Inventory`,
read-only — never written to from here). Keeping it byte-identical is what makes
re-syncing safe, so **every deliberate difference is listed here.** If you add
one, add it here too.

The default fix for "a cloud migration doesn't apply on PC" is
`supabase/pc-prelude.sql`, which prepares the database *before* the migrations
run. Editing a vendored migration is the last resort.

---

## Handled in `pc-prelude.sql` (no migration edited)

### `0017_order_seq.sql` — `setval(..., 0)` on an empty database
The migration ends with `setval('purchase_order_seq', coalesce(max(order_seq), 0), true)`.
A sequence's default `minvalue` is 1, so on a **fresh** install — where both
order tables are empty and the `coalesce` yields 0 — PostgreSQL raises
`value 0 is out of bounds for sequence`. The prelude pre-creates both sequences
with `minvalue 0`, which makes the migration's own
`create sequence if not exists` a no-op and its `setval` legal. `nextval()`
still returns 1 first, so order numbering is unchanged.

This is a latent bug in the cloud copy as well; it is simply unreachable there
because those tables were never empty when the migration ran.

### `0022` / `0028` — the `storage` schema
Both reference `storage.buckets`, `storage.objects` and `storage.foldername()`.
Those are created by Supabase's `storage-api` service, which the PC edition does
not ship, and `platform-bootstrap.sql` does not create them either. The prelude
provides a schema-compatible shim.

> The shim exists so the migrations apply and the org-isolation policies stay
> enforceable. Nothing reads or writes through it: attachments are handled by
> `0034_pc_local_storage.sql` instead — see below.

---

## Edited vendored migrations

### `0032_permissions_enforce.sql` — guard assumes pre-existing users
The migration aborts if `user_permissions` is empty, as a check that 0031's
backfill ran. 0031 backfills one row per existing **profile**, and a fresh PC
database has none (the first profile is created at runtime by
`pc_first_run_bootstrap`, long after migrations). So the backfill correctly
inserts nothing and the guard then kills provisioning on a perfectly healthy
database.

Changed to fire only when there was data to back-fill:

```sql
if exists (select 1 from profiles) and not exists (select 1 from user_permissions) then
```

The cloud copy has the same latent flaw and would hit it on any brand-new
tenant provisioned from empty. Worth fixing there too — not done from this repo.

---

## PC-only migrations

### `0034_pc_local_storage.sql`
Attachment **bytes** live in Postgres (`pc_file_bytes`), reached through
`pc_file_put` / `pc_file_get` / `pc_file_delete`, because the PC edition ships
no storage service. `order_attachments` remains the single source of metadata;
this table holds content only, keyed by the same `storage_path`.

Access is checked against the attachment **row**, not a path prefix, so knowing
a path proves nothing — stricter than the cloud's bucket policy. Uploads are
still required to sit under the caller's org folder so paths keep the same
shape across editions.

`frontend/src/services/attachments.ts` is the matching client change: same
exported surface as the cloud version, so `AttachmentsPanel` and everything
above it is untouched. Previews come back as object URLs rather than signed
URLs, cached per path so repeated renders neither refetch nor leak blobs.

One real consequence: attachments are inside `pg_dump`, so a backup file is
genuinely the whole shop — and database size now grows with scans and photos.
The 10 MB per-file ceiling from `0022` is enforced in `pc_file_put`.

### `0033_pc_local_auth.sql`
Exists only here. Replaces the cloud Edge Functions (no Deno runtime on a shop
PC) with SECURITY DEFINER SQL, and redefines `auth.uid()/role()/email()` to read
the `request.jwt.claims` JSON GUC — PostgREST 16 no longer sets the per-claim
GUCs that `platform-bootstrap.sql`'s versions read, so without this **every RLS
policy fails closed.**

Note for anyone reading its "fresh install" logic: migrations `0009`/`0024` seed
a default `organization` row (slug `shabana`) as part of the cloud's
single-tenant → multi-tenant conversion. A freshly migrated PC database
therefore always has exactly one organization and zero users, so "is this
install unclaimed?" must test for **no profile**, not for no organization.
`pc_first_run_bootstrap` clears that placeholder org — guarded on nothing being
attached to it — before calling `provision_organization` with the owner's real
business name.
