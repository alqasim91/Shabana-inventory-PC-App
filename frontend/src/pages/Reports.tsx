import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { PermGate } from '@/components/shared/PermGate';
import { formatMoney } from '@/components/shared/MoneyDisplay';
import { ManualCashModal } from '@/components/reports/ManualCashModal';
import { contactBalanceInfo } from '@/lib/contactBalance';
import { useSite } from '@/contexts/SiteContext';
import { formatDateShort, todayISODate } from '@/lib/date';
import { REPORTS, SO_STATUS_LABEL, PO_STATUS_LABEL, CONTACT_TYPE_LABEL, COMMON } from '@/labels';
import { addDaysISO, cashReport, purchasesReport, salesReport } from '@/services/reports';
import { listContacts } from '@/services/contacts';
import type { SoStatus } from '@/types/database';

type Tab = 'sales' | 'purchases' | 'cash' | 'balances';

const TABS: { key: Tab; label: string }[] = [
  { key: 'sales', label: REPORTS.tabSales },
  { key: 'purchases', label: REPORTS.tabPurchases },
  { key: 'cash', label: REPORTS.tabCash },
  { key: 'balances', label: REPORTS.tabBalances },
];

const SO_STATUS_CLASS: Record<SoStatus, string> = {
  draft: 'bg-row-alt text-muted',
  invoiced: 'bg-teal-soft text-teal',
  placed: 'bg-amber-soft text-amber-text',
  closed: 'bg-success-soft text-success-text',
};

function SummaryCard({ label, value, tone }: { label: string; value: string; tone?: 'teal' | 'amber' }) {
  const color = tone === 'teal' ? 'text-success-text' : tone === 'amber' ? 'text-amber-text' : 'text-ink';
  return (
    <div className="rounded-[10px] bg-row-alt px-4 py-2.5">
      <div className="text-[11.5px] font-semibold text-muted">{label}</div>
      <div className={`text-[15px] font-bold ${color}`}>{value}</div>
    </div>
  );
}

