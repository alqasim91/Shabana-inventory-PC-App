import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PermGate } from '@/components/shared/PermGate';
import { formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { PaymentsPanel } from '@/components/shared/PaymentsPanel';
import { AttachmentsPanel } from '@/components/shared/AttachmentsPanel';
import { ConfirmDialog } from '@/components/shared/ConfirmDialog';
import { useToast } from '@/components/shared/Toast';
import { ConversionModal } from '@/components/purchases/ConversionModal';
import { ItemizedConversionModal } from '@/components/purchases/ItemizedConversionModal';
import { PurchaseOrderFormModal } from '@/components/purchases/PurchaseOrderFormModal';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort } from '@/lib/date';
import { formatPoNo } from '@/lib/orderNo';
import { PURCHASES, PO_FORM, UNIT_LABEL, COMMON } from '@/labels';
import { deleteConversion, deleteLineConversion, getPurchaseOrder } from '@/services/purchases';
import { listItems } from '@/services/inventory';
import { listPayments, paymentsTotal } from '@/services/payments';
import type { UUID } from '@/types/database';

export function PurchaseOrderDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { show } = useToast();
  const { sites } = useSite();
  const [showConvert, setShowConvert] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  const [reverseId, setReverseId] = useState<UUID | null>(null);
  const [itemizedConvertLine, setItemizedConvertLine] = useState<UUID | null | 'any'>(null);
  const [reverseLineConvId, setReverseLineConvId] = useState<UUID | null>(null);

  const { data: po, isLoading } = useQuery({
    queryKey: ['purchase-order', id],
    queryFn: () => getPurchaseOrder(id!),
    enabled: !!id,
  });

  const { data: items = [] } = useQuery({ queryKey: ['items'], queryFn: listItems });

  const { data: payments = [] } = useQuery({
    queryKey: ['payments', 'po', id],
    queryFn: () => listPayments('po', id!),
    enabled: !!id,
  });

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['purchase-order', id] });
    queryClient.invalidateQueries({ queryKey: ['purchase-orders'] });
    queryClient.invalidateQueries({ queryKey: ['inventory-rows'] });
  }

  async function handleReverse() {
    if (!reverseId) return;
    try {
      await deleteConversion(reverseId);
      show(PURCHASES.reversed, 'success');
      invalidate();
    } catch (err) {
      show(err instanceof Error ? err.message : PURCHASES.reverseError, 'error');
    } finally {
      setReverseId(null);
    }
  }

  async function handleReverseLineConv() {
    if (!reverseLineConvId) return;
    try {
      await deleteLineConversion(reverseLineConvId);
      show(PURCHASES.reversed, 'success');
      invalidate();
    } catch (err) {
      show(err instanceof Error ? err.message : PURCHASES.reverseError, 'error');
    } finally {
      setReverseLineConvId(null);
    }
  }

  if (isLoading || !po) {
    return <p className="text-sm text-faint">{COMMON.loading}</p>;
  }

  const isItemized = po.po_type === 'itemized';
  const paid = paymentsTotal(payments);
  const remaining = Math.max(0, Number(po.total_amount) - paid);
  const pct = Number(po.total_kg) > 0 ? Math.min(100, Math.round((po.convertedKg / Number(po.total_kg)) * 100)) : 0;
  const fullyPaid = remaining <= 0.005 && Number(po.total_amount) > 0;

  return (
    <div>
      <button
        onClick={() => navigate('/purchases')}
        className="mb-4 inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
      >
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <line x1="5" y1="12" x2="19" y2="12" />
          <polyline points="13,6 19,12 13,18" />
        </svg>
        <span>{PURCHASES.backToList}</span>
      </button>

      <div className="mb-5">
        <div className="mb-1.5 flex items-center gap-3">
          <h1 className="m-0 text-[22px] font-bold">{formatPoNo(po.order_seq)}</h1>
          {isItemized && (
            <span className="rounded-pill bg-teal-soft px-3 py-1 text-[12.5px] font-bold text-teal">
              {PURCHASES.itemizedBadge}
            </span>
          )}
          {fullyPaid && (
            <span className="rounded-pill bg-success-soft px-3 py-1 text-[12.5px] font-bold text-success-text">
              {PURCHASES.fullyPaid}
            </span>
          )}
          <PermGate need="purchases.edit">
            <button
              onClick={() => setShowEdit(true)}
              className="ms-auto rounded-[9px] border border-border bg-white px-3.5 py-2 text-[12.5px] font-bold text-ink hover:border-teal"
            >
              {PURCHASES.edit}
            </button>
          </PermGate>
        </div>
        <div className="text-[13.5px] text-muted">
          {po.vendorName} · {formatDateShort(po.order_date)}
          {po.product_name ? <> · <span className="font-semibold text-ink">{po.product_name}</span></> : null}
        </div>
        {po.notes ? (
          <div className="mt-2 rounded-[10px] border border-border-soft bg-row-alt px-3.5 py-2.5 text-[13px] text-ink">
            <span className="font-semibold text-muted">{PO_FORM.notes}: </span>
            <span className="whitespace-pre-wrap">{po.notes}</span>
          </div>
        ) : null}
      </div>

      <div className="mb-5 grid grid-cols-2 gap-4 lg:grid-cols-4">
        {isItemized ? (
          <div className="rounded-card border border-border bg-white p-4">
            <div className="mb-2 text-[12.5px] font-semibold text-muted">{PURCHASES.linesTitle}</div>
            <div className="text-[19px] font-bold">
              {po.lines.length.toLocaleString('ar-EG')} {PURCHASES.itemsCountSuffix}
            </div>
          </div>
        ) : (
          <>
            <div className="rounded-card border border-border bg-white p-4">
              <div className="mb-2 text-[12.5px] font-semibold text-muted">{PURCHASES.totalKg}</div>
              <div className="text-[19px] font-bold">{formatQty(po.total_kg ?? 0, UNIT_LABEL.kg)}</div>
            </div>
            <div className="rounded-card border border-border bg-white p-4">
              <div className="mb-2 text-[12.5px] font-semibold text-muted">{PURCHASES.convertedKg}</div>
              <div className="mb-2 text-[19px] font-bold">{formatQty(po.convertedKg, UNIT_LABEL.kg)}</div>
              <div className="h-[7px] overflow-hidden rounded-pill bg-[#EFE9DC]">
                <div className="h-full rounded-pill bg-teal" style={{ width: `${pct}%` }} />
              </div>
            </div>
          </>
        )}
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{PURCHASES.total}</div>
          <div className="text-[19px] font-bold">{formatMoney(po.total_amount)}</div>
        </div>
        <div className="rounded-card border border-border bg-white p-4">
          <div className="mb-2 text-[12.5px] font-semibold text-muted">{PURCHASES.paidRemaining}</div>
          <div className="mb-0.5 text-[15px] font-bold text-success-text">{formatMoney(paid)}</div>
          <div className="text-[12.5px] font-bold text-amber-text">
            {formatMoney(remaining)} {PURCHASES.remainingSuffix}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-start gap-4">
        {isItemized ? (
          <div className="min-w-[340px] flex-1 rounded-card border border-border bg-white p-5">
            <div className="mb-3.5 flex items-center justify-between">
              <h3 className="m-0 text-[15px] font-bold">{PURCHASES.linesTitle}</h3>
              <PermGate need="purchases.convert">
                {po.lines.some((l) => l.remaining > 0.0005) && (
                  <button
                    onClick={() => setItemizedConvertLine('any')}
                    className="rounded-[9px] border-none bg-teal px-3 py-2 text-[12.5px] font-bold text-white hover:bg-teal-hover"
                  >
                    + {PURCHASES.conversionsTitle}
                  </button>
                )}
              </PermGate>
            </div>

            <div className="flex items-center gap-3 border-b border-border pb-2.5 text-xs font-bold text-muted">
              <span className="min-w-0 flex-1">{PURCHASES.colProduct}</span>
              <span className="w-16 flex-shrink-0 text-left">{PURCHASES.colOrdered}</span>
              <span className="w-16 flex-shrink-0 text-left">{PURCHASES.colReceived}</span>
              <span className="w-20 flex-shrink-0 text-left">{PURCHASES.colRemaining}</span>
            </div>
            {po.lines.map((l) => (
              <div key={l.id} className="border-b border-border-soft py-3 last:border-b-0">
                <div className="flex items-center gap-3 text-[13px]">
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-semibold text-ink">{l.itemName}</span>
                    <span className="text-[11.5px] text-muted">{formatMoney(l.line_total)}</span>
                  </span>
                  <span className="w-16 flex-shrink-0 text-left text-muted">{formatQty(l.qty, UNIT_LABEL[l.unitType])}</span>
                  <span className="w-16 flex-shrink-0 text-left font-semibold text-teal">{formatQty(l.converted, UNIT_LABEL[l.unitType])}</span>
                  <span className="w-20 flex-shrink-0 text-left font-bold">
                    {l.remaining > 0.0005 ? (
                      <PermGate
                        need="purchases.convert"
                        fallback={<span className="text-amber-text">{formatQty(l.remaining, UNIT_LABEL[l.unitType])}</span>}
                      >
                        <button
                          onClick={() => setItemizedConvertLine(l.id)}
                          className="rounded-[8px] bg-teal-soft px-2.5 py-1 text-[12px] font-bold text-teal hover:bg-teal/20"
                        >
                          {PURCHASES.receiveAction} · {formatQty(l.remaining, UNIT_LABEL[l.unitType])}
                        </button>
                      </PermGate>
                    ) : (
                      <span className="rounded-pill bg-success-soft px-2 py-0.5 text-[11px] font-bold text-success-text">
                        {PURCHASES.receivedTag}
                      </span>
                    )}
                  </span>
                </div>

                {l.conversions.length > 0 && (
                  <div className="mt-2 flex flex-col gap-1.5 pr-1">
                    {l.conversions.map((c) => (
                      <div key={c.id} className="flex items-center gap-2 text-[11.5px] text-muted">
                        <span className="rounded-pill bg-teal-soft px-2 py-0.5 font-bold text-teal">{c.siteName}</span>
                        <span className="font-semibold text-ink">{formatQty(c.qty, UNIT_LABEL[l.unitType])}</span>
                        <span>· {formatDateShort(c.conversion_date)}</span>
                        <PermGate need="purchases.delete">
                          <button
                            onClick={() => setReverseLineConvId(c.id)}
                            aria-label={PURCHASES.reverseLineConv}
                            className="text-muted hover:text-red-600"
                          >
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                              <polyline points="1 4 1 10 7 10" />
                              <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
                            </svg>
                          </button>
                        </PermGate>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        ) : (
          <div className="min-w-[340px] flex-1 rounded-card border border-border bg-white p-5">
          <div className="mb-3.5 flex items-center justify-between">
            <h3 className="m-0 text-[15px] font-bold">{PURCHASES.conversionsTitle}</h3>
            <PermGate need="purchases.convert">
              {po.remainingKg > 0.0005 && (
                <button
                  onClick={() => setShowConvert(true)}
                  className="rounded-[9px] border-none bg-teal px-3 py-2 text-[12.5px] font-bold text-white hover:bg-teal-hover"
                >
                  + {PURCHASES.addConversion}
                </button>
              )}
            </PermGate>
          </div>

          <div className="mb-3 text-[12.5px] text-muted">
            {PURCHASES.remainingKg}: <span className="font-bold text-ink">{formatQty(po.remainingKg, UNIT_LABEL.kg)}</span>
          </div>

          {po.conversions.length > 0 ? (
            po.conversions.map((c) => (
              <div key={c.id} className="flex items-center gap-3 border-b border-border-soft py-3 last:border-b-0">
                <span className="w-16 flex-shrink-0 text-[12.5px] text-muted">{formatDateShort(c.conversion_date)}</span>
                <span className="flex-shrink-0 rounded-pill bg-teal-soft px-2.5 py-1 text-[12px] font-bold text-teal">
                  {c.siteName}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[13px] font-semibold">{c.itemName}</span>
                  <span className="text-[11.5px] text-muted">
                    {formatQty(c.kg_consumed, UNIT_LABEL.kg)} → {formatQty(c.output_qty, UNIT_LABEL[c.output_unit])}
                    {c.createdByName ? ` · ${PURCHASES.by} ${c.createdByName}` : ''}
                  </span>
                </span>
                <PermGate need="purchases.delete">
                  <button
                    onClick={() => setReverseId(c.id)}
                    aria-label={PURCHASES.reverseConversion}
                    className="flex-shrink-0 text-muted hover:text-red-600"
                  >
                    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                      <polyline points="1 4 1 10 7 10" />
                      <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
                    </svg>
                  </button>
                </PermGate>
              </div>
            ))
          ) : (
            <div className="py-4 text-center text-[13px] text-faint">{COMMON.noData}</div>
          )}
          </div>
        )}

        <div className="min-w-[340px] flex-1">
          <PaymentsPanel
            parentType="po"
            parentId={po.id}
            total={Number(po.total_amount)}
            sites={sites}
            contactId={po.vendor_id}
            contactName={po.vendorName}
            onChanged={invalidate}
          />
        </div>
      </div>

      {/* The vendor's paper order, filed against this PO. Full width below the
          panels — a photographed A4 sheet needs the room to stay readable. */}
      <div className="mt-4 print:hidden">
        <AttachmentsPanel orderType="purchase" orderId={po.id} />
      </div>

      <ConversionModal
        open={showConvert}
        onClose={() => setShowConvert(false)}
        poId={po.id}
        remainingKg={po.remainingKg}
        items={items}
        sites={sites}
        onSaved={invalidate}
      />

      <ItemizedConversionModal
        open={itemizedConvertLine !== null}
        onClose={() => setItemizedConvertLine(null)}
        lines={po.lines.filter((l) => l.remaining > 0.0005)}
        sites={sites}
        initialLineId={itemizedConvertLine === 'any' ? null : itemizedConvertLine}
        onSaved={invalidate}
      />

      <PurchaseOrderFormModal
        open={showEdit}
        onClose={() => setShowEdit(false)}
        editing={po}
        onSaved={invalidate}
      />

      <ConfirmDialog
        open={reverseId !== null}
        title={PURCHASES.reverseConfirmTitle}
        message={PURCHASES.reverseConfirmMsg}
        onConfirm={handleReverse}
        onCancel={() => setReverseId(null)}
        danger
      />

      <ConfirmDialog
        open={reverseLineConvId !== null}
        title={PURCHASES.reverseLineConv}
        message={PURCHASES.reverseLineConvMsg}
        onConfirm={handleReverseLineConv}
        onCancel={() => setReverseLineConvId(null)}
        danger
      />
    </div>
  );
}
