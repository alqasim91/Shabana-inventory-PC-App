import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { PermGate } from '@/components/shared/PermGate';
import { MoneyDisplay, formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { PurchaseOrderFormModal } from '@/components/purchases/PurchaseOrderFormModal';
import { formatDateShort } from '@/lib/date';
import { formatPoNo } from '@/lib/orderNo';
import { PURCHASES, UNIT_LABEL } from '@/labels';
import { listPurchaseOrders, type PurchaseOrderListRow } from '@/services/purchases';

const isPaid = (row: PurchaseOrderListRow) => row.totalAmount - row.paid <= 0.005;

function payChip(row: PurchaseOrderListRow) {
  const remaining = row.totalAmount - row.paid;
  if (isPaid(row)) {
    return { label: PURCHASES.fullyPaid, cls: 'bg-success-soft text-success-text' };
  }
  return { label: `${formatMoney(remaining)} ${PURCHASES.remainingSuffix}`, cls: 'bg-amber-soft text-amber-text' };
}

/** Converted-vs-ordered for either PO kind: KG for general, units for itemized. */
function conversionProgress(r: PurchaseOrderListRow) {
  const itemized = r.poType === 'itemized';
  const done = itemized ? r.itemizedConverted : r.convertedKg;
  const total = itemized ? r.itemizedOrdered : r.totalKg ?? 0;
  return { itemized, done, total, pct: total > 0 ? Math.min(100, Math.round((done / total) * 100)) : 0 };
}

const CONV_STATES = ['none', 'partial', 'done'] as const;
const CONV_LABEL: Record<string, string> = {
  none: PURCHASES.convNotStarted,
  partial: PURCHASES.convPartial,
  done: PURCHASES.convDone,
};

function conversionState(r: PurchaseOrderListRow): string {
  const { done, total } = conversionProgress(r);
  if (done <= 0.0005) return 'none';
  return total > 0 && done >= total - 0.0005 ? 'done' : 'partial';
}

const PAY_STATES = ['paid', 'partial'] as const;
const PAY_LABEL: Record<string, string> = {
  paid: PURCHASES.fullyPaid,
  partial: PURCHASES.payPartial,
};

export function Purchases() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [showAdd, setShowAdd] = useState(false);

  const { data: orders = [], isLoading } = useQuery({
    queryKey: ['purchase-orders'],
    queryFn: listPurchaseOrders,
  });

  const columns: DataTableColumn<PurchaseOrderListRow>[] = [
    {
      key: 'code',
      header: PURCHASES.colCode,
      sortBy: (r) => r.orderSeq,
      render: (r) => (
        <span className="text-[13px] font-bold text-teal">{formatPoNo(r.orderSeq)}</span>
      ),
    },
    {
      key: 'vendor',
      header: PURCHASES.colVendor,
      filter: { valueOf: (r) => r.vendorName },
      sortBy: (r) => r.vendorName,
      render: (r) => <span className="truncate text-[13.5px] font-semibold">{r.vendorName}</span>,
    },
    {
      key: 'product',
      header: PURCHASES.colProduct,
      // Itemized orders have no single product, so they group under the مصنّف
      // badge — which doubles as "show me only itemized orders".
      filter: { valueOf: (r) => (r.poType === 'itemized' ? PURCHASES.itemizedBadge : r.productName ?? '') },
      render: (r) =>
        r.poType === 'itemized' ? (
          <span className="inline-flex items-center gap-1.5">
            <span className="rounded-pill bg-teal-soft px-2 py-0.5 text-[11px] font-bold text-teal">
              {PURCHASES.itemizedBadge}
            </span>
            <span className="text-[12.5px] text-muted">
              {r.lineCount.toLocaleString('ar-EG')} {PURCHASES.itemsCountSuffix}
            </span>
          </span>
        ) : (
          <span className="truncate text-[13px] text-ink">{r.productName ?? '—'}</span>
        ),
    },
    {
      key: 'date',
      header: PURCHASES.colDate,
      width: '80px',
      sortBy: (r) => r.orderDate,
      render: (r) => <span className="text-[12.5px] text-muted">{formatDateShort(r.orderDate)}</span>,
    },
    {
      key: 'conversion',
      header: PURCHASES.colConversion,
      width: '180px',
      filter: { valueOf: conversionState, labelOf: (v) => CONV_LABEL[v], options: [...CONV_STATES] },
      sortBy: (r) => conversionProgress(r).pct,
      render: (r) => {
        const { itemized: isItemized, done, total, pct } = conversionProgress(r);
        const unitSuffix = isItemized ? undefined : UNIT_LABEL.kg;
        return (
          <span className="block">
            <span className="mb-1.5 flex justify-between text-[11.5px] text-muted">
              <span>
                {formatQty(done)} / {formatQty(total, unitSuffix)}
              </span>
              <span>{pct.toLocaleString('ar-EG')}٪</span>
            </span>
            <span className="block h-[7px] overflow-hidden rounded-pill bg-[#EFE9DC]">
              <span className="block h-full rounded-pill bg-teal" style={{ width: `${pct}%` }} />
            </span>
          </span>
        );
      },
    },
    {
      key: 'total',
      header: PURCHASES.colTotal,
      width: '110px',
      sortBy: (r) => r.totalAmount,
      render: (r) => <MoneyDisplay amount={r.totalAmount} className="text-[13.5px]" />,
    },
    {
      key: 'pay',
      header: PURCHASES.colPayStatus,
      width: '150px',
      filter: {
        valueOf: (r) => (isPaid(r) ? 'paid' : 'partial'),
        labelOf: (v) => PAY_LABEL[v],
        options: [...PAY_STATES],
      },
      sortBy: (r) => r.totalAmount - r.paid,
      render: (r) => {
        const chip = payChip(r);
        return <span className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${chip.cls}`}>{chip.label}</span>;
      },
    },
  ];

  return (
    <div>
      <PageHeader
        title={PURCHASES.title}
        subtitle={PURCHASES.subtitle}
        actions={
          <PermGate need="purchases.create">
            <button
              onClick={() => setShowAdd(true)}
              className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
            >
              + {PURCHASES.addPo}
            </button>
          </PermGate>
        }
      />

      <DataTable
        columns={columns}
        data={orders}
        rowKey={(r) => r.id}
        onRowClick={(r) => navigate(`/purchases/${r.id}`)}
        emptyMessage={PURCHASES.noPos}
        isLoading={isLoading}
        minWidth="900px"
      />

      <PurchaseOrderFormModal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        onSaved={(id) => {
          queryClient.invalidateQueries({ queryKey: ['purchase-orders'] });
          navigate(`/purchases/${id}`);
        }}
      />
    </div>
  );
}
