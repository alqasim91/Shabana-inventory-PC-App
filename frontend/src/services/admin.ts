import { supabase } from '@/lib/supabase';
import type { Profile, Site, AppRole, UUID } from '@/types/database';

// ---- Users (profiles) ------------------------------------------------------
// Account CREATION stays in the Supabase Auth dashboard (the client key cannot
// mint users, and signUp would replace the admin's own session); this service
// manages the profile that hangs off each account: name, role, active.

export async function listProfiles(): Promise<Profile[]> {
  const { data, error } = await supabase.from('profiles').select('*').order('full_name');
  if (error) throw error;
  return data as Profile[];
}

export interface ProfileUpdateInput {
  full_name: string;
  role: AppRole;
  active: boolean;
}

export async function updateProfile(userId: UUID, input: ProfileUpdateInput): Promise<void> {
  const { error } = await supabase.from('profiles').update(input).eq('user_id', userId);
  if (error) throw error;
}

// Account CREATION goes through the admin-create-user Edge Function (service
// role), which verifies the caller is an admin, creates the auth.users account
// with an initial password, and inserts the matching profile. Returns a code
// on business failures (e.g. 'email_exists') for the UI to translate.
export interface CreateUserInput {
  username: string;
  password: string;
  full_name: string;
  role: AppRole;
}

export type CreateUserResult = { ok: true } | { ok: false; code: string; detail?: string };

export async function createUser(input: CreateUserInput): Promise<CreateUserResult> {
  const username = input.username.trim().toLowerCase();
  // Only the bare username is sent. The Edge Function derives the login email
  // from the CALLER's own organization slug — if the client supplied it, an
  // admin could mint an account inside another business's namespace.
  const body = {
    username,
    password: input.password,
    full_name: input.full_name,
    role: input.role,
  };
  const { data, error } = await supabase.functions.invoke('admin-create-user', { body });
  if (error) {
    // Non-2xx (unauthorized / forbidden / server error) — try to read the body.
    const ctx = (error as { context?: Response }).context;
    if (ctx && typeof ctx.json === 'function') {
      try {
        return (await ctx.json()) as CreateUserResult;
      } catch {
        /* fall through */
      }
    }
    return { ok: false, code: 'server_error', detail: error.message };
  }
  return data as CreateUserResult;
}

// ---- Sites -----------------------------------------------------------------
// Unlike SiteContext's fetchSites (active only, for the switcher), the admin
// screen lists every site including deactivated ones.

export async function listAllSites(): Promise<Site[]> {
  const { data, error } = await supabase.from('sites').select('*').order('name_ar');
  if (error) throw error;
  return data as Site[];
}

export interface SiteFormInput {
  name_ar: string;
  active: boolean;
}

export async function createSite(input: SiteFormInput): Promise<UUID> {
  const { data, error } = await supabase.from('sites').insert(input).select('id').single();
  if (error) throw error;
  return (data as { id: UUID }).id;
}

export async function updateSite(id: UUID, input: SiteFormInput): Promise<void> {
  const { error } = await supabase.from('sites').update(input).eq('id', id);
  if (error) throw error;
}
