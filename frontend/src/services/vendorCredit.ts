import { supabase } from '@/lib/supabase';
import type { CreditTender, ISODate, PaymentMethod, UUID, VendorCredit } from '@/types/database';

/** A real tender (never the internal 'credit' method). */
export type TenderMethod = Exclude<PaymentMethod, 'credit'>;

export interface VendorCreditMovementRow extends VendorCredit {
  siteName: string | null;
  actorName: string | null;
}

/** Current advance we hold at a vendor (money we've prepaid them). */
export async function getVendorCreditBalance(contactId: UUID): Promise<number> {
  const { data, error } = await supabase.rpc('get_vendor_credit', { p_contact_id: contactId });
  if (error) throw error;
  return Number(data ?? 0);
}

/** Full dated advance ledger for a vendor, newest first, with site + actor names. */
export async function listVendorCreditMovements(contactId: UUID): Promise<VendorCreditMovementRow[]> {
  const { data, error } = await supabase
    .from('vendor_credits')
    .select('*, site:sites(name_ar)')
    .eq('contact_id', contactId)
    .order('occurred_on', { ascending: false })
    .order('created_at', { ascending: false });
  if (error) throw error;

  const rows = (data ?? []) as unknown as (VendorCredit & { site: { name_ar: string } | null })[];

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

export interface OverpayPOInput {
  poId: UUID;
  amount: number; // full amount tendered (> order remaining)
  method: TenderMethod;
  siteId: UUID | null; // drawer for cash
  methodRef?: UUID | null; // stored account for instapay/bank
  paidAt: ISODate;
  note?: string | null;
}

/** Settle a purchase order to its total and bank the excess as a vendor advance. */
export async function overpayPurchaseOrder(
  input: OverpayPOInput,
): Promise<{ paid: number; creditAdded: number }> {
  const { data, error } = await supabase.rpc('po_overpay', {
    p_po_id: input.poId,
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

export interface ApplyVendorCreditInput {
  poId: UUID;
  amount: number;
  paidAt: ISODate;
  note?: string | null;
}

/** Apply a stored advance to a purchase order as a non-cash payment (manager+). */
export async function applyVendorCredit(input: ApplyVendorCreditInput): Promise<void> {
  const { error } = await supabase.rpc('vendor_credit_apply', {
    p_po_id: input.poId,
    p_amount: input.amount,
    p_paid_at: input.paidAt,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}

export interface VendorCreditCashInput {
  contactId: UUID;
  amount: number;
  method: CreditTender;
  siteId: UUID | null; // required when method === 'cash'
  note?: string | null;
}

/** Prepay a vendor directly — a down-payment before any PO (staff+). Cash leaves the drawer. */
export async function depositVendorCredit(input: VendorCreditCashInput): Promise<void> {
  const { error } = await supabase.rpc('vendor_credit_deposit', {
    p_contact_id: input.contactId,
    p_amount: input.amount,
    p_method: input.method,
    p_site_id: input.siteId,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}

/** Refund an advance from the vendor — cash back into a drawer, or instapay (manager+). */
export async function refundVendorCredit(input: VendorCreditCashInput): Promise<void> {
  const { error } = await supabase.rpc('vendor_credit_refund', {
    p_contact_id: input.contactId,
    p_amount: input.amount,
    p_method: input.method,
    p_site_id: input.siteId,
    p_note: input.note ?? null,
  });
  if (error) throw error;
}
