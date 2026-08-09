import { supabase } from '@/lib/supabase';
import { formatPoNo, formatSoNo } from '@/lib/orderNo';
import type {
  Contact,
  ContactPaymentMethod,
  ContactPhone,
  ContactType,
  ISODate,
  ISODateTime,
  PaymentMethod,
  UUID,
} from '@/types/database';

export interface ContactListRow {
  id: UUID;
  name: string;
  type: ContactType;
  phone: string | null;
  balance: number;
}

export interface ContactWithDetails extends Contact {
  phones: ContactPhone[];
  paymentMethods: ContactPaymentMethod[];
}

export interface ContactPhoneInput {
  phone: string;
  is_primary: boolean;
}

export interface ContactPaymentMethodInput {
  method: PaymentMethod;
  instapay_number?: string | null;
  bank_name?: string | null;
  account_number?: string | null;
}

export interface ContactFormInput {
  type: ContactType;
  name: string;
  address?: string | null;
  notes?: string | null;
  phones: ContactPhoneInput[];
  paymentMethods: ContactPaymentMethodInput[];
}

export async function getContactBalance(contactId: UUID): Promise<number> {
  const { data, error } = await supabase.rpc('get_contact_balance', { p_contact_id: contactId });
  if (error) throw error;
  return Number(data);
}

export async function listContacts(): Promise<ContactListRow[]> {
  const { data, error } = await supabase
    .from('contacts')
    .select('id, name, type, contact_phones(phone, is_primary)')
    .order('name');
  if (error) throw error;

  const rows = (data ?? []) as unknown as Array<{
    id: UUID;
    name: string;
    type: ContactType;
    contact_phones: { phone: string; is_primary: boolean }[];
  }>;

  const balances = await Promise.all(rows.map((r) => getContactBalance(r.id)));

  return rows.map((r, i) => {
    const primary = r.contact_phones.find((p) => p.is_primary) ?? r.contact_phones[0];
    return {
      id: r.id,
      name: r.name,
      type: r.type,
      phone: primary?.phone ?? null,
      balance: balances[i],
    };
  });
}

export async function getContact(id: UUID): Promise<ContactWithDetails> {
  const { data, error } = await supabase
    .from('contacts')
    .select('*, contact_phones(*), contact_payment_methods(*)')
    .eq('id', id)
    .single();
  if (error) throw error;

  const row = data as unknown as Contact & {
    contact_phones: ContactPhone[];
    contact_payment_methods: ContactPaymentMethod[];
  };

  return {
    ...row,
    phones: row.contact_phones,
    paymentMethods: row.contact_payment_methods,
  };
}

async function writePhonesAndMethods(
  contactId: UUID,
  phones: ContactPhoneInput[],
  paymentMethods: ContactPaymentMethodInput[],
) {
  if (phones.length > 0) {
    const { error } = await supabase
      .from('contact_phones')
      .insert(phones.map((p) => ({ ...p, contact_id: contactId })));
    if (error) throw error;
  }
  if (paymentMethods.length > 0) {
    const { error } = await supabase
      .from('contact_payment_methods')
      .insert(paymentMethods.map((m) => ({ ...m, contact_id: contactId })));
    if (error) throw error;
  }
}

export async function createContact(input: ContactFormInput): Promise<UUID> {
  const { data, error } = await supabase
    .from('contacts')
    .insert({
      type: input.type,
      name: input.name,
      address: input.address ?? null,
      notes: input.notes ?? null,
    })
    .select('id')
    .single();
  if (error) throw error;

  const contactId = (data as { id: UUID }).id;
  await writePhonesAndMethods(contactId, input.phones, input.paymentMethods);
  return contactId;
}

export async function updateContact(id: UUID, input: ContactFormInput): Promise<void> {
  const { error } = await supabase
    .from('contacts')
    .update({
      type: input.type,
      name: input.name,
      address: input.address ?? null,
      notes: input.notes ?? null,
    })
    .eq('id', id);
  if (error) throw error;

  // Phones/payment methods have no ledger semantics (unlike stock/cash) — a
  // wholesale replace on edit is simplest and matches how the form submits them.
  const [{ error: delPhonesErr }, { error: delMethodsErr }] = await Promise.all([
    supabase.from('contact_phones').delete().eq('contact_id', id),
    supabase.from('contact_payment_methods').delete().eq('contact_id', id),
  ]);
  if (delPhonesErr) throw delPhonesErr;
  if (delMethodsErr) throw delMethodsErr;

  await writePhonesAndMethods(id, input.phones, input.paymentMethods);
}

// ---- Ledger (كشف حساب) -------------------------------------------------------

export interface LedgerRow {
  date: ISODate | ISODateTime;
  desc: string;
  debit: number;
  credit: number;
  balance: number;
  /** In-app route to the SO/PO this movement belongs to, if any. */
  link?: string;
}

