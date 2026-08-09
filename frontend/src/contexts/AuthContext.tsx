import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import type { Session } from '@supabase/supabase-js';
import { supabase } from '@/lib/supabase';
import type { PermissionKey, Profile, UUID } from '@/types/database';

interface AuthContextValue {
  session: Session | null;
  profile: Profile | null;
  /** What this user may do. Empty until the profile loads. */
  permissions: Set<PermissionKey>;
  /** null = every branch; otherwise the ids this user is limited to. */
  allowedSiteIds: UUID[] | null;
  /** The one question the UI asks. Admins are always true — see has_perm(). */
  can: (perm: PermissionKey) => boolean;
  loading: boolean;
  signIn: (email: string, password: string) => Promise<{ error: string | null }>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

async function fetchProfile(userId: string): Promise<Profile | null> {
  const { data, error } = await supabase.from('profiles').select('*').eq('user_id', userId).single();
  if (error) return null;
  return data as Profile;
}

/**
 * The caller's own permission rows. RLS lets every user read their own, so this
 * needs no special privilege.
 *
 * A failure here returns an EMPTY set, never a permissive one: if we cannot
 * establish what someone may do, the answer is nothing. The buttons vanish,
 * which is recoverable and obvious — whereas guessing generously would draw
 * actions the database is about to reject anyway.
 */
async function fetchPermissions(userId: string): Promise<Set<PermissionKey>> {
  const { data, error } = await supabase.from('user_permissions').select('perm').eq('user_id', userId);
  if (error) return new Set();
  return new Set((data ?? []).map((r) => (r as { perm: PermissionKey }).perm));
}

async function fetchAllowedSites(userId: string, allSites: boolean): Promise<UUID[] | null> {
  if (allSites) return null;
  const { data, error } = await supabase.from('user_sites').select('site_id').eq('user_id', userId);
  if (error) return [];
  return (data ?? []).map((r) => (r as { site_id: UUID }).site_id);
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [profile, setProfile] = useState<Profile | null>(null);
  const [permissions, setPermissions] = useState<Set<PermissionKey>>(new Set());
  const [allowedSiteIds, setAllowedSiteIds] = useState<UUID[] | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let active = true;

    // The profile decides whether we even need to ask about branches, so these
    // are sequential rather than parallel.
    async function load(userId: string): Promise<void> {
      const p = await fetchProfile(userId);
      if (!active) return;
      setProfile(p);
      if (!p) {
        setPermissions(new Set());
        setAllowedSiteIds(null);
        return;
      }
      const [perms, sites] = await Promise.all([
        fetchPermissions(userId),
        fetchAllowedSites(userId, p.all_sites ?? true),
      ]);
      if (!active) return;
      setPermissions(perms);
      setAllowedSiteIds(sites);
    }

    supabase.auth.getSession().then(async ({ data }) => {
      if (!active) return;
      setSession(data.session);
      if (data.session) await load(data.session.user.id);
      if (active) setLoading(false);
    });

    const { data: sub } = supabase.auth.onAuthStateChange(async (_event, newSession) => {
      setSession(newSession);
      if (newSession) {
        await load(newSession.user.id);
      } else {
        setProfile(null);
        setPermissions(new Set());
        setAllowedSiteIds(null);
      }
    });

    return () => {
      active = false;
      sub.subscription.unsubscribe();
    };
  }, []);

  async function signIn(email: string, password: string) {
    const { error } = await supabase.auth.signInWithPassword({ email, password });
    return { error: error ? error.message : null };
  }

  async function signOut() {
    await supabase.auth.signOut();
    // This device may be shared at the counter — don't leave the previous
    // user's cached orders/balances sitting in localStorage for the next login.
    localStorage.removeItem('shabana:queryCache');
  }

  // Admins short-circuit for the same reason has_perm() does in the database:
  // they are the recovery path, and a business that can lock its own owner out
  // of the permissions screen has no way back.
  const can = (perm: PermissionKey): boolean =>
    profile?.role === 'admin' || permissions.has(perm);

  return (
    <AuthContext.Provider
      value={{ session, profile, permissions, allowedSiteIds, can, loading, signIn, signOut }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
