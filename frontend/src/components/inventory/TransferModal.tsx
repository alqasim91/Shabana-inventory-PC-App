import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { formatQty } from '@/components/shared/MoneyDisplay';
import { TRANSFER, UNIT_LABEL, COMMON } from '@/labels';
import { createTransfer, getStock } from '@/services/inventory';
import type { Item, Site, UUID } from '@/types/database';

interface TransferModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  items: Item[];
  sites: Site[];
  defaultItemId?: UUID;
  defaultFromSiteId?: UUID;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function TransferModal({
  open,
  onClose,
  onSaved,
  items,
  sites,
  defaultItemId,
  defaultFromSiteId,
}: TransferModalProps) {
  const { show } = useToast();

  const [itemId, setItemId] = useState('');
  const [fromSiteId, setFromSiteId] = useState('');
  const [toSiteId, setToSiteId] = useState('');
  const [qty, setQty] = useState('');
  const [note, setNote] = useState('');
  const [available, setAvailable] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    const initialItem = defaultItemId ?? items[0]?.id ?? '';
    const initialFrom = defaultFromSiteId ?? sites[0]?.id ?? '';
    const initialTo = sites.find((s) => s.id !== initialFrom)?.id ?? sites[0]?.id ?? '';
    setItemId(initialItem);
    setFromSiteId(initialFrom);
    setToSiteId(initialTo);
    setQty('');
    setNote('');
    setError(null);
  }, [open, defaultItemId, defaultFromSiteId, items, sites]);

  useEffect(() => {
    if (!open || !itemId || !fromSiteId) {
      setAvailable(null);
      return;
    }
    let active = true;
    getStock(fromSiteId, itemId).then((stock) => {
      if (active) setAvailable(stock);
    });
    return () => {
      active = false;
    };
  }, [open, itemId, fromSiteId]);

  const selectedItem = items.find((i) => i.id === itemId);

  async function handleSubmit() {
    const qtyNum = Number(qty);
    if (fromSiteId === toSiteId) {
      setError(TRANSFER.sameSiteError);
      return;
    }
    if (!qtyNum || qtyNum <= 0) {
      setError(TRANSFER.qtyRequired);
      return;
    }
    setError(null);

    setSubmitting(true);
    try {
      await createTransfer({
        from_site: fromSiteId,
        to_site: toSiteId,
        item_id: itemId,
        qty: qtyNum,
        note: note.trim() || null,
      });
      show(TRANSFER.success, 'success');
      onSaved();
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : TRANSFER.genericError;
      show(message, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={TRANSFER.title}
      width="420px"
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
            {TRANSFER.submit}
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
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{TRANSFER.item}</label>
        <select value={itemId} onChange={(e) => setItemId(e.target.value)} className={inputClass}>
          {items.map((i) => (
            <option key={i.id} value={i.id}>
              {i.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div className="flex gap-3">
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{TRANSFER.fromSite}</label>
          <select value={fromSiteId} onChange={(e) => setFromSiteId(e.target.value)} className={inputClass}>
            {sites.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name_ar}
              </option>
            ))}
          </select>
        </div>
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{TRANSFER.toSite}</label>
          <select value={toSiteId} onChange={(e) => setToSiteId(e.target.value)} className={inputClass}>
            {sites.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name_ar}
              </option>
            ))}
          </select>
        </div>
      </div>

      {available !== null && selectedItem && (
        <div className="text-[12.5px] text-muted">
          {TRANSFER.available}: <span className="font-bold text-ink">{formatQty(available, UNIT_LABEL[selectedItem.unit_type])}</span>
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{TRANSFER.qty}</label>
        <input
          type="number"
          inputMode="decimal"
          value={qty}
          onChange={(e) => setQty(e.target.value)}
          placeholder={TRANSFER.qtyPlaceholder}
          className={inputClass}
        />
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{TRANSFER.note}</label>
        <input value={note} onChange={(e) => setNote(e.target.value)} className={inputClass} />
      </div>
    </Modal>
  );
}
