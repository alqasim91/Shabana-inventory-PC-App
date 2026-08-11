import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { useToast } from '@/components/shared/Toast';
import { useSite } from '@/contexts/SiteContext';
import { supabase } from '@/lib/supabase';
import { todayISODate } from '@/lib/date';
import { SO_FORM, SALES, UNIT_LABEL, COMMON } from '@/labels';
import { getStock, listItems } from '@/services/inventory';
import {
  adminUpdateSalesOrder,
  createSalesOrder,
  updateSalesOrderDraft,
  type SalesOrderDetail,
} from '@/services/sales';
import type { SoDiscountType, UUID } from '@/types/database';
import { NumberInput } from '@/components/shared/NumberInput';

interface SalesOrderFormModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: (id: UUID) => void;
  /** When set, edit an existing draft instead of creating a new one. */
  editing?: SalesOrderDetail | null;
}

interface LineDraft {
  item_id: string;
  qty: string;
  unit_price: string;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

async function fetchClients() {
  const { data, error } = await supabase
    .from('contacts')
    .select('id, name, type')
    .in('type', ['client', 'both'])
    .order('name');
  if (error) throw error;
  return data as { id: UUID; name: string; type: string }[];
}

function emptyLine(): LineDraft {
  return { item_id: '', qty: '', unit_price: '' };
}

const DISCOUNT_TABS: { value: SoDiscountType; label: string }[] = [
  { value: 'none', label: SO_FORM.discountNone },
  { value: 'amount', label: SO_FORM.discountAmount },
  { value: 'percent', label: SO_FORM.discountPercent },
];

/**
 * The money the discount takes off, rounded to piastres exactly as the DB does
 * it (`round(subtotal * value / 100, 2)`) — so the number shown here while the
 * user types is the number that ends up on the invoice.
 */
function discountMoney(type: SoDiscountType, value: number, subtotal: number): number {
  if (type === 'amount') return Math.min(Math.max(value, 0), subtotal);
  if (type === 'percent') {
    return Math.min(Math.round(((subtotal * Math.max(value, 0)) / 100) * 100) / 100, subtotal);
  }
  return 0;
}

export function SalesOrderFormModal({ open, onClose, onSaved, editing }: SalesOrderFormModalProps) {
  const { show } = useToast();
  const { sites, selectedSiteIdForQuery } = useSite();

  const [siteId, setSiteId] = useState('');
  const [clientId, setClientId] = useState('');
  const [orderDate, setOrderDate] = useState(todayISODate());
  const [lines, setLines] = useState<LineDraft[]>([emptyLine()]);
  const [discountType, setDiscountType] = useState<SoDiscountType>('none');
  const [discountValue, setDiscountValue] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const { data: clients = [] } = useQuery({ queryKey: ['clients'], queryFn: fetchClients, enabled: open });
  const { data: items = [] } = useQuery({ queryKey: ['items'], queryFn: listItems, enabled: open });

  useEffect(() => {
    if (!open) return;
    if (editing) {
      setSiteId(editing.site_id);
      setClientId(editing.client_id);
      setOrderDate(editing.order_date);
      setLines(
        editing.lines.length > 0
          ? editing.lines.map((l) => ({ item_id: l.item_id, qty: String(l.qty), unit_price: String(l.unit_price) }))
          : [emptyLine()],
      );
      setDiscountType(editing.discount_type ?? 'none');
      setDiscountValue(
        !editing.discount_type || editing.discount_type === 'none' ? '' : String(editing.discount_value),
      );
    } else {
      // Default to the current switcher site, else the first site.
      setSiteId(selectedSiteIdForQuery ?? sites[0]?.id ?? '');
      setClientId('');
      setOrderDate(todayISODate());
      setLines([emptyLine()]);
      setDiscountType('none');
      setDiscountValue('');
    }
  }, [open, editing, sites, selectedSiteIdForQuery]);

  function updateLine(index: number, patch: Partial<LineDraft>) {
    setLines((prev) => prev.map((l, i) => (i === index ? { ...l, ...patch } : l)));
  }

  // Prefill the unit price from the item's preset sale price (still editable).
  function onItemChange(index: number, itemId: string) {
    const item = items.find((i) => i.id === itemId);
    updateLine(index, { item_id: itemId, unit_price: item ? String(item.sale_price) : '' });
  }

  function addLine() {
    setLines((prev) => [...prev, emptyLine()]);
  }

  function removeLine(index: number) {
    setLines((prev) => (prev.length === 1 ? prev : prev.filter((_, i) => i !== index)));
  }

  // Live stock at the SO's branch, for the items actually chosen. Only the
  // picked items are looked up, so an empty form costs nothing and a five-line
  // order costs five small reads — cached, so retyping a quantity doesn't refetch.
  const chosenItemIds = useMemo(
    () => [...new Set(lines.map((l) => l.item_id).filter(Boolean))].sort(),
    [lines],
  );

  const { data: stockByItem = {} } = useQuery({
    queryKey: ['so-form-stock', siteId, chosenItemIds],
    queryFn: async () => {
      const entries = await Promise.all(
        chosenItemIds.map(async (id) => [id, await getStock(siteId, id)] as const),
      );
      return Object.fromEntries(entries) as Record<string, number>;
    },
    enabled: open && !!siteId && chosenItemIds.length > 0,
    staleTime: 30_000,
  });

  // Stock is only "short" for an order that hasn't taken it yet. Once an order
  // is placed its quantities are already out of the branch, so comparing what's
  // left against its own lines would flag every placed order as a problem.
  const stockMatters = !editing || editing.status === 'draft' || editing.status === 'invoiced';

  const subtotal = lines.reduce((sum, l) => {
    const q = Number(l.qty);
    const p = Number(l.unit_price);
    return sum + (q > 0 && p >= 0 ? q * p : 0);
  }, 0);

  const discountNum = Number(discountValue);
  const discountOff = discountMoney(discountType, Number.isFinite(discountNum) ? discountNum : 0, subtotal);
  const netTotal = subtotal - discountOff;
  // Flagged live under the field rather than only on save — a discount that
  // overshoots the order is a typo the user should see the moment they make it.
  const discountError =
    discountType === 'none' || !discountValue
      ? null
      : discountType === 'percent' && discountNum > 100
        ? SO_FORM.discountPercentTooBig
        : discountType === 'amount' && discountNum > subtotal + 0.005
          ? SO_FORM.discountTooBig
          : null;

  function pickDiscountType(next: SoDiscountType) {
    setDiscountType(next);
    if (next === 'none') setDiscountValue('');
  }

  async function handleSubmit() {
    if (!siteId) {
      show(SO_FORM.siteRequired, 'error');
      return;
    }
    if (!clientId) {
      show(SO_FORM.clientRequired, 'error');
      return;
    }
    const clean = lines
      .map((l) => ({ item_id: l.item_id, qty: Number(l.qty), unit_price: Number(l.unit_price) }))
      .filter((l) => l.item_id && l.qty > 0 && l.unit_price >= 0);
    if (clean.length === 0) {
      show(SO_FORM.linesRequired, 'error');
      return;
    }
    // A row that was partially filled (item chosen but qty/price invalid) is a mistake, not an omission.
    const hasBrokenRow = lines.some(
      (l) => (l.item_id || l.qty || l.unit_price) && !(l.item_id && Number(l.qty) > 0 && Number(l.unit_price) >= 0),
    );
    if (hasBrokenRow) {
      show(SO_FORM.lineInvalid, 'error');
      return;
    }
    if (discountError) {
      show(discountError, 'error');
      return;
    }
    if (discountType !== 'none' && !(discountNum > 0)) {
      show(SO_FORM.discountRequired, 'error');
      return;
    }

    setSubmitting(true);
    try {
      const payload = {
        site_id: siteId,
        client_id: clientId,
        order_date: orderDate,
        lines: clean,
        discount_type: discountType,
        discount_value: discountType === 'none' ? 0 : discountNum,
      };
      if (editing) {
        // Draft edits use the plain path; a locked order (invoiced/placed/closed)
        // goes through the admin RPC that safely reverses & re-applies stock.
        if (editing.status === 'draft') {
          await updateSalesOrderDraft(editing.id, payload);
        } else {
          await adminUpdateSalesOrder(editing.id, payload);
        }
        show(SO_FORM.savedEdit, 'success');
        onSaved(editing.id);
      } else {
        const id = await createSalesOrder(payload);
        show(SO_FORM.savedNew, 'success');
        onSaved(id);
      }
      onClose();
    } catch (err) {
      show(err instanceof Error ? err.message : SO_FORM.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={editing ? SALES.editSo : SALES.newSo}
      width="560px"
      footer={
        <>
          <button
            onClick={onClose}
            className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
          >
            {COMMON.cancel}
          </button>
          <button
            onClick={handleSubmit}
            disabled={submitting}
            className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {COMMON.save}
          </button>
        </>
      }
    >
      {editing && editing.status !== 'draft' && (
        <div className="rounded-[10px] border border-amber-text/30 bg-amber-soft px-3.5 py-2.5 text-[12.5px] font-semibold text-amber-text">
          {SALES.editLockedWarning}
        </div>
      )}

      <div className="flex gap-3">
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SO_FORM.site}</label>
          <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
            {sites.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name_ar}
              </option>
            ))}
          </select>
        </div>
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SO_FORM.client}</label>
          <select value={clientId} onChange={(e) => setClientId(e.target.value)} className={inputClass}>
            <option value="">{SO_FORM.clientPlaceholder}</option>
            {clients.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SO_FORM.orderDate}</label>
        <ArabicDatePicker value={orderDate} onChange={setOrderDate} />
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SO_FORM.lines}</label>
        <div className="flex flex-col gap-2">
          {lines.map((line, i) => {
            const q = Number(line.qty);
            const p = Number(line.unit_price);
            const lineTotal = q > 0 && p >= 0 ? q * p : 0;
            // Price label follows the item's unit: سعر الكيلو for weight, سعر الوحدة for count.
            const lineUnit = items.find((it) => it.id === line.item_id)?.unit_type;
            const priceLabel = lineUnit === 'kg' ? SO_FORM.unitPriceByKg : SO_FORM.unitPrice;
            const available = line.item_id ? stockByItem[line.item_id] : undefined;
            const short = stockMatters && available !== undefined && q > 0 && q > available + 0.0005;
            return (
              <div key={i} className="flex flex-wrap items-end gap-2 rounded-[10px] bg-row-alt p-2.5">
                <div className="flex-[2]">
                  <label className="mb-1 block text-[11px] font-semibold text-muted">{SO_FORM.item}</label>
                  <select
                    value={line.item_id}
                    onChange={(e) => onItemChange(i, e.target.value)}
                    className={`${inputClass} bg-white`}
                  >
                    <option value="">—</option>
                    {items.map((it) => (
                      <option key={it.id} value={it.id}>
                        {it.name_ar}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="w-20">
                  <label className="mb-1 block text-[11px] font-semibold text-muted">{SO_FORM.qty}</label>
                  <NumberInput
                    value={line.qty}
                    onChange={(e) => updateLine(i, { qty: e.target.value })}
                    className={`${inputClass} bg-white ${short ? 'border-red-500' : ''}`}
                  />
                </div>
                <div className="w-24">
                  <label className="mb-1 block text-[11px] font-semibold text-muted">{priceLabel}</label>
                  <NumberInput
                    value={line.unit_price}
                    onChange={(e) => updateLine(i, { unit_price: e.target.value })}
                    className={`${inputClass} bg-white`}
                  />
                </div>
                <div className="w-24 pb-2 text-[12.5px] font-bold text-ink">{formatMoney(lineTotal)}</div>
                <button
                  onClick={() => removeLine(i)}
                  aria-label={SO_FORM.removeLine}
                  disabled={lines.length === 1}
                  className="mb-1.5 flex-shrink-0 text-muted hover:text-red-600 disabled:opacity-40"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                    <polyline points="3 6 5 6 21 6" />
                    <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" />
                    <path d="M10 11v6M14 11v6" />
                  </svg>
                </button>

                {/* The stock line sits under the row, on its own full-width
                    line, so a long branch quantity never squeezes the inputs. */}
                {line.item_id && (
                  <div className="w-full pr-0.5 text-[11.5px]">
                    {available === undefined ? (
                      <span className="text-faint">{SO_FORM.stockLoading}</span>
                    ) : (
                      <span className={short ? 'font-bold text-red-600' : 'text-muted'}>
                        {SO_FORM.available}: {formatQty(available, UNIT_LABEL[lineUnit ?? 'unit'])}
                        {short ? ` — ${SO_FORM.notEnoughStock}` : ''}
                      </span>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>

        <button
          onClick={addLine}
          className="mt-2 rounded-[9px] border border-dashed border-teal bg-transparent px-3 py-2 text-[12.5px] font-bold text-teal hover:bg-teal-soft"
        >
          + {SO_FORM.addLine}
        </button>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SO_FORM.discount}</label>
        <div className="flex items-center gap-2">
          {/* Type first, then the number — reading right-to-left, you choose what
              kind of discount this is before you say how much. */}
          <div className="flex overflow-hidden rounded-[10px] border border-border">
            {DISCOUNT_TABS.map((tab) => (
              <button
                key={tab.value}
                type="button"
                onClick={() => pickDiscountType(tab.value)}
                className={`min-w-[58px] px-3 py-2 text-[12.5px] font-bold ${
                  discountType === tab.value ? 'bg-teal text-white' : 'bg-white text-muted hover:bg-row-alt'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </div>
          {discountType !== 'none' && (
            <NumberInput
              value={discountValue}
              onChange={(e) => setDiscountValue(e.target.value)}
              placeholder={
                discountType === 'percent' ? SO_FORM.discountPercentPlaceholder : SO_FORM.discountAmountPlaceholder
              }
              className={`${inputClass} flex-1 ${discountError ? 'border-red-500' : ''}`}
            />
          )}
        </div>
        {discountError && <p className="m-0 mt-1.5 text-[12px] font-semibold text-red-600">{discountError}</p>}
      </div>

      <div className="rounded-[10px] bg-sand px-4 py-3">
        {/* Only shown once there IS a discount — an un-discounted order should
            look exactly as it always has: one line, one number. */}
        {discountOff > 0 && (
          <>
            <div className="flex items-center justify-between pb-1.5 text-[13px]">
              <span className="text-muted">{SO_FORM.subtotal}</span>
              <span className="font-semibold text-ink">{formatMoney(subtotal)}</span>
            </div>
            <div className="flex items-center justify-between border-b border-border pb-2 text-[13px]">
              <span className="text-muted">
                {SO_FORM.discountLine}
                {discountType === 'percent' ? ` (${discountNum.toLocaleString('ar-EG')}٪)` : ''}
              </span>
              <span className="font-bold text-red-600">− {formatMoney(discountOff)}</span>
            </div>
          </>
        )}
        <div className={`flex items-center justify-between ${discountOff > 0 ? 'pt-2' : ''}`}>
          <span className="text-[13.5px] font-bold text-muted">
            {discountOff > 0 ? SO_FORM.netTotal : SO_FORM.grandTotal}
          </span>
          <span className="text-[16px] font-bold text-teal-dark">{formatMoney(netTotal)}</span>
        </div>
      </div>
    </Modal>
  );
}
