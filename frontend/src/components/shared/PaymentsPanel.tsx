import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { MoneyDisplay, formatMoney } from './MoneyDisplay';
import { ConfirmDialog } from './ConfirmDialog';
import { ArabicDatePicker } from './ArabicDatePicker';
import { PermGate, usePerm } from './PermGate';
import { useToast } from './Toast';
import { formatDateShort, todayISODate } from '@/lib/date';
import { PAYMENTS, PAYMENT_METHOD_LABEL, COMMON } from '@/labels';
import {
  addPayment,
  deletePayment,
  listPayments,
  listContactPaymentMethods,
  paymentsTotal,
} from '@/services/payments';
import { applyCredit, getCreditBalance, overpaySalesOrder } from '@/services/credit';
import {
  applyVendorCredit,
  getVendorCreditBalance,
  overpayPurchaseOrder,
} from '@/services/vendorCredit';
import type { ContactPaymentMethod, PaymentMethod, PaymentParent, Site, UUID } from '@/types/database';

interface PaymentsPanelProps {
  parentType: PaymentParent;
  parentId: UUID;
  /** The order total; remaining = total − Σ payments. */
  total: number;
  sites: Site[];
  /**
   * When set, cash payments silently hit this drawer (SO case — its own site).
   * When omitted, a cash payment shows a drawer-site selector (PO case).
   */
  fixedCashSiteId?: UUID;
  /**
   * The order's contact (client for SO, vendor for PO). Drives the instapay/
   * bank-transfer account picker; for an SO it also enables client credit.
   */
  contactId?: UUID;
  contactName?: string;
  /** Called after any successful add/delete so the parent can refetch its own derived data. */
  onChanged?: () => void;
  /**
   * When false, hides the add-payment form and shows `cannotAddNote` instead
   * (draft SOs — the DB rejects payments on drafts, so don't offer the form).
   */
  canAdd?: boolean;
  cannotAddNote?: string;
}

// Real tenders the user can pick. 'credit' is applied via the dedicated flow.
const METHODS: PaymentMethod[] = ['cash', 'instapay', 'bank_transfer'];

const inputClass =
  'w-full rounded-[9px] border border-border bg-white px-3 py-2 text-[13px] text-ink outline-none focus:border-teal';

/** Human label for a stored account (the number / bank the transfer used). */
function accountLabel(m: Pick<ContactPaymentMethod, 'method' | 'instapay_number' | 'bank_name' | 'account_number'>): string {
  if (m.method === 'instapay') return m.instapay_number ?? '';
  if (m.method === 'bank_transfer') return [m.bank_name, m.account_number].filter(Boolean).join(' · ');
  return '';
}

