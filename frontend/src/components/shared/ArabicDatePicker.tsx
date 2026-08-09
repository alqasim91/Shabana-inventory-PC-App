import { useRef } from 'react';

interface ArabicDatePickerProps {
  /** ISO date string ('YYYY-MM-DD'). */
  value: string;
  onChange: (isoDate: string) => void;
  className?: string;
}

function formatArabicDate(iso: string): string {
  const d = new Date(`${iso}T00:00:00`);
  return d.toLocaleDateString('ar-EG', { day: 'numeric', month: 'long', year: 'numeric' });
}

/** Pill-styled date field: native <input type="date"> under an Arabic-formatted label, matching the mockup's date pill. */
export function ArabicDatePicker({ value, onChange, className = '' }: ArabicDatePickerProps) {
  const inputRef = useRef<HTMLInputElement>(null);

  function openPicker() {
    const el = inputRef.current;
    if (!el) return;
    try {
      if ('showPicker' in el && typeof el.showPicker === 'function') {
        el.showPicker();
        return;
      }
    } catch {
      // showPicker() can throw if not user-activated — fall through to focus/click.
    }
    el.focus();
    el.click();
  }

  return (
    <div className={`relative inline-flex ${className}`}>
      <button
        type="button"
        onClick={openPicker}
        aria-label="اختر التاريخ"
        className="flex items-center gap-2 rounded-pill border border-border bg-white px-3.5 py-2 text-[13.5px] font-semibold text-ink"
      >
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
          <rect x="3.5" y="5" width="17" height="16" rx="2" />
          <line x1="3.5" y1="10" x2="20.5" y2="10" />
          <line x1="8" y1="3" x2="8" y2="7" />
          <line x1="16" y1="3" x2="16" y2="7" />
        </svg>
        <span>{formatArabicDate(value)}</span>
      </button>
      {/* Off-screen native input: the button drives it via showPicker(); it never
          intercepts clicks (pointer-events-none) so a single tap always registers. */}
      <input
        ref={inputRef}
        type="date"
        value={value}
        onChange={(e) => e.target.value && onChange(e.target.value)}
        tabIndex={-1}
        aria-hidden="true"
        className="pointer-events-none absolute bottom-0 right-3 h-0 w-0 opacity-0"
      />
    </div>
  );
}
