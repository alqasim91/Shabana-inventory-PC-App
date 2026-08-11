import { useState, useEffect, type FormEvent } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { APP_NAME, SETUP } from '@/labels';
import { isValidUsername } from '@/lib/username';
import { useQueryClient } from '@tanstack/react-query';
import { needsSetup, firstRunBootstrap } from '@/services/setup';

/**
 * PC EDITION ONLY. The one-time screen shown on a fresh install, before any
 * account exists. Collects the business name, the first admin, and the first
 * فرع, then hands them to pc_first_run_bootstrap (migration 0033), which
 * creates them in a single transaction and mints the admin's auth account.
 *
 * The installer does deliberately NOT collect these — a Windows wizard that
 * has to stand up a database has no good "retry" story and risks a password
 * landing in an install log. This screen owns it instead, in the app's own
 * Arabic UI, with real validation and retry.
 *
 * Caddy additionally restricts /setup to loopback (see Caddyfile), so on a
 * tunnelled install a remote visitor cannot reach this form during the window
 * before the owner claims the machine. The RPC's one-shot guard is the true
 * protection; the Caddy rule is defence in depth.
 */
export function FirstRunSetup() {
  const navigate = useNavigate();

  // Freeze the "is this a fresh install?" answer on mount. null = still asking.
  const [needed, setNeeded] = useState<boolean | null>(null);
  useEffect(() => {
    let alive = true;
    void needsSetup()
      .then((n) => {
        if (alive) setNeeded(n);
      })
      .catch(() => {
        // Could not reach the API. Show the form rather than leaving the
        // submit button disabled forever with no explanation - the RPC behind
        // it is one-shot and guarded server-side, so offering it costs
        // nothing, and a real failure now surfaces as an error on submit.
        if (alive) setNeeded(true);
      });
    return () => {
      alive = false;
    };
  }, []);

  const [businessName, setBusinessName] = useState('');
  const [adminFullName, setAdminFullName] = useState('');
  const [adminUsername, setAdminUsername] = useState('');
  const [password, setPassword] = useState('');
  const [passwordConfirm, setPasswordConfirm] = useState('');
  const [siteName, setSiteName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const queryClient = useQueryClient();

  // Already provisioned → this screen has no purpose; send them to log in.
  if (needed === false) return <Navigate to="/shabana/login" replace />;

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (!isValidUsername(adminUsername.trim().toLowerCase())) {
      setError(SETUP.invalidUsername);
      return;
    }
    if (password.length < 6) {
      setError(SETUP.passwordTooShort);
      return;
    }
    if (password !== passwordConfirm) {
      setError(SETUP.passwordMismatch);
      return;
    }

    setSubmitting(true);
    const result = await firstRunBootstrap({
      businessName,
      adminFullName,
      adminUsername,
      adminPassword: password,
      siteName,
    });
    setSubmitting(false);

    if (result.ok) {
      // Tell the cache the install is claimed BEFORE navigating.
      //
      // Login.tsx answers "is this a fresh install?" from a react-query entry
      // with a 24-hour staleTime, and that entry was populated with `true` when
      // this very screen first loaded. Navigating without correcting it sends
      // the owner to a login page that reads the stale `true`, redirects back
      // to /setup, which re-checks, gets a fresh `false`, and bounces to login
      // again - an infinite redirect loop the moment setup succeeds, on every
      // machine. The bootstrap itself worked; the app just never let anyone
      // reach the login screen.
      queryClient.setQueryData(['pc-needs-setup'], false);
      navigate('/shabana/login', { replace: true });
      return;
    }
    setError(
      result.code === 'already_setup'
        ? SETUP.alreadySetup
        : result.code === 'invalid_input'
          ? SETUP.invalidInput
          : SETUP.genericError,
    );
  }

  return (
    <div dir="rtl" className="flex min-h-screen items-center justify-center bg-sand px-4 py-8">
      <div className="w-full max-w-[440px] rounded-card border border-border bg-white p-8">
        <div className="mb-6 flex flex-col items-center text-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-[12px] bg-amber text-xl font-bold text-teal-dark">
            ش
          </div>
          <h1 className="m-0 text-xl font-bold">{APP_NAME}</h1>
          <p className="m-0 mt-1 text-[13px] font-semibold text-teal">{SETUP.title}</p>
          <p className="m-0 mt-1 text-[12.5px] text-muted">{SETUP.subtitle}</p>
        </div>

        <form onSubmit={handleSubmit} className="flex flex-col gap-3.5">
          <Field label={SETUP.businessName} hint={SETUP.businessNameHint}>
            <input
              type="text"
              required
              value={businessName}
              onChange={(e) => setBusinessName(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              placeholder={SETUP.businessNamePlaceholder}
            />
          </Field>

          <Field label={SETUP.adminFullName}>
            <input
              type="text"
              required
              value={adminFullName}
              onChange={(e) => setAdminFullName(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              placeholder={SETUP.adminFullNamePlaceholder}
            />
          </Field>

          <Field label={SETUP.adminUsername} hint={SETUP.adminUsernameHint}>
            <input
              type="text"
              required
              dir="ltr"
              autoCapitalize="none"
              autoCorrect="off"
              value={adminUsername}
              onChange={(e) => setAdminUsername(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              placeholder="admin"
            />
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field label={SETUP.adminPassword}>
              <input
                type="password"
                required
                dir="ltr"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              />
            </Field>
            <Field label={SETUP.adminPasswordConfirm}>
              <input
                type="password"
                required
                dir="ltr"
                value={passwordConfirm}
                onChange={(e) => setPasswordConfirm(e.target.value)}
                className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              />
            </Field>
          </div>

          <Field label={SETUP.siteName}>
            <input
              type="text"
              value={siteName}
              onChange={(e) => setSiteName(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              placeholder={SETUP.siteNamePlaceholder}
            />
          </Field>

          {error && <p className="m-0 text-[13px] font-semibold text-red-600">{error}</p>}

          <button
            type="submit"
            disabled={submitting || needed === null}
            className="mt-1 rounded-[10px] bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {submitting ? SETUP.submitting : SETUP.submit}
          </button>
        </form>
      </div>
    </div>
  );
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{label}</label>
      {children}
      {hint && <p className="m-0 mt-1 text-[11.5px] text-faint">{hint}</p>}
    </div>
  );
}
