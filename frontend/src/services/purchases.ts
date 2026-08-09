import { supabase } from '@/lib/supabase';
import type {
  ISODate,
  ISODateTime,
  PoConversion,
  PoLine,
  PoLineConversion,
  PoStatus,
  PoType,
  PurchaseOrder,
  UnitType,
  UUID,
} from '@/types/database';

export interface PurchaseOrderListRow {
  id: UUID;
  orderSeq: number;
  orderCode: string | null;
  createdAt: ISODateTime;
  vendorName: string;
  poType: PoType;
  productName: string | null;
  lineCount: number; // itemized: number of lines
  itemizedOrdered: number; // itemized: total units ordered across lines
  itemizedConverted: number; // itemized: total units received into stock
  orderDate: ISODate;
  totalKg: number | null;
  convertedKg: number;
  totalAmount: number;
  paid: number;
  status: PoStatus;
}

export interface ConversionRow extends PoConversion {
  siteName: string;
  itemName: string;
  createdByName: string | null;
}

export interface PoLineConversionRow extends PoLineConversion {
  siteName: string;
}

export interface PoLineRow extends PoLine {
  itemName: string;
  unitType: UnitType;
  converted: number; // Σ conversion qty
  remaining: number; // qty − converted
  conversions: PoLineConversionRow[];
}

export interface PurchaseOrderDetail extends PurchaseOrder {
  vendorName: string;
  convertedKg: number;
  remainingKg: number;
  conversions: ConversionRow[];
  lines: PoLineRow[]; // itemized lines (empty for general orders)
}

export interface PurchaseOrderFormInput {
  vendor_id: UUID;
  product_name: string | null;
  notes: string | null;
  order_date: ISODate;
  total_kg: number;
  price_per_kg: number;
  total_amount: number;
}

/**
 * A line on an itemized PO: either an existing item, or a new one to create.
 * Site-agnostic — stock lands later via conversions. line_total is carried so
 * the user can drive it directly (which back-computes unit_price).
 */
export interface ItemizedLineInput {
  item_id: UUID | null;
  new_name?: string | null;
  new_unit?: UnitType | null;
  qty: number;
  unit_price: number;
  line_total: number;
}

export interface ItemizedPurchaseOrderInput {
  vendor_id: UUID;
  order_date: ISODate;
  notes: string | null;
  lines: ItemizedLineInput[];
}

/** Receive a quantity of an itemized line's item into a branch (splittable). */
export interface LineConversionInput {
  po_line_id: UUID;
  site_id: UUID;
  qty: number;
  conversion_date?: ISODate;
}

export interface ConversionInput {
  po_id: UUID;
  site_id: UUID;
  item_id: UUID;
  kg_consumed: number;
  output_qty: number;
  output_unit: UnitType;
  conversion_date?: ISODate;
}

async function paymentsByPo(poIds: UUID[]): Promise<Map<UUID, number>> {
  const map = new Map<UUID, number>();
  if (poIds.length === 0) return map;
  const { data, error } = await supabase
    .from('payments')
    .select('parent_id, amount')
    .eq('parent_type', 'po')
    .in('parent_id', poIds);
  if (error) throw error;
  for (const p of data ?? []) {
    map.set(p.parent_id, (map.get(p.parent_id) ?? 0) + Number(p.amount));
  }
  return map;
}

export async function listPurchaseOrders(): Promise<PurchaseOrderListRow[]> {
  const { data, error } = await supabase
    .from('purchase_orders')
    .select(
      '*, vendor:contacts(name), po_conversions(kg_consumed), po_lines(qty, po_line_conversions(qty))',
    )
    .order('order_date', { ascending: false })
    .order('created_at', { ascending: false });
  if (error) throw error;

  const rows = (data ?? []) as unknown as (PurchaseOrder & {
    vendor: { name: string } | null;
    po_conversions: { kg_consumed: number }[];
    po_lines: { qty: number; po_line_conversions: { qty: number }[] }[];
  })[];

  const paidMap = await paymentsByPo(rows.map((r) => r.id));

  return rows.map((r) => {
    const lines = r.po_lines ?? [];
    return {
      id: r.id,
      orderSeq: r.order_seq,
      orderCode: r.order_code,
      createdAt: r.created_at,
      vendorName: r.vendor?.name ?? '—',
      poType: r.po_type,
      productName: r.product_name,
      lineCount: lines.length,
      itemizedOrdered: lines.reduce((s, l) => s + Number(l.qty), 0),
      itemizedConverted: lines.reduce(
        (s, l) => s + (l.po_line_conversions ?? []).reduce((cs, c) => cs + Number(c.qty), 0),
        0,
      ),
      orderDate: r.order_date,
      totalKg: r.total_kg == null ? null : Number(r.total_kg),
      convertedKg: r.po_conversions.reduce((sum, c) => sum + Number(c.kg_consumed), 0),
      totalAmount: Number(r.total_amount),
      paid: paidMap.get(r.id) ?? 0,
      status: r.status,
    };
  });
}

/** Map of user_id → full_name, used to label who created each conversion. */
async function profileNames(userIds: (UUID | null)[]): Promise<Map<UUID, string>> {
  const ids = [...new Set(userIds.filter((id): id is UUID => !!id))];
  const map = new Map<UUID, string>();
  if (ids.length === 0) return map;
  const { data, error } = await supabase.from('profiles').select('user_id, full_name').in('user_id', ids);
  if (error) throw error;
  for (const p of data ?? []) map.set(p.user_id, p.full_name);
  return map;
}

