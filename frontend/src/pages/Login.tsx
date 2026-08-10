import { useState, type FormEvent } from 'react';
import { Navigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from '@/contexts/AuthContext';
import { getOrgPublicName } from '@/services/organization';
import { needsSetup } from '@/services/setup';
import { APP_NAME, AUTH } from '@/labels';
import {
  isValidOrgSlug,
  rememberOrgSlug,
  rememberedOrgSlug,
  toLoginEmail,
} from '@/lib/username';

/**
 * Two doors into the same building:
 *   /:orgSlug/login  — a client's own link; the business is already known
 *   /login           — generic; ask for the business code (or reuse the one
 *                      this device used last, so shop tablets never see it)
 *
 * The slug in the URL is NOT a security boundary — it only decides which email
 * the username maps to. Authorization comes from the session's profile.org_id
 * and the RLS policies built on it, so visiting another business's login URL
 * grants nothing: the derived email simply doesn't exist and the sign-in fails.
 */
export function Login() {
  const { session, loading, signIn } = useAuth();
  const { orgSlug: routeSlug } = useParams<{ orgSlug: string }>();

  // The business is considered "known" only when the slug arrived from the
  // client's own link or from this device's last successful login — never from
  // what is currently being typed. Looking up every keystroke would turn this
  // page into a directory you could walk by guessing codes; frozen on mount, it
  // only ever names a business whose code the visitor already had.
  const [knownSlug] = useState(() => (routeSlug ?? rememberedOrgSlug() ?? '').trim().toLowerCase());

  // When the URL carries the slug there is nothing to ask; otherwise prefill
  // from this device's last successful login.
  const [slug, setSlug] = useState(knownSlug);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const askForSlug = !routeSlug;

  // Cached for a day and persisted with the rest of the query cache, so a shop
  // tablet opening its own bookmark shows the right name instantly — and still
  // shows it with no connection at all.
  const { data: businessName, isLoading: nameLoading } = useQuery({
    queryKey: ['org-public-name', knownSlug],
    queryFn: () => getOrgPublicName(knownSlug),
    enabled: isValidOrgSlug(knownSlug),
    staleTime: 1000 * 60 * 60 * 24,
    retry: false, // a missing name is cosmetic; don't hammer the API for it
  });

  // PC EDITION: detect a fresh, unclaimed install (no organization yet).
  const { data: freshInstall } = useQuery({
    queryKey: ['pc-needs-setup'],
    queryFn: needsSetup,
    // Seconds, not a day. This value decides a REDIRECT, and it flips exactly
    // once in the life of an install - the moment setup completes. Holding a
    // stale `true` after that point sends the owner back to /setup, which
    // checks again, gets `false`, and returns here: a redirect loop with no way
    // out. FirstRunSetup writes the new value into this cache directly on
    // success, so the short window here is only a backstop for anyone arriving
    // by some other route.
    staleTime: 5_000,
    retry: false,
  });

  if (!loading && session) return <Navigate to="/dashboard" replace />;

  // PC EDITION: on a fresh install no organization exists yet — forward to the
  // one-time setup screen instead of showing a login nobody can pass. Once an
  // org exists this returns false and the login form shows normally.
  if (!loading && !session && freshInstall) return <Navigate to="/setup" replace />;

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    const orgSlug = slug.trim().toLowerCase();
    if (orgSlug && !isValidOrgSlug(orgSlug)) {
      setError(AUTH.invalidOrgCode);
      return;
    }

    setSubmitting(true);
    // Map the bare username to the email Supabase authenticates against
    // (a full email typed by an admin passes through unchanged).
    const { error: signInError } = await signIn(toLoginEmail(username, orgSlug), password);
    setSubmitting(false);

    if (signInError) {
      // Deliberately the same message for a wrong password and an unknown
      // business — otherwise this page would report which slugs exist.
      setError(AUTH.invalidCredentials);
      return;
    }
    if (orgSlug) rememberOrgSlug(orgSlug);
  }

  return (
    <div dir="rtl" className="flex min-h-screen items-center justify-center bg-sand px-4">
      <div className="w-full max-w-[380px] rounded-card border border-border bg-white p-8">
        <div className="mb-7 flex flex-col items-center text-center">
          <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-[12px] bg-amber text-xl font-bold text-teal-dark">
            ش
          </div>
          <h1 className="m-0 text-xl font-bold">{APP_NAME}</h1>
          {/* The product on top, the business underneath. While the name is in
              flight we hold the line's height with a non-breaking space rather
              than printing the generic subtitle and swapping it a moment later —
              a header that rewrites itself reads like a glitch. */}
          <p className="m-0 mt-1 text-[13px] text-muted">
            {nameLoading ? '\u00A0' : (businessName ?? AUTH.loginSubtitle)}
          </p>
        </div>

        <form onSubmit={handleSubmit} className="flex flex-col gap-3.5">
          {askForSlug && (
            <div>
              <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
                {AUTH.orgCode}
              </label>
              <input
                type="text"
                required
                dir="ltr"
                autoCapitalize="none"
                autoCorrect="off"
                value={slug}
                onChange={(e) => setSlug(e.target.value)}
                className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
                placeholder={AUTH.orgCodePlaceholder}
              />
              <p className="m-0 mt-1 text-[11.5px] text-faint">{AUTH.orgCodeHint}</p>
            </div>
          )}

          <div>
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{AUTH.username}</label>
            <input
              type="text"
              required
              dir="ltr"
              autoCapitalize="none"
              autoCorrect="off"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
              placeholder={AUTH.usernamePlaceholder}
            />
          </div>
          <div>
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{AUTH.password}</label>
            <input
              type="password"
              required
              dir="ltr"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-[9px] border border-border px-3 py-2.5 text-[13.5px] text-right"
            />
          </div>

          {error && <p className="m-0 text-[13px] font-semibold text-red-600">{error}</p>}

          <button
            type="submit"
            disabled={submitting}
            className="mt-1 rounded-[10px] bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {submitting ? AUTH.submitting : AUTH.submit}
          </button>
        </form>
      </div>
    </div>
  );
}
