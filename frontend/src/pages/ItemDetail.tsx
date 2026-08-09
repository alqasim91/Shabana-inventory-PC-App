import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PermGate } from '@/components/shared/PermGate';
import { MoneyDisplay, formatQty } from '@/components/shared/MoneyDisplay';
import { ItemFormModal } from '@/components/inventory/ItemFormModal';
import { AdjustmentModal } from '@/components/inventory/AdjustmentModal';
import { useSite } from '@/contexts/SiteContext';
import { INVENTORY, UNIT_LABEL, STOCK_SOURCE_LABEL, COMMON } from '@/labels';
import { formatDateShort } from '@/lib/date';
import { getItem, listInventoryRows, listMovements } from '@/services/inventory';

export function ItemDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { sites, selectedSiteIdForQuery } = useSite();
  const [showEdit, setShowEdit] = useState(false);
  const [showAdjust, setShowAdjust] = useState(false);

  const { data: item, isLoading } = useQuery({
    queryKey: ['item', id],
    queryFn: () => getItem(id!),
    enabled: !!id,
  });

  const { data: stockRows = [] } = useQuery({
    queryKey: ['item-stock', id],
    queryFn: () => listInventoryRows([item!], sites),
    enabled: !!item && sites.length > 0,
  });

  const { data: movements = [] } = useQuery({
    queryKey: ['item-movements', id],
    queryFn: () => listMovements(id!),
    enabled: !!id,
  });

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['item', id] });
    queryClient.invalidateQueries({ queryKey: ['item-stock', id] });
    queryClient.invalidateQueries({ queryKey: ['item-movements', id] });
    queryClient.invalidateQueries({ queryKey: ['items'] });
    queryClient.invalidateQueries({ queryKey: ['inventory-rows'] });
  }

  if (isLoading || !item) {
    return <p className="text-sm text-faint">{COMMON.loading}</p>;
  }

  return (
    <div className="max-w-[820px]">
      <button
        onClick={() => navigate('/inventory')}
        className="mb-4 inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
      >
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <line x1="5" y1="12" x2="19" y2="12" />
          <polyline points="13,6 19,12 13,18" />
        </svg>
        <span>{INVENTORY.backToList}</span>
      </button>

      <div className="mb-4 rounded-card border border-border bg-white p-5">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="m-0 mb-1 text-xl font-bold">{item.name_ar}</h1>
            <div className="flex items-center gap-3 text-[12.5px] text-muted">
              <span>{UNIT_LABEL[item.unit_type]}</span>
              <span>·</span>
              <span>
                {INVENTORY.salePrice}: <MoneyDisplay amount={item.sale_price} className="text-[12.5px]" />
              </span>
            </div>
          </div>
          <div className="flex gap-2">
            <PermGate need="inventory.items">
              <button
                onClick={() => setShowAdjust(true)}
                className="rounded-[10px] border border-border bg-white px-4 py-2.5 text-[13px] font-bold text-ink"
              >
                {INVENTORY.adjust}
              </button>
              <button
                onClick={() => setShowEdit(true)}
                className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
              >
                {COMMON.edit}
              </button>
            </PermGate>
          </div>
        </div>

        <div className="text-[11.5px] font-semibold text-muted">{INVENTORY.perSiteStock}</div>
        <div className="mt-2 flex flex-wrap gap-3">
          {stockRows.map((r) => (
            <div key={r.siteId} className="rounded-[10px] bg-row-alt px-4 py-2.5">
              <div className="text-[11.5px] text-muted">{r.siteName}</div>
              <div className="text-[15px] font-bold">{formatQty(r.qty, UNIT_LABEL[r.unitType])}</div>
            </div>
          ))}
        </div>
      </div>

      <div className="rounded-card border border-border bg-white p-5">
        <h3 className="m-0 mb-3.5 text-[15px] font-bold">{INVENTORY.movementHistory}</h3>
        {movements.length > 0 ? (
          <>
            <div className="flex items-center gap-3 border-b border-border pb-2.5 text-xs font-bold text-muted">
              <span className="w-20 flex-shrink-0">{INVENTORY.colDate}</span>
              <span className="w-32 flex-shrink-0">{INVENTORY.colSite}</span>
              <span className="w-28 flex-shrink-0">{INVENTORY.colType}</span>
              <span className="w-24 flex-shrink-0">{INVENTORY.colChange}</span>
              <span className="flex-1">{INVENTORY.colNote}</span>
            </div>
            {movements.map((m) => (
              <div key={m.id} className="flex items-center gap-3 border-b border-border-soft py-2.5 text-[13px] last:border-b-0">
                <span className="w-20 flex-shrink-0 text-[12.5px] text-muted">{formatDateShort(m.created_at)}</span>
                <span className="w-32 flex-shrink-0 text-[12.5px] text-muted">{m.siteName}</span>
                <span className="w-28 flex-shrink-0 font-semibold">{STOCK_SOURCE_LABEL[m.source_type]}</span>
                <span className={`w-24 flex-shrink-0 font-bold ${m.qty_delta >= 0 ? 'text-success-text' : 'text-amber-text'}`}>
                  {m.qty_delta >= 0 ? '+' : ''}
                  {formatQty(m.qty_delta, UNIT_LABEL[item.unit_type])}
                </span>
                <span className="flex-1 truncate text-[12.5px] text-muted">{m.note ?? '—'}</span>
              </div>
            ))}
          </>
        ) : (
          <div className="py-6 text-center text-[13px] text-faint">{INVENTORY.noMovements}</div>
        )}
      </div>

      <ItemFormModal open={showEdit} onClose={() => setShowEdit(false)} item={item} onSaved={invalidate} />

      <AdjustmentModal
        open={showAdjust}
        onClose={() => setShowAdjust(false)}
        item={item}
        sites={sites}
        defaultSiteId={selectedSiteIdForQuery ?? undefined}
        onSaved={invalidate}
      />
    </div>
  );
}
