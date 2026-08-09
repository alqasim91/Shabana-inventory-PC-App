import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { REPORTS, COMMON } from '@/labels';
import { createManualCashMovement, type ManualCashKind } from '@/services/reports';
import type { Site, UUID } from '@/types/database';

interface ManualCashModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  sites: Site[];
  defaultSiteId?: UUID | null;
}

const KINDS: { value: ManualCashKind; label: string }[] = [
  { value: 'deposit', label: REPORTS.manualDeposit },
  { value: 'withdraw', label: REPORTS.manualWithdraw },
  { value: 'adjust', label: REPORTS.manualAdjust },
];

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

/** Manual drawer movement (rule 5): إيداع / سحب / تسوية, mandatory reason. Admin only (RLS-enforced). */
export function ManualCashModal({ open, onClose, onSaved, sites, defaultSiteId }: ManualCashModalProps) {
  const { show } = useToast();

  const [siteId, setSiteId] = useState('');
  const [kind, setKind] = useState<ManualCashKind>('deposit');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setSiteId(defaultSiteId ?? sites[0]?.id ?? '');
    setKind('deposit');
    setAmount('');
    setReason('');
    setError(null);
  }, [open, defaultSiteId, sites]);

  async function handleSubmit() {
    const amountNum = Number(amount);
    if (!amountNum) {
      setError(REPORTS.manualAmountRequired);
      return;
    }
    if (!reason.trim()) {
      setError(REPORTS.manualReasonRequired);
      return;
    }
    setError(null);

    setSubmitting(true);
    try {
      await createManualCashMovement({ site_id: siteId, kind, amount: amountNum, reason: reason.trim() });
      show(REPORTS.manualSuccess, 'success');
      onSaved();
      onClose();
    } catch (err) {
      // The DB floor guard rejects a withdrawal beyond the drawer balance — surface its Arabic message.
      show(err instanceof Error ? err.message : REPORTS.manualError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={REPORTS.manualMovement}
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
            {COMMON.save}
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
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{REPORTS.manualSiteLabel}</label>
        <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name_ar}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{REPORTS.manualType}</label>
        <div className="flex gap-1.5">
          {KINDS.map((k) => (
            <button
              key={k.value}
              type="button"
              onClick={() => setKind(k.value)}
              className={`flex-1 rounded-[9px] border px-3 py-2 text-[12.5px] font-bold ${
                kind === k.value ? 'border-teal bg-teal text-white' : 'border-border bg-white text-muted'
              }`}
            >
              {k.label}
            </button>
          ))}
        </div>
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{REPORTS.manualAmount}</label>
        <input type="number" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} className={inputClass} />
        {kind === 'adjust' && <p className="mt-1 text-[11.5px] text-faint">{REPORTS.manualAmountHint}</p>}
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{REPORTS.manualReason}</label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={2}
          placeholder={REPORTS.manualReasonPlaceholder}
          className={`${inputClass} resize-none`}
        />
      </div>
    </Modal>
  );
}
