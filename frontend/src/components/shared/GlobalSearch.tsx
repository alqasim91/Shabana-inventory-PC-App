import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useSite } from '@/contexts/SiteContext';
import { matchesQuery, normalize } from '@/lib/search';
import { formatDateShort } from '@/lib/date';
import { formatPoNo, formatSoNo } from '@/lib/orderNo';
import { formatMoney } from './MoneyDisplay';
import { CONTACT_TYPE_LABEL, SEARCH, SO_STATUS_LABEL, UNIT_LABEL } from '@/labels';
import { listPurchaseOrders } from '@/services/purchases';
import { listSalesOrders } from '@/services/sales';
import { listItems } from '@/services/inventory';
import { listContacts } from '@/services/contacts';

interface Hit {
  id: string;
  group: string;
  title: string;
  meta: string;
  to: string;
}

const PER_GROUP = 5;

/**
 * App-wide search: one box over purchase orders, sales orders, items and
 * contacts, grouped by kind.
 *
 * It reuses the exact query keys the list pages use, so whatever the user has
 * already visited is served from the TanStack cache with no round trip, and the
 * queries only run while the overlay is open. Matching goes through
 * `normalize()` so Arabic-Indic order numbers and loose hamza/ta-marbuta
 * spelling both hit.
 */
export function GlobalSearch({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const { selectedSiteIdForQuery } = useSite();
  const inputRef = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState('');
  const [cursor, setCursor] = useState(0);

  const { data: pos = [] } = useQuery({ queryKey: ['purchase-orders'], queryFn: listPurchaseOrders, enabled: open });
  const { data: sos = [] } = useQuery({
    queryKey: ['sales-orders', selectedSiteIdForQuery],
    queryFn: () => listSalesOrders(selectedSiteIdForQuery),
    enabled: open,
  });
  const { data: items = [] } = useQuery({ queryKey: ['items'], queryFn: listItems, enabled: open });
  const { data: contacts = [] } = useQuery({ queryKey: ['contacts'], queryFn: listContacts, enabled: open });

  const q = normalize(query);

  const groups = useMemo(() => {
    if (!q) return [];

    const build = (group: string, all: Hit[]) => ({ group, hits: all.slice(0, PER_GROUP), total: all.length });

    return [
      build(
        SEARCH.groupPurchases,
        pos
          .filter((r) => matchesQuery(`${formatPoNo(r.orderSeq)} ${r.vendorName} ${r.productName ?? ''}`, q))
          .map((r) => ({
            id: r.id,
            group: SEARCH.groupPurchases,
            title: `${formatPoNo(r.orderSeq)} · ${r.vendorName}`,
            meta: `${formatDateShort(r.orderDate)} · ${formatMoney(r.totalAmount)}`,
            to: `/purchases/${r.id}`,
          })),
      ),
      build(
        SEARCH.groupSales,
        sos
          .filter((r) => matchesQuery(`${formatSoNo(r.orderSeq)} ${r.clientName} ${r.invoiceNumber ?? ''}`, q))
          .map((r) => ({
            id: r.id,
            group: SEARCH.groupSales,
            title: `${formatSoNo(r.orderSeq)} · ${r.clientName}`,
            meta: `${SO_STATUS_LABEL[r.status]} · ${formatDateShort(r.orderDate)} · ${formatMoney(r.totalAmount)}`,
            to: `/sales/${r.id}`,
          })),
      ),
      build(
        SEARCH.groupItems,
        items
          .filter((r) => matchesQuery(r.name_ar, q))
          .map((r) => ({
            id: r.id,
            group: SEARCH.groupItems,
            title: r.name_ar,
            meta: `${UNIT_LABEL[r.unit_type]} · ${formatMoney(r.sale_price)}`,
            to: `/inventory/${r.id}`,
          })),
      ),
      build(
        SEARCH.groupContacts,
        contacts
          .filter((r) => matchesQuery(`${r.name} ${r.phone ?? ''}`, q))
          .map((r) => ({
            id: r.id,
            group: SEARCH.groupContacts,
            title: r.name,
            meta: `${CONTACT_TYPE_LABEL[r.type]}${r.phone ? ` · ${r.phone}` : ''}`,
            to: `/contacts/${r.id}`,
          })),
      ),
    ].filter((g) => g.hits.length > 0);
  }, [q, pos, sos, items, contacts]);

  const flat = useMemo(() => groups.flatMap((g) => g.hits), [groups]);

  useEffect(() => setCursor(0), [q]);

  useEffect(() => {
    if (!open) return;
    setQuery('');
    setCursor(0);
    // Autofocus after the overlay paints, otherwise the caret lands nowhere.
    const t = window.setTimeout(() => inputRef.current?.focus(), 0);
    return () => window.clearTimeout(t);
  }, [open]);

  if (!open) return null;

  const go = (hit: Hit) => {
    onClose();
    navigate(hit.to);
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      onClose();
      return;
    }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!flat.length) return;
      setCursor((c) => (c + (e.key === 'ArrowDown' ? 1 : flat.length - 1)) % flat.length);
      return;
    }
    if (e.key === 'Enter' && flat[cursor]) {
      e.preventDefault();
      go(flat[cursor]);
    }
  };

  let index = -1;

  return (
    <div
      onClick={onClose}
      className="fixed inset-0 z-[100] flex items-start justify-center bg-[rgba(43,38,33,0.45)] px-4 pt-[12vh]"
    >
      <div
        dir="rtl"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
        className="flex max-h-[70vh] w-full max-w-[560px] flex-col overflow-hidden rounded-2xl bg-white shadow-lg"
      >
        <div className="flex flex-shrink-0 items-center gap-2.5 border-b border-border px-4 py-3">
          <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" className="flex-shrink-0 text-faint">
            <circle cx="11" cy="11" r="6.5" />
            <line x1="20" y1="20" x2="16" y2="16" />
          </svg>
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={SEARCH.placeholder}
            className="min-w-0 flex-1 border-none bg-transparent text-[14.5px] text-ink outline-none placeholder:text-faint"
          />
          <button
            onClick={onClose}
            aria-label={SEARCH.close}
            className="flex h-[30px] w-[30px] flex-shrink-0 items-center justify-center rounded-lg bg-sand text-muted"
          >
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <line x1="6" y1="6" x2="18" y2="18" />
              <line x1="18" y1="6" x2="6" y2="18" />
            </svg>
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-2">
          {!q && <p className="px-3 py-8 text-center text-[13px] text-faint">{SEARCH.hint}</p>}
          {!!q && groups.length === 0 && (
            <p className="px-3 py-8 text-center text-[13px] text-faint">{SEARCH.noResults}</p>
          )}

          {groups.map((g) => (
            <div key={g.group} className="mb-1.5">
              <p className="px-3 pb-1 pt-2 text-[11.5px] font-bold text-faint">{g.group}</p>
              {g.hits.map((hit) => {
                index += 1;
                const activeRow = index === cursor;
                return (
                  <button
                    key={hit.id}
                    onMouseEnter={() => setCursor(flat.indexOf(hit))}
                    onClick={() => go(hit)}
                    className={`flex w-full items-baseline justify-between gap-3 rounded-lg px-3 py-2 text-right ${
                      activeRow ? 'bg-teal-soft' : 'hover:bg-sand'
                    }`}
                  >
                    <span className={`min-w-0 truncate text-[13.5px] font-semibold ${activeRow ? 'text-teal' : 'text-ink'}`}>
                      {hit.title}
                    </span>
                    <span className="flex-shrink-0 text-[11.5px] text-muted">{hit.meta}</span>
                  </button>
                );
              })}
              {g.total > g.hits.length && (
                <p className="px-3 py-1 text-[11.5px] text-faint">{SEARCH.moreSuffix(g.total - g.hits.length)}</p>
              )}
            </div>
          ))}
        </div>

        <div className="hidden flex-shrink-0 border-t border-border-soft px-4 py-2 text-[11.5px] text-faint sm:block">
          {SEARCH.navHint}
        </div>
      </div>
    </div>
  );
}
