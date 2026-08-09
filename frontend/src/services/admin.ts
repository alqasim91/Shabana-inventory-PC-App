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

// Account CREATION.
//
// PC EDITION: there is no Deno edge runtime on a shop PC and nowhere safe to
// keep a service_role key, so the cloud app's admin-create-user Edge Function
// is replaced by the pc_create_user() SQL function (migration 0033), a
// SECURITY DEFINER routine that writes auth.users directly. It enforces the
// SAME authority server-side: the caller must be an admin of an org, and the
// login email is derived from the caller's own org slug inside the function —
// never from anything the client sends. Same {ok,code} contract as the Edge
// Function, so the calling UI and its error translations are unchanged.
export interface CreateUserInput {
  username: string;
  password: string;
  full_name: string;
  role: AppRole;
}

export type CreateUserResult = { ok: true } | { ok: false; code: string; detail?: string };

export async function createUser(input: CreateUserInput): Promise<CreateUserResult> {
  const username = input.username.trim().toLowerCase();
  // Only the bare username is sent; pc_create_user derives the email from the
  // caller's org slug. Passing the org from the client would let an admin mint
  // an account in another business's namespace — but on a single-tenant PC
  // there is only one org anyway; this preserves the cloud invariant regardless.
  const { data, error } = await supabase.rpc('pc_create_user', {
    p_username: username,
    p_password: input.password,
    p_full_name: input.full_name,
    p_role: input.role,
  });
  if (error) {
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
