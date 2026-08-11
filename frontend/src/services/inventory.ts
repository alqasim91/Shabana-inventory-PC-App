import { supabase } from '@/lib/supabase';
import type { Item, ISODate, StockMovement, StockSource, UUID, UnitType } from '@/types/database';

export interface ItemFormInput {
  name_ar: string;
  unit_type: UnitType;
  low_stock_threshold: number;
  sale_price: number;
  active: boolean;
}

export async function listItems(): Promise<Item[]> {
  const { data, error } = await supabase.from('items').select('*').eq('active', true).order('name_ar');
  if (error) throw error;
  return data as Item[];
}

export async function getItem(id: UUID): Promise<Item> {
  const { data, error } = await supabase.from('items').select('*').eq('id', id).single();
  if (error) throw error;
  return data as Item;
}

export async function createItem(input: ItemFormInput): Promise<UUID> {
  const { data, error } = await supabase.from('items').insert(input).select('id').single();
  if (error) throw error;
  return (data as { id: UUID }).id;
}

export async function updateItem(id: UUID, input: ItemFormInput): Promise<void> {
  const { error } = await supabase.from('items').update(input).eq('id', id);
  if (error) throw error;
}

export async function getStock(siteId: UUID | null, itemId: UUID, asOf?: ISODate | null): Promise<number> {
  const { data, error } = await supabase.rpc('get_stock', {
    p_site_id: siteId,
    p_item_id: itemId,
    p_as_of: asOf ?? null,
  });
  if (error) throw error;
  return Number(data);
}

export interface InventoryRow {
  itemId: UUID;
  itemName: string;
  unitType: UnitType;
  siteId: UUID;
  siteName: string;
  qty: number;
  threshold: number;
  salePrice: number;
}

/**
 * Cross-product of the given items × sites, with live stock per pair.
 * Callers pass the exact (item, site) pairs to show — one row per site when
 * a single site is selected, one row per (item, site) combination for "all sites".
 */
export async function listInventoryRows(
  items: Item[],
  sites: { id: UUID; name_ar: string }[],
): Promise<InventoryRow[]> {
  const pairs = items.flatMap((item) => sites.map((site) => ({ item, site })));
  const quantities = await Promise.all(pairs.map((p) => getStock(p.site.id, p.item.id)));

  return pairs.map((p, i) => ({
    itemId: p.item.id,
    itemName: p.item.name_ar,
    unitType: p.item.unit_type,
    siteId: p.site.id,
    siteName: p.site.name_ar,
    qty: quantities[i],
    threshold: p.item.low_stock_threshold,
    salePrice: p.item.sale_price,
  }));
}

export interface MovementRow extends StockMovement {
  siteName: string;
}

export async function listMovements(itemId: UUID): Promise<MovementRow[]> {
  const { data, error } = await supabase
    .from('stock_movements')
    .select('*, sites(name_ar)')
    .eq('item_id', itemId)
    .order('created_at', { ascending: false });
  if (error) throw error;

  return (data as unknown as (StockMovement & { sites: { name_ar: string } })[]).map((row) => ({
    ...row,
    siteName: row.sites.name_ar,
  }));
}

export interface AdjustmentInput {
  site_id: UUID;
  item_id: UUID;
  qty_delta: number;
  note: string;
}

export async function createAdjustment(input: AdjustmentInput): Promise<void> {
  const { error } = await supabase.from('stock_movements').insert({
    site_id: input.site_id,
    item_id: input.item_id,
    qty_delta: input.qty_delta,
    source_type: 'adjustment' satisfies StockSource,
    note: input.note,
  });
  if (error) throw error;
}

export interface OpeningStockInput {
  site_id: UUID;
  item_id: UUID;
  /** The counted quantity on the shelf — NOT a delta. */
  qty: number;
  note?: string | null;
}

/**
 * Admin-only: state what an item's stock actually is at a فرع (migration 0035).
 *
 * The RPC works out the difference against the ledger and posts it as a single
 * 'opening' movement, so nothing is ever overwritten. Returns the delta it
 * wrote — 0 means the ledger already agreed with the count.
 */
export async function setOpeningStock(input: OpeningStockInput): Promise<number> {
  const { data, error } = await supabase.rpc('set_opening_stock', {
    p_site_id: input.site_id,
    p_item_id: input.item_id,
    p_qty: input.qty,
    p_note: input.note ?? null,
  });
  if (error) throw error;
  return Number(data);
}

export interface TransferInput {
  from_site: UUID;
  to_site: UUID;
  item_id: UUID;
  qty: number;
  note?: string | null;
}

export async function createTransfer(input: TransferInput): Promise<void> {
  const { error } = await supabase.from('stock_transfers').insert({
    from_site: input.from_site,
    to_site: input.to_site,
    item_id: input.item_id,
    qty: input.qty,
    note: input.note ?? null,
  });
  if (error) throw error;
}
