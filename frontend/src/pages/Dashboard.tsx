import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { formatMoney, formatQty } from '@/components/shared/MoneyDisplay';
import { useSite } from '@/contexts/SiteContext';
import { todayISODate } from '@/lib/date';
import { DASHBOARD, PAYMENT_METHOD_LABEL, UNIT_LABEL, SITE, COMMON } from '@/labels';
import { getDashboard } from '@/services/dashboard';
import type { PaymentMethod } from '@/types/database';

const METHOD_ORDER: PaymentMethod[] = ['cash', 'instapay', 'bank_transfer'];

function KpiCard({ label, value, hint, accent }: { label: string; value: string; hint?: string; accent?: 'teal' | 'amber' }) {
  const valueColor = accent === 'teal' ? 'text-teal-dark' : accent === 'amber' ? 'text-amber-text' : 'text-ink';
  return (
    <div className="rounded-card border border-border bg-white p-4">
      <div className="mb-2 text-[12.5px] font-semibold text-muted">{label}</div>
      <div className={`text-[20px] font-bold ${valueColor}`}>{value}</div>
      {hint && <div className="mt-1 text-[11px] text-faint">{hint}</div>}
    </div>
  );
}

function MethodBreakdown({
  title,
  emptyText,
  totalTag,
  byMethod,
  tone,
}: {
  title: string;
  emptyText: string;
  totalTag: string;
  byMethod: Partial<Record<PaymentMethod, number>>;
  tone: 'teal' | 'amber';
}) {
  const rows = METHOD_ORDER.map((m) => ({ m, amt: Number(byMethod[m] ?? 0) })).filter((r) => r.amt > 0);
  const total = rows.reduce((s, r) => s + r.amt, 0);
  const totalColor = tone === 'teal' ? 'text-success-text' : 'text-amber-text';
  const chipColor = tone === 'teal' ? 'bg-teal-soft text-teal' : 'bg-amber-soft text-amber-soft-text';

  return (
    <div className="rounded-card border border-border bg-white p-5">
      <h3 className="m-0 mb-3.5 text-[15px] font-bold">{title}</h3>
      {rows.length > 0 ? (
        <>
          {rows.map((r) => (
            <div key={r.m} className="flex items-center justify-between border-b border-border-soft py-2.5 last:border-b-0">
              <span className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${chipColor}`}>
                {PAYMENT_METHOD_LABEL[r.m]}
              </span>
              <span className="text-[13.5px] font-bold text-ink">{formatMoney(r.amt)}</span>
            </div>
          ))}
          <div className="mt-3 flex items-center justify-between rounded-[10px] bg-row-alt px-3 py-2.5">
            <span className="text-[12.5px] font-semibold text-muted">{totalTag}</span>
            <span className={`text-sm font-bold ${totalColor}`}>{formatMoney(total)}</span>
          </div>
        </>
      ) : (
        <div className="py-5 text-center text-[13px] text-faint">{emptyText}</div>
      )}
    </div>
  );
}

export function Dashboard() {
  const { selectedSiteIdForQuery, selectedSiteName, selectedSiteId } = useSite();
  const [date, setDate] = useState(todayISODate());
  const isAllSites = selectedSiteIdForQuery === null;

  const { data, isLoading } = useQuery({
    queryKey: ['dashboard', selectedSiteIdForQuery, date],
    queryFn: () => getDashboard(selectedSiteIdForQuery, date),
  });

  const topItems = (data?.top_items ?? []).map((t) => ({ name: t.name_ar, value: Number(t.moved) }));
  const combinedDrawers = (data?.site_drawers ?? []).reduce((s, d) => s + Number(d.closing_cash), 0);

  return (
    <div>
      <PageHeader
        title={DASHBOARD.title}
        subtitle={DASHBOARD.subtitle}
        actions={
          <div className="flex items-center gap-2">
            <span className="rounded-pill bg-teal-soft px-3 py-1.5 text-[12.5px] font-bold text-teal">
              {selectedSiteId === 'all' ? SITE.allSites : selectedSiteName}
            </span>
            <ArabicDatePicker value={date} onChange={setDate} />
          </div>
        }
      />

      {isLoading || !data ? (
        <p className="text-sm text-faint">{COMMON.loading}</p>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <KpiCard label={DASHBOARD.openingCash} value={formatMoney(Number(data.opening_cash))} hint={DASHBOARD.openingHint} />
            <KpiCard label={DASHBOARD.salesToday} value={formatMoney(Number(data.sales_total))} accent="teal" />
            <KpiCard label={DASHBOARD.collectionsToday} value={formatMoney(Number(data.collections_total))} accent="teal" />
            <KpiCard label={DASHBOARD.closingCash} value={formatMoney(Number(data.closing_cash))} accent="teal" />
          </div>

          <div className="flex flex-wrap items-start gap-4">
            <div className="min-w-[320px] flex-[1.4] rounded-card border border-border bg-white p-5">
              <h3 className="m-0 mb-4 text-[15px] font-bold">{DASHBOARD.topItemsTitle}</h3>
              {topItems.length > 0 ? (
                <div className="flex flex-col gap-3.5">
                  {(() => {
                    const max = Math.max(...topItems.map((t) => t.value)) || 1;
                    return topItems.map((it, i) => (
                      <div key={i}>
                        <div className="mb-1.5 flex items-center justify-between gap-3">
                          <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-ink">{it.name}</span>
                          <span className="flex-shrink-0 text-[13px] font-bold text-teal">
                            {it.value.toLocaleString('ar-EG')}
                          </span>
                        </div>
                        <div className="h-2.5 overflow-hidden rounded-pill bg-[#EFE9DC]">
                          <div
                            className="h-full rounded-pill bg-teal"
                            style={{ width: `${Math.max(4, (it.value / max) * 100)}%` }}
                          />
                        </div>
                      </div>
                    ));
                  })()}
                </div>
              ) : (
                <div className="py-8 text-center text-[13px] text-faint">{DASHBOARD.noTopItems}</div>
              )}
            </div>

            <div className="flex min-w-[300px] flex-1 flex-col gap-4">
              <MethodBreakdown
                title={DASHBOARD.collectionsTitle}
                emptyText={DASHBOARD.noCollections}
                totalTag={DASHBOARD.collectionsTotalTag}
                byMethod={data.collections_by_method}
                tone="teal"
              />
              <MethodBreakdown
                title={DASHBOARD.payoutsTitle}
                emptyText={DASHBOARD.noPayouts}
                totalTag={DASHBOARD.payoutsTotalTag}
                byMethod={data.payments_out_by_method}
                tone="amber"
              />
            </div>
          </div>

          {isAllSites && data.site_drawers.length > 0 && (
            <div className="rounded-card border border-border bg-white p-5">
              <h3 className="m-0 mb-3.5 text-[15px] font-bold">{DASHBOARD.drawersTitle}</h3>
              <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
                {data.site_drawers.map((d) => (
                  <div key={d.site_id} className="rounded-[10px] bg-row-alt px-4 py-3">
                    <div className="mb-1 text-[12.5px] font-semibold text-muted">{d.site_name}</div>
                    <div className="text-[15px] font-bold text-teal-dark">{formatMoney(Number(d.closing_cash))}</div>
                  </div>
                ))}
                <div className="rounded-[10px] bg-teal-soft px-4 py-3">
                  <div className="mb-1 text-[12.5px] font-semibold text-teal">{DASHBOARD.combinedTotal}</div>
                  <div className="text-[15px] font-bold text-teal-dark">{formatMoney(combinedDrawers)}</div>
                </div>
              </div>
            </div>
          )}

          <div className="rounded-card border border-border bg-white p-5">
            <h3 className="m-0 mb-3.5 text-[15px] font-bold">{DASHBOARD.lowStockTitle}</h3>
            {data.low_stock.length > 0 ? (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse text-[13px]">
                  <thead>
                    <tr className="border-b border-border text-[12px] text-muted">
                      <th className="px-1.5 py-2 text-right font-semibold">{DASHBOARD.lowStockCol}</th>
                      <th className="px-1.5 py-2 text-right font-semibold">{DASHBOARD.lowStockSite}</th>
                      <th className="px-1.5 py-2 text-left font-semibold">{DASHBOARD.lowStockQty}</th>
                      <th className="px-1.5 py-2 text-left font-semibold">{DASHBOARD.lowStockThreshold}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.low_stock.map((l) => (
                      <tr key={`${l.item_id}-${l.site_id}`} className="border-b border-border-soft last:border-b-0">
                        <td className="px-1.5 py-2.5 font-semibold">{l.name_ar}</td>
                        <td className="px-1.5 py-2.5 text-muted">{l.site_name}</td>
                        <td className="px-1.5 py-2.5 text-left">
                          <span className="rounded-pill bg-amber-soft px-2.5 py-1 text-[12px] font-bold text-amber-text">
                            {formatQty(Number(l.qty), UNIT_LABEL[l.unit_type])}
                          </span>
                        </td>
                        <td className="px-1.5 py-2.5 text-left text-muted">{formatQty(Number(l.threshold), UNIT_LABEL[l.unit_type])}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="py-5 text-center text-[13px] text-faint">{DASHBOARD.noLowStock}</div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
