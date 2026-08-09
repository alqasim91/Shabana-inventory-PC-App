import { supabase } from '@/lib/supabase';
import type {
  ISODate,
  ISODateTime,
  SalesOrder,
  SalesOrderLine,
  SoDiscountType,
  SoStatus,
  UnitType,
  UUID,
} from '@/types/database';

export interface SalesOrderListRow {
  id: UUID;
  orderSeq: number;
  invoiceNumber: string | null;
  createdAt: ISODateTime;
  clientName: string;
  siteName: string;
  orderDate: ISODate;
  totalAmount: number;
  paid: number;
  status: SoStatus;
}

export interface SalesOrderLineRow extends SalesOrderLine {
  itemName: string;
  unitType: UnitType;
}

export interface SalesOrderDetail extends SalesOrder {
  clientName: string;
  siteName: string;
  createdByName: string | null;
  lines: SalesOrderLineRow[];
}

export interface SalesOrderLineInput {
  item_id: UUID;
  qty: number;
  unit_price: number;
}

export interface SalesOrderFormInput {
  site_id: UUID;
  client_id: UUID;
  order_date: ISODate;
  lines: SalesOrderLineInput[];
  /** خصم على الأمر — 'none' when there isn't one (migration 0030). */
  discount_type: SoDiscountType;
  /** ج.م when discount_type is 'amount', a percentage when it's 'percent'. */
  discount_value: number;
}

/**
 * The discount is a HEADER field, but it can only be judged against the lines
 * it discounts — the DB rejects a discount bigger than the subtotal. So every
 * write path here saves the lines FIRST and the discount LAST, against the
 * order's final subtotal rather than whatever it happened to be before.
 */
function discountPatch(input: SalesOrderFormInput) {
  return {
    discount_type: input.discount_type,
    discount_value: input.discount_type === 'none' ? 0 : input.discount_value,
  };
}

async function paymentsBySo(soIds: UUID[]): Promise<Map<UUID, number>> {
  const map = new Map<UUID, number>();
  if (soIds.length === 0) return map;
  const { data, error } = await supabase
    .from('payments')
    .select('parent_id, amount')
    .eq('parent_type', 'so')
    .in('parent_id', soIds);
  if (error) throw error;
  for (const p of data ?? []) {
    map.set(p.parent_id, (map.get(p.parent_id) ?? 0) + Number(p.amount));
  }
  return map;
}

/** List sales orders, optionally scoped to one site (null = all sites). */
export async function listSalesOrders(siteId: UUID | null): Promise<SalesOrderListRow[]> {
  let query = supabase
    .from('sales_orders')
    .select('*, client:contacts(name), site:sites(name_ar)')
    .order('order_date', { ascending: false })
    .order('created_at', { ascending: false });
  if (siteId) query = query.eq('site_id', siteId);

  const { data, error } = await query;
  if (error) throw error;

  const rows = (data ?? []) as unknown as (SalesOrder & {
    client: { name: string } | null;
    site: { name_ar: string } | null;
  })[];

  const paidMap = await paymentsBySo(rows.map((r) => r.id));

  return rows.map((r) => ({
    id: r.id,
    orderSeq: r.order_seq,
    invoiceNumber: r.invoice_number,
    createdAt: r.created_at,
    clientName: r.client?.name ?? '—',
    siteName: r.site?.name_ar ?? '—',
    orderDate: r.order_date,
    totalAmount: Number(r.total_amount),
    paid: paidMap.get(r.id) ?? 0,
    status: r.status,
  }));
}

async function creatorName(userId: UUID | null): Promise<string | null> {
  if (!userId) return null;
  const { data, error } = await supabase.from('profiles').select('full_name').eq('user_id', userId).maybeSingle();
  if (error) throw error;
  return (data as { full_name: string } | null)?.full_name ?? null;
}

export async function getSalesOrder(id: UUID): Promise<SalesOrderDetail> {
  const { data, error } = await supabase
    .from('sales_orders')
    .select('*, client:contacts(name), site:sites(name_ar), sales_order_lines(*, item:items(name_ar, unit_type))')
    .eq('id', id)
    .single();
  if (error) throw error;

  const row = data as unknown as SalesOrder & {
    client: { name: string } | null;
    site: { name_ar: string } | null;
    sales_order_lines: (SalesOrderLine & { item: { name_ar: string; unit_type: UnitType } })[];
  };

  const lines: SalesOrderLineRow[] = [...row.sales_order_lines]
    .sort((a, b) => a.id.localeCompare(b.id))
    .map((l) => ({
      ...l,
      itemName: l.item.name_ar,
      unitType: l.item.unit_type,
    }));

  return {
    ...row,
    clientName: row.client?.name ?? '—',
    siteName: row.site?.name_ar ?? '—',
    createdByName: await creatorName(row.created_by),
    lines,
  };
}

