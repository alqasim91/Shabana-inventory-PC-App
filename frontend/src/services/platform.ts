import { supabase } from '@/lib/supabase';
import type { PlatformOrg } from '@/types/database';

/**
 * Operator-only service: creating and listing CLIENT BUSINESSES (منشآت).
 *
 * Distinct from services/admin.ts, which manages users *inside* one business.
 * Everything here is gated on `platform_admins` server-side — a tenant admin,
 * however senior, can never mint another business.
 *
 * Note what this deliberately cannot do: read any client's contacts, orders, or
 * money. current_org() governs every row and a platform admin belongs to no
 * organization, so the tenant tables return nothing for them. Only the metadata
 * exposed by platform_list_orgs() is visible.
 */

/** True if the signed-in user may create businesses. */
export async function isPlatformAdmin(): Promise<boolean> {
  const { data, error } = await supabase.rpc('is_platform_admin');
  if (error) return false;
  return Boolean(data);
}

export async function listPlatformOrgs(): Promise<PlatformOrg[]> {
  const { data, error } = await supabase.rpc('platform_list_orgs');
  if (error) throw error;
  return (data ?? []) as PlatformOrg[];
}

export interface CreateOrgInput {
  slug: string;
  business_name: string;
  owner_name: string;
  owner_username: string;
  owner_password: string;
  site_name?: string;
  address_line?: string | null;
  phone_line?: string | null;
}

export type CreateOrgResult =
  | { ok: true; org_id: string; slug: string; login_url: string }
  | { ok: false; code: string; detail?: string };

/**
 * Provision a whole new client: organization + first admin + default site +
 * document counters. Runs through the create-organization Edge Function because
 * minting an auth account needs the service role, which the browser never holds.
 */
export async function createOrganization(input: CreateOrgInput): Promise<CreateOrgResult> {
  const body = {
    slug: input.slug.trim().toLowerCase(),
    business_name: input.business_name.trim(),
    owner_name: input.owner_name.trim(),
    owner_username: input.owner_username.trim().toLowerCase(),
    owner_password: input.owner_password,
    site_name: input.site_name?.trim() || undefined,
    address_line: input.address_line?.trim() || null,
    phone_line: input.phone_line?.trim() || null,
  };

  const { data, error } = await supabase.functions.invoke('create-organization', { body });
  if (error) {
    // Non-2xx (unauthorized / forbidden / server error) — try to read the body.
    const ctx = (error as { context?: Response }).context;
    if (ctx && typeof ctx.json === 'function') {
      try {
        return (await ctx.json()) as CreateOrgResult;
      } catch {
        /* fall through */
      }
    }
    return { ok: false, code: 'server_error', detail: error.message };
  }
  return data as CreateOrgResult;
}