export function Reports() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { sites, selectedSiteIdForQuery } = useSite();
  const [tab, setTab] = useState<Tab>('sales');
  const [from, setFrom] = useState(() => addDaysISO(todayISODate(), -30));
  const [to, setTo] = useState(todayISODate());
  const [showManual, setShowManual] = useState(false);

  const { data: sales = [], isLoading: salesLoading } = useQuery({
    queryKey: ['report-sales', selectedSiteIdForQuery, from, to],
    queryFn: () => salesReport(selectedSiteIdForQuery, from, to),
    enabled: tab === 'sales',
  });

  const { data: purchases = [], isLoading: poLoading } = useQuery({
    queryKey: ['report-purchases', from, to],
    queryFn: () => purchasesReport(from, to),
    enabled: tab === 'purchases',
  });

  const { data: cash, isLoading: cashLoading } = useQuery({
    queryKey: ['report-cash', selectedSiteIdForQuery, from, to],
    queryFn: () => cashReport(selectedSiteIdForQuery, from, to),
    enabled: tab === 'cash',
  });

  const { data: contacts = [], isLoading: balLoading } = useQuery({
    queryKey: ['contacts'],
    queryFn: listContacts,
    enabled: tab === 'balances',
  });

  const salesTotal = sales.reduce((s, r) => s + r.total, 0);
  const salesPaid = sales.reduce((s, r) => s + r.paid, 0);
  const poTotal = purchases.reduce((s, r) => s + r.total, 0);
  const poPaid = purchases.reduce((s, r) => s + r.paid, 0);

  const nonZero = contacts.filter((c) => Math.abs(c.balance) >= 0.005).sort((a, b) => Math.abs(b.balance) - Math.abs(a.balance));
  const owedToUs = nonZero.filter((c) => c.balance > 0).reduce((s, c) => s + c.balance, 0);
  const owedByUs = nonZero.filter((c) => c.balance < 0).reduce((s, c) => s + -c.balance, 0);

  const thClass = 'px-1.5 py-2 text-right text-[12px] font-semibold text-muted';
  const tdClass = 'px-1.5 py-2.5';

  return (
    <div>
      <PageHeader
        title={REPORTS.title}
        subtitle={REPORTS.subtitle}
        actions={
          <button
            onClick={() => window.print()}
            className="flex items-center gap-2 rounded-[10px] border border-border bg-white px-4 py-2.5 text-[13px] font-bold text-ink hover:border-teal print:hidden"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              <rect x="6" y="9" width="12" height="7" rx="1" />
              <path d="M8 9V4h8v5" />
              <path d="M8 16v4h8v-4" />
            </svg>
            <span>{REPORTS.print}</span>
          </button>
        }
      />

      <div className="mb-4 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <div className="flex gap-1.5">
          {TABS.map((t) => (
            <button
              key={t.key}
              onClick={() => setTab(t.key)}
              className={`rounded-pill px-3.5 py-1.5 text-[12.5px] font-bold ${
                tab === t.key ? 'bg-teal text-white' : 'border border-border bg-white text-muted'
              }`}
            >
              {t.label}
            </button>
          ))}
        </div>

        {tab !== 'balances' && (
          <div className="flex flex-wrap items-center gap-2.5">
            <span className="text-[12.5px] font-semibold text-muted">{REPORTS.from}</span>
            <ArabicDatePicker value={from} onChange={setFrom} />
            <span className="text-[12.5px] font-semibold text-muted">{REPORTS.to}</span>
            <ArabicDatePicker value={to} onChange={setTo} />
          </div>
        )}
      </div>

      {/* ---- المبيعات ---- */}
      {tab === 'sales' && (
        <div className="rounded-card border border-border bg-white p-5">
          <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
            <SummaryCard label={REPORTS.ordersCount} value={sales.length.toLocaleString('ar-EG')} />
            <SummaryCard label={REPORTS.totalsTag} value={formatMoney(salesTotal)} />
            <SummaryCard label={REPORTS.paidTag} value={formatMoney(salesPaid)} tone="teal" />
            <SummaryCard label={REPORTS.remainingTag} value={formatMoney(salesTotal - salesPaid)} tone="amber" />
          </div>
          {salesLoading ? (
            <p className="py-6 text-center text-sm text-faint">{COMMON.loading}</p>
          ) : sales.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-[13px]">
                <thead>
                  <tr className="border-b border-border">
                    <th className={thClass}>{REPORTS.colDoc}</th>
                    <th className={thClass}>{REPORTS.colParty}</th>
                    <th className={thClass}>{REPORTS.colDate}</th>
                    <th className={thClass}>{REPORTS.colTotal}</th>
                    <th className={thClass}>{REPORTS.colPaid}</th>
                    <th className={thClass}>{REPORTS.colStatus}</th>
                  </tr>
                </thead>
                <tbody>
                  {sales.map((r) => (
                    <tr
                      key={r.id}
                      onClick={() => navigate(`/sales/${r.id}`)}
                      className="cursor-pointer border-b border-border-soft last:border-b-0 hover:bg-row-alt"
                    >
                      <td className={`${tdClass} font-bold text-teal`}>{r.doc}</td>
                      <td className={`${tdClass} font-semibold`}>{r.clientName}</td>
                      <td className={`${tdClass} text-muted`}>{formatDateShort(r.date)}</td>
                      <td className={tdClass}>{formatMoney(r.total)}</td>
                      <td className={`${tdClass} text-success-text`}>{formatMoney(r.paid)}</td>
                      <td className={tdClass}>
                        <span className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${SO_STATUS_CLASS[r.status]}`}>
                          {SO_STATUS_LABEL[r.status]}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="py-6 text-center text-sm text-faint">{REPORTS.noRows}</p>
          )}
        </div>
      )}

      {/* ---- المشتريات ---- */}
      {tab === 'purchases' && (
        <div className="rounded-card border border-border bg-white p-5">
          <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
            <SummaryCard label={REPORTS.ordersCount} value={purchases.length.toLocaleString('ar-EG')} />
            <SummaryCard label={REPORTS.totalsTag} value={formatMoney(poTotal)} />
            <SummaryCard label={REPORTS.paidTag} value={formatMoney(poPaid)} tone="teal" />
            <SummaryCard label={REPORTS.remainingTag} value={formatMoney(poTotal - poPaid)} tone="amber" />
          </div>
          {poLoading ? (
            <p className="py-6 text-center text-sm text-faint">{COMMON.loading}</p>
          ) : purchases.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-[13px]">
                <thead>
                  <tr className="border-b border-border">
                    <th className={thClass}>{REPORTS.colDoc}</th>
                    <th className={thClass}>{REPORTS.colParty}</th>
                    <th className={thClass}>{REPORTS.colDate}</th>
                    <th className={thClass}>{REPORTS.colTotal}</th>
                    <th className={thClass}>{REPORTS.colPaid}</th>
                    <th className={thClass}>{REPORTS.colStatus}</th>
                  </tr>
                </thead>
                <tbody>
                  {purchases.map((r) => (
                    <tr
                      key={r.id}
                      onClick={() => navigate(`/purchases/${r.id}`)}
                      className="cursor-pointer border-b border-border-soft last:border-b-0 hover:bg-row-alt"
                    >
                      <td className={`${tdClass} font-bold text-teal`}>{r.doc}</td>
                      <td className={`${tdClass} font-semibold`}>{r.vendorName}</td>
                      <td className={`${tdClass} text-muted`}>{formatDateShort(r.date)}</td>
                      <td className={tdClass}>{formatMoney(r.total)}</td>
                      <td className={`${tdClass} text-success-text`}>{formatMoney(r.paid)}</td>
                      <td className={`${tdClass} text-muted`}>{PO_STATUS_LABEL[r.status]}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="py-6 text-center text-sm text-faint">{REPORTS.noRows}</p>
          )}
        </div>
      )}

      {/* ---- حركة الخزينة ---- */}
      {tab === 'cash' && (
        <div className="rounded-card border border-border bg-white p-5">
          <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
            <div className="grid flex-1 grid-cols-2 gap-3 lg:grid-cols-4">
              <SummaryCard label={REPORTS.inflowTag} value={formatMoney(cash?.inflow ?? 0)} tone="teal" />
              <SummaryCard label={REPORTS.outflowTag} value={formatMoney(cash?.outflow ?? 0)} tone="amber" />
              <SummaryCard label={REPORTS.closingTag} value={formatMoney(cash?.closing ?? 0)} />
              <SummaryCard
                label={REPORTS.cashSite}
                value={
                  selectedSiteIdForQuery
                    ? sites.find((s) => s.id === selectedSiteIdForQuery)?.name_ar ?? '—'
                    : REPORTS.allSitesCash
                }
              />
            </div>
            <PermGate need="cash.manual">
              <button
                onClick={() => setShowManual(true)}
                className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover print:hidden"
              >
                + {REPORTS.manualMovement}
              </button>
            </PermGate>
          </div>

          {cashLoading ? (
            <p className="py-6 text-center text-sm text-faint">{COMMON.loading}</p>
          ) : cash && cash.rows.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-[13px]">
                <thead>
                  <tr className="border-b border-border">
                    <th className={thClass}>{REPORTS.colDate}</th>
                    {!selectedSiteIdForQuery && <th className={thClass}>{REPORTS.cashSite}</th>}
                    <th className={thClass}>{REPORTS.colReason}</th>
                    <th className={thClass}>{REPORTS.colAmount}</th>
                    {selectedSiteIdForQuery && <th className={thClass}>{REPORTS.colBalanceAfter}</th>}
                  </tr>
                </thead>
                <tbody>
                  {cash.rows.map((m) => {
                    const delta = Number(m.amount_delta);
                    return (
                      <tr key={m.id} className="border-b border-border-soft last:border-b-0">
                        <td className={`${tdClass} text-muted`}>{formatDateShort(m.created_at)}</td>
                        {!selectedSiteIdForQuery && <td className={`${tdClass} text-muted`}>{m.siteName}</td>}
                        <td className={`${tdClass} font-semibold`}>{m.reason}</td>
                        <td className={`${tdClass} font-bold ${delta >= 0 ? 'text-success-text' : 'text-amber-text'}`}>
                          {delta >= 0 ? '+' : '−'}
                          {formatMoney(Math.abs(delta))}
                        </td>
                        {selectedSiteIdForQuery && (
                          <td className={`${tdClass} font-bold`}>{formatMoney(m.balanceAfter ?? 0)}</td>
                        )}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="py-6 text-center text-sm text-faint">{REPORTS.noRows}</p>
          )}
        </div>
      )}

      {/* ---- الأرصدة ---- */}
      {tab === 'balances' && (
        <div className="rounded-card border border-border bg-white p-5">
          <div className="mb-4 grid grid-cols-1 gap-3 lg:grid-cols-2">
            <SummaryCard label={REPORTS.owedToUs} value={formatMoney(owedToUs)} tone="amber" />
            <SummaryCard label={REPORTS.owedByUs} value={formatMoney(owedByUs)} tone="teal" />
          </div>
          {balLoading ? (
            <p className="py-6 text-center text-sm text-faint">{COMMON.loading}</p>
          ) : nonZero.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-[13px]">
                <thead>
                  <tr className="border-b border-border">
                    <th className={thClass}>{REPORTS.colContact}</th>
                    <th className={thClass}>{REPORTS.colType}</th>
                    <th className={thClass}>{REPORTS.colBalance}</th>
                  </tr>
                </thead>
                <tbody>
                  {nonZero.map((c) => {
                    const info = contactBalanceInfo(c.balance);
                    const toneClass = info.tone === 'positive' ? 'text-success-text' : 'text-amber-text';
                    return (
                      <tr
                        key={c.id}
                        onClick={() => navigate(`/contacts/${c.id}`)}
                        className="cursor-pointer border-b border-border-soft last:border-b-0 hover:bg-row-alt"
                      >
                        <td className={`${tdClass} font-semibold`}>{c.name}</td>
                        <td className={`${tdClass} text-muted`}>{CONTACT_TYPE_LABEL[c.type]}</td>
                        <td className={`${tdClass} font-bold ${toneClass}`}>{info.amountLabel}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="py-6 text-center text-sm text-faint">{REPORTS.noBalances}</p>
          )}
        </div>
      )}

      <ManualCashModal
        open={showManual}
        onClose={() => setShowManual(false)}
        sites={sites}
        defaultSiteId={selectedSiteIdForQuery}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ['report-cash'] });
          queryClient.invalidateQueries({ queryKey: ['dashboard'] });
        }}
      />
    </div>
  );
}
