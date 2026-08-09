/** Accepts an ISO date ('YYYY-MM-DD') or ISO datetime and returns its date portion for display helpers. */
function toDate(iso: string): Date {
  return iso.includes('T') ? new Date(iso) : new Date(`${iso}T00:00:00`);
}

export function formatDateShort(iso: string): string {
  return toDate(iso).toLocaleDateString('ar-EG', { day: 'numeric', month: 'short' });
}

export function formatDateLong(iso: string): string {
  return toDate(iso).toLocaleDateString('ar-EG', { day: 'numeric', month: 'long', year: 'numeric' });
}

/** Formats a Date as 'YYYY-MM-DD' in LOCAL time — toISOString() is UTC, which
 *  is 2–3h behind Egypt and would date late-night orders with yesterday. */
export function toLocalISODate(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

export function todayISODate(): string {
  return toLocalISODate(new Date());
}
