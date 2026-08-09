import { supabase } from '@/lib/supabase';
import type { ContactPaymentMethod, Payment, PaymentMethod, PaymentParent, UUID } from '@/types/database';

export interface PaymentInput {
  parent_type: PaymentParent;
  parent_id: UUID;
  amount: number;
  method: PaymentMethod;
  /** Required for cash payments; the drawer the cash hits. Null lets the DB default it (SO → its own site). */
  site_id?: UUID | null;
  /** For instapay/bank_transfer: which stored account on the contact's card was used. */
  contact_payment_method_id?: UUID | null;
  paid_at?: string;
  note?: string | null;
}

/** A payment plus the stored account it referenced (for display). */
export interface PaymentRow extends Payment {
  methodDetail: Pick<ContactPaymentMethod, 'method' | 'instapay_number' | 'bank_name' | 'account_number'> | null;
}

export async function listPayments(parentType: PaymentParent, parentId: UUID): Promise<PaymentRow[]> {
  const { data, error } = await supabase
    .from('payments')
    .select('*, methodDetail:contact_payment_methods(method, instapay_number, bank_name, account_number)')
    .eq('parent_type', parentType)
    .eq('parent_id', parentId)
    .order('paid_at', { ascending: false });
  if (error) throw error;
  return (data ?? []) as unknown as PaymentRow[];
}

export async function addPayment(input: PaymentInput): Promise<void> {
  const { error } = await supabase.from('payments').insert({
    parent_type: input.parent_type,
    parent_id: input.parent_id,
    amount: input.amount,
    method: input.method,
    site_id: input.site_id ?? null,
    contact_payment_method_id: input.contact_payment_method_id ?? null,
    paid_at: input.paid_at,
    note: input.note ?? null,
  });
  if (error) throw error;
}

export async function deletePayment(id: UUID): Promise<void> {
  const { error } = await supabase.from('payments').delete().eq('id', id);
  if (error) throw error;
}

export function paymentsTotal(payments: Payment[]): number {
  return payments.reduce((sum, p) => sum + Number(p.amount), 0);
}

/** A contact's stored payment methods — used to pick the instapay/bank account a payment hits. */
export async function listContactPaymentMethods(contactId: UUID): Promise<ContactPaymentMethod[]> {
  const { data, error } = await supabase
    .from('contact_payment_methods')
    .select('*')
    .eq('contact_id', contactId);
  if (error) throw error;
  return (data ?? []) as ContactPaymentMethod[];
}
