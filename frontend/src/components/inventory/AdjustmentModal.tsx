import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { ADJUSTMENT, COMMON } from '@/labels';
import { createAdjustment } from '@/services/inventory';
import type { Item, Site, UUID } from '@/types/database';

interface AdjustmentModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  item: Item;
  sites: Site[];
  defaultSiteId?: UUID;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function AdjustmentModal({ open, onClose, onSaved, item, sites, defaultSiteId }: AdjustmentModalProps) {
  const { show } = useToast();

  const [siteId, setSiteId] = useState('');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setSiteId(defaultSiteId ?? sites[0]?.id ?? '');
    setAmount('');
    setReason('');
    setError(null);
  }, [open, defaultSiteId, sites]);

  async function handleSubmit() {
    const amountNum = Number(amount);
    if (!amountNum) {
      setError(ADJUSTMENT.amountRequired);
      return;
    }
    if (!reason.trim()) {
      setError(ADJUSTMENT.reasonRequired);
      return;
    }
    setError(null);

    setSubmitting(true);
    try {
      await createAdjustment({ site_id: siteId, item_id: item.id, qty_delta: amountNum, note: reason.trim() });
      show(ADJUSTMENT.success, 'success');
      onSaved();
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : ADJUSTMENT.genericError;
      show(message, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`${ADJUSTMENT.title} — ${item.name_ar}`}
      width="400px"
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
            {ADJUSTMENT.submit}
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
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ADJUSTMENT.site}</label>
        <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ADJUSTMENT.changeAmount}</label>
        <input type="number" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} className={inputClass} />
        <p className="mt-1 text-[11.5px] text-faint">{ADJUSTMENT.changeHint}</p>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ADJUSTMENT.reason}</label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={2}
          placeholder={ADJUSTMENT.reasonPlaceholder}
          className={`${inputClass} resize-none`}
        />
      </div>
    </Modal>
  );
}