/** Create a draft SO plus its lines. Totals are recomputed by DB triggers. */
export async function createSalesOrder(input: SalesOrderFormInput): Promise<UUID> {
  const { data, error } = await supabase
    .from('sales_orders')
    .insert({ site_id: input.site_id, client_id: input.client_id, order_date: input.order_date })
    .select('id')
    .single();
  if (error) throw error;
  const soId = (data as { id: UUID }).id;

  if (input.lines.length > 0) {
    const { error: linesError } = await supabase
      .from('sales_order_lines')
      .insert(input.lines.map((l) => ({ so_id: soId, item_id: l.item_id, qty: l.qty, unit_price: l.unit_price })));
    if (linesError) throw linesError;
  }

  if (input.discount_type !== 'none') {
    const { error: discError } = await supabase
      .from('sales_orders')
      .update(discountPatch(input))
      .eq('id', soId);
    if (discError) throw discError;
  }
  return soId;
}

/**
 * Replace a draft's header + lines. Lines are editable only while draft
 * (enforced by trg_so_line_guard); we delete-then-insert so removed rows go too.
 */
export async function updateSalesOrderDraft(id: UUID, input: SalesOrderFormInput): Promise<void> {
  // Clear the discount before touching the lines. Emptying the lines drops the
  // subtotal to zero, and a discount left standing above it is exactly what the
  // DB refuses — so the order passes through "no discount" on its way from the
  // old lines to the new ones, and the real discount goes back on at the end.
  const { error: clearError } = await supabase
    .from('sales_orders')
    .update({ discount_type: 'none', discount_value: 0 })
    .eq('id', id);
  if (clearError) throw clearError;

  const { error: delError } = await supabase.from('sales_order_lines').delete().eq('so_id', id);
  if (delError) throw delError;

  if (input.lines.length > 0) {
    const { error: insError } = await supabase
      .from('sales_order_lines')
      .insert(input.lines.map((l) => ({ so_id: id, item_id: l.item_id, qty: l.qty, unit_price: l.unit_price })));
    if (insError) throw insError;
  }

  const { error: headerError } = await supabase
    .from('sales_orders')
    .update({
      site_id: input.site_id,
      client_id: input.client_id,
      order_date: input.order_date,
      ...discountPatch(input),
    })
    .eq('id', id);
  if (headerError) throw headerError;
}

/**
 * Admin-only full edit at ANY status (invoiced/placed/closed included). The
 * DB RPC safely returns any deducted stock, rewrites header + lines, revalidates
 * payments against the new total, and re-deducts stock — all in one transaction.
 * Rejected (whole edit rolled back) if the branch lacks stock to re-place or the
 * recorded payments would exceed the new total.
 */
export async function adminUpdateSalesOrder(id: UUID, input: SalesOrderFormInput): Promise<void> {
  const { error } = await supabase.rpc('admin_update_sales_order', {
    p_id: id,
    p_site: input.site_id,
    p_client: input.client_id,
    p_date: input.order_date,
    p_lines: input.lines,
    p_discount_type: input.discount_type,
    p_discount_value: input.discount_type === 'none' ? 0 : input.discount_value,
  });
  if (error) throw error;
}

/** Advance/rewind lifecycle by setting status; the DB triggers do the real work. */
async function setStatus(id: UUID, status: SoStatus): Promise<void> {
  const { error } = await supabase.from('sales_orders').update({ status }).eq('id', id);
  if (error) throw error;
}

/** draft → invoiced: assigns SO-YYYY-#### and locks the lines (DB trigger). */
export const invoiceSalesOrder = (id: UUID) => setStatus(id, 'invoiced');
/** invoiced → placed: deducts stock at the SO's site (rejected if insufficient). */
export const placeSalesOrder = (id: UUID) => setStatus(id, 'placed');
/** placed → invoiced (admin): writes compensating positive movements. */
export const cancelPlacement = (id: UUID) => setStatus(id, 'invoiced');

export async function deleteSalesOrder(id: UUID): Promise<void> {
  const { error } = await supabase.from('sales_orders').delete().eq('id', id);
  if (error) throw error;
}
