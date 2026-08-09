// ============================================================================
// database.ts — TypeScript types mirroring the Supabase schema (Phase 0)
// ----------------------------------------------------------------------------
// Hand-maintained to match supabase/migrations/*.sql. Once the project is
// linked, regenerate with:
//   supabase gen types typescript --local > src/types/database.ts
// and this file becomes the generated output. Until then, these types let the
// service layer (/src/services) be fully typed.
// ============================================================================

// ---- Enums -----------------------------------------------------------------
export type AppRole = 'admin' | 'manager' | 'staff';
export type ContactType = 'vendor' | 'client' | 'both';
export type PaymentMethod = 'cash' | 'instapay' | 'bank_transfer' | 'credit';
/** Tenders a client can actually hand over for a credit deposit/refund. */
export type CreditTender = 'cash' | 'instapay';
export type CreditSource = 'overpayment' | 'deposit' | 'applied' | 'refund' | 'adjustment';
export type UnitType = 'kg' | 'unit';
export type PoType = 'general' | 'itemized';
/** Which kind of order an attachment is filed against (migration 0022). */
export type OrderDocType = 'purchase' | 'sale';
export type PoStatus = 'open' | 'fully_converted' | 'closed';
export type SoStatus = 'draft' | 'invoiced' | 'placed' | 'closed';

// How a sales-order discount was entered (migration 0030). 'amount' is ج.م off
// the order; 'percent' is a share of the lines total, resolved to ج.م on write.
export type SoDiscountType = 'none' | 'amount' | 'percent';

/**
 * Everything a user can be permitted to do (migration 0031). These strings are
 * the primary keys of `permission_keys` — the database is the source of truth,
 * this union is here so a typo in a PermGate is a compile error rather than a
 * silently-hidden button.
 *
 * Roles still exist as the presets these are seeded from, and admins bypass all
 * of them, which is what keeps a business from locking itself out.
 */
export type PermissionKey =
  | 'sales.view' | 'sales.draft' | 'sales.discount' | 'sales.invoice' | 'sales.place'
  | 'sales.cancel_placement' | 'sales.edit_locked' | 'sales.delete'
  | 'purchases.view' | 'purchases.create' | 'purchases.edit' | 'purchases.convert'
  | 'purchases.delete'
  | 'inventory.view' | 'inventory.items' | 'inventory.adjust' | 'inventory.transfer'
  | 'inventory.item_delete'
  | 'payments.record' | 'payments.credit' | 'payments.delete'
  | 'cash.view' | 'cash.manual'
  | 'contacts.view' | 'contacts.manage' | 'contacts.delete'
  | 'attachments.manage'
  | 'reports.view' | 'audit.view'
  | 'users.manage' | 'sites.manage' | 'settings.manage';

/** A row of the catalog the permission editor renders itself from. */
export interface PermissionKeyRow {
  key: PermissionKey;
  area: string;
  sort: number;
  note: string | null;
}

/** One user's complete access picture, as the editor loads and saves it. */
export interface UserAccess {
  permissions: PermissionKey[];
  allSites: boolean;
  siteIds: UUID[];
}
export type PaymentParent = 'po' | 'so';
export type StockSource = 'po_conversion' | 'sale' | 'adjustment' | 'transfer';
export type CashSource = 'payment' | 'manual';

// UUID / date aliases for readability.
export type UUID = string;
export type ISODate = string;      // 'YYYY-MM-DD'
export type ISODateTime = string;  // timestamptz

// ---- Row types -------------------------------------------------------------
export interface Site {
  id: UUID;
  name_ar: string;
  active: boolean;
  created_at: ISODateTime;
}

export interface Profile {
  user_id: UUID;
  /** Which business this user belongs to. Resolved server-side by current_org()
   *  and the basis of every RLS policy — never sent by the client on writes. */
  org_id: UUID;
  full_name: string;
  username: string | null;
  role: AppRole;
  active: boolean;
  /** false = this user works only in the فروع listed for them (migration 0031). */
  all_sites: boolean;
  created_at: ISODateTime;
}

export type AuditAction = 'insert' | 'update' | 'delete';

// One row per audited mutation (append-only, written by the audit_row trigger).
// `data` is the full row snapshot at the time of the change.
export interface AuditLog {
  id: UUID;
  actor: UUID | null;
  action: AuditAction;
  entity: string;
  entity_id: string | null;
  data: Record<string, unknown>;
  created_at: ISODateTime;
}

