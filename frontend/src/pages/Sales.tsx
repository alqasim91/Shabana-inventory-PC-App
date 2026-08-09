import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { PermGate } from '@/components/shared/PermGate';
import { MoneyDisplay, formatMoney } from '@/components/shared/MoneyDisplay';
import { SalesOrderFormModal } from '@/components/sales/SalesOrderFormModal';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort } from '@/lib/date';
import { formatSoNo } from '@/lib/orderNo';
import { SALES, SO_STATUS_LABEL } from '@/labels';
import { listSalesOrders, type SalesOrderListRow } from '@/services/sales';
import type { SoStatus } from '@/types/database';

const STATUS_CLASS: Record<SoStatus, string> = {
  draft: 'bg-row-alt text-muted',
  invoiced: 'bg-teal-soft text-teal',
  placed: 'bg-amber-soft text-amber-text',
  closed: 'bg-success-soft text-success-text',
};

const isPaid = (row: SalesOrderListRow) => row.totalAmount > 0 && row.totalAmount - row.paid <= 0.005;

function payChip(row: SalesOrderListRow) {
  const remaining = row.totalAmount - row.paid;
  if (isPaid(row)) {
    return { label: SALES.fullyPaid, cls: 'bg-success-soft text-success-text' };
  }
  return { label: `${formatMoney(remaining)} ${SALES.remainingSuffix}`, cls: 'bg-amber-soft text-amber-text' };
}

const SO_STATUS_ORDER: SoStatus[] = ['draft', 'invoiced', 'placed', 'closed'];

const PAY_STATES = ['paid', 'partial'] as const;
const PAY_LABEL: Record<string, string> = {
  paid: SALES.fullyPaid,
  partial: SALES.payPartial,
};

export function Sales() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { selectedSiteIdForQuery } = useSite();
  const [showAdd, setShowAdd] = useState(false);

  const { data: orders = [], isLoading } = useQuery({
    queryKey: ['sales-orders', selectedSiteIdForQuery],
    queryFn: () => listSalesOrders(selectedSiteIdForQuery),
  });

  const columns: DataTableColumn<SalesOrderListRow>[] = [
    {
      key: 'code',
      header: SALES.colCode,
      sortBy: (r) => r.orderSeq,
      render: (r) => (
        <span className="text-[13px] font-bold text-teal">{formatSoNo(r.orderSeq)}</span>
      ),
    },
    {
      key: 'client',
      header: SALES.colClient,
      filter: { valueOf: (r) => r.clientName },
      sortBy: (r) => r.clientName,
      render: (r) => <span className="truncate text-[13.5px] font-semibold">{r.clientName}</span>,
    },
    {
      key: 'site',
      header: SALES.colSite,
      width: '120px',
      filter: { valueOf: (r) => r.siteName },
      render: (r) => <span className="text-[12.5px] text-muted">{r.siteName}</span>,
    },
    {
      key: 'date',
      header: SALES.colDate,
      width: '80px',
      sortBy: (r) => r.orderDate,
      render: (r) => <span className="text-[12.5px] text-muted">{formatDateShort(r.orderDate)}</span>,
    },
    {
      key: 'total',
      header: SALES.colTotal,
      width: '110px',
      sortBy: (r) => r.totalAmount,
      render: (r) => <MoneyDisplay amount={r.totalAmount} className="text-[13.5px]" />,
    },
    {
      key: 'status',
      header: SALES.colStatus,
      width: '90px',
      filter: {
        valueOf: (r) => r.status,
        labelOf: (v) => SO_STATUS_LABEL[v as SoStatus],
        options: SO_STATUS_ORDER,
      },
      render: (r) => (
        <span className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${STATUS_CLASS[r.status]}`}>
          {SO_STATUS_LABEL[r.status]}
        </span>
      ),
    },
    {
      key: 'pay',
      header: SALES.colPayStatus,
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
        title={SALES.title}
        subtitle={SALES.subtitle}
        actions={
          <PermGate need="sales.draft">
            <button
              onClick={() => setShowAdd(true)}
              className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
            >
              + {SALES.addSo}
            </button>
          </PermGate>
        }
      />

      <DataTable
        columns={columns}
        data={orders}
        rowKey={(r) => r.id}
        onRowClick={(r) => navigate(`/sales/${r.id}`)}
        emptyMessage={SALES.noSos}
        isLoading={isLoading}
        minWidth="950px"
      />

      <SalesOrderFormModal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        onSaved={(id) => {
          queryClient.invalidateQueries({ queryKey: ['sales-orders'] });
          navigate(`/sales/${id}`);
        }}
      />
    </div>
  );
}
