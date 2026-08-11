import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { MoneyDisplay, formatMoney } from '@/components/shared/MoneyDisplay';
import { PermGate } from '@/components/shared/PermGate';
import { useToast } from '@/components/shared/Toast';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort } from '@/lib/date';
import { VENDOR_CREDIT, COMMON, PAYMENT_METHOD_LABEL } from '@/labels';
import {
  depositVendorCredit,
  refundVendorCredit,
  getVendorCreditBalance,
  listVendorCreditMovements,
} from '@/services/vendorCredit';
import type { CreditSource, CreditTender, UUID } from '@/types/database';
import { NumberInput } from '@/components/shared/NumberInput';

const SOURCE_LABEL: Record<CreditSource, string> = {
  overpayment: VENDOR_CREDIT.source_overpayment,
  deposit: VENDOR_CREDIT.source_deposit,
  applied: VENDOR_CREDIT.source_applied,
  refund: VENDOR_CREDIT.source_refund,
  adjustment: VENDOR_CREDIT.source_adjustment,
};

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

type Mode = 'deposit' | 'refund';

interface VendorCreditCardProps {
  contactId: UUID;
  contactName: string;
  onChanged?: () => void;
}

/**
 * الدفعات المقدّمة للمورّد (عربون) — the vendor mirror of the client credit
 * wallet. Money we PREPAY a vendor before, or beyond, any purchase order; it is
 * later deducted from POs or refunded. Cash direction is flipped from the client
 * side: a deposit here leaves the drawer, a refund puts money back in.
 */
export function VendorCreditCard({ contactId, contactName, onChanged }: VendorCreditCardProps) {
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
    queryKey: ['vendor-credit', contactId],
    queryFn: () => getVendorCreditBalance(contactId),
  });
  const { data: movements = [] } = useQuery({
    queryKey: ['vendor-credit-movements', contactId],
    queryFn: () => listVendorCreditMovements(contactId),
  });

  function openModal(m: Mode) {
    setMode(m);
    setAmount('');
    setMethod('cash');
    setSiteId(sites[0]?.id ?? '');
    setNote('');
  }

  function refresh() {
    queryClient.invalidateQueries({ queryKey: ['vendor-credit', contactId] });
    queryClient.invalidateQueries({ queryKey: ['vendor-credit-movements', contactId] });
    queryClient.invalidateQueries({ queryKey: ['contact-ledger', contactId] });
    onChanged?.();
  }

  async function handleSubmit() {
    const amt = Number(amount);
    if (!amt || amt <= 0) {
      show(VENDOR_CREDIT.amount, 'error');
      return;
    }
    if (mode === 'refund' && amt > balance + 0.005) {
      show(VENDOR_CREDIT.refundExceeds, 'error');
      return;
    }
    if (method === 'cash' && !siteId) {
      show(VENDOR_CREDIT.drawerSite, 'error');
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
        await depositVendorCredit(payload);
        show(VENDOR_CREDIT.depositDone, 'success');
      } else {
        await refundVendorCredit(payload);
        show(VENDOR_CREDIT.refundDone, 'success');
      }
      refresh();
      setMode(null);
    } catch (err) {
      show(err instanceof Error ? err.message : VENDOR_CREDIT.genericError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="mb-4 rounded-card border border-border bg-white p-5">
      <div className="mb-3.5 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="m-0 text-[15px] font-bold">{VENDOR_CREDIT.title}</h3>
          <p className="m-0 mt-0.5 text-[12px] text-muted">{VENDOR_CREDIT.subtitle}</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={() => openModal('deposit')}
            className="rounded-[10px] border border-teal bg-white px-3.5 py-2 text-[12.5px] font-bold text-teal hover:bg-teal-soft"
          >
            + {VENDOR_CREDIT.deposit}
          </button>
          <PermGate need="payments.credit">
            <button
              onClick={() => openModal('refund')}
              disabled={balance <= 0.005}
              className="rounded-[10px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-muted disabled:opacity-50"
            >
              {VENDOR_CREDIT.refund}
            </button>
          </PermGate>
        </div>
      </div>

      <div className="mb-3 rounded-xl bg-amber-soft px-5 py-3.5">
        <div className="mb-1 text-[12px] font-semibold text-muted">{VENDOR_CREDIT.balance}</div>
        <div className="text-[24px] font-bold text-amber-text">{formatMoney(balance)}</div>
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
                    {m.actorName ? ` · ${VENDOR_CREDIT.by} ${m.actorName}` : ''}
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
        <div className="py-4 text-center text-[13px] text-faint">{VENDOR_CREDIT.noMovements}</div>
      )}

      <Modal
        open={mode !== null}
        onClose={() => setMode(null)}
        title={mode === 'refund' ? VENDOR_CREDIT.refundTitle : VENDOR_CREDIT.depositTitle}
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
              {mode === 'refund' ? VENDOR_CREDIT.refund : VENDOR_CREDIT.deposit}
            </button>
          </>
        }
      >
        {mode === 'refund' && (
          <div className="mb-1 text-[12.5px] text-muted">
            {VENDOR_CREDIT.balance}: <span className="font-bold text-amber-text">{formatMoney(balance)}</span> · {contactName}
          </div>
        )}
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{VENDOR_CREDIT.amount}</label>
          <NumberInput
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            placeholder={VENDOR_CREDIT.amount}
            className={`${inputClass} font-bold`}
          />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{VENDOR_CREDIT.method}</label>
          <select value={method} onChange={(e) => setMethod(e.target.value as CreditTender)} className={inputClass}>
            <option value="cash">{PAYMENT_METHOD_LABEL.cash}</option>
            <option value="instapay">{PAYMENT_METHOD_LABEL.instapay}</option>
          </select>
        </div>
        {method === 'cash' && (
          <div>
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{VENDOR_CREDIT.drawerSite}</label>
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
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{VENDOR_CREDIT.note}</label>
          <input value={note} onChange={(e) => setNote(e.target.value)} className={inputClass} />
        </div>
      </Modal>
    </div>
  );
}
