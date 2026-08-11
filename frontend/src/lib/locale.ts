/**
 * The business's currency and timezone, held in one module-level place.
 *
 * formatMoney / formatDateShort / todayISODate are pure functions called from
 * roughly fifty components, printed invoices and PDF-bound pages included.
 * Threading a React context through all of them would mean rewriting every one
 * of those call sites and would still leave the print paths — which render
 * outside the tree — with no way to reach it.
 *
 * So the settings live here and are pushed in once, when the organization
 * loads. The trade-off is honest: this is module-level mutable state, which is
 * only safe because a session belongs to exactly ONE business. If this app ever
 * shows two organizations at once, this becomes wrong and must become context.
 */

export interface CurrencyInfo {
  code: string;
  /** Arabic symbol as it should appear next to an amount. */
  symbol: string;
  /** Arabic name, for the settings dropdown. */
  name: string;
}

/**
 * Two-decimal currencies only. Every money column is NUMERIC(12,2), so the
 * three-decimal Gulf dinars (KWD, BHD, OMR, JOD) would lose their last digit —
 * they are left out rather than silently truncated. Adding them means widening
 * every money column first.
 */
export const CURRENCIES: CurrencyInfo[] = [
  { code: 'EGP', symbol: 'ج.م', name: 'جنيه مصري' },
  { code: 'SAR', symbol: 'ر.س', name: 'ريال سعودي' },
  { code: 'AED', symbol: 'د.إ', name: 'درهم إماراتي' },
  { code: 'QAR', symbol: 'ر.ق', name: 'ريال قطري' },
  { code: 'USD', symbol: '$', name: 'دولار أمريكي' },
  { code: 'EUR', symbol: '€', name: 'يورو' },
  { code: 'TRY', symbol: '₺', name: 'ليرة تركية' },
  { code: 'MAD', symbol: 'د.م', name: 'درهم مغربي' },
  { code: 'LBP', symbol: 'ل.ل', name: 'ليرة لبنانية' },
  { code: 'ILS', symbol: '₪', name: 'شيكل' },
  { code: 'DZD', symbol: 'د.ج', name: 'دينار جزائري' },
  { code: 'SYP', symbol: 'ل.س', name: 'ليرة سورية' },
];

/** Timezones for the region this product serves, plus the Gulf and Levant. */
export const TIMEZONES: { zone: string; name: string }[] = [
  { zone: 'Africa/Cairo', name: 'القاهرة' },
  { zone: 'Asia/Riyadh', name: 'الرياض' },
  { zone: 'Asia/Dubai', name: 'دبي / أبوظبي' },
  { zone: 'Asia/Qatar', name: 'الدوحة' },
  { zone: 'Asia/Kuwait', name: 'الكويت' },
  { zone: 'Asia/Bahrain', name: 'المنامة' },
  { zone: 'Asia/Muscat', name: 'مسقط' },
  { zone: 'Asia/Amman', name: 'عمّان' },
  { zone: 'Asia/Beirut', name: 'بيروت' },
  { zone: 'Asia/Damascus', name: 'دمشق' },
  { zone: 'Asia/Baghdad', name: 'بغداد' },
  { zone: 'Asia/Jerusalem', name: 'القدس' },
  { zone: 'Africa/Khartoum', name: 'الخرطوم' },
  { zone: 'Africa/Tripoli', name: 'طرابلس' },
  { zone: 'Africa/Tunis', name: 'تونس' },
  { zone: 'Africa/Algiers', name: 'الجزائر' },
  { zone: 'Africa/Casablanca', name: 'الدار البيضاء' },
  { zone: 'Europe/Istanbul', name: 'إسطنبول' },
];

const DEFAULT_CURRENCY = CURRENCIES[0];
const DEFAULT_TIMEZONE = 'Africa/Cairo';

let currentCurrency: CurrencyInfo = DEFAULT_CURRENCY;
let currentTimezone: string = DEFAULT_TIMEZONE;

/** Called once when the organization loads. Unknown values fall back rather
 *  than throw — a bad setting should not blank every price in the app. */
export function setOrgLocale(currencyCode?: string | null, timezone?: string | null): void {
  currentCurrency = CURRENCIES.find((c) => c.code === currencyCode) ?? DEFAULT_CURRENCY;
  currentTimezone = timezone && timezone.trim() ? timezone : DEFAULT_TIMEZONE;
}

export function currencySymbol(): string {
  return currentCurrency.symbol;
}

export function currencyCode(): string {
  return currentCurrency.code;
}

export function orgTimezone(): string {
  return currentTimezone;
}