export interface ContactLedger {
  rows: LedgerRow[];
  finalBalance: number;
}

interface LedgerEntry {
  date: ISODate | ISODateTime;
  desc: string;
  debit: number;
  credit: number;
  link?: string;
}

/**
 * Chronological debit/credit ledger for a contact, merging their sales orders
 * (as client), purchase orders (as vendor), and payments against either.
 * Convention matches get_contact_balance: debit increases what they owe us
 * (عليه), credit increases what we owe them (له); running balance = Σ(debit-credit).
 * Always computed from full history so the running balance stays correct,
 * then sliced to [from, to] for display (rule: date range narrows the view,
 * not the math).
 */
export async function getContactLedger(
  contactId: UUID,
  range?: { from?: ISODate; to?: ISODate },
): Promise<ContactLedger> {
  const [soRes, poRes] = await Promise.all([
    supabase
      .from('sales_orders')
      .select('id, order_seq, order_date, total_amount')
      .eq('client_id', contactId)
      .neq('status', 'draft'),
    supabase
      .from('purchase_orders')
      .select('id, order_seq, order_date, total_amount')
      .eq('vendor_id', contactId),
  ]);
  if (soRes.error) throw soRes.error;
  if (poRes.error) throw poRes.error;

  const sos = soRes.data ?? [];
  const pos = poRes.data ?? [];
  const soIds = new Set(sos.map((s) => s.id));
  const parentIds = [...sos.map((s) => s.id), ...pos.map((p) => p.id)];

  let payments: { parent_id: UUID; amount: number; paid_at: ISODateTime; method: string }[] = [];
  if (parentIds.length > 0) {
    const { data, error } = await supabase
      .from('payments')
      .select('parent_id, amount, paid_at, method')
      .in('parent_id', parentIds);
    if (error) throw error;
    payments = data ?? [];
  }

  // Credit wallet movements (client only). 'applied' rows and their mirror
  // 'credit'-method payments are internal settlement — dropping both pairs
  // keeps the running balance exact while avoiding double lines.
  const { data: creditRows, error: cErr } = await supabase
    .from('client_credits')
    .select('amount_delta, source_type, source_id, occurred_on')
    .eq('contact_id', contactId)
    .neq('source_type', 'applied');
  if (cErr) throw cErr;

  const CREDIT_DESC: Record<string, string> = {
    overpayment: 'دفعة زائدة (رصيد)',
    deposit: 'إيداع رصيد',
    refund: 'استرداد رصيد',
    adjustment: 'تسوية رصيد',
  };

  const entries: LedgerEntry[] = [];

  for (const so of sos) {
    entries.push({
      date: so.order_date,
      desc: formatSoNo(so.order_seq),
      debit: so.total_amount,
      credit: 0,
      link: `/sales/${so.id}`,
    });
  }
  for (const po of pos) {
    entries.push({
      date: po.order_date,
      desc: formatPoNo(po.order_seq),
      debit: 0,
      credit: po.total_amount,
      link: `/purchases/${po.id}`,
    });
  }
  for (const p of payments) {
    // 'credit'-method payments are the mirror of dropped 'applied' credit rows.
    if (p.method === 'credit') continue;
    const isSoPayment = soIds.has(p.parent_id);
    entries.push({
      date: p.paid_at,
      desc: isSoPayment ? 'دفعة تحصيل' : 'دفعة سداد',
      debit: isSoPayment ? 0 : p.amount,
      credit: isSoPayment ? p.amount : 0,
      link: `${isSoPayment ? '/sales' : '/purchases'}/${p.parent_id}`,
    });
  }
  for (const cc of creditRows ?? []) {
    const added = cc.amount_delta > 0; // + = we owe them (credit); - = credit consumed (debit)
    entries.push({
      date: cc.occurred_on,
      desc: CREDIT_DESC[cc.source_type] ?? 'حركة رصيد',
      debit: added ? 0 : Math.abs(cc.amount_delta),
      credit: added ? cc.amount_delta : 0,
      // Overpayment credit originates on a sales order (source_id = SO id);
      // manual wallet ops (deposit/refund/adjustment) have no order to link to.
      link: cc.source_type === 'overpayment' && cc.source_id ? `/sales/${cc.source_id}` : undefined,
    });
  }

  entries.sort((a, b) => a.date.localeCompare(b.date));

  let running = 0;
  const allRows: LedgerRow[] = entries.map((e) => {
    running += e.debit - e.credit;
    return { ...e, balance: running };
  });

  const rows = allRows.filter((r) => {
    if (range?.from && r.date < range.from) return false;
    if (range?.to && r.date > `${range.to}T23:59:59`) return false;
    return true;
  });

  return { rows, finalBalance: running };
}
