import { useRef } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useReactToPrint } from 'react-to-print';
import { formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { formatDateLong, todayISODate } from '@/lib/date';
import { formatSoNo } from '@/lib/orderNo';
import { APP_NAME, INVOICE, UNIT_LABEL, COMMON } from '@/labels';
import { useOrganization } from '@/hooks/useOrganization';
import { getSalesOrder } from '@/services/sales';
import { listPayments, paymentsTotal } from '@/services/payments';

export function SalesOrderInvoice() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const printRef = useRef<HTMLDivElement>(null);
  const org = useOrganization();

  const { data: so } = useQuery({
    queryKey: ['sales-order', id],
    queryFn: () => getSalesOrder(id!),
    enabled: !!id,
  });

  const { data: payments = [] } = useQuery({
    queryKey: ['payments', 'so', id],
    queryFn: () => listPayments('so', id!),
    enabled: !!id,
  });

  const handlePrint = useReactToPrint({
    contentRef: printRef,
    documentTitle: so ? `${INVOICE.title} - ${formatSoNo(so.order_seq)}` : INVOICE.title,
  });

  if (!so) return <p className="text-sm text-faint">{COMMON.loading}</p>;

  const paid = paymentsTotal(payments);
  const remaining = Math.max(0, Number(so.total_amount) - paid);
  const discount = Number(so.discount_amount ?? 0);

  return (
    <div className="overflow-x-auto">
      <div className="mb-5 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <button
          onClick={() => navigate(`/sales/${so.id}`)}
          className="inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
        >
          <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <line x1="5" y1="12" x2="19" y2="12" />
            <polyline points="13,6 19,12 13,18" />
          </svg>
          <span>{INVOICE.back}</span>
        </button>

        {so.invoice_number && (
          <button
            onClick={() => handlePrint()}
            className="flex items-center gap-2 rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              <rect x="6" y="9" width="12" height="7" rx="1" />
              <path d="M8 9V4h8v5" />
              <path d="M8 16v4h8v-4" />
            </svg>
            <span>{INVOICE.print}</span>
          </button>
        )}
      </div>

      {!so.invoice_number ? (
        <div className="mx-auto max-w-[800px] rounded-card border border-border bg-white p-10 text-center text-[14px] text-faint">
          {INVOICE.notInvoiced}
        </div>
      ) : (
        <div
          ref={printRef}
          className="mx-auto max-w-[800px] rounded-card border border-border bg-white p-10 print:max-w-none print:border-none print:p-0"
        >
          <div className="mb-5 flex items-start justify-between gap-5 border-b-2 border-teal-dark pb-4">
            <div>
              <div className="mb-1 text-[19px] font-bold text-teal-dark">{org.businessName}</div>
              <div className="text-[12.5px] leading-relaxed text-muted">
                {org.addressLine}
                <br />
                {org.phoneLine}
              </div>
            </div>
            <div className="text-left">
              <div className="mb-1 text-lg font-bold">{INVOICE.title}</div>
              <div className="text-[12.5px] text-muted">
                {INVOICE.invoiceNumber}: <span className="font-bold text-ink">{formatSoNo(so.order_seq)}</span>
              </div>
              <div className="text-[12.5px] text-muted">
                {INVOICE.issueDate}: {formatDateLong(todayISODate())}
              </div>
              <div className="text-[12.5px] text-muted">
                {INVOICE.orderDate}: {formatDateLong(so.order_date)}
              </div>
            </div>
          </div>

          <div className="mb-5 flex flex-wrap gap-8 text-[13.5px]">
            <div>
              <span className="text-muted">{INVOICE.client}: </span>
              <span className="font-bold">{so.clientName}</span>
            </div>
            <div>
              <span className="text-muted">{INVOICE.site}: </span>
              <span className="font-bold">{so.siteName}</span>
            </div>
          </div>

          <table className="w-full border-collapse text-[13px]">
            <thead>
              <tr className="border-b-2 border-ink">
                <th className="w-10 px-1.5 py-2 text-right">{INVOICE.colIndex}</th>
                <th className="px-1.5 py-2 text-right">{INVOICE.colItem}</th>
                <th className="px-1.5 py-2 text-left">{INVOICE.colQty}</th>
                <th className="px-1.5 py-2 text-left">{INVOICE.colUnitPrice}</th>
                <th className="px-1.5 py-2 text-left">{INVOICE.colLineTotal}</th>
              </tr>
            </thead>
            <tbody>
              {so.lines.map((l, i) => (
                <tr key={l.id} className="border-b border-border">
                  <td className="px-1.5 py-2.5 text-muted">{(i + 1).toLocaleString('ar-EG')}</td>
                  <td className="px-1.5 py-2.5 font-semibold">{l.itemName}</td>
                  <td className="px-1.5 py-2.5 text-left text-muted">{formatQty(l.qty, UNIT_LABEL[l.unitType])}</td>
                  <td className="px-1.5 py-2.5 text-left">{formatMoney(l.unit_price)}</td>
                  <td className="px-1.5 py-2.5 text-left font-bold">{formatMoney(l.line_total)}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="mt-5 flex justify-end">
            <div className="min-w-[280px] rounded-[10px] bg-sand px-5 py-3.5">
              {/* A discount the client was given belongs ON the paper: the lines
                  total, what came off, and only then what is owed. */}
              {discount > 0 && (
                <>
                  <div className="flex items-center justify-between pb-1.5 text-[13px]">
                    <span className="text-muted">{INVOICE.subtotal}</span>
                    <span className="font-semibold">{formatMoney(so.subtotal)}</span>
                  </div>
                  <div className="flex items-center justify-between pb-2 text-[13px]">
                    <span className="text-muted">
                      {INVOICE.discount}
                      {so.discount_type === 'percent'
                        ? ` (${Number(so.discount_value).toLocaleString('ar-EG')}٪)`
                        : ''}
                    </span>
                    <span className="font-bold">− {formatMoney(discount)}</span>
                  </div>
                </>
              )}
              <div className="flex items-center justify-between border-b border-border pb-2">
                <span className="text-[13.5px] font-bold text-muted">{INVOICE.grandTotal}</span>
                <span className="text-base font-bold text-teal-dark">{formatMoney(so.total_amount)}</span>
              </div>
              <div className="flex items-center justify-between pt-2 text-[13px]">
                <span className="text-muted">{INVOICE.paid}</span>
                <span className="font-bold text-success-text">{formatMoney(paid)}</span>
              </div>
              <div className="flex items-center justify-between pt-1 text-[13px]">
                <span className="text-muted">{INVOICE.remaining}</span>
                <span className="font-bold text-amber-text">{formatMoney(remaining)}</span>
              </div>
            </div>
          </div>

          <div className="mt-16 flex justify-between gap-10">
            <div className="flex-1 border-t border-ink pt-2 text-center text-[12.5px] text-muted">
              {INVOICE.clientSignature}
            </div>
            <div className="flex-1 border-t border-ink pt-2 text-center text-[12.5px] text-muted">
              {INVOICE.sellerSignature}
            </div>
          </div>

          <div className="mt-8 hidden text-center text-[11px] text-faint print:block">{APP_NAME}</div>
        </div>
      )}
    </div>
  );
}