export async function getPurchaseOrder(id: UUID): Promise<PurchaseOrderDetail> {
  const { data, error } = await supabase
    .from('purchase_orders')
    .select(
      '*, vendor:contacts(name), po_conversions(*, site:sites(name_ar), item:items(name_ar)), ' +
        'po_lines(*, item:items(name_ar, unit_type), po_line_conversions(*, site:sites(name_ar)))',
    )
    .eq('id', id)
    .single();
  if (error) throw error;

  const row = data as unknown as PurchaseOrder & {
    vendor: { name: string } | null;
    po_conversions: (PoConversion & { site: { name_ar: string }; item: { name_ar: string } })[];
    po_lines: (PoLine & {
      item: { name_ar: string; unit_type: UnitType };
      po_line_conversions: (PoLineConversion & { site: { name_ar: string } })[];
    })[];
  };

  const names = await profileNames(row.po_conversions.map((c) => c.created_by));

  const conversions: ConversionRow[] = [...row.po_conversions]
    .sort((a, b) => a.created_at.localeCompare(b.created_at))
    .map((c) => ({
      ...c,
      siteName: c.site.name_ar,
      itemName: c.item.name_ar,
      createdByName: c.created_by ? names.get(c.created_by) ?? null : null,
    }));

  const lines: PoLineRow[] = [...(row.po_lines ?? [])]
    .sort((a, b) => a.created_at.localeCompare(b.created_at))
    .map((l) => {
      const lineConvs: PoLineConversionRow[] = [...(l.po_line_conversions ?? [])]
        .sort((a, b) => a.created_at.localeCompare(b.created_at))
        .map((c) => ({ ...c, siteName: c.site.name_ar }));
      const converted = lineConvs.reduce((s, c) => s + Number(c.qty), 0);
      return {
        ...l,
        itemName: l.item.name_ar,
        unitType: l.item.unit_type,
        converted,
        remaining: Number(l.qty) - converted,
        conversions: lineConvs,
      };
    });

  const convertedKg = conversions.reduce((sum, c) => sum + Number(c.kg_consumed), 0);

  return {
    ...row,
    vendorName: row.vendor?.name ?? '—',
    conversions,
    lines,
    convertedKg,
    remainingKg: row.total_kg == null ? 0 : Number(row.total_kg) - convertedKg,
  };
}

export async function createPurchaseOrder(input: PurchaseOrderFormInput): Promise<UUID> {
  const { data, error } = await supabase.from('purchase_orders').insert(input).select('id').single();
  if (error) throw error;
  return (data as { id: UUID }).id;
}

/**
 * Create an itemized PO in one atomic RPC: the order, any brand-new inventory
 * items named on its lines, and the lines themselves — site-agnostic, no stock
 * yet (stock lands later via line conversions). All in one transaction.
 */
export async function createItemizedPurchaseOrder(input: ItemizedPurchaseOrderInput): Promise<UUID> {
  const { data, error } = await supabase.rpc('create_itemized_po', {
    p_vendor: input.vendor_id,
    p_date: input.order_date,
    p_notes: input.notes,
    p_lines: input.lines,
  });
  if (error) throw error;
  return data as UUID;
}

/**
 * Admin-only edit of an itemized PO: rewrites header + lines. Rewriting the
 * lines cascades away their conversions (returning that stock, guarded); the
 * admin re-converts afterwards. Rejected (whole edit rolled back) if a branch
 * no longer holds stock a removed line's conversion had added.
 */
export async function updateItemizedPurchaseOrder(id: UUID, input: ItemizedPurchaseOrderInput): Promise<void> {
  const { error } = await supabase.rpc('update_itemized_po', {
    p_id: id,
    p_vendor: input.vendor_id,
    p_date: input.order_date,
    p_notes: input.notes,
    p_lines: input.lines,
  });
  if (error) throw error;
}

/** Receive part (or all) of an itemized line's quantity into a branch's stock. */
export async function addLineConversion(input: LineConversionInput): Promise<void> {
  const { error } = await supabase.from('po_line_conversions').insert(input);
  if (error) throw error;
}

/** Reverse an itemized line conversion (admin) — returns the stock, guarded. */
export async function deleteLineConversion(id: UUID): Promise<void> {
  const { error } = await supabase.from('po_line_conversions').delete().eq('id', id);
  if (error) throw error;
}

/**
 * Admin-only edit of a purchase order at any stage. The DB RPC rejects the edit
 * (Arabic message) if the new total_kg is below what has already been converted
 * to stock, or the new total is below what has already been paid.
 */
export async function adminUpdatePurchaseOrder(id: UUID, input: PurchaseOrderFormInput): Promise<void> {
  const { error } = await supabase.rpc('admin_update_purchase_order', {
    p_id: id,
    p_vendor: input.vendor_id,
    p_product: input.product_name,
    p_notes: input.notes,
    p_date: input.order_date,
    p_total_kg: input.total_kg,
    p_ppk: input.price_per_kg,
    p_total: input.total_amount,
  });
  if (error) throw error;
}

export async function addConversion(input: ConversionInput): Promise<void> {
  const { error } = await supabase.from('po_conversions').insert(input);
  if (error) throw error;
}

export async function deleteConversion(id: UUID): Promise<void> {
  const { error } = await supabase.from('po_conversions').delete().eq('id', id);
  if (error) throw error;
}
