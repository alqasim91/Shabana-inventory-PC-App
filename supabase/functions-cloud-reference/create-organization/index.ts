// create-organization — provisions a whole new client business (منشأة): the
// organization, its first admin's auth account and profile, a default site and
// its document counters.
//
// Gated on the caller being a PLATFORM admin (the platform_admins table), which
// is a different thing from a tenant admin: a tenant admin runs one business and
// must never be able to mint another. There is no self-serve signup by design —
// the operator onboards each client.
//
// Ordering matters. The auth account cannot share a transaction with the SQL
// half, so it is created first and deleted again if provisioning fails; that way
// a failure leaves nothing behind rather than an account that can log in but
// belongs to no business. Everything after it runs inside
// provision_organization(), a single transaction.
import { createClient } from 'jsr:@supabase/supabase-js@2';

const cors = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
};

// Must match org_slug_format in 0024 — it becomes a DNS label in the login
// email (ahmed@<slug>.local) as well as the URL segment (/<slug>/login).
const SLUG_RE = /^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$/;
const USERNAME_RE = /^[a-z0-9._-]+$/;

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

    // 2. Service-role client: reads platform_admins (which has no RLS policy at
    //    all, so only this role can see it) and mints the auth account.
    const admin = createClient(url, serviceKey, {
      auth: { autoRefreshToken: false, persistSession: false },
    });

    // 3. The caller MUST be a platform admin. Never trust the client for this.
    const { data: platformRow } = await admin
      .from('platform_admins')
      .select('user_id')
      .eq('user_id', caller.id)
      .maybeSingle();
    if (!platformRow) return json({ ok: false, code: 'forbidden' }, 403);

    // 4. Validate input.
    const body = await req.json().catch(() => ({}));
    const slug = String(body.slug ?? '').trim().toLowerCase();
    const businessName = String(body.business_name ?? '').trim();
    const ownerName = String(body.owner_name ?? '').trim();
    const username = String(body.owner_username ?? '').trim().toLowerCase();
    const password = String(body.owner_password ?? '');
    const siteName = String(body.site_name ?? '').trim() || 'الفرع الرئيسي';
    const addressLine = body.address_line ? String(body.address_line).trim() : null;
    const phoneLine = body.phone_line ? String(body.phone_line).trim() : null;

    if (
      !SLUG_RE.test(slug) || !businessName || !ownerName ||
      !USERNAME_RE.test(username) || password.length < 6
    ) {
      return json({ ok: false, code: 'invalid_input' }, 400);
    }

    // The login email is derived from the slug, which is why the slug must be a
    // valid DNS label: username@<slug>.local. This is what lets two businesses
    // each have their own 'admin' or 'ahmed'.
    const email = `${username}@${slug}.local`;

    // 5. Reject a duplicate slug BEFORE creating an auth account, so the common
    //    mistake does not leave an account to clean up.
    const { data: existing } = await admin
      .from('organization')
      .select('id')
      .eq('slug', slug)
      .maybeSingle();
    if (existing) return json({ ok: false, code: 'slug_exists' }, 200);

    // 6. Create the owner's auth account, pre-confirmed so they can log in now.
    //    Supabase generates the UUID — it is never chosen by hand.
    const { data: created, error: createErr } = await admin.auth.admin.createUser({
      email,
      password,
      email_confirm: true,
    });
    if (createErr || !created?.user) {
      const code = createErr?.code === 'email_exists' ? 'email_exists' : 'create_failed';
      return json({ ok: false, code, detail: createErr?.message }, 200);
    }

    // 7. Everything else in ONE transaction. If it fails, remove the auth user
    //    so no orphan account survives — safe here because the business has no
    //    ledger rows yet, so there is nothing to compensate.
    const { data: orgId, error: provErr } = await admin.rpc('provision_organization', {
      p_slug: slug,
      p_business_name: businessName,
      p_owner_user_id: created.user.id,
      p_owner_name: ownerName,
      p_owner_username: username,
      p_site_name: siteName,
      p_address_line: addressLine,
      p_phone_line: phoneLine,
    });

    if (provErr) {
      await admin.auth.admin.deleteUser(created.user.id);
      const code = provErr.code === '23505' ? 'slug_or_username_exists' : 'provision_failed';
      return json({ ok: false, code, detail: provErr.message }, 200);
    }

    return json({
      ok: true,
      org_id: orgId,
      slug,
      owner_user_id: created.user.id,
      login_url: `/${slug}/login`,
    }, 200);
  } catch (e) {
    return json({ ok: false, code: 'server_error', detail: String(e) }, 500);
  }
});
