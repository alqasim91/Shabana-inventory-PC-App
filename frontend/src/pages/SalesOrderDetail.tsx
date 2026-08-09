import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PermGate } from '@/components/shared/PermGate';
import { formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { PaymentsPanel } from '@/components/shared/PaymentsPanel';
import { ConfirmDialog } from '@/components/shared/ConfirmDialog';
import { useToast } from '@/components/shared/Toast';
import { SalesOrderFormModal } from '@/components/sales/SalesOrderFormModal';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort } from '@/lib/date';
import { formatSoNo } from '@/lib/orderNo';
import { SALES, SO_STATUS_LABEL, UNIT_LABEL, COMMON } from '@/labels';
import {
  cancelPlacement,
  getSalesOrder,
  invoiceSalesOrder,
  placeSalesOrder,
} from '@/services/sales';
import { listPayments, paymentsTotal } from '@/services/payments';
import type { SoStatus } from '@/types/database';

const STATUS_CLASS: Record<SoStatus, string> = {
  draft: 'bg-row-alt text-muted',
  invoiced: 'bg-teal-soft text-teal',
  placed: 'bg-amber-soft text-amber-text',
  closed: 'bg-success-soft text-success-text',
};

type LifecycleAction = 'invoice' | 'place' | 'cancel';

