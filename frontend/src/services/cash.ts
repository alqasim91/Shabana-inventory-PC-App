import { supabase } from '@/lib/supabase';
import type { ISODate, UUID } from '@/types/database';

/** Drawer balance from the ledger. siteId NULL = all drawers; asOf NULL = now. */
export async function getCashBalance(siteId: UUID | null, asOf?: ISODate | null): Promise<number> {
  const { data, error } = await supabase.rpc('get_cash_balance', {
    p_site_id: siteId,
    p_as_of: asOf ?? null,
  });
  if (error) throw error;
  return Number(data);
}
