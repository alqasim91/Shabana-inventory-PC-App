import { currencySymbol } from '@/lib/locale';

export function formatMoney(amount: number): string {
  // Money is NUMERIC(12,2) — show the piasters when they exist. Rounding to
  // whole pounds made printed invoices inconsistent (١٩ × ٣ ≠ ٥٦ for 18.50 × 3).
  const v = Math.round(amount * 100) / 100;
  const label = v.toLocaleString('ar-EG', {
    minimumFractionDigits: Number.isInteger(v) ? 0 : 2,
    maximumFractionDigits: 2,
  });
  // Symbol read at call time, not at import time — the organization loads
  // after the first render, and prices must re-read it once it does.
  return `${label} ${currencySymbol()}`;
}

export function formatQty(amount: number, unitLabel?: string): string {
  const n = Number.isInteger(amount) ? amount : Math.round(amount * 1000) / 1000;
  return unitLabel ? `${n.toLocaleString('ar-EG')} ${unitLabel}` : n.toLocaleString('ar-EG');
}

interface MoneyDisplayProps {
  amount: number;
  className?: string;
  /** Applies success/amber tint for positive/negative balances (له/عليه use cases). */
  tone?: 'default' | 'positive' | 'negative';
}

const TONE_CLASS: Record<NonNullable<MoneyDisplayProps['tone']>, string> = {
  default: 'text-ink',
  positive: 'text-success-text',
  negative: 'text-amber-text',
};

export function MoneyDisplay({ amount, className = '', tone = 'default' }: MoneyDisplayProps) {
  return <span className={`font-bold ${TONE_CLASS[tone]} ${className}`}>{formatMoney(amount)}</span>;
}
