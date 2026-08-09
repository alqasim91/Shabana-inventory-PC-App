import { supabase } from '@/lib/supabase';
import type { AppRole, PermissionKey, PermissionKeyRow, UserAccess, UUID } from '@/types/database';

/**
 * Reading and writing what a user may do (migrations 0031 / 0032).
 *
 * Everything here is advisory: the same rules are enforced again by RLS, by the
 * lifecycle triggers, and inside every SECURITY DEFINER RPC. Saving through
 * `admin_set_user_access` rather than writing the tables directly is what keeps
 * the lockout rules — last admin, self-demotion, no granting what you don't
 * hold — in one place instead of scattered across the client.
 */

/** The catalog the editor renders itself from, already grouped for display. */
export async function listPermissionKeys(): Promise<PermissionKeyRow[]> {
  const { data, error } = await supabase
    .from('permission_keys')
    .select('*')
    .order('area')
    .order('sort');
  if (error) throw error;
  return (data ?? []) as PermissionKeyRow[];
}

/** Everything the editor needs about one user, in one round trip each. */
export async function getUserAccess(userId: UUID, allSites: boolean): Promise<UserAccess> {
  const [{ data: perms, error: permErr }, { data: sites, error: siteErr }] = await Promise.all([
    supabase.from('user_permissions').select('perm').eq('user_id', userId),
    supabase.from('user_sites').select('site_id').eq('user_id', userId),
  ]);
  if (permErr) throw permErr;
  if (siteErr) throw siteErr;

  return {
    permissions: (perms ?? []).map((r) => (r as { perm: PermissionKey }).perm),
    allSites,
    siteIds: (sites ?? []).map((r) => (r as { site_id: UUID }).site_id),
  };
}

export interface SaveUserAccessInput {
  userId: UUID;
  fullName: string;
  role: AppRole;
  active: boolean;
  permissions: PermissionKey[];
  allSites: boolean;
  siteIds: UUID[];
}

/**
 * One transaction for the whole picture — name, role, active, permissions and
 * branches. A half-saved user (new role, old permissions) is not a state this
 * can produce, which is why the editor never writes the tables itself.
 */
export async function saveUserAccess(input: SaveUserAccessInput): Promise<void> {
  const { error } = await supabase.rpc('admin_set_user_access', {
    p_user_id: input.userId,
    p_full_name: input.fullName.trim(),
    p_role: input.role,
    p_active: input.active,
    p_perms: input.permissions,
    p_all_sites: input.allSites,
    p_site_ids: input.allSites ? [] : input.siteIds,
  });
  if (error) throw error;
}

/**
 * The permissions a role preset starts from — read from the database rather
 * than duplicated here, so the presets the editor offers and the ones a newly
 * created account is seeded with can never drift apart.
 */
export async function presetPermissions(role: AppRole): Promise<PermissionKey[]> {
  const { data, error } = await supabase.rpc('role_preset_permissions', { p_role: role });
  if (error) throw error;
  // A set-returning function comes back as an array of scalars.
  return (data ?? []) as PermissionKey[];
}
