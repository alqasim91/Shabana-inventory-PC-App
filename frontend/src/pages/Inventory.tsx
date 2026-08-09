import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { PermGate } from '@/components/shared/PermGate';
import { MoneyDisplay, formatQty } from '@/components/shared/MoneyDisplay';
import { ItemFormModal } from '@/components/inventory/ItemFormModal';
import { TransferModal } from '@/components/inventory/TransferModal';
import { useSite } from '@/contexts/SiteContext';
import { INVENTORY, UNIT_LABEL } from '@/labels';
import { listItems, listInventoryRows, type InventoryRow } from '@/services/inventory';

/** A row is "منخفض" only when a threshold is actually configured for the item. */
const isLow = (r: InventoryRow) => r.threshold > 0 && r.qty <= r.threshold;

export function Inventory() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { sites, selectedSiteIdForQuery } = useSite();
  const [showAddItem, setShowAddItem] = useState(false);
  const [transferPreset, setTransferPreset] = useState<{ itemId?: string; siteId?: string } | null>(null);

  const { data: items = [] } = useQuery({ queryKey: ['items'], queryFn: listItems });

  const relevantSites = useMemo(
    () => (selectedSiteIdForQuery ? sites.filter((s) => s.id === selectedSiteIdForQuery) : sites),
    [sites, selectedSiteIdForQuery],
  );

  const { data: rows = [], isLoading } = useQuery({
    queryKey: ['inventory-rows', selectedSiteIdForQuery, items.map((i) => i.id).join(',')],
    queryFn: () => listInventoryRows(items, relevantSites),
    enabled: items.length > 0 && relevantSites.length > 0,
  });

  const showSiteColumn = !selectedSiteIdForQuery;

  const columns: DataTableColumn<InventoryRow>[] = [
    {
      key: 'item',
      header: INVENTORY.colItem,
      filter: { valueOf: (r) => r.itemName },
      sortBy: (r) => r.itemName,
      render: (r) => <span className="truncate text-[13.5px] font-semibold">{r.itemName}</span>,
    },
    ...(showSiteColumn
      ? [
          {
            key: 'site',
            header: INVENTORY.colSite,
            width: '150px',
            filter: { valueOf: (r: InventoryRow) => r.siteName },
            render: (r: InventoryRow) => <span className="text-[12.5px] text-muted">{r.siteName}</span>,
          },
        ]
      : []),
    {
      key: 'qty',
      header: INVENTORY.colQty,
      width: '120px',
      sortBy: (r) => r.qty,
      render: (r) => <span className="text-[13.5px] font-bold">{formatQty(r.qty, UNIT_LABEL[r.unitType])}</span>,
    },
    {
      key: 'min',
      header: INVENTORY.colMin,
      width: '120px',
      render: (r) => (
        <span className="text-[12.5px] text-muted">
          {r.threshold > 0 ? formatQty(r.threshold, UNIT_LABEL[r.unitType]) : '—'}
        </span>
      ),
    },
    {
      key: 'status',
      header: INVENTORY.colStatus,
      width: '90px',
      filter: {
        valueOf: (r) => (isLow(r) ? 'low' : 'ok'),
        labelOf: (v) => (v === 'low' ? INVENTORY.statusLow : INVENTORY.statusOk),
        options: ['low', 'ok'],
      },
      render: (r) => {
        const low = isLow(r);
        return (
          <span
            className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${
              low ? 'bg-amber-soft text-amber-text' : 'bg-success-soft text-success-text'
            }`}
          >
            {low ? INVENTORY.statusLow : INVENTORY.statusOk}
          </span>
        );
      },
    },
    {
      key: 'salePrice',
      header: INVENTORY.colSalePrice,
      width: '110px',
      sortBy: (r) => r.salePrice,
      render: (r) => <MoneyDisplay amount={r.salePrice} className="text-[13px]" />,
    },
    {
      key: 'actions',
      header: '',
      width: '50px',
      render: (r) => (
        <PermGate need="inventory.transfer">
          <button
            onClick={(e) => {
              e.stopPropagation();
              setTransferPreset({ itemId: r.itemId, siteId: r.siteId });
            }}
            aria-label={INVENTORY.transferBetweenSites}
            className="flex h-8 w-8 items-center justify-center rounded-lg border border-border bg-white text-muted hover:bg-row-alt"
          >
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="16,3 20,7 16,11" />
              <line x1="20" y1="7" x2="4" y2="7" />
              <polyline points="8,21 4,17 8,13" />
              <line x1="4" y1="17" x2="20" y2="17" />
            </svg>
          </button>
        </PermGate>
      ),
    },
  ];

  return (
    <div>
      <PageHeader
        title={INVENTORY.title}
        subtitle={INVENTORY.subtitle}
        actions={
          <PermGate need="inventory.items">
            <div className="flex gap-2">
              <button
                onClick={() => setShowAddItem(true)}
                className="rounded-[10px] border border-border bg-white px-4 py-2.5 text-[13px] font-bold text-ink"
              >
                + {INVENTORY.addItem}
              </button>
              <button
                onClick={() => setTransferPreset({})}
                className="flex items-center gap-2 rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="16,3 20,7 16,11" />
                  <line x1="20" y1="7" x2="4" y2="7" />
                  <polyline points="8,21 4,17 8,13" />
                  <line x1="4" y1="17" x2="20" y2="17" />
                </svg>
                <span>{INVENTORY.transferBetweenSites}</span>
              </button>
            </div>
          </PermGate>
        }
      />

      <DataTable
        columns={columns}
        data={rows}
        rowKey={(r) => `${r.itemId}:${r.siteId}`}
        onRowClick={(r) => navigate(`/inventory/${r.itemId}`)}
        emptyMessage={INVENTORY.noItems}
        isLoading={isLoading}
        minWidth="820px"
      />

      <ItemFormModal
        open={showAddItem}
        onClose={() => setShowAddItem(false)}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ['items'] });
          queryClient.invalidateQueries({ queryKey: ['inventory-rows'] });
        }}
      />

      <TransferModal
        open={transferPreset !== null}
        onClose={() => setTransferPreset(null)}
        items={items}
        sites={sites}
        defaultItemId={transferPreset?.itemId}
        defaultFromSiteId={transferPreset?.siteId}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ['inventory-rows'] });
        }}
      />
    </div>
  );
}
