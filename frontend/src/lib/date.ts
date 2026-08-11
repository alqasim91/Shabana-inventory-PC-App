import { orgTimezone } from '@/lib/locale';

/** Accepts an ISO date ('YYYY-MM-DD') or ISO datetime and returns its date portion for display helpers. */
function toDate(iso: string): Date {
  return iso.includes('T') ? new Date(iso) : new Date(`${iso}T00:00:00`);
}

// A bare 'YYYY-MM-DD' is a calendar date, not an instant: it is parsed at local
// midnight above and must be rendered as written. Forcing it through a timezone
// would shift it a day for anyone east or west of the business. Only real
// timestamps get the business's zone applied.
function zoneFor(iso: string): string | undefined {
  return iso.includes('T') ? orgTimezone() : undefined;
}

export function formatDateShort(iso: string): string {
  return toDate(iso).toLocaleDateString('ar-EG', {
    day: 'numeric', month: 'short', timeZone: zoneFor(iso),
  });
}

export function formatDateLong(iso: string): string {
  return toDate(iso).toLocaleDateString('ar-EG', {
    day: 'numeric', month: 'long', year: 'numeric', timeZone: zoneFor(iso),
  });
}

/** Formats a Date as 'YYYY-MM-DD' in LOCAL time — toISOString() is UTC, which
 *  is 2–3h behind Egypt and would date late-night orders with yesterday. */
export function toLocalISODate(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/**
 * Today in the BUSINESS's timezone, not the device's.
 *
 * This is the one that actually matters: the dashboard and the cash drawer are
 * per-day (rule 10), so a Riyadh shop whose tablet is set to Cairo would file
 * its late-evening sales under yesterday. en-CA renders as 'YYYY-MM-DD', which
 * is exactly the shape the rest of the app passes around.
 */
export function todayISODate(): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: orgTimezone(), year: 'numeric', month: '2-digit', day: '2-digit',
  }).format(new Date());
}
