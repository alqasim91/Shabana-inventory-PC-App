-- 0029 — the login page needs the business name BEFORE anyone signs in.
--
-- `organization` is protected by `org_read` (`id = current_org()`), which is
-- exactly right for a signed-in user and useless on the login page: there is no
-- session yet, so current_org() is null and the table reads back empty. That is
-- why /shabana/login could only ever show a hardcoded subtitle.
--
-- The narrowest possible hole: one SECURITY DEFINER function that takes a slug
-- and returns ONE column — the display name — for ACTIVE businesses only. It
-- cannot be coaxed into returning an id, a phone, an address, or a row count,
-- and it never touches any tenant table. Suspending a business (active = false)
-- also blanks its login header, which is the behaviour you want.
--
-- Trade-off, stated plainly: someone who guesses a slug learns that business's
-- name. That is a deliberate downgrade from the login form's "same error for
-- wrong password and unknown business" rule, and it is the price of the feature
-- — a shop cannot be shown its own name without the name being fetchable. It
-- leaks a name to a correct guess, nothing else: no data, no user list, no hint
-- about whether any particular username or password exists.

create or replace function public.org_public_name(p_slug text)
returns text
language sql
stable
security definer
set search_path = public
as $$
  select business_name
  from organization
  where slug = lower(trim(p_slug))
    and active
$$;

comment on function public.org_public_name(text) is
  'Login-page display name for a business, by slug. Anonymous-callable by design; returns the name only, and only for active organizations.';

-- EXECUTE is granted to PUBLIC by default — take it back first, then hand it
-- out deliberately, so the grant list is the whole story.
revoke all on function public.org_public_name(text) from public;
grant execute on function public.org_public_name(text) to anon, authenticated;
