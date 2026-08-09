import { PURCHASES, SALES } from '@/labels';

/**
 * Human-facing order identifier: the order's running number in Arabic-Indic
 * digits, prefixed with its type noun. e.g. "أمر بيع ٧", "أمر شراء ١٢".
 * Backed by the persistent `order_seq` column (migration 0017).
 *
 * `seq` is tolerated as nullish so a stale hydrated cache (rows persisted
 * before order_seq existed) renders the noun alone instead of crashing; the
 * background refetch then fills in the number.
 */
const seqLabel = (noun: string, seq: number | null | undefined): string =>
  seq == null || Number.isNaN(seq) ? noun : `${noun} ${seq.toLocaleString('ar-EG')}`;

export const formatSoNo = (seq: number | null | undefined): string => seqLabel(SALES.orderNoun, seq);
export const formatPoNo = (seq: number | null | undefined): string => seqLabel(PURCHASES.orderNoun, seq);