export function SalesOrderDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { show } = useToast();
  const { sites } = useSite();
  const [confirm, setConfirm] = useState<LifecycleAction | null>(null);
  const [showEdit, setShowEdit] = useState(false);
  const [busy, setBusy] = useState(false);

  const { data: so, isLoading } = useQuery({
    queryKey: ['sales-order', id],
    queryFn: () => getSalesOrder(id!),
    enabled: !!id,
  });

  const { data: payments = [] } = useQuery({
    queryKey: ['payments', 'so', id],
    queryFn: () => listPayments('so', id!),
    enabled: !!id,
  });

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['sales-order', id] });
    queryClient.invalidateQueries({ queryKey: ['sales-orders'] });
    queryClient.invalidateQueries({ queryKey: ['inventory-rows'] });
  }

  async function runAction() {
    if (!confirm || !so) return;
    setBusy(true);
    try {
      if (confirm === 'invoice') {
        await invoiceSalesOrder(so.id);
        show(SALES.invoiced, 'success');
      } else if (confirm === 'place') {
        await placeSalesOrder(so.id);
        show(SALES.placed, 'success');
      } else {
        await cancelPlacement(so.id);
        show(SALES.cancelled, 'success');
      }
      invalidate();
    } catch (err) {
      const fallback =
        confirm === 'invoice' ? SALES.invoiceError : confirm === 'place' ? SALES.placeError : SALES.cancelError;
      show(err instanceof Error ? err.message : fallback, 'error');
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  }

  if (isLoading || !so) {
    return <p className="text-sm text-faint">{COMMON.loading}</p>;
  }

  const paid = paymentsTotal(payments);
  const remaining = Math.max(0, Number(so.total_amount) - paid);
  const discount = Number(so.discount_amount ?? 0);
  const fullyPaid = remaining <= 0.005 && Number(so.total_amount) > 0;
  const canEditDraft = so.status === 'draft' && payments.length === 0;

  return (
    <div>
      <button
        onClick={() => navigate('/sales')}
        className="mb-4 inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
      >
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <line x1="5" y1="12" x2="19" y2="12" />
          <polyline points="13,6 19,12 13,18" />
        </svg>
        <span>{SALES.backToList}</span>
      </button>

      <div className="mb-5 flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="mb-1.5 flex items-center gap-3">
            <h1 className="m-0 text-[22px] font-bold">{formatSoNo(so.order_seq)}</h1>
            <span className={`rounded-pill px-3 py-1 text-[12.5px] font-bold ${STATUS_CLASS[so.status]}`}>
              {SO_STATUS_LABEL[so.status]}
            </span>
            {fullyPaid && (
              <span className="rounded-pill bg-success-soft px-3 py-1 text-[12.5px] font-bold text-success-text">
                {SALES.fullyPaid}
              </span>
            )}
          </div>
          <div className="text-[13.5px] text-muted">
            {so.clientName} · {so.siteName} · {formatDateShort(so.order_date)}
            {so.createdByName ? ` · ${SALES.by} ${so.createdByName}` : ''}
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {canEditDraft ? (
            <PermGate need="sales.draft">
              <button
                onClick={() => setShowEdit(true)}
                className="rounded-[9px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-ink hover:border-teal"
              >
                {SALES.editDraft}
              </button>
            </PermGate>
          ) : (
            // A locked order (invoiced/placed/closed) can still be corrected by an
            // admin — the edit safely reverses & re-applies stock server-side.
            <PermGate need="sales.edit_locked">
              <button
                onClick={() => setShowEdit(true)}
                className="rounded-[9px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-ink hover:border-teal"
              >
                {SALES.edit}
              </button>
            </PermGate>
          )}
          {so.status === 'draft' && (
            <PermGate need="sales.invoice">
              <button
                onClick={() => setConfirm('invoice')}
                className="rounded-[9px] border-none bg-teal px-3.5 py-2 text-[12.5px] font-bold text-white hover:bg-teal-hover"
              >
                {SALES.invoice}
              </button>
            </PermGate>
          )}
          {so.status === 'invoiced' && (
            <PermGate need="sales.place">
              <button
                onClick={() => setConfirm('place')}
                className="rounded-[9px] border-none bg-teal px-3.5 py-2 text-[12.5px] font-bold text-white hover:bg-teal-hover"
              >
                {SALES.place}
              </button>
            </PermGate>
          )}
          {so.status === 'placed' && (
            <PermGate need="sales.cancel_placement">
              <button
                onClick={() => setConfirm('cancel')}
                className="rounded-[9px] border border-amber-text bg-white px-3.5 py-2 text-[12.5px] font-bold text-amber-text hover:bg-amber-soft"
              >
                {SALES.cancelPlacement}
              </button>
            </PermGate>
          )}
          {so.invoice_number && (
            <button
              onClick={() => navigate(`/sales/${so.id}/invoice`)}
              className="flex items-center gap-2 rounded-[9px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-ink hover:border-teal"
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                <rect x="6" y="9" width="12" height="7" rx="1" />
                <path d="M8 9V4h8v5" />
                <path d="M8 16v4h8v-4" />
              </svg>
              <span>{SALES.printInvoice}</span>
            </button>
          )}
        </div>
      </div>

      <div className="mb-5 grid grid-cols-2 gap-4 lg:grid-cols-4">
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{SALES.total}</div>
          <div className="text-[19px] font-bold">{formatMoney(so.total_amount)}</div>
          {/* The discount is spelled out under the total rather than folded into
              it — "why is this ٩٠٠ and not ١٠٠٠" is the first thing anyone asks. */}
          {discount > 0 && (
            <div className="mt-1.5 text-[11.5px] leading-relaxed text-muted">
              {SALES.subtotal} {formatMoney(so.subtotal)}
              <br />
              <span className="font-semibold text-red-600">
                {SALES.discount}
                {so.discount_type === 'percent' ? ` ${Number(so.discount_value).toLocaleString('ar-EG')}٪` : ''} −
                {formatMoney(discount)}
              </span>
            </div>
          )}
        </div>
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{SALES.paidRemaining}</div>
          <div className="mb-0.5 text-[15px] font-bold text-success-text">{formatMoney(paid)}</div>
          <div className="text-[12.5px] font-bold text-amber-text">
            {formatMoney(remaining)} {SALES.remainingSuffix}
          </div>
        </div>
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{SALES.status}</div>
          <span className={`inline-block rounded-pill px-3 py-1 text-[13px] font-bold ${STATUS_CLASS[so.status]}`}>
            {SO_STATUS_LABEL[so.status]}
          </span>
        </div>
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{SALES.linesCount}</div>
          <div className="text-[19px] font-bold">{so.lines.length.toLocaleString('ar-EG')}</div>
        </div>
      </div>

      <div className="flex flex-wrap items-start gap-4">
        <div className="min-w-[340px] flex-1 rounded-card border border-border bg-white p-5">
          <h3 className="m-0 mb-3.5 text-[15px] font-bold">{SALES.linesTitle}</h3>

          {so.lines.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-[13px]">
                <thead>
                  <tr className="border-b border-border text-[12px] text-muted">
                    <th className="px-1.5 py-2 text-right font-semibold">{SALES.colItem}</th>
                    <th className="px-1.5 py-2 text-left font-semibold">{SALES.colQty}</th>
                    <th className="px-1.5 py-2 text-left font-semibold">{SALES.colUnitPrice}</th>
                    <th className="px-1.5 py-2 text-left font-semibold">{SALES.colLineTotal}</th>
                  </tr>
                </thead>
                <tbody>
                  {so.lines.map((l) => (
                    <tr key={l.id} className="border-b border-border-soft last:border-b-0">
                      <td className="px-1.5 py-2.5 font-semibold">{l.itemName}</td>
                      <td className="px-1.5 py-2.5 text-left text-muted">{formatQty(l.qty, UNIT_LABEL[l.unitType])}</td>
                      <td className="px-1.5 py-2.5 text-left text-muted">{formatMoney(l.unit_price)}</td>
                      <td className="px-1.5 py-2.5 text-left font-bold">{formatMoney(l.line_total)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="py-4 text-center text-[13px] text-faint">{SALES.noLines}</div>
          )}
        </div>

        <div className="min-w-[340px] flex-1">
          <PaymentsPanel
            parentType="so"
            parentId={so.id}
            total={Number(so.total_amount)}
            sites={sites}
            fixedCashSiteId={so.site_id}
            contactId={so.client_id}
            contactName={so.clientName}
            onChanged={invalidate}
            canAdd={so.status !== 'draft'}
            cannotAddNote={SALES.draftNoPayments}
          />
        </div>
      </div>

      <SalesOrderFormModal
        open={showEdit}
        onClose={() => setShowEdit(false)}
        editing={so}
        onSaved={invalidate}
      />

      <ConfirmDialog
        open={confirm === 'invoice'}
        title={SALES.invoiceConfirmTitle}
        message={SALES.invoiceConfirmMsg}
        onConfirm={runAction}
        onCancel={() => !busy && setConfirm(null)}
      />
      <ConfirmDialog
        open={confirm === 'place'}
        title={SALES.placeConfirmTitle}
        message={SALES.placeConfirmMsg}
        onConfirm={runAction}
        onCancel={() => !busy && setConfirm(null)}
      />
      <ConfirmDialog
        open={confirm === 'cancel'}
        title={SALES.cancelConfirmTitle}
        message={SALES.cancelConfirmMsg}
        onConfirm={runAction}
        onCancel={() => !busy && setConfirm(null)}
        danger
      />
    </div>
  );
}
