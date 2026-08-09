// admin-create-user — lets an authenticated ADMIN create a team account
// (auth.users + profiles) in one call. The browser's anon key cannot mint auth
// users, so this runs with the service role. Every privileged step is gated on
// the caller actually being an admin, verified server-side against `profiles`.
import { createClient } from 'jsr:@supabase/supabase-js@2';

const cors = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
};

const ROLES = ['admin', 'manager', 'staff'];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...cors, 'Content-Type': 'application/json' },
  });
}

Deno.serve(async (req: Request) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });

  try {
    const url = Deno.env.get('SUPABASE_URL')!;
    const anonKey = Deno.env.get('SUPABASE_ANON_KEY')!;
    const serviceKey = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
    const authHeader = req.headers.get('Authorization') ?? '';

    // 1. Identify the caller from their JWT.
    const callerClient = createClient(url, anonKey, {
      global: { headers: { Authorization: authHeader } },
    });
    const {
      data: { user: caller },
    } = await callerClient.auth.getUser();
    if (!caller) return json({ ok: false, code: 'unauthorized' }, 401);

    // 2. Service-role client — privileged checks + user creation (bypasses RLS).
    const admin = createClient(url, serviceKey, {
      auth: { autoRefreshToken: false, persistSession: false },
    });

    // 3. The caller MUST be an admin. Never trust the client for this.
    //    org_id comes from the caller's own profile, never from the request —
    //    otherwise a tenant admin could plant a user inside another business.
    const { data: callerProfile } = await admin
      .from('profiles')
      .select('role, org_id')
      .eq('user_id', caller.id)
      .single();
    if (callerProfile?.role !== 'admin') return json({ ok: false, code: 'forbidden' }, 403);
    if (!callerProfile?.org_id) return json({ ok: false, code: 'no_org' }, 403);

    // 4. Validate input.
    const body = await req.json().catch(() => ({}));
    const password = String(body.password ?? '');
    const fullName = String(body.full_name ?? '').trim();
    const role = String(body.role ?? '');
    // The bare login handle (email is derived from it client-side); stored on the
    // profile so an admin can see how each user logs in.
    const username = String(body.username ?? '').trim().toLowerCase();
    if (password.length < 6 || !fullName || !ROLES.includes(role) || !/^[a-z0-9._-]+$/.test(username)) {
      return json({ ok: false, code: 'invalid_input' }, 400);
    }

    // The login email is derived HERE, from the caller's own organization —
    // never taken from the request. If the client supplied it, a tenant admin
    // could pass `ahmed@othercompany.local` and mint an account inside another
    // business's namespace. The slug comes from the org the caller belongs to.
    const { data: org } = await admin
      .from('organization')
      .select('slug')
      .eq('id', callerProfile.org_id)
      .single();
    if (!org?.slug) return json({ ok: false, code: 'no_org' }, 403);
    const email = `${username}@${org.slug}.local`;

    // 5. Create the auth account, pre-confirmed so it can log in immediately.
    const { data: created, error: createErr } = await admin.auth.admin.createUser({
      email,
      password,
      email_confirm: true,
    });
    if (createErr || !created?.user) {
      const code = createErr?.code === 'email_exists' ? 'email_exists' : 'create_failed';
      return json({ ok: false, code, detail: createErr?.message }, 200);
    }

    // 6. Attach the profile. Roll back the auth user if this fails, so we never
    //    leave an orphan account that can log in but has no role.
    // org_id must be explicit: this runs with the service role, where
    // auth.uid() is null, so profiles' lack of a current_org() default would
    // otherwise fail the NOT NULL constraint.
    const { error: profileErr } = await admin.from('profiles').insert({
      user_id: created.user.id,
      org_id: callerProfile.org_id,
      full_name: fullName,
      username,
      role,
      active: true,
    });
    if (profileErr) {
      await admin.auth.admin.deleteUser(created.user.id);
      // A unique violation here means the username is already taken.
      const code = profileErr.code === '23505' ? 'email_exists' : 'profile_failed';
      return json({ ok: false, code, detail: profileErr.message }, 200);
    }

    return json({ ok: true, user_id: created.user.id }, 200);
  } catch (e) {
    return json({ ok: false, code: 'server_error', detail: String(e) }, 500);
  }
});
