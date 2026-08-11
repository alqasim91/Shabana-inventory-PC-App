import { useEffect, useMemo, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { NumberInput } from '@/components/shared/NumberInput';
import { useToast } from '@/components/shared/Toast';
import { formatQty } from '@/components/shared/MoneyDisplay';
import { COMMON, OPENING, UNIT_LABEL } from '@/labels';
import { getStock, setOpeningStock } from '@/services/inventory';
import type { Item, Site, UUID } from '@/types/database';

interface OpeningStockModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  items: Item[];
  sites: Site[];
  defaultSiteId?: UUID;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

/**
 * Migration onboarding: an admin types the quantity actually on the shelf for
 * each صنف at one فرع, and the app posts the difference.
 *
 * One فرع at a time on purpose. A grid of every item × every branch is where
 * someone files الفرع الرئيسي's count under المخزن and doesn't notice.
 */
export function OpeningStockModal({
  open,
  onClose,
  onSaved,
  items,
  sites,
  defaultSiteId,
}: OpeningStockModalProps) {
  const { show } = useToast();

  const [siteId, setSiteId] = useState('');
  const [current, setCurrent] = useState<Record<UUID, number>>({});
  const [loading, setLoading] = useState(false);
  const [counted, setCounted] = useState<Record<UUID, string>>({});
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    if (!open) return;
    setSiteId(defaultSiteId ?? sites[0]?.id ?? '');
    setCounted({});
    setNote('');
    setError(null);
    setConfirming(false);
  }, [open, defaultSiteId, sites]);

  // Live balances for the chosen فرع. Re-read whenever the فرع changes so the
  // "الفرق" column is never computed against another branch's numbers.
  useEffect(() => {
    if (!open || !siteId || items.length === 0) return;
    let active = true;
    setLoading(true);
    Promise.all(items.map((i) => getStock(siteId, i.id)))
      .then((qtys) => {
        if (!active) return;
        setCurrent(Object.fromEntries(items.map((item, i) => [item.id, qtys[i]])));
      })
      .catch(() => {
        if (active) setError(OPENING.genericError);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [open, siteId, items]);

  /** Only items the admin actually typed a different number into. */
  const changes = useMemo(() => {
    return items
      .map((item) => {
        const raw = counted[item.id];
        if (raw === undefined || raw.trim() === '') return null;
        const qty = Number(raw);
        if (!Number.isFinite(qty)) return null;
        const now = current[item.id] ?? 0;
        // Rounded before comparing so 25 vs 25.0000001 isn't a "change".
        const delta = Math.round((qty - now) * 1000) / 1000;
        if (delta === 0) return null;
        return { item, qty, delta };
      })
      .filter((c): c is { item: Item; qty: number; delta: number } => c !== null);
  }, [items, counted, current]);

  const hasNegative = useMemo(
    () =>
      Object.values(counted).some((v) => v.trim() !== '' && Number.isFinite(Number(v)) && Number(v) < 0),
    [counted],
  );

  async function handleSubmit() {
    if (hasNegative) {
      setError(OPENING.negative);
      return;
    }
    if (changes.length === 0) {
      setError(OPENING.noChanges);
      return;
    }
    setError(null);

    // Confirmation is a step inside this modal, NOT window.confirm(). A native
    // dialog can be suppressed by the browser (or by the user ticking "block
    // more dialogs"), and confirm() then returns false — which read exactly
    // like the save button doing nothing at all.
    if (!confirming) {
      setConfirming(true);
      return;
    }
    setConfirming(false);

    setSubmitting(true);
    try {
      // Sequential, not Promise.all: each row is its own ledger write, and if
      // one fails the message should name the صنف that failed rather than
      // leaving the admin guessing which of twenty calls broke.
      let done = 0;
      for (const c of changes) {
        try {
          await setOpeningStock({
            site_id: siteId,
            item_id: c.item.id,
            qty: c.qty,
            note: note.trim() || null,
          });
          done += 1;
        } catch (err) {
          const message = err instanceof Error ? err.message : OPENING.genericError;
          throw new Error(`${c.item.name_ar}: ${message}`);
        }
      }
      show(OPENING.success(done), 'success');
      onSaved();
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : OPENING.genericError;
      show(message, 'error');
      // Rows before the failure DID post. Refresh so what's on screen is real.
      onSaved();
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={OPENING.title}
      width="620px"
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
            disabled={submitting || loading || changes.length === 0}
            className={`flex-1 rounded-[10px] border-none py-2.5 text-[13.5px] font-bold text-white disabled:opacity-60 ${
              confirming ? 'bg-[#B3402C] hover:opacity-90' : 'bg-teal hover:bg-teal-hover'
            }`}
          >
            {confirming ? OPENING.confirmSubmit : OPENING.submit}
            {changes.length > 0 ? ` (${changes.length.toLocaleString('ar-EG')})` : ''}
          </button>
        </>
      }
    >
      {error && (
        <div className="rounded-[10px] bg-[#FBE7E1] px-3.5 py-2.5 text-[12.5px] font-semibold text-[#B3402C]">
          {error}
        </div>
      )}

      {confirming ? (
        <div className="rounded-[10px] bg-amber-soft px-3.5 py-2.5 text-[12.5px] font-semibold text-amber-text">
          {OPENING.confirm(changes.length)}
        </div>
      ) : (
        <p className="m-0 text-[12.5px] leading-6 text-muted">{OPENING.intro}</p>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{OPENING.site}</label>
        <select
          value={siteId}
          onChange={(e) => {
            setConfirming(false);
            setSiteId(e.target.value);
          }}
          className={inputClass}
        >
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name_ar}
            </option>
          ))}
        </select>
      </div>

      {items.length === 0 ? (
        <p className="m-0 py-6 text-center text-[13px] text-faint">{OPENING.noItems}</p>
      ) : (
        <div className="overflow-hidden rounded-[10px] border border-border">
          <table className="w-full border-collapse text-[12.5px]">
            <thead>
              <tr className="bg-sand text-right text-[11.5px] font-bold text-muted">
                <th className="px-3 py-2 font-bold">{OPENING.colItem}</th>
                <th className="w-[110px] px-3 py-2 font-bold">{OPENING.colCurrent}</th>
                <th className="w-[120px] px-3 py-2 font-bold">{OPENING.colActual}</th>
                <th className="w-[90px] px-3 py-2 font-bold">{OPENING.colDiff}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => {
                const now = current[item.id] ?? 0;
                const raw = counted[item.id] ?? '';
                const typed = raw.trim() !== '' && Number.isFinite(Number(raw));
                const delta = typed ? Math.round((Number(raw) - now) * 1000) / 1000 : 0;
                return (
                  <tr key={item.id} className="border-t border-border">
                    <td className="px-3 py-1.5 font-semibold">{item.name_ar}</td>
                    <td className="px-3 py-1.5 text-muted">
                      {loading ? '…' : formatQty(now, UNIT_LABEL[item.unit_type])}
                    </td>
                    <td className="px-3 py-1.5">
                      <NumberInput
                        value={raw}
                        onChange={(e) => {
                          // Editing after asking to confirm invalidates the
                          // confirmation — never save a set they stopped seeing.
                          setConfirming(false);
                          setCounted((prev) => ({ ...prev, [item.id]: e.target.value }));
                        }}
                        className="w-full rounded-lg border border-border bg-white px-2 py-1.5 text-[13px] text-ink outline-none focus:border-teal"
                      />
                    </td>
                    <td
                      className={`px-3 py-1.5 font-bold ${
                        delta > 0 ? 'text-success-text' : delta < 0 ? 'text-[#B3402C]' : 'text-faint'
                      }`}
                    >
                      {delta === 0 ? '—' : `${delta > 0 ? '+' : ''}${delta.toLocaleString('ar-EG')}`}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{OPENING.note}</label>
        <input
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder={OPENING.notePlaceholder}
          className={inputClass}
        />
      </div>
    </Modal>
  );
}
