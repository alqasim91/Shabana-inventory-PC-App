import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { matchesQuery, normalize } from '@/lib/search';
import { TABLE } from '@/labels';

export interface FilterOption {
  value: string;
  label: string;
}

interface ColumnFilterProps {
  /** The column header — titles the mobile sheet so it stays self-explanatory. */
  columnLabel: string;
  options: FilterOption[];
  /** Selected values; an empty array means "no filter on this column". */
  selected: string[];
  onChange: (next: string[]) => void;
}

const PANEL_WIDTH = 236;
const SEARCHABLE_FROM = 8; // options count above which an inline search box appears
const MOBILE_BREAKPOINT = 640; // Tailwind `sm`

/**
 * The funnel in a DataTable header cell. Selections apply live — the table
 * updates behind the open panel — so there's no "apply" step to forget; the
 * footer only clears the column or closes the panel.
 *
 * The panel is portalled to <body> with fixed positioning because the table
 * lives inside an `overflow-x-auto` card, which would otherwise clip an
 * absolutely-positioned dropdown. On phones it becomes a bottom sheet instead,
 * where a 236px popover anchored to a scrolled-off header would be unusable.
 */
export function ColumnFilter({ columnLabel, options, selected, onChange }: ColumnFilterProps) {
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [anchor, setAnchor] = useState<{ top: number; right: number } | null>(null);

  const active = selected.length > 0;
  const isSheet = anchor === null;

  const place = useCallback(() => {
    const el = buttonRef.current;
    if (!el) return;
    if (window.innerWidth < MOBILE_BREAKPOINT) {
      setAnchor(null); // render as a bottom sheet
      return;
    }
    const r = el.getBoundingClientRect();
    // RTL: hang the panel from the button's right edge, clamped into the viewport.
    const right = Math.min(Math.max(window.innerWidth - r.right - 4, 8), window.innerWidth - PANEL_WIDTH - 8);
    setAnchor({ top: r.bottom + 6, right });
  }, []);

  // `selected` is a dependency on purpose: applying a filter changes the table's
  // height (and pops the active-filter chips above it), which moves the header
  // cell the panel is anchored to. Re-placing keeps it glued.
  useLayoutEffect(() => {
    if (!open) return;
    place();
    // `true` = capture, so scrolling the table card (not just the window) keeps
    // the panel glued to its header cell.
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [open, place, selected]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        setOpen(false);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open]);

  useEffect(() => {
    if (!open) setQuery('');
  }, [open]);

  const toggle = (value: string) =>
    onChange(selected.includes(value) ? selected.filter((v) => v !== value) : [...selected, value]);

  const q = normalize(query);
  const shown = options.filter((o) => matchesQuery(o.label, q));
  const allShownSelected = shown.length > 0 && shown.every((o) => selected.includes(o.value));

  const list = (
    <>
      {options.length >= SEARCHABLE_FROM && (
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={TABLE.filterSearch}
          className="mb-1.5 w-full rounded-lg border border-border bg-white px-2.5 py-1.5 text-[12.5px] font-normal text-ink outline-none focus:border-teal"
        />
      )}

      <div className="max-h-[240px] overflow-y-auto">
        {shown.length === 0 && <p className="px-1 py-3 text-center text-[12.5px] font-normal text-faint">{TABLE.noOptions}</p>}

        {shown.length > 0 && (
          <button
            type="button"
            onClick={() =>
              onChange(
                allShownSelected
                  ? selected.filter((v) => !shown.some((o) => o.value === v))
                  : [...new Set([...selected, ...shown.map((o) => o.value)])],
              )
            }
            className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-right text-[12.5px] font-bold text-teal hover:bg-sand"
          >
            <Box checked={allShownSelected} />
            <span className="truncate">{TABLE.selectAll}</span>
          </button>
        )}

        {shown.map((o) => {
          const checked = selected.includes(o.value);
          return (
            <button
              key={o.value}
              type="button"
              onClick={() => toggle(o.value)}
              className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-right text-[12.5px] hover:bg-sand ${
                checked ? 'font-bold text-ink' : 'font-normal text-muted'
              }`}
            >
              <Box checked={checked} />
              <span className="truncate">{o.label}</span>
            </button>
          );
        })}
      </div>

      <div className="mt-1.5 flex gap-1.5 border-t border-border-soft pt-1.5">
        <button
          type="button"
          onClick={() => onChange([])}
          disabled={!active}
          className="flex-1 rounded-lg px-2 py-1.5 text-[12.5px] font-bold text-muted hover:bg-sand disabled:opacity-40"
        >
          {TABLE.clearColumn}
        </button>
        <button
          type="button"
          onClick={() => setOpen(false)}
          className="flex-1 rounded-lg bg-teal px-2 py-1.5 text-[12.5px] font-bold text-white hover:bg-teal-hover"
        >
          {TABLE.done}
        </button>
      </div>
    </>
  );

  return (
    <>
      <button
        ref={buttonRef}
        type="button"
        aria-label={`${TABLE.filterAria}: ${columnLabel}`}
        aria-expanded={open}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((v) => !v);
        }}
        className={`flex h-6 w-6 flex-shrink-0 items-center justify-center rounded-md transition-colors ${
          active ? 'bg-teal-soft text-teal' : open ? 'bg-sand text-ink' : 'text-faint hover:bg-sand hover:text-muted'
        }`}
      >
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M4 5h16l-6.2 7.2V19l-3.6-2v-4.8L4 5Z" />
        </svg>
      </button>

      {open &&
        createPortal(
          <>
            <div className="fixed inset-0 z-[90] bg-[rgba(43,38,33,0.25)] sm:bg-transparent" onClick={() => setOpen(false)} />
            {isSheet ? (
              <div
                ref={panelRef}
                dir="rtl"
                className="fixed inset-x-0 bottom-0 z-[95] max-h-[70vh] overflow-y-auto rounded-t-2xl border-t border-border bg-white p-4 pb-6 shadow-lg"
              >
                <h4 className="mb-3 text-[13.5px] font-bold text-ink">{columnLabel}</h4>
                {list}
              </div>
            ) : (
              <div
                ref={panelRef}
                dir="rtl"
                style={{ top: anchor.top, right: anchor.right, width: PANEL_WIDTH }}
                className="fixed z-[95] rounded-xl border border-border bg-white p-1.5 shadow-lg"
              >
                {list}
              </div>
            )}
          </>,
          document.body,
        )}
    </>
  );
}

function Box({ checked }: { checked: boolean }) {
  return (
    <span
      className={`flex h-4 w-4 flex-shrink-0 items-center justify-center rounded-[5px] border ${
        checked ? 'border-teal bg-teal text-white' : 'border-border bg-white'
      }`}
    >
      {checked && (
        <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="5,13 10,18 19,7" />
        </svg>
      )}
    </span>
  );
}
