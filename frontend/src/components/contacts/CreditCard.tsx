import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { MoneyDisplay, formatMoney } from '@/components/shared/MoneyDisplay';
import { PermGate } from '@/components/shared/PermGate';
import { useToast } from '@/components/shared/Toast';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort } from '@/lib/date';
import { CREDIT, COMMON, PAYMENT_METHOD_LABEL } from '@/labels';
import { depositCredit, refundCredit, getCreditBalance, listCreditMovements } from '@/services/credit';
import type { CreditSource, CreditTender, UUID } from '@/types/database';

const SOURCE_LABEL: Record<CreditSource, string> = {
  overpayment: CREDIT.source_overpayment,
  deposit: CREDIT.source_deposit,
  applied: CREDIT.source_applied,
  refund: CREDIT.source_refund,
  adjustment: CREDIT.source_adjustment,
};

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

type Mode = 'deposit' | 'refund';

interface CreditCardProps {
  contactId: UUID;
  contactName: string;
  onChanged?: () => void;
}

export function CreditCard({ contactId, contactName, onChanged }: CreditCardProps) {
  const { show } = useToast();
  const queryClient = useQueryClient();
  const { sites } = useSite();

  const [mode, setMode] = useState<Mode | null>(null);
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState<CreditTender>('cash');
  const [siteId, setSiteId] = useState(sites[0]?.id ?? '');
  const [note, setNote] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const { data: balance = 0 } = useQuery({
    queryKey: ['client-credit', contactId],
    queryFn: () => getCreditBalance(contactId),
  });
  const { data: movements = [] } = useQuery({
    queryKey: ['client-credit-movements', contactId],
    queryFn: () => listCreditMovements(contactId),
  });

  function openModal(m: Mode) {
    setMode(m);
    setAmount('');
    setMethod('cash');
    setSiteId(sites[0]?.id ?? '');
    setNote('');
  }

  function refresh() {
    queryClient.invalidateQueries({ queryKey: ['client-credit', contactId] });
    queryClient.invalidateQueries({ queryKey: ['client-credit-movements', contactId] });
    queryClient.invalidateQueries({ queryKey: ['contact-ledger', contactId] });
    onChanged?.();
  }

  async function handleSubmit() {
    const amt = Number(amount);
    if (!amt || amt <= 0) {
      show(CREDIT.amount, 'error');
      return;
    }
    if (mode === 'refund' && amt > balance + 0.005) {
      show(CREDIT.refundExceeds, 'error');
      return;
    }
    if (method === 'cash' && !siteId) {
      show(CREDIT.drawerSite, 'error');
      return;
    }
    setSubmitting(true);
    try {
      const payload = {
        contactId,
        amount: amt,
        method,
        siteId: method === 'cash' ? siteId : null,
        note: note.trim() || null,
      };
      if (mode === 'deposit') {
        await depositCredit(payload);
        show(CREDIT.depositDone, 'success');
      } else {
        await refundCredit(payload);
        show(CREDIT.refundDone, 'success');
      }
      refresh();
      setMode(null);
    } catch (err) {
      show(err instanceof Error ? err.message : CREDIT.genericError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="mb-4 rounded-card border border-border bg-white p-5">
      <div className="mb-3.5 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="m-0 text-[15px] font-bold">{CREDIT.title}</h3>
          <p className="m-0 mt-0.5 text-[12px] text-muted">{CREDIT.subtitle}</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={() => openModal('deposit')}
            className="rounded-[10px] border border-teal bg-white px-3.5 py-2 text-[12.5px] font-bold text-teal hover:bg-teal-soft"
          >
            + {CREDIT.deposit}
          </button>
          <PermGate need="payments.credit">
            <button
              onClick={() => openModal('refund')}
              disabled={balance <= 0.005}
              className="rounded-[10px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-muted disabled:opacity-50"
            >
              {CREDIT.refund}
            </button>
          </PermGate>
        </div>
      </div>

      <div className="mb-3 rounded-xl bg-teal-soft px-5 py-3.5">
        <div className="mb-1 text-[12px] font-semibold text-muted">{CREDIT.balance}</div>
        <div className="text-[24px] font-bold text-teal">{formatMoney(balance)}</div>
      </div>

      {movements.length > 0 ? (
        <div>
          {movements.map((m) => {
            const positive = m.amount_delta > 0;
            return (
              <div key={m.id} className="flex items-center gap-3 border-b border-border-soft py-2.5 last:border-b-0">
                <span className="w-16 flex-shrink-0 text-[12px] text-muted">{formatDateShort(m.occurred_on)}</span>
                <span className="min-w-0 flex-1">
                  <span className="block text-[13px] font-semibold">{SOURCE_LABEL[m.source_type]}</span>
                  <span className="text-[11.5px] text-muted">
                    {m.method ? PAYMENT_METHOD_LABEL[m.method] : '—'}
                    {m.siteName ? ` · ${m.siteName}` : ''}
                    {m.actorName ? ` · ${CREDIT.by} ${m.actorName}` : ''}
                    {m.note ? ` · ${m.note}` : ''}
                  </span>
                </span>
                <span className={`flex-shrink-0 text-[13.5px] font-bold ${positive ? 'text-success-text' : 'text-[#B3402C]'}`}>
                  {positive ? '+' : '−'}
                  <MoneyDisplay amount={Math.abs(m.amount_delta)} />
                </span>
              </div>
            );
          })}
        </div>
      ) : (
        <div className="py-4 text-center text-[13px] text-faint">{CREDIT.noMovements}</div>
      )}

      <Modal
        open={mode !== null}
        onClose={() => setMode(null)}
        title={mode === 'refund' ? CREDIT.refundTitle : CREDIT.depositTitle}
        width="400px"
        footer={
          <>
            <button
              onClick={() => setMode(null)}
              className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
            >
              {COMMON.cancel}
            </button>
            <button
              onClick={handleSubmit}
              disabled={submitting}
              className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
            >
              {mode === 'refund' ? CREDIT.refund : CREDIT.deposit}
            </button>
          </>
        }
      >
        {mode === 'refund' && (
          <div className="mb-1 text-[12.5px] text-muted">
            {CREDIT.balance}: <span className="font-bold text-teal">{formatMoney(balance)}</span> · {contactName}
          </div>
        )}
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CREDIT.amount}</label>
          <input
            type="number"
            inputMode="decimal"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            placeholder={CREDIT.amount}
            className={`${inputClass} font-bold`}
          />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CREDIT.method}</label>
          <select value={method} onChange={(e) => setMethod(e.target.value as CreditTender)} className={inputClass}>
            <option value="cash">{PAYMENT_METHOD_LABEL.cash}</option>
            <option value="instapay">{PAYMENT_METHOD_LABEL.instapay}</option>
          </select>
        </div>
        {method === 'cash' && (
          <div>
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CREDIT.drawerSite}</label>
            <select value={siteId} onChange={(e) => setSiteId(e.target.value)} className={inputClass}>
              {sites.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name_ar}
                </option>
              ))}
            </select>
          </div>
        )}
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CREDIT.note}</label>
          <input value={note} onChange={(e) => setNote(e.target.value)} className={inputClass} />
        </div>
      </Modal>
    </div>
  );
}
