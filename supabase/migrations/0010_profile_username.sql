-- Staff log in with a plain username (no email). The auth identity is still an
-- email under the hood — the app maps username → username@shabana.local — but
-- we store the bare username on the profile so an admin can SEE how each person
-- logs in (otherwise it's only inside auth.users.email, which the client can't
-- read). Unique so two people can't share a login.
alter table profiles add column username text unique;

-- Backfill every existing account (seed users + the bootstrap admin) from the
-- local-part of their auth email, so the Users list shows a username for all.
update profiles p
set username = split_part(u.email, '@', 1)
from auth.users u
where u.id = p.user_id and p.username is null;