export function PaymentsPanel({
  parentType,
  parentId,
  total,
  sites,
  fixedCashSiteId,
  contactId,
  contactName,
  onChanged,
  canAdd = true,
  cannotAddNote,
}: PaymentsPanelProps) {
  const { show } = useToast();
  const queryClient = useQueryClient();
  const canUseCreditPerm = usePerm('payments.credit');

  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState<PaymentMethod>('cash');
  const [methodRefId, setMethodRefId] = useState('');
  const [date, setDate] = useState(todayISODate());
  const [note, setNote] = useState('');
  const [cashSiteId, setCashSiteId] = useState(fixedCashSiteId ?? sites[0]?.id ?? '');
  const [submitting, setSubmitting] = useState(false);
  const [deleteId, setDeleteId] = useState<UUID | null>(null);
  const [overpayAmount, setOverpayAmount] = useState<number | null>(null);
  const [useCreditOpen, setUseCreditOpen] = useState(false);
  const [creditAmount, setCreditAmount] = useState('');

  // Both order types carry a wallet: SO → client credit, PO → vendor advance.
  const isSO = parentType === 'so';
  const walletKey = isSO ? 'client-credit' : 'vendor-credit';
  const creditEnabled = !!contactId;

  // Sites load async; on a hard refresh straight into a detail page the initial
  // useState ran with an empty list — backfill the default once they arrive.
  useEffect(() => {
    if (!cashSiteId && sites.length > 0) setCashSiteId(fixedCashSiteId ?? sites[0].id);
  }, [sites, cashSiteId, fixedCashSiteId]);

  const { data: payments = [] } = useQuery({
    queryKey: ['payments', parentType, parentId],
    queryFn: () => listPayments(parentType, parentId),
  });

  const { data: contactMethods = [] } = useQuery({
    queryKey: ['contact-payment-methods', contactId],
    queryFn: () => listContactPaymentMethods(contactId!),
    enabled: !!contactId,
  });

  const { data: creditBalance = 0 } = useQuery({
    queryKey: [walletKey, contactId],
    queryFn: () => (isSO ? getCreditBalance(contactId!) : getVendorCreditBalance(contactId!)),
    enabled: creditEnabled,
  });

  // instapay/bank payments must name which stored account they hit.
  const needsAccount = method === 'instapay' || method === 'bank_transfer';
  const accountsForMethod = contactMethods.filter((m) => m.method === method);

  // Default the account picker to the first match whenever the method changes.
  useEffect(() => {
    if (method === 'instapay' || method === 'bank_transfer') {
      const first = contactMethods.find((m) => m.method === method);
      setMethodRefId(first?.id ?? '');
    } else {
      setMethodRefId('');
    }
  }, [method, contactMethods]);

  const paid = paymentsTotal(payments);
  const remaining = Math.max(0, total - paid);
  const needsDrawerSelector = method === 'cash' && !fixedCashSiteId;
  const canUseCredit = creditEnabled && canUseCreditPerm && creditBalance > 0.005 && remaining > 0.005;

  function refresh() {
    queryClient.invalidateQueries({ queryKey: ['payments', parentType, parentId] });
    if (contactId) queryClient.invalidateQueries({ queryKey: [walletKey, contactId] });
    onChanged?.();
  }

  /** Guard the instapay/bank account selection; returns false (and toasts) if invalid. */
  function accountValid(): boolean {
    if (!needsAccount) return true;
    if (accountsForMethod.length === 0) {
      show(PAYMENTS.noAccountForMethod, 'error');
      return false;
    }
    if (!methodRefId) {
      show(PAYMENTS.selectAccount, 'error');
      return false;
    }
    return true;
  }

  async function doAddPayment(amountNum: number) {
    setSubmitting(true);
    try {
      await addPayment({
        parent_type: parentType,
        parent_id: parentId,
        amount: amountNum,
        method,
        // Only cash payments touch a drawer; instapay/bank carry no site.
        site_id: method === 'cash' ? (fixedCashSiteId ?? cashSiteId) : null,
        contact_payment_method_id: needsAccount ? methodRefId : null,
        paid_at: date,
        note: note.trim() || null,
      });
      show(PAYMENTS.success, 'success');
      setAmount('');
      setNote('');
      refresh();
    } catch (err) {
      show(err instanceof Error ? err.message : PAYMENTS.genericError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  function handleAdd() {
    const amountNum = Number(amount);
    if (!amountNum || amountNum <= 0) {
      show(PAYMENTS.amountRequired, 'error');
      return;
    }
    if (method === 'cash' && !fixedCashSiteId && !cashSiteId) {
      show(PAYMENTS.cashSiteRequired, 'error');
      return;
    }
    if (!accountValid()) return;
    if (amountNum > remaining + 0.005) {
      // SO → bank the excess as client credit; PO → as a vendor advance.
      if (creditEnabled) {
        setOverpayAmount(amountNum);
        return;
      }
      show(PAYMENTS.overpay, 'error');
      return;
    }
    void doAddPayment(amountNum);
  }

  async function confirmOverpay() {
    if (overpayAmount == null) return;
    setSubmitting(true);
    try {
      const common = {
        amount: overpayAmount,
        method: (method === 'credit' ? 'cash' : method) as Exclude<PaymentMethod, 'credit'>,
        siteId: method === 'cash' ? (fixedCashSiteId ?? cashSiteId) : null,
        methodRef: needsAccount ? methodRefId : null,
        paidAt: date,
        note: note.trim() || null,
      };
      const { creditAdded } = isSO
        ? await overpaySalesOrder({ soId: parentId, ...common })
        : await overpayPurchaseOrder({ poId: parentId, ...common });
      show((isSO ? PAYMENTS.overpaid : PAYMENTS.overpaidVendor)(formatMoney(creditAdded)), 'success');
      setAmount('');
      setNote('');
      refresh();
    } catch (err) {
      show(err instanceof Error ? err.message : PAYMENTS.genericError, 'error');
    } finally {
      setSubmitting(false);
      setOverpayAmount(null);
    }
  }

  async function handleApplyCredit() {
    const amt = Number(creditAmount);
    if (!amt || amt <= 0) {
      show(PAYMENTS.amountRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      if (isSO) await applyCredit({ soId: parentId, amount: amt, paidAt: date, note: null });
      else await applyVendorCredit({ poId: parentId, amount: amt, paidAt: date, note: null });
      show(isSO ? PAYMENTS.applied : PAYMENTS.appliedVendor, 'success');
      setCreditAmount('');
      setUseCreditOpen(false);
      refresh();
    } catch (err) {
      show(err instanceof Error ? err.message : PAYMENTS.genericError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  async function handleDelete() {
    if (!deleteId) return;
    try {
      await deletePayment(deleteId);
      show(PAYMENTS.deleted, 'success');
      refresh();
    } catch (err) {
      show(err instanceof Error ? err.message : PAYMENTS.deleteError, 'error');
    } finally {
      setDeleteId(null);
    }
  }

  return (
    <div className="rounded-card border border-border bg-white p-5">
      <h3 className="m-0 mb-3.5 text-[15px] font-bold">{PAYMENTS.title}</h3>

      <div className="mb-3 flex justify-between rounded-[10px] bg-row-alt px-3 py-2.5">
        <div>
          <div className="text-[11.5px] text-muted">{PAYMENTS.paid}</div>
          <div className="text-sm font-bold text-success-text">{formatMoney(paid)}</div>
        </div>
        <div className="text-left">
          <div className="text-[11.5px] text-muted">{PAYMENTS.remaining}</div>
          <div className="text-sm font-bold text-amber-text">{formatMoney(remaining)}</div>
        </div>
      </div>

      {payments.length > 0 ? (
        payments.map((p) => (
          <div key={p.id} className="flex items-center gap-3 border-b border-border-soft py-2.5 last:border-b-0">
            <span className="w-16 flex-shrink-0 text-[12.5px] text-muted">{formatDateShort(p.paid_at)}</span>
            <span className="min-w-0 flex-shrink-0">
              <span className="block rounded-pill bg-amber-soft px-2.5 py-1 text-center text-[12px] font-bold text-amber-soft-text">
                {PAYMENT_METHOD_LABEL[p.method]}
              </span>
              {p.methodDetail && accountLabel(p.methodDetail) && (
                <span dir="ltr" className="mt-0.5 block truncate text-[10.5px] text-muted">
                  {accountLabel(p.methodDetail)}
                </span>
              )}
            </span>
            <span className="flex-1 text-left">
              <MoneyDisplay amount={Number(p.amount)} className="text-[13.5px]" />
            </span>
            <PermGate need="payments.delete">
              <button
                onClick={() => setDeleteId(p.id)}
                aria-label={PAYMENTS.deleteConfirmTitle}
                className="flex-shrink-0 text-muted hover:text-red-600"
              >
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="3 6 5 6 21 6" />
                  <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" />
                  <path d="M10 11v6M14 11v6" />
                </svg>
              </button>
            </PermGate>
          </div>
        ))
      ) : (
        <div className="py-4 text-center text-[13px] text-faint">{PAYMENTS.noPayments}</div>
      )}

      {!canAdd && cannotAddNote && (
        <div className="mt-3.5 rounded-[10px] bg-row-alt px-3.5 py-2.5 text-center text-[12.5px] text-muted">
          {cannotAddNote}
        </div>
      )}

      {canAdd && remaining > 0.005 && (
        <div className="mt-3.5 flex flex-col gap-2 border-t border-border-soft pt-3.5">
          <div>
            <label className="mb-1 block text-[11.5px] font-semibold text-muted">{PAYMENTS.amount}</label>
            <input
              type="number"
              inputMode="decimal"
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              placeholder={PAYMENTS.amount}
              className={`${inputClass} py-2.5 text-[15px] font-bold`}
            />
          </div>

          <div>
            <label className="mb-1 block text-[11.5px] font-semibold text-muted">{PAYMENTS.method}</label>
            <select value={method} onChange={(e) => setMethod(e.target.value as PaymentMethod)} className={inputClass}>
              {METHODS.map((m) => (
                <option key={m} value={m}>
                  {PAYMENT_METHOD_LABEL[m]}
                </option>
              ))}
            </select>
          </div>

          {needsAccount && (
            <div>
              <label className="mb-1 block text-[11.5px] font-semibold text-muted">
                {method === 'instapay' ? PAYMENTS.instapayAccount : PAYMENTS.bankAccount}
              </label>
              {accountsForMethod.length > 0 ? (
                <select value={methodRefId} onChange={(e) => setMethodRefId(e.target.value)} className={inputClass}>
                  {accountsForMethod.map((m) => (
                    <option key={m.id} value={m.id}>
                      {accountLabel(m)}
                    </option>
                  ))}
                </select>
              ) : (
                <div className="rounded-[9px] bg-amber-soft px-3 py-2 text-[12px] font-semibold text-amber-soft-text">
                  {PAYMENTS.noAccountForMethod}
                </div>
              )}
            </div>
          )}

          {needsDrawerSelector && (
            <div>
              <label className="mb-1 block text-[11.5px] font-semibold text-muted">{PAYMENTS.drawerSite}</label>
              <select value={cashSiteId} onChange={(e) => setCashSiteId(e.target.value)} className={inputClass}>
                {sites.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name_ar}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div className="flex items-center gap-2">
            <ArabicDatePicker value={date} onChange={setDate} />
            <input
              value={note}
              onChange={(e) => setNote(e.target.value)}
              placeholder={PAYMENTS.note}
              className={inputClass}
            />
          </div>

          <button
            onClick={handleAdd}
            disabled={submitting}
            className="rounded-[9px] border-none bg-teal py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {PAYMENTS.addPayment}
          </button>
        </div>
      )}

      {canUseCredit && (
        <div className="mt-3 rounded-[10px] border border-teal-soft bg-teal-soft p-3">
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-[12px] font-semibold text-teal">
              {isSO ? PAYMENTS.creditAvailable : PAYMENTS.advanceAvailable}
            </span>
            <span className="text-[13px] font-bold text-teal">{formatMoney(creditBalance)}</span>
          </div>
          {!useCreditOpen ? (
            <button
              onClick={() => {
                setCreditAmount(String(Math.min(creditBalance, remaining)));
                setUseCreditOpen(true);
              }}
              className="w-full rounded-[9px] border border-teal bg-white py-2 text-[12.5px] font-bold text-teal hover:bg-teal-soft"
            >
              {isSO ? PAYMENTS.useCredit : PAYMENTS.useAdvance}
            </button>
          ) : (
            <div className="flex flex-col gap-2">
              <input
                type="number"
                inputMode="decimal"
                value={creditAmount}
                onChange={(e) => setCreditAmount(e.target.value)}
                placeholder={isSO ? PAYMENTS.applyAmount : PAYMENTS.applyAmountVendor}
                className={`${inputClass} font-bold`}
              />
              <div className="flex gap-2">
                <button
                  onClick={handleApplyCredit}
                  disabled={submitting}
                  className="flex-1 rounded-[9px] border-none bg-teal py-2 text-[12.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
                >
                  {PAYMENTS.applyProceed}
                </button>
                <button
                  onClick={() => setUseCreditOpen(false)}
                  className="rounded-[9px] border border-border bg-white px-3 py-2 text-[12.5px] font-bold text-muted"
                >
                  {COMMON.cancel}
                </button>
              </div>
            </div>
          )}
        </div>
      )}

      <ConfirmDialog
        open={overpayAmount !== null}
        title={PAYMENTS.overpayTitle}
        message={(isSO ? PAYMENTS.overpayConfirm : PAYMENTS.overpayConfirmVendor)(
          formatMoney(Math.max(0, (overpayAmount ?? 0) - remaining)),
          contactName ?? '',
        )}
        confirmLabel={PAYMENTS.overpayProceed}
        onConfirm={confirmOverpay}
        onCancel={() => setOverpayAmount(null)}
      />

      <ConfirmDialog
        open={deleteId !== null}
        title={PAYMENTS.deleteConfirmTitle}
        message={PAYMENTS.deleteConfirmMsg}
        onConfirm={handleDelete}
        onCancel={() => setDeleteId(null)}
        danger
      />
    </div>
  );
}