// Singleton (one row, id = true): business identity printed on invoices /
// statements and shown in the sidebar. Editable by admins in الإعدادات.
/**
 * A client business (منشأة). Was a singleton settings row (`id boolean`) until
 * migration 0024 turned it into a real multi-tenant table.
 *
 * `slug` is the business's handle: it is the URL segment (/shabana/login) AND
 * the domain half of every login email (ahmed@shabana.local), which is what
 * lets two businesses each have their own `ahmed` or `admin`.
 */
export interface Organization {
  id: UUID;
  slug: string;
  business_name: string;
  address_line: string | null;
  phone_line: string | null;
  active: boolean;
  created_at: ISODateTime;
  updated_at: ISODateTime;
}

/** Row shape returned by platform_list_orgs() — metadata only, no financials. */
export interface PlatformOrg {
  id: UUID;
  slug: string;
  business_name: string;
  active: boolean;
  created_at: ISODateTime;
  user_count: number;
  site_count: number;
}

export interface Contact {
  id: UUID;
  type: ContactType;
  name: string;
  address: string | null;
  notes: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface ContactPhone {
  id: UUID;
  contact_id: UUID;
  phone: string;
  is_primary: boolean;
}

export interface ContactPaymentMethod {
  id: UUID;
  contact_id: UUID;
  method: PaymentMethod;
  instapay_number: string | null;
  bank_name: string | null;
  account_number: string | null;
}

export interface Item {
  id: UUID;
  name_ar: string;
  unit_type: UnitType;
  low_stock_threshold: number;
  sale_price: number;
  active: boolean;
  created_at: ISODateTime;
}

export interface PurchaseOrder {
  id: UUID;
  order_seq: number; // persistent running number (migration 0017) → "أمر شراء ١"
  order_code: string | null;
  vendor_id: UUID;
  po_type: PoType; // 'general' = weight PO, 'itemized' = line list (migration 0019)
  product_name: string | null;
  notes: string | null;
  order_date: ISODate;
  total_kg: number | null; // null for itemized orders
  price_per_kg: number | null; // null for itemized orders
  total_amount: number;
  status: PoStatus;
  created_by: UUID | null;
  created_at: ISODateTime;
}

// Itemized-PO line (0019; site-agnostic since 0020): an ordered item + qty +
// price. Stock lands later via po_line_conversions, splittable across branches.
export interface PoLine {
  id: UUID;
  po_id: UUID;
  item_id: UUID;
  site_id: UUID | null; // legacy/unused since 0020 (itemized lines are site-agnostic)
  qty: number;
  unit_price: number;
  line_total: number;
  created_by: UUID | null;
  created_at: ISODateTime;
}

// A receipt of some quantity of an itemized line's item into a branch (0020).
export interface PoLineConversion {
  id: UUID;
  po_line_id: UUID;
  site_id: UUID;
  qty: number;
  conversion_date: ISODate;
  created_by: UUID | null;
  created_at: ISODateTime;
}

/** A scan/photo of the vendor's paper order, filed against a PO (migration 0022). */
export interface OrderAttachment {
  id: UUID;
  order_type: OrderDocType;
  order_id: UUID;
  /** Key inside the private `order-docs` bucket — never a public URL. */
  storage_path: string;
  file_name: string;
  mime_type: string;
  byte_size: number;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface PoConversion {
  id: UUID;
  po_id: UUID;
  site_id: UUID;
  item_id: UUID;
  kg_consumed: number;
  output_qty: number;
  output_unit: UnitType;
  conversion_date: ISODate;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface SalesOrder {
  id: UUID;
  order_seq: number; // persistent running number (migration 0017) → "أمر بيع ١"
  site_id: UUID;
  client_id: UUID;
  status: SoStatus;
  order_date: ISODate;
  subtotal: number; // sum of the lines, before any discount (migration 0030)
  discount_type: SoDiscountType;
  discount_value: number; // as entered: ج.م when 'amount', % when 'percent'
  discount_amount: number; // derived money-off, stored so the invoice can't drift
  total_amount: number; // what the client owes = subtotal − discount_amount
  invoice_number: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface SalesOrderLine {
  id: UUID;
  so_id: UUID;
  item_id: UUID;
  qty: number;
  unit_price: number;
  line_total: number; // generated: round(qty * unit_price, 2)
}

export interface Payment {
  id: UUID;
  parent_type: PaymentParent;
  parent_id: UUID;
  amount: number;
  method: PaymentMethod;
  site_id: UUID | null;
  /** For instapay/bank_transfer: which stored account on the contact's card was used. */
  contact_payment_method_id: UUID | null;
  paid_at: ISODateTime;
  note: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface ClientCredit {
  id: UUID;
  contact_id: UUID;
  amount_delta: number; // signed: + added, - consumed
  source_type: CreditSource;
  source_id: UUID | null;
  method: CreditTender | null;
  site_id: UUID | null;
  occurred_on: ISODate;
  note: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

/** Money we've PREPAID a vendor (عربون / دفعة مقدّمة). Mirror of ClientCredit. */
export interface VendorCredit {
  id: UUID;
  contact_id: UUID;
  amount_delta: number; // signed: + advance added, - consumed
  source_type: CreditSource;
  source_id: UUID | null;
  method: CreditTender | null;
  site_id: UUID | null;
  occurred_on: ISODate;
  note: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface StockMovement {
  id: UUID;
  site_id: UUID;
  item_id: UUID;
  qty_delta: number; // signed
  source_type: StockSource;
  source_id: UUID | null;
  note: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface StockTransfer {
  id: UUID;
  from_site: UUID;
  to_site: UUID;
  item_id: UUID;
  qty: number;
  note: string | null;
  created_by: UUID | null;
  created_at: ISODateTime;
}

export interface CashMovement {
  id: UUID;
  site_id: UUID;
  amount_delta: number; // signed
  source_type: CashSource;
  source_id: UUID | null;
  reason: string;
  created_by: UUID | null;
  created_at: ISODateTime;
}

// ---- RPC return shapes -----------------------------------------------------
export interface DashboardLowStockRow {
  item_id: UUID;
  name_ar: string;
  unit_type: UnitType;
  site_id: UUID;
  site_name: string;
  qty: number;
  threshold: number;
}

export interface DashboardTopItem {
  name_ar: string;
  moved: number;
}

export interface DashboardSiteDrawer {
  site_id: UUID;
  site_name: string;
  closing_cash: number;
}

export interface Dashboard {
  opening_cash: number;
  closing_cash: number;
  sales_total: number;
  collections_total: number;
  collections_by_method: Partial<Record<PaymentMethod, number>>;
  payments_out_by_method: Partial<Record<PaymentMethod, number>>;
  low_stock: DashboardLowStockRow[];
  top_items: DashboardTopItem[];
  site_drawers: DashboardSiteDrawer[];
}

// ---- Insert helpers (columns with DB defaults are optional) ----------------
export type SiteInsert = Pick<Site, 'name_ar'> & Partial<Site>;
export type ContactInsert = Pick<Contact, 'type' | 'name'> & Partial<Contact>;
export type ItemInsert = Pick<Item, 'name_ar' | 'unit_type'> & Partial<Item>;
export type PurchaseOrderInsert =
  Pick<PurchaseOrder, 'vendor_id' | 'total_kg' | 'price_per_kg' | 'total_amount'> &
  Partial<PurchaseOrder>;
export type PoConversionInsert =
  Pick<PoConversion, 'po_id' | 'site_id' | 'item_id' | 'kg_consumed' | 'output_qty' | 'output_unit'> &
  Partial<PoConversion>;
export type SalesOrderInsert =
  Pick<SalesOrder, 'site_id' | 'client_id'> & Partial<SalesOrder>;
export type SalesOrderLineInsert =
  Pick<SalesOrderLine, 'so_id' | 'item_id' | 'qty' | 'unit_price'>;
export type PaymentInsert =
  Pick<Payment, 'parent_type' | 'parent_id' | 'amount' | 'method'> & Partial<Payment>;
export type StockTransferInsert =
  Pick<StockTransfer, 'from_site' | 'to_site' | 'item_id' | 'qty'> & Partial<StockTransfer>;

// ---- RPC argument maps -----------------------------------------------------
export interface RpcArgs {
  get_stock: { p_site_id: UUID | null; p_item_id: UUID; p_as_of?: ISODate | null };
  get_cash_balance: { p_site_id: UUID | null; p_as_of?: ISODate | null };
  get_contact_balance: { p_contact_id: UUID };
  get_dashboard: { p_site_id: UUID | null; p_date?: ISODate };
  po_converted_kg: { p_po_id: UUID };
  po_remaining_kg: { p_po_id: UUID };
}
