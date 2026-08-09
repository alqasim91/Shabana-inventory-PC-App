import { useMemo, useState, type ReactNode } from 'react';
import { ColumnFilter, type FilterOption } from './ColumnFilter';
import { COMMON, TABLE } from '@/labels';

export interface DataTableColumn<T> {
  key: string;
  header: string;
  /** Fixed width (e.g. '110px'); omit to let the column auto-size to its content. */
  width?: string;
  render: (row: T) => ReactNode;
  /** Text alignment override; numbers/amounts typically use 'end'. */
  align?: 'start' | 'end';
  /** Allow this auto-width column's content to wrap instead of sizing to one line. */
  wrap?: boolean;
  /**
   * Adds a funnel to this column's header — a multi-select over the values the
   * column actually holds. Options are derived from the *unfiltered* data, so
   * they don't vanish as the user narrows things down.
   */
  filter?: {
    /** The filterable value for a row, e.g. `(r) => r.siteName`. '' excludes the row from the option list. */
    valueOf: (row: T) => string;
    /** Display text for a value; defaults to the value itself. */
    labelOf?: (value: string) => string;
    /** Fixed option order (e.g. a status enum); defaults to Arabic-collated. */
    options?: string[];
  };
  /** Makes the header clickable to sort by this key: asc → desc → off. */
  sortBy?: (row: T) => number | string;
}

