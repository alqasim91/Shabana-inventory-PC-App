import { supabase } from '@/lib/supabase';
import type { Dashboard, ISODate, UUID } from '@/types/database';

/**
 * One day's figures for one site (or كل الفروع when siteId is null), all
 * computed from the append-only ledgers up to end-of-day — so opening balance
 * of day D equals closing of D−1 (rule 10). Thin wrapper over the get_dashboard RPC.
 */
export async function getDashboard(siteId: UUID | null, date: ISODate): Promise<Dashboard> {
  const { data, error } = await supabase.rpc('get_dashboard', {
    p_site_id: siteId,
    p_date: date,
  });
  if (error) throw error;
  return data as Dashboard;
}
