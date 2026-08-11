import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { formatMoney } from '@/components/shared/MoneyDisplay';
import { useToast } from '@/components/shared/Toast';
import { supabase } from '@/lib/supabase';
import { todayISODate } from '@/lib/date';
import { PO_FORM, PURCHASES, COMMON, UNIT_LABEL } from '@/labels';
import { listItems } from '@/services/inventory';
import {
  adminUpdatePurchaseOrder,
  createItemizedPurchaseOrder,
  createPurchaseOrder,
  updateItemizedPurchaseOrder,
  type ItemizedLineInput,
  type PurchaseOrderDetail,
} from '@/services/purchases';
import type { PoType, UnitType, UUID } from '@/types/database';
import { NumberInput } from '@/components/shared/NumberInput';

interface PurchaseOrderFormModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: (id: UUID) => void;
  /** When set, edit an existing order (admin-only) instead of creating one. */
  editing?: PurchaseOrderDetail | null;
}

type PriceField = 'kg' | 'ppk' | 'total';

const NEW_ITEM = '__new__';

interface ItemLine {
  item_id: string; // '' | uuid | NEW_ITEM
  new_name: string;
  new_unit: UnitType;
  qty: string;
  unit_price: string;
  line_total: string;
  // Which of unit_price / line_total the user is driving; the other is derived
  // (qty is always the anchor). Same idea as the weight PO's bidirectional pricing.
  priceMode: 'unit' | 'total';
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

async function fetchVendors() {
  const { data, error } = await supabase
    .from('contacts')
    .select('id, name, type')
    .in('type', ['vendor', 'both'])
    .order('name');
  if (error) throw error;
  return data as { id: UUID; name: string; type: string }[];
}

function round(value: number, dp: number): string {
  if (!Number.isFinite(value)) return '';
  const factor = 10 ** dp;
  return String(Math.round(value * factor) / factor);
}

function emptyItemLine(): ItemLine {
  return { item_id: '', new_name: '', new_unit: 'unit', qty: '', unit_price: '', line_total: '', priceMode: 'unit' };
}

function toNum(s: string): number {
  const n = parseFloat(s);
  return Number.isFinite(n) ? n : 0;
}

// Recompute the derived price field (qty is the anchor) after any of qty /
// unit_price / line_total changes.
function recomputeLine(line: ItemLine): ItemLine {
  const qty = toNum(line.qty);
  if (line.priceMode === 'unit') {
    const up = toNum(line.unit_price);
    return { ...line, line_total: qty > 0 && line.unit_price !== '' ? round(qty * up, 2) : '' };
  }
  const lt = toNum(line.line_total);
  return { ...line, unit_price: qty > 0 && line.line_total !== '' ? round(lt / qty, 2) : '' };
}

export function PurchaseOrderFormModal({ open, onClose, onSaved, editing }: PurchaseOrderFormModalProps) {
  const { show } = useToast();

  const [poType, setPoType] = useState<PoType>('general');
  const [vendorId, setVendorId] = useState('');
  const [productName, setProductName] = useState('');
  const [notes, setNotes] = useState('');
  const [orderDate, setOrderDate] = useState(todayISODate());
  // General (weight) order fields.
  const [kg, setKg] = useState('');
  const [ppk, setPpk] = useState('');
  const [total, setTotal] = useState('');
  // Most-recently-edited first. The last entry is the auto-computed (derived) field.
  const [order, setOrder] = useState<PriceField[]>(['kg', 'ppk', 'total']);
  // Itemized order lines.
  const [itemLines, setItemLines] = useState<ItemLine[]>([]);
  const [submitting, setSubmitting] = useState(false);

  const { data: vendors = [] } = useQuery({ queryKey: ['vendors'], queryFn: fetchVendors, enabled: open });
  const { data: items = [] } = useQuery({ queryKey: ['items'], queryFn: listItems, enabled: open });

  useEffect(() => {
    if (!open) return;
    if (editing) {
      setPoType(editing.po_type);
      setVendorId(editing.vendor_id);
      setProductName(editing.product_name ?? '');
      setNotes(editing.notes ?? '');
      setOrderDate(editing.order_date);
      setKg(editing.total_kg == null ? '' : String(editing.total_kg));
      setPpk(editing.price_per_kg == null ? '' : String(editing.price_per_kg));
      setTotal(String(editing.total_amount));
      setItemLines(
        editing.po_type === 'itemized' && editing.lines.length > 0
          ? editing.lines.map((l) => ({
              item_id: l.item_id,
              new_name: '',
              new_unit: l.unitType,
              qty: String(l.qty),
              unit_price: String(l.unit_price),
              line_total: String(l.line_total),
              priceMode: 'unit' as const,
            }))
          : [emptyItemLine()],
      );
    } else {
      setPoType('general');
      setVendorId('');
      setProductName('');
      setNotes('');
      setOrderDate(todayISODate());
      setKg('');
      setPpk('');
      setTotal('');
      setItemLines([emptyItemLine()]);
    }
    setOrder(['kg', 'ppk', 'total']);
  }, [open, editing]);

  const derived: PriceField = order[2];

  function editPrice(field: PriceField, value: string) {
    const newOrder: PriceField[] = [field, ...order.filter((f) => f !== field)];
    const nextDerived = newOrder[2];

    const values: Record<PriceField, string> = { kg, ppk, total, [field]: value } as Record<PriceField, string>;

    const kgN = parseFloat(values.kg);
    const ppkN = parseFloat(values.ppk);
    const totalN = parseFloat(values.total);

    // Recompute the derived field from the two the user is actively driving.
    if (nextDerived === 'total') {
      values.total = kgN > 0 && ppkN >= 0 ? round(kgN * ppkN, 2) : '';
    } else if (nextDerived === 'ppk') {
      values.ppk = kgN > 0 && totalN >= 0 ? round(totalN / kgN, 2) : '';
    } else {
      values.kg = ppkN > 0 && totalN >= 0 ? round(totalN / ppkN, 3) : '';
    }

    setOrder(newOrder);
    setKg(values.kg);
    setPpk(values.ppk);
    setTotal(values.total);
  }

  // ---- itemized line helpers ------------------------------------------------
  function updateItemLine(index: number, patch: Partial<ItemLine>) {
    setItemLines((prev) => prev.map((l, i) => (i === index ? { ...l, ...patch } : l)));
  }
  function addItemLine(count = 1) {
    setItemLines((prev) => [...prev, ...Array.from({ length: count }, emptyItemLine)]);
  }
  function removeItemLine(index: number) {
    setItemLines((prev) => (prev.length === 1 ? prev : prev.filter((_, i) => i !== index)));
  }
  // qty / unit_price / line_total edits — keep the derived field in sync.
  function editLineQty(index: number, value: string) {
    setItemLines((prev) => prev.map((l, i) => (i === index ? recomputeLine({ ...l, qty: value }) : l)));
  }
  function editLineUnitPrice(index: number, value: string) {
    setItemLines((prev) =>
      prev.map((l, i) => (i === index ? recomputeLine({ ...l, unit_price: value, priceMode: 'unit' }) : l)),
    );
  }
  function editLineTotal(index: number, value: string) {
    setItemLines((prev) =>
      prev.map((l, i) => (i === index ? recomputeLine({ ...l, line_total: value, priceMode: 'total' }) : l)),
    );
  }

  const itemizedTotal = itemLines.reduce((sum, l) => sum + toNum(l.line_total), 0);

  // ---- submit ---------------------------------------------------------------
  async function handleSubmit() {
    if (!vendorId) {
      show(PO_FORM.vendorRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      if (poType === 'itemized') {
        await submitItemized();
      } else {
        await submitGeneral();
      }
    } catch (err) {
      show(err instanceof Error ? err.message : PO_FORM.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  async function submitGeneral() {
    const kgN = parseFloat(kg);
    const ppkN = parseFloat(ppk);
    const totalN = parseFloat(total);
    if (!(kgN > 0)) {
      show(PO_FORM.kgRequired, 'error');
      return;
    }
    if (!Number.isFinite(ppkN) || !Number.isFinite(totalN)) {
      show(PO_FORM.amountsRequired, 'error');
      return;
    }
    const payload = {
      vendor_id: vendorId,
      product_name: productName.trim() || null,
      notes: notes.trim() || null,
      order_date: orderDate,
      total_kg: kgN,
      price_per_kg: ppkN,
      total_amount: totalN,
    };
    if (editing) {
      await adminUpdatePurchaseOrder(editing.id, payload);
      show(PO_FORM.savedEdit, 'success');
      onSaved(editing.id);
    } else {
      const id = await createPurchaseOrder(payload);
      show(PO_FORM.saved, 'success');
      onSaved(id);
    }
    onClose();
  }

  async function submitItemized() {
    // A line is "started" if the user touched anything on it; a started line
    // must be fully valid (item/new-name, qty > 0, price >= 0).
    const started = (l: ItemLine) =>
      l.item_id !== '' || l.new_name.trim() !== '' || l.qty !== '' || l.unit_price !== '' || l.line_total !== '';
    const valid = (l: ItemLine) =>
      Number(l.qty) > 0 &&
      Number(l.unit_price) >= 0 &&
      (l.item_id === NEW_ITEM ? l.new_name.trim() !== '' : !!l.item_id);

    if (itemLines.some((l) => started(l) && !valid(l))) {
      show(PO_FORM.lineInvalid, 'error');
      return;
    }
    const clean: ItemizedLineInput[] = itemLines.filter(valid).map((l) => ({
      item_id: l.item_id === NEW_ITEM ? null : l.item_id,
      new_name: l.item_id === NEW_ITEM ? l.new_name.trim() : null,
      new_unit: l.item_id === NEW_ITEM ? l.new_unit : null,
      qty: Number(l.qty),
      unit_price: Number(l.unit_price),
      line_total: Math.round(toNum(l.line_total) * 100) / 100,
    }));
    if (clean.length === 0) {
      show(PO_FORM.linesRequired, 'error');
      return;
    }
    const payload = { vendor_id: vendorId, order_date: orderDate, notes: notes.trim() || null, lines: clean };
    if (editing) {
      await updateItemizedPurchaseOrder(editing.id, payload);
      show(PO_FORM.savedEdit, 'success');
      onSaved(editing.id);
    } else {
      const id = await createItemizedPurchaseOrder(payload);
      show(PO_FORM.savedItemized, 'success');
      onSaved(id);
    }
    onClose();
  }

  function priceField(field: PriceField, label: string, value: string, dp: number) {
    const isDerived = derived === field;
    return (
      <div className="flex-1">
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{label}</label>
        <NumberInput
          value={value}
          onChange={(e) => editPrice(field, e.target.value)}
          className={`${inputClass} ${isDerived ? 'bg-row-alt' : ''}`}
          step={dp === 3 ? '0.001' : '0.01'}
        />
        {isDerived && <p className="mt-1 text-[11px] text-teal">{PO_FORM.computed}</p>}
      </div>
    );
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={editing ? PURCHASES.editPo : PURCHASES.newPo}
      width={poType === 'itemized' ? '640px' : '480px'}
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
      {editing && editing.po_type === 'general' && editing.convertedKg > 0.0005 && (
        <div className="rounded-[10px] border border-amber-text/30 bg-amber-soft px-3.5 py-2.5 text-[12.5px] font-semibold text-amber-text">
          {PURCHASES.editConvertedWarning}
        </div>
      )}
      {editing && editing.po_type === 'itemized' && (
        <div className="rounded-[10px] border border-amber-text/30 bg-amber-soft px-3.5 py-2.5 text-[12.5px] font-semibold text-amber-text">
          {PURCHASES.editItemizedWarning}
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.vendor}</label>
        <select value={vendorId} onChange={(e) => setVendorId(e.target.value)} className={inputClass}>
          <option value="">{PO_FORM.vendorPlaceholder}</option>
          {vendors.map((v) => (
            <option key={v.id} value={v.id}>
              {v.name}
            </option>
          ))}
        </select>
      </div>

      {/* Order type — chosen right after the vendor; locked when editing. */}
      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.poType}</label>
        <select
          value={poType}
          onChange={(e) => setPoType(e.target.value as PoType)}
          disabled={!!editing}
          className={`${inputClass} disabled:opacity-70`}
        >
          <option value="general">{PO_FORM.typeGeneral}</option>
          <option value="itemized">{PO_FORM.typeItemized}</option>
        </select>
      </div>

      {poType === 'general' && (
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.productName}</label>
          <input
            type="text"
            value={productName}
            onChange={(e) => setProductName(e.target.value)}
            placeholder={PO_FORM.productNamePlaceholder}
            className={inputClass}
          />
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.orderDate}</label>
        <ArabicDatePicker value={orderDate} onChange={setOrderDate} />
      </div>

      {poType === 'general' ? (
        <>
          <div className="flex gap-3">
            {priceField('kg', PO_FORM.totalKg, kg, 3)}
            {priceField('ppk', PO_FORM.pricePerKg, ppk, 2)}
          </div>
          {priceField('total', PO_FORM.total, total, 2)}
        </>
      ) : (
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.lines}</label>
          <div className="flex flex-col gap-2.5">
            {itemLines.map((line, i) => {
              const isNew = line.item_id === NEW_ITEM;
              const lineUnit: UnitType = isNew
                ? line.new_unit
                : items.find((it) => it.id === line.item_id)?.unit_type ?? 'unit';
              const priceLabel = lineUnit === 'kg' ? PO_FORM.pricePerKg : PO_FORM.itemUnitPrice;
              return (
                <div key={i} className="rounded-[10px] bg-row-alt p-2.5">
                  <div className="mb-2 flex items-end gap-2">
                    <div className="flex-1">
                      <label className="mb-1 block text-[11px] font-semibold text-muted">{PO_FORM.item}</label>
                      <select
                        value={line.item_id}
                        onChange={(e) => updateItemLine(i, { item_id: e.target.value })}
                        className={`${inputClass} bg-white`}
                      >
                        <option value="">{PO_FORM.itemPlaceholder}</option>
                        {items.map((it) => (
                          <option key={it.id} value={it.id}>
                            {it.name_ar}
                          </option>
                        ))}
                        <option value={NEW_ITEM}>{PO_FORM.newItemOption}</option>
                      </select>
                    </div>
                    <button
                      onClick={() => removeItemLine(i)}
                      aria-label={PO_FORM.removeLine}
                      disabled={itemLines.length === 1}
                      className="mb-1.5 flex-shrink-0 text-muted hover:text-red-600 disabled:opacity-40"
                    >
                      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                        <polyline points="3 6 5 6 21 6" />
                        <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" />
                        <path d="M10 11v6M14 11v6" />
                      </svg>
                    </button>
                  </div>

                  {isNew && (
                    <div className="mb-2 flex gap-2">
                      <div className="flex-1">
                        <label className="mb-1 block text-[11px] font-semibold text-muted">{PO_FORM.newItemName}</label>
                        <input
                          type="text"
                          value={line.new_name}
                          onChange={(e) => updateItemLine(i, { new_name: e.target.value })}
                          placeholder={PO_FORM.newItemNamePlaceholder}
                          className={`${inputClass} bg-white`}
                        />
                      </div>
                      <div className="w-28">
                        <label className="mb-1 block text-[11px] font-semibold text-muted">{PO_FORM.newItemUnit}</label>
                        <select
                          value={line.new_unit}
                          onChange={(e) => updateItemLine(i, { new_unit: e.target.value as UnitType })}
                          className={`${inputClass} bg-white`}
                        >
                          <option value="unit">{UNIT_LABEL.unit}</option>
                          <option value="kg">{UNIT_LABEL.kg}</option>
                        </select>
                      </div>
                    </div>
                  )}

                  <div className="flex items-end gap-2">
                    <div className="flex-1">
                      <label className="mb-1 block text-[11px] font-semibold text-muted">{PO_FORM.qty}</label>
                      <NumberInput
                        value={line.qty}
                        onChange={(e) => editLineQty(i, e.target.value)}
                        className={`${inputClass} bg-white`}
                      />
                    </div>
                    <div className="flex-1">
                      <label className="mb-1 block text-[11px] font-semibold text-muted">{priceLabel}</label>
                      <NumberInput
                        value={line.unit_price}
                        onChange={(e) => editLineUnitPrice(i, e.target.value)}
                        className={`${inputClass} bg-white ${line.priceMode === 'total' ? 'bg-row-alt' : ''}`}
                      />
                    </div>
                    <div className="flex-1">
                      <label className="mb-1 block text-[11px] font-semibold text-muted">{PO_FORM.lineTotal}</label>
                      <NumberInput
                        value={line.line_total}
                        onChange={(e) => editLineTotal(i, e.target.value)}
                        className={`${inputClass} bg-white ${line.priceMode === 'unit' ? 'bg-row-alt' : ''}`}
                      />
                    </div>
                  </div>
                </div>
              );
            })}
          </div>

          <div className="mt-2 flex flex-wrap gap-2">
            <button
              onClick={() => addItemLine(1)}
              className="rounded-[9px] border border-dashed border-teal bg-transparent px-3 py-2 text-[12.5px] font-bold text-teal hover:bg-teal-soft"
            >
              + {PO_FORM.addLine}
            </button>
            <button
              onClick={() => addItemLine(5)}
              className="rounded-[9px] border border-dashed border-border bg-transparent px-3 py-2 text-[12.5px] font-bold text-muted hover:bg-row-alt"
            >
              + {PO_FORM.addLine5}
            </button>
            <button
              onClick={() => addItemLine(10)}
              className="rounded-[9px] border border-dashed border-border bg-transparent px-3 py-2 text-[12.5px] font-bold text-muted hover:bg-row-alt"
            >
              + {PO_FORM.addLine10}
            </button>
          </div>

          <div className="mt-3 flex items-center justify-between rounded-[10px] bg-sand px-4 py-3">
            <span className="text-[13.5px] font-bold text-muted">{PO_FORM.grandTotal}</span>
            <span className="text-[16px] font-bold text-teal-dark">{formatMoney(itemizedTotal)}</span>
          </div>
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PO_FORM.notes}</label>
        <textarea
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
          placeholder={PO_FORM.notesPlaceholder}
          rows={2}
          className={`${inputClass} resize-y`}
        />
      </div>
    </Modal>
  );
}
