import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { formatQty } from '@/components/shared/MoneyDisplay';
import { CONVERSION_FORM, UNIT_LABEL, COMMON } from '@/labels';
import { addConversion } from '@/services/purchases';
import type { Item, Site, UnitType, UUID } from '@/types/database';

interface ConversionModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  poId: UUID;
  remainingKg: number;
  items: Item[];
  sites: Site[];
}

const UNIT_TYPES: UnitType[] = ['kg', 'unit'];

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function ConversionModal({ open, onClose, onSaved, poId, remainingKg, items, sites }: ConversionModalProps) {
  const { show } = useToast();

  const [itemId, setItemId] = useState('');
  const [siteId, setSiteId] = useState('');
  const [kgConsumed, setKgConsumed] = useState('');
  const [outputQty, setOutputQty] = useState('');
  const [outputUnit, setOutputUnit] = useState<UnitType>('unit');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setItemId(items[0]?.id ?? '');
    setSiteId(sites[0]?.id ?? '');
    setKgConsumed('');
    setOutputQty('');
    setOutputUnit(items[0]?.unit_type ?? 'unit');
    setError(null);
  }, [open, items, sites]);

  // Default the output unit to the chosen item's unit type (still overridable).
  function onItemChange(id: string) {
    setItemId(id);
    const item = items.find((i) => i.id === id);
    if (item) setOutputUnit(item.unit_type);
  }

  async function handleSubmit() {
    const kgNum = Number(kgConsumed);
    const outNum = Number(outputQty);
    if (!(kgNum > 0)) {
      setError(CONVERSION_FORM.kgRequired);
      return;
    }
    if (!(outNum > 0)) {
      setError(CONVERSION_FORM.outputRequired);
      return;
    }
    if (kgNum > remainingKg + 0.0005) {
      setError(CONVERSION_FORM.exceedsRemaining);
      return;
    }
    setError(null);

    setSubmitting(true);
    try {
      await addConversion({
        po_id: poId,
        site_id: siteId,
        item_id: itemId,
        kg_consumed: kgNum,
        output_qty: outNum,
        output_unit: outputUnit,
      });
      show(CONVERSION_FORM.success, 'success');
      onSaved();
      onClose();
    } catch (err) {
      show(err instanceof Error ? err.message : CONVERSION_FORM.genericError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={CONVERSION_FORM.title}
      width="440px"
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
            {CONVERSION_FORM.title}
          </button>
        </>
      }
    >
      {error && (
        <div className="rounded-[10px] bg-[#FBE7E1] px-3.5 py-2.5 text-[12.5px] font-semibold text-[#B3402C]">
          {error}
        </div>
      )}

      <div className="text-[12.5px] text-muted">
        {CONVERSION_FORM.available}: <span className="font-bold text-ink">{formatQty(remainingKg, UNIT_LABEL.kg)}</span>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONVERSION_FORM.item}</label>
        <select value={itemId} onChange={(e) => onItemChange(e.target.value)} className={inputClass}>
          {items.map((i) => (
            <option key={i.id} value={i.id}>
              {i.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONVERSION_FORM.site}</label>
        <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONVERSION_FORM.kgConsumed}</label>
        <input type="number" inputMode="decimal" value={kgConsumed} onChange={(e) => setKgConsumed(e.target.value)} className={inputClass} />
      </div>

      <div className="flex gap-3">
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONVERSION_FORM.outputQty}</label>
          <input type="number" inputMode="decimal" value={outputQty} onChange={(e) => setOutputQty(e.target.value)} className={inputClass} />
        </div>
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONVERSION_FORM.outputUnit}</label>
          <select value={outputUnit} onChange={(e) => setOutputUnit(e.target.value as UnitType)} className={inputClass}>
            {UNIT_TYPES.map((u) => (
              <option key={u} value={u}>
                {UNIT_LABEL[u]}
              </option>
            ))}
          </select>
        </div>
      </div>
    </Modal>
  );
}
