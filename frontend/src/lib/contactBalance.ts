import { CONTACT_BALANCE, COMMON } from '@/labels';
import { formatMoney } from '@/components/shared/MoneyDisplay';

export type ContactBalanceStatus = 'receivable' | 'payable' | 'settled';

export interface ContactBalanceInfo {
  status: ContactBalanceStatus;
  tag: string;
  sub: string;
  /** Absolute amount with the له/عليه suffix, e.g. "36,400 ج.م عليه". */
  amountLabel: string;
  tone: 'default' | 'positive' | 'negative';
}

/**
 * Convention (matches get_contact_balance): positive = they owe us (عليه),
 * negative = we owe them (له).
 */
export function contactBalanceInfo(balance: number): ContactBalanceInfo {
  // Money is NUMERIC(12,2). Show the exact balance to the piaster — never round
  // to whole pounds (that turned 13.50 into 14 on statements). "Settled" means
  // the balance is within half a piaster of zero, not "rounds to zero".
  if (Math.abs(balance) < 0.005) {
    return {
      status: 'settled',
      tag: CONTACT_BALANCE.settledTag,
      sub: CONTACT_BALANCE.settledSub,
      amountLabel: `٠ ${COMMON.currency}`,
      tone: 'default',
    };
  }

  if (balance > 0) {
    return {
      status: 'receivable',
      tag: CONTACT_BALANCE.receivableTag,
      sub: CONTACT_BALANCE.receivableSub,
      amountLabel: `${formatMoney(balance)} ${CONTACT_BALANCE.receivableTag}`,
      tone: 'negative',
    };
  }

  return {
    status: 'payable',
    tag: CONTACT_BALANCE.payableTag,
    sub: CONTACT_BALANCE.payableSub,
    amountLabel: `${formatMoney(Math.abs(balance))} ${CONTACT_BALANCE.payableTag}`,
    tone: 'positive',
  };
}