interface DataTableProps<T> {
  columns: DataTableColumn<T>[];
  data: T[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
  /** Keeps columns from collapsing on narrow screens; the card scrolls horizontally instead. */
  minWidth?: string;
  emptyMessage?: string;
  isLoading?: boolean;
}

type SortState = { key: string; dir: 'asc' | 'desc' } | null;

/**
 * RTL data table matching the mockup's list-card pattern (used for POs, SOs,
 * inventory, contacts…). Built on a real <table> with auto layout so each
 * auto-width column sizes to its own content and stays aligned across every
 * row — long text can't spill into the next column, it just widens the table
 * (which scrolls) or, for fixed columns, is bounded by the given width.
 *
 * Filtering and sorting are owned here and applied in-memory: every list in
 * this app is already fully loaded by TanStack Query, so narrowing a table is
 * instant and needs no round trip. Pages opt in per column via `filter`/`sortBy`
 * and otherwise keep passing their full dataset.
 */
export function DataTable<T>({
  columns,
  data,
  rowKey,
  onRowClick,
  minWidth = '700px',
  emptyMessage = COMMON.noData,
  isLoading = false,
}: DataTableProps<T>) {
  const [filters, setFilters] = useState<Record<string, string[]>>({});
  const [sort, setSort] = useState<SortState>(null);

  const cellAlign = (col: DataTableColumn<T>) => (col.align === 'end' ? 'left' : 'right');
  // Auto columns hold their content on one line (so the column fits the text);
  // fixed-width columns wrap within their width. Opt back into wrapping with `wrap`.
  const nowrap = (col: DataTableColumn<T>) => !col.width && !col.wrap;

  const optionsByKey = useMemo(() => {
    const out: Record<string, FilterOption[]> = {};
    for (const col of columns) {
      if (!col.filter) continue;
      const { valueOf, labelOf, options } = col.filter;
      const found = new Map<string, string>();
      for (const row of data) {
        const v = valueOf(row);
        if (v && !found.has(v)) found.set(v, labelOf ? labelOf(v) : v);
      }
      out[col.key] = options
        ? options.filter((v) => found.has(v)).map((v) => ({ value: v, label: found.get(v) as string }))
        : [...found.entries()]
            .map(([value, label]) => ({ value, label }))
            .sort((a, b) => a.label.localeCompare(b.label, 'ar'));
    }
    return out;
  }, [columns, data]);

  const rows = useMemo(() => {
    let out = data;
    for (const col of columns) {
      const picked = filters[col.key];
      if (!col.filter || !picked?.length) continue;
      const valueOf = col.filter.valueOf;
      out = out.filter((r) => picked.includes(valueOf(r)));
    }

    const sortCol = sort ? columns.find((c) => c.key === sort.key) : undefined;
    if (sort && sortCol?.sortBy) {
      const by = sortCol.sortBy;
      out = [...out].sort((a, b) => {
        const av = by(a);
        const bv = by(b);
        const cmp =
          typeof av === 'number' && typeof bv === 'number'
            ? av - bv
            : String(av).localeCompare(String(bv), 'ar');
        return sort.dir === 'asc' ? cmp : -cmp;
      });
    }
    return out;
  }, [data, columns, filters, sort]);

  const activeChips = columns.flatMap((col) => {
    const picked = filters[col.key];
    if (!col.filter || !picked?.length) return [];
    const labels = optionsByKey[col.key]?.filter((o) => picked.includes(o.value)).map((o) => o.label) ?? picked;
    return [{ key: col.key, header: col.header, text: labels.join('، ') }];
  });

  const toggleSort = (key: string) =>
    setSort((s) =>
      s?.key !== key ? { key, dir: 'asc' } : s.dir === 'asc' ? { key, dir: 'desc' } : null,
    );

  const isFiltered = activeChips.length > 0;

  return (
    <div>
      {isFiltered && (
        <div className="mb-2.5 flex flex-wrap items-center gap-1.5">
          {activeChips.map((chip) => (
            <button
              key={chip.key}
              onClick={() => setFilters((f) => ({ ...f, [chip.key]: [] }))}
              className="flex max-w-full items-center gap-1.5 rounded-pill border border-teal-light bg-teal-soft px-3 py-1 text-[12px] font-bold text-teal"
            >
              <span className="truncate">
                {chip.header ? `${chip.header}: ` : ''}
                {chip.text}
              </span>
              <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round">
                <line x1="6" y1="6" x2="18" y2="18" />
                <line x1="18" y1="6" x2="6" y2="18" />
              </svg>
            </button>
          ))}
          <button onClick={() => setFilters({})} className="px-2 py-1 text-[12px] font-bold text-muted hover:text-ink">
            {TABLE.clearAll}
          </button>
          <span className="text-[12px] text-faint">{TABLE.showingCount(rows.length, data.length)}</span>
        </div>
      )}

      <div className="overflow-x-auto rounded-card border border-border bg-white">
        <table className="w-full border-collapse" style={{ minWidth }}>
          <thead>
            <tr className="border-b border-border bg-row-alt text-xs font-bold text-muted">
              {columns.map((col) => {
                const sorted = sort?.key === col.key ? sort.dir : null;
                return (
                  <th
                    key={col.key}
                    className="whitespace-nowrap ps-3 pe-3 py-2.5 font-bold first:ps-5 last:pe-5"
                    style={{ width: col.width, textAlign: cellAlign(col) }}
                  >
                    <span
                      className={`inline-flex items-center gap-1 ${col.align === 'end' ? 'flex-row-reverse' : ''}`}
                    >
                      {col.sortBy ? (
                        <button
                          type="button"
                          onClick={() => toggleSort(col.key)}
                          aria-label={`${TABLE.sortAria}: ${col.header}`}
                          className={`inline-flex items-center gap-1 rounded-md px-1 py-0.5 hover:bg-sand ${
                            sorted ? 'text-teal' : ''
                          }`}
                        >
                          <span>{col.header}</span>
                          <svg
                            width="11"
                            height="11"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.4"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            className={sorted ? '' : 'opacity-35'}
                          >
                            {sorted === 'desc' ? (
                              <polyline points="6,10 12,16 18,10" />
                            ) : sorted === 'asc' ? (
                              <polyline points="6,14 12,8 18,14" />
                            ) : (
                              <>
                                <polyline points="7,10 12,5 17,10" />
                                <polyline points="7,14 12,19 17,14" />
                              </>
                            )}
                          </svg>
                        </button>
                      ) : (
                        <span>{col.header}</span>
                      )}

                      {col.filter && (optionsByKey[col.key]?.length ?? 0) > 0 && (
                        <ColumnFilter
                          columnLabel={col.header}
                          options={optionsByKey[col.key]}
                          selected={filters[col.key] ?? []}
                          onChange={(next) => setFilters((f) => ({ ...f, [col.key]: next }))}
                        />
                      )}
                    </span>
                  </th>
                );
              })}
            </tr>
          </thead>

          <tbody>
            {isLoading && (
              <tr>
                <td colSpan={columns.length} className="px-5 py-10 text-center text-sm text-faint">
                  {COMMON.loading}
                </td>
              </tr>
            )}

            {!isLoading && rows.length === 0 && (
              <tr>
                <td colSpan={columns.length} className="px-5 py-10 text-center text-sm text-faint">
                  {isFiltered ? TABLE.noMatch : emptyMessage}
                </td>
              </tr>
            )}

            {!isLoading &&
              rows.map((row) => (
                <tr
                  key={rowKey(row)}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  className={`border-b border-border-soft last:border-b-0 ${
                    onRowClick ? 'cursor-pointer hover:bg-row-alt' : ''
                  }`}
                >
                  {columns.map((col) => (
                    <td
                      key={col.key}
                      className={`ps-3 pe-3 py-3.5 align-middle first:ps-5 last:pe-5 ${
                        nowrap(col) ? 'whitespace-nowrap' : ''
                      }`}
                      style={{ width: col.width, textAlign: cellAlign(col) }}
                    >
                      {col.render(row)}
                    </td>
                  ))}
                </tr>
              ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
