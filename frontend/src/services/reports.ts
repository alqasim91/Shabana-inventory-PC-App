import { supabase } from '@/lib/supabase';
import { getCashBalance } from '@/services/cash';
import { formatPoNo, formatSoNo } from '@/lib/orderNo';
import type { CashMovement, ISODate, PoStatus, SoStatus, UUID } from '@/types/database';

export function addDaysISO(iso: ISODate, days: number): ISODate {
  const d = new Date(`${iso}T00:00:00`);
  d.setDate(d.getDate() + days);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

async function paidByParent(parentType: 'so' | 'po', parentIds: UUID[]): Promise<Map<UUID, number>> {
  const map = new Map<UUID, number>();
  if (parentIds.length === 0) return map;
  const { data, error } = await supabase
    .from('payments')
    .select('parent_id, amount')
    .eq('parent_type', parentType)
    .in('parent_id', parentIds);
  if (error) throw error;
  for (const p of data ?? []) map.set(p.parent_id, (map.get(p.parent_id) ?? 0) + Number(p.amount));
  return map;
}

// ---- Sales report ----------------------------------------------------------

export interface SalesReportRow {
  id: UUID;
  doc: string;
  clientName: string;
  date: ISODate;
  total: number;
  paid: number;
  status: SoStatus;
}

/** Non-draft sales orders in [from, to], optionally scoped to one site. */
export async function salesReport(siteId: UUID | null, from: ISODate, to: ISODate): Promise<SalesReportRow[]> {
  let query = supabase
    .from('sales_orders')
    .select('id, order_seq, order_date, total_amount, status, client:contacts(name)')
    .neq('status', 'draft')
    .gte('order_date', from)
    .lte('order_date', to)
    .order('order_date', { ascending: false });
  if (siteId) query = query.eq('site_id', siteId);
  const { data, error } = await query;
  if (error) throw error;

  const rows = (data ?? []) as unknown as {
    id: UUID; order_seq: number; order_date: ISODate;
    total_amount: number; status: SoStatus; client: { name: string } | null;
  }[];
  const paid = await paidByParent('so', rows.map((r) => r.id));

  return rows.map((r) => ({
    id: r.id,
    doc: formatSoNo(r.order_seq),
    clientName: r.client?.name ?? '—',
    date: r.order_date,
    total: Number(r.total_amount),
    paid: paid.get(r.id) ?? 0,
    status: r.status,
  }));
}

// ---- Purchases report ------------------------------------------------------

export interface PurchasesReportRow {
  id: UUID;
  doc: string;
  vendorName: string;
  date: ISODate;
  total: number;
  paid: number;
  status: PoStatus;
}

/** Purchase orders in [from, to]. POs are site-agnostic — no site filter. */
export async function purchasesReport(from: ISODate, to: ISODate): Promise<PurchasesReportRow[]> {
  const { data, error } = await supabase
    .from('purchase_orders')
    .select('id, order_seq, order_date, total_amount, status, vendor:contacts(name)')
    .gte('order_date', from)
    .lte('order_date', to)
    .order('order_date', { ascending: false });
  if (error) throw error;

  const rows = (data ?? []) as unknown as {
    id: UUID; order_seq: number; order_date: ISODate;
    total_amount: number; status: PoStatus; vendor: { name: string } | null;
  }[];
  const paid = await paidByParent('po', rows.map((r) => r.id));

  return rows.map((r) => ({
    id: r.id,
    doc: formatPoNo(r.order_seq),
    vendorName: r.vendor?.name ?? '—',
    date: r.order_date,
    total: Number(r.total_amount),
    paid: paid.get(r.id) ?? 0,
    status: r.status,
  }));
}

// ---- Cash movements report -------------------------------------------------

export interface CashReportRow extends CashMovement {
  siteName: string;
  /** Running drawer balance after this row — only computed for a single site. */
  balanceAfter: number | null;
}

export interface CashReport {
  rows: CashReportRow[];
  opening: number;
  closing: number;
  inflow: number;
  outflow: number;
}

/**
 * Drawer movements in [from, to]. With a specific site the rows carry a
 * running balance (opening = closing of from−1, rule-10 style); with كل الفروع
 * the running balance is omitted (interleaved drawers have no single balance).
 */
export async function cashReport(siteId: UUID | null, from: ISODate, to: ISODate): Promise<CashReport> {
  let query = supabase
    .from('cash_movements')
    .select('*, sites(name_ar)')
    .gte('created_at', from)
    .lt('created_at', addDaysISO(to, 1))
    .order('created_at', { ascending: true });
  if (siteId) query = query.eq('site_id', siteId);
  const { data, error } = await query;
  if (error) throw error;

  const raw = (data ?? []) as unknown as (CashMovement & { sites: { name_ar: string } })[];
  const opening = await getCashBalance(siteId, addDaysISO(from, -1));

  let running = opening;
  let inflow = 0;
  let outflow = 0;
  const rows: CashReportRow[] = raw.map((m) => {
    const delta = Number(m.amount_delta);
    if (delta >= 0) inflow += delta;
    else outflow += -delta;
    running += delta;
    return { ...m, siteName: m.sites.name_ar, balanceAfter: siteId ? running : null };
  });

  return {
    rows: rows.reverse(), // newest first for display
    opening,
    closing: opening + inflow - outflow,
    inflow,
    outflow,
  };
}

// ---- Manual drawer movement (rule 5: إيداع / سحب / تسوية) -------------------

export type ManualCashKind = 'deposit' | 'withdraw' | 'adjust';

export interface ManualCashInput {
  site_id: UUID;
  kind: ManualCashKind;
  /** Entered positive for deposit/withdraw; may be signed for adjust. */
  amount: number;
  reason: string;
}

export async function createManualCashMovement(input: ManualCashInput): Promise<void> {
  const delta =
    input.kind === 'deposit' ? Math.abs(input.amount)
    : input.kind === 'withdraw' ? -Math.abs(input.amount)
    : input.amount;
  const { error } = await supabase.from('cash_movements').insert({
    site_id: input.site_id,
    amount_delta: delta,
    source_type: 'manual',
    reason: input.reason,
  });
  if (error) throw error;
}
