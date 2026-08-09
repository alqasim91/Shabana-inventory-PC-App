import { supabase } from '@/lib/supabase';
import type { Organization } from '@/types/database';

// Business identity for the sidebar / invoice / statement headers.
//
// This was a singleton table keyed on `id = true` until migration 0024 made it
// a real multi-tenant table keyed on a uuid. No filter is needed any more — and
// `.eq('id', true)` would now be a uuid syntax error, silently falling every
// header back to the hardcoded labels: the org_read policy is
// `id = current_org()`, so a signed-in user sees exactly one row — their own
// business — and never anyone else's.

export async function getOrganization(): Promise<Organization | null> {
  const { data, error } = await supabase.from('organization').select('*').maybeSingle();
  if (error) throw error;
  return data as Organization | null;
}

/**
 * The business's display name, fetched BEFORE anyone signs in.
 *
 * `getOrganization()` above cannot do this: its policy is `id = current_org()`,
 * and on the login page there is no session, so the table reads back empty.
 * `org_public_name` (migration 0029) is a SECURITY DEFINER function that
 * returns that one column and nothing else, for active businesses only.
 *
 * Returns null for an unknown or suspended slug — the caller falls back to the
 * generic subtitle rather than showing an error, because a login page is not
 * the place to explain that a business code was wrong.
 */
export async function getOrgPublicName(slug: string): Promise<string | null> {
  const { data, error } = await supabase.rpc('org_public_name', {
    p_slug: slug.trim().toLowerCase(),
  });
  if (error) throw error;
  return (data as string | null) || null;
}

export interface OrganizationUpdateInput {
  business_name: string;
  address_line: string | null;
  phone_line: string | null;
}

export async function updateOrganization(input: OrganizationUpdateInput): Promise<void> {
  // Read the caller's own id first so the update carries an explicit filter,
  // rather than relying on an unfiltered UPDATE. RLS confines it either way,
  // but an unfiltered update against a multi-tenant table is exactly the shape
  // of statement that turns dangerous the day a policy is loosened.
  //
  // No upsert any more: a missing row can no longer self-heal, because creating
  // an organization is a provisioning action (migration 0027) and deliberately
  // has no INSERT policy for tenants.
  const { data: org, error: readErr } = await supabase
    .from('organization')
    .select('id')
    .maybeSingle();
  if (readErr) throw readErr;
  if (!org) throw new Error('لا توجد منشأة مرتبطة بهذا الحساب');

  const { error } = await supabase
    .from('organization')
    .update(input)
    .eq('id', (org as { id: string }).id);
  if (error) throw error;
}
