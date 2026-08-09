/**
 * Arabic-aware text matching for the global search and the column filters.
 *
 * Two things make a naive `includes()` useless here:
 *  1. Every number in the UI is rendered in Arabic-Indic digits (٧, ١٢) but the
 *     staff type on a keyboard that produces ASCII ones — and vice versa.
 *  2. Arabic spelling varies freely on hamza/ta-marbuta/alef-maqsura (أحمد vs
 *     احمد, شبانة vs شبانه, على vs علي). Users don't type the "correct" form,
 *     they type the fast one.
 *
 * So both sides of a comparison get folded to a single canonical shape first.
 */

const ARABIC_INDIC = '٠١٢٣٤٥٦٧٨٩';
const EASTERN_ARABIC = '۰۱۲۳۴۵۶۷۸۹'; // Persian/Urdu forms some keyboards emit

export function normalize(input: string): string {
  return input
    .replace(/[٠-٩]/g, (d) => String(ARABIC_INDIC.indexOf(d)))
    .replace(/[۰-۹]/g, (d) => String(EASTERN_ARABIC.indexOf(d)))
    .replace(/[ً-ْٰـ]/g, '') // tashkeel + dagger alef + tatweel
    .replace(/[أإآٱ]/g, 'ا')
    .replace(/ة/g, 'ه')
    .replace(/[ىئ]/g, 'ي')
    .replace(/ؤ/g, 'و')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

/**
 * True when every whitespace-separated term in `query` appears somewhere in
 * `haystack`. Word-order independent, so "عياد ١٢" finds "أمر بيع ١٢ · الامام عياد".
 * `query` must already be normalized (callers normalize once per keystroke).
 */
export function matchesQuery(haystack: string, normalizedQuery: string): boolean {
  if (!normalizedQuery) return true;
  const hay = normalize(haystack);
  return normalizedQuery.split(' ').every((term) => hay.includes(term));
}
