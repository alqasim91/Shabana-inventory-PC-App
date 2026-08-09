// Staff log in with a bare username; Supabase Auth needs an email, so the app
// maps username → username@<org-slug>.local transparently. Both login and
// user-creation use these helpers so the mapping lives in exactly one place.
//
// The org slug is the DOMAIN half, which is what makes usernames unique PER
// BUSINESS rather than globally: شبانة's `ahmed` is ahmed@shabana.local and
// Acme's is ahmed@acme.local — two different accounts. Without this, the first
// client to register `admin` would block every other client from ever having
// one.
//
// شبانة's slug is deliberately 'shabana', so its existing users' emails come
// out byte-identical to what they already are — no auth.users rewrite, no
// password resets, nothing any user has to be told about.

/** Fallback slug — keeps behaviour identical for the original business. */
export const DEFAULT_ORG_SLUG = 'shabana';

/** Remembered between visits so shop tablets skip the business-code field. */
const SLUG_STORAGE_KEY = 'shabana:orgSlug';

// Usernames become an email local-part, so keep them to a safe ASCII set.
const USERNAME_RE = /^[a-z0-9._-]+$/;
// Slugs become a DNS label — must match org_slug_format in migration 0024.
const SLUG_RE = /^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$/;

/** True if the input is already a full email (contains '@'). */
export function looksLikeEmail(input: string): boolean {
  return input.includes('@');
}

/** Validate a bare username (not an email). */
export function isValidUsername(username: string): boolean {
  return USERNAME_RE.test(username);
}

/** Validate an organization slug (same rule the database enforces). */
export function isValidOrgSlug(slug: string): boolean {
  return SLUG_RE.test(slug);
}

/**
 * Turn a login input into the email Supabase authenticates against.
 * - already an email (`admin@shabana.local`, a real gmail) → used as-is
 * - a bare username (`ahmed`) + slug `acme` → `ahmed@acme.local`
 *
 * Usernames and slugs are lowercased/trimmed so `Ahmed ` and `ahmed` are the
 * same login.
 */
export function toLoginEmail(input: string, orgSlug: string = DEFAULT_ORG_SLUG): string {
  const trimmed = input.trim();
  if (looksLikeEmail(trimmed)) return trimmed.toLowerCase();
  const slug = (orgSlug || DEFAULT_ORG_SLUG).trim().toLowerCase();
  return `${trimmed.toLowerCase()}@${slug}.local`;
}

/** Last business this device signed into, if any. */
export function rememberedOrgSlug(): string | null {
  try {
    return localStorage.getItem(SLUG_STORAGE_KEY);
  } catch {
    return null; // private mode / storage disabled — just ask for the code
  }
}

export function rememberOrgSlug(slug: string): void {
  try {
    localStorage.setItem(SLUG_STORAGE_KEY, slug.trim().toLowerCase());
  } catch {
    /* non-fatal: the user retypes the business code next time */
  }
}
