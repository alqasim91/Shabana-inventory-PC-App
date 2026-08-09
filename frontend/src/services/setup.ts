import { supabase } from '@/lib/supabase';

// PC EDITION ONLY. First-run provisioning, the local stand-in for the cloud
// create-organization Edge Function. Backed by SQL functions in migration
// 0033 (pc_needs_setup / pc_first_run_bootstrap), both callable by anon
// because on a fresh install nobody is logged in yet. pc_first_run_bootstrap
// is strictly one-shot server-side (it refuses the moment any organization
// exists), so exposing it to anon cannot create a second tenant or hijack an
// existing install.

/** True on a fresh, unclaimed install (no organization exists yet). */
export async function needsSetup(): Promise<boolean> {
  const { data, error } = await supabase.rpc('pc_needs_setup');
  if (error) {
    // Fail SAFE: if we can't tell, assume setup is NOT needed rather than
    // exposing the setup form on a machine that may already be provisioned.
    return false;
  }
  return data === true;
}

export interface FirstRunInput {
  businessName: string;
  adminFullName: string;
  adminUsername: string;
  adminPassword: string;
  siteName?: string;
}

export type FirstRunResult = { ok: true } | { ok: false; code: string };

export async function firstRunBootstrap(input: FirstRunInput): Promise<FirstRunResult> {
  const { data, error } = await supabase.rpc('pc_first_run_bootstrap', {
    p_business_name: input.businessName.trim(),
    p_admin_fullname: input.adminFullName.trim(),
    p_admin_username: input.adminUsername.trim().toLowerCase(),
    p_admin_password: input.adminPassword,
    p_site_name: input.siteName?.trim() || undefined,
  });
  if (error) return { ok: false, code: 'server_error' };
  return data as FirstRunResult;
}
