import { supabase } from '@/lib/supabase';
import type { ClientCredit, CreditTender, ISODate, PaymentMethod, UUID } from '@/types/database';

/** A real tender (never the internal 'credit' method). */
export type TenderMethod = Exclude<PaymentMethod, 'credit'>;

export interface CreditMovementRow extends ClientCredit {
  siteName: string | null;
  actorName: string | null;
}

/** Current stored credit balance for a contact (money we hold for them). */
export async function getCreditBalance(contactId: UUID): Promise<number> {
  const { data, error } = await supabase.rpc('get_client_credit', { p_contact_id: contactId });
  if (error) throw error;
  return Number(data ?? 0);
}

/** Full dated credit ledger for a contact, newest first, with site + actor names. */
export async function listCreditMovements(contactId: UUID): Promise<CreditMovementRow[]> {
  const { data, error } = await supabase
    .from('client_credits')
    .select('*, site:sites(name_ar)')
    .eq('contact_id', contactId)
    .order('occurred_on', { ascending: false })
    .order('created_at', { ascending: false });
  if (error) throw error;

  const rows = (data ?? []) as unknown as (ClientCredit & { site: { name_ar: string } | null })[];

  const actorIds = [...new Set(rows.map((r) => r.created_by).filter((id): id is UUID => !!id))];
  const names = new Map<UUID, string>();
  if (actorIds.length > 0) {
    const { data: profs, error: pErr } = await supabase
      .from('profiles')
      .select('user_id, full_name')
      .in('user_id', actorIds);
    if (pErr) throw pErr;
    for (const p of profs ?? []) names.set(p.user_id, p.full_name);
  }

  return rows.map((r) => ({
    ...r,
    siteName: r.site?.name_ar ?? null,
    actorName: r.created_by ? names.get(r.created_by) ?? null : null,
  }));
}

export interface OverpayInput {
  soId: UUID;
  amount: number; // full amount tendered (> order remaining)
  method: TenderMethod;
  siteId: UUID | null;
  methodRef?: UUID | null; // stored account for instapay/bank
  paidAt: ISODate;
  note?: string | null;
}

/** Settle a sales order to its total and bank the excess as client credit. */
export async function overpaySalesOrder(input: OverpayInput): Promise<{ paid: number; creditAdded: number }> {
  const { data, error } = await supabase.rpc('so_overpay', {
    p_so_id: input.soId,
    p_amount: input.amount,
    p_method: input.method,
    p_site_id: input.siteId,
    p_paid_at: input.paidAt,
    p_note: input.note ?? null,
    p_method_ref: input.methodRef ?? null,
  });
  if (error) throw error;
  const r = (data ?? {}) as { paid?: number; credit_added?: number };
  return { paid: Number(r.paid ?? 0), creditAdded: Number(r.credit_added ?? 0) };
}

export interface ApplyCreditInput {
  soId: UUID;
  amount: number;
  paidAt: ISODate;
  note?: string | null;
}

/** Apply stored credit to a sales order as a non-cash payment (manager+). */
export async function applyCredit(input: ApplyCreditInput): Promise<void> {
  const { error } = await supabase.rpc('credit_apply', {
    p_so_id: input.soId,
    p_amount: input.amount,
    p_paid_at: input.paidAt,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}

export interface CreditCashInput {
  contactId: UUID;
  amount: number;
  method: CreditTender;
  siteId: UUID | null; // required when method === 'cash'
  note?: string | null;
}

/** Add credit directly — a client prepay/deposit (staff+). */
export async function depositCredit(input: CreditCashInput): Promise<void> {
  const { error } = await supabase.rpc('credit_deposit', {
    p_contact_id: input.contactId,
    p_amount: input.amount,
    p_method: input.method,
    p_site_id: input.siteId,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}

/** Refund stored credit to the client — cash out of a drawer, or instapay (manager+). */
export async function refundCredit(input: CreditCashInput): Promise<void> {
  const { error } = await supabase.rpc('credit_refund', {
    p_contact_id: input.contactId,
    p_amount: input.amount,
    p_method: input.method,
    p_site_id: input.siteId,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}
