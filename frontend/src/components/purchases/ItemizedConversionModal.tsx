import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { formatQty } from '@/components/shared/MoneyDisplay';
import { ITEMIZED_CONVERT, UNIT_LABEL, COMMON } from '@/labels';
import { addLineConversion, type PoLineRow } from '@/services/purchases';
import type { Site, UUID } from '@/types/database';
import { NumberInput } from '@/components/shared/NumberInput';

interface ItemizedConversionModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  /** Only lines with remaining > 0 should be passed in. */
  lines: PoLineRow[];
  sites: Site[];
  /** Preselect a specific line (e.g. the row whose "تحويل" button was clicked). */
  initialLineId?: UUID | null;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function ItemizedConversionModal({
  open,
  onClose,
  onSaved,
  lines,
  sites,
  initialLineId,
}: ItemizedConversionModalProps) {
  const { show } = useToast();

  const [lineId, setLineId] = useState('');
  const [siteId, setSiteId] = useState('');
  const [qty, setQty] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setLineId(initialLineId ?? lines[0]?.id ?? '');
    setSiteId(sites[0]?.id ?? '');
    setQty('');
    setError(null);
  }, [open, initialLineId, lines, sites]);

  const line = lines.find((l) => l.id === lineId);

  async function handleSubmit() {
    const qtyNum = Number(qty);
    if (!lineId) return;
    if (!(qtyNum > 0)) {
      setError(ITEMIZED_CONVERT.qtyRequired);
      return;
    }
    if (line && qtyNum > line.remaining + 0.0005) {
      setError(ITEMIZED_CONVERT.exceeds);
      return;
    }
    setError(null);
    setSubmitting(true);
    try {
      await addLineConversion({ po_line_id: lineId, site_id: siteId, qty: qtyNum });
      show(ITEMIZED_CONVERT.success, 'success');
      onSaved();
      onClose();
    } catch (err) {
      show(err instanceof Error ? err.message : ITEMIZED_CONVERT.error, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={ITEMIZED_CONVERT.title}
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
            {ITEMIZED_CONVERT.title}
          </button>
        </>
      }
    >
      {error && (
        <div className="rounded-[10px] bg-[#FBE7E1] px-3.5 py-2.5 text-[12.5px] font-semibold text-[#B3402C]">
          {error}
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ITEMIZED_CONVERT.line}</label>
        <select value={lineId} onChange={(e) => setLineId(e.target.value)} className={inputClass}>
          {lines.map((l) => (
            <option key={l.id} value={l.id}>
              {l.itemName}
            </option>
          ))}
        </select>
      </div>

      {line && (
        <div className="text-[12.5px] text-muted">
          {ITEMIZED_CONVERT.remaining}:{' '}
          <span className="font-bold text-ink">{formatQty(line.remaining, UNIT_LABEL[line.unitType])}</span>
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ITEMIZED_CONVERT.site}</label>
        <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ITEMIZED_CONVERT.qty}</label>
        <NumberInput
          value={qty}
          onChange={(e) => setQty(e.target.value)}
          className={inputClass}
        />
      </div>
    </Modal>
  );
}
