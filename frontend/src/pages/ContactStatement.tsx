import { useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useReactToPrint } from 'react-to-print';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { formatMoney } from '@/components/shared/MoneyDisplay';
import { formatDateLong, todayISODate, toLocalISODate } from '@/lib/date';
import { contactBalanceInfo } from '@/lib/contactBalance';
import { APP_NAME, STATEMENT, CONTACTS, CONTACT_TYPE_LABEL, COMMON } from '@/labels';
import { useOrganization } from '@/hooks/useOrganization';
import { getContact, getContactLedger } from '@/services/contacts';

function isoDaysAgo(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return toLocalISODate(d);
}

export function ContactStatement() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const printRef = useRef<HTMLDivElement>(null);
  const org = useOrganization();

  const [from, setFrom] = useState(() => isoDaysAgo(365));
  const [to, setTo] = useState(() => todayISODate());

  const { data: contact } = useQuery({
    queryKey: ['contact', id],
    queryFn: () => getContact(id!),
    enabled: !!id,
  });

  const { data: ledger } = useQuery({
    queryKey: ['contact-ledger', id, from, to],
    queryFn: () => getContactLedger(id!, { from, to }),
    enabled: !!id,
  });

  const handlePrint = useReactToPrint({
    contentRef: printRef,
    documentTitle: contact ? `${STATEMENT.title} - ${contact.name}` : STATEMENT.title,
  });

  if (!contact) return <p className="text-sm text-faint">{COMMON.loading}</p>;

  const finalBalance = contactBalanceInfo(ledger?.finalBalance ?? 0);

  return (
    <div className="overflow-x-auto">
      <div className="mb-5 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <button
          onClick={() => navigate(`/contacts/${contact.id}`)}
          className="inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
        >
          <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <line x1="5" y1="12" x2="19" y2="12" />
            <polyline points="13,6 19,12 13,18" />
          </svg>
          <span>{STATEMENT.back}</span>
        </button>

        <div className="flex flex-wrap items-center gap-3">
          <span className="text-[12.5px] font-semibold text-muted">{STATEMENT.from}</span>
          <ArabicDatePicker value={from} onChange={setFrom} />
          <span className="text-[12.5px] font-semibold text-muted">{STATEMENT.to}</span>
          <ArabicDatePicker value={to} onChange={setTo} />
          <button
            onClick={() => handlePrint()}
            className="flex items-center gap-2 rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              <rect x="6" y="9" width="12" height="7" rx="1" />
              <path d="M8 9V4h8v5" />
              <path d="M8 16v4h8v-4" />
            </svg>
            <span>{STATEMENT.print}</span>
          </button>
        </div>
      </div>

      <div ref={printRef} className="mx-auto max-w-[800px] rounded-card border border-border bg-white p-10 print:max-w-none print:border-none print:p-0">
        <div className="mb-5 flex items-start justify-between gap-5 border-b-2 border-teal-dark pb-4">
          <div>
            <div className="mb-1 text-[19px] font-bold text-teal-dark">{org.businessName}</div>
            <div className="text-[12.5px] leading-relaxed text-muted">
              {org.addressLine}
              <br />
              {org.phoneLine}
            </div>
          </div>
          <div className="text-left">
            <div className="mb-1 text-lg font-bold">{STATEMENT.title}</div>
            <div className="text-[12.5px] text-muted">
              {STATEMENT.issueDate}: {formatDateLong(todayISODate())}
            </div>
            <div className="text-[12.5px] text-muted">
              {STATEMENT.period}: {formatDateLong(from)} — {formatDateLong(to)}
            </div>
          </div>
        </div>

        <div className="mb-5 flex flex-wrap gap-8 text-[13.5px]">
          <div>
            <span className="text-muted">{STATEMENT.accountName}: </span>
            <span className="font-bold">{contact.name}</span>
          </div>
          <div>
            <span className="text-muted">{STATEMENT.accountType}: </span>
            <span className="font-bold">{CONTACT_TYPE_LABEL[contact.type]}</span>
          </div>
        </div>

        <table className="w-full border-collapse text-[13px]">
          <thead>
            <tr className="border-b-2 border-ink">
              <th className="px-1.5 py-2 text-right">{CONTACTS.colDate}</th>
              <th className="px-1.5 py-2 text-right">{CONTACTS.colDesc}</th>
              <th className="px-1.5 py-2 text-right">{CONTACTS.colDebit}</th>
              <th className="px-1.5 py-2 text-right">{CONTACTS.colCredit}</th>
              <th className="px-1.5 py-2 text-right">{CONTACTS.colBalance}</th>
            </tr>
          </thead>
          <tbody>
            {ledger && ledger.rows.length > 0 ? (
              ledger.rows.map((row, i) => (
                <tr key={i} className="border-b border-border">
                  <td className="px-1.5 py-2.5 text-muted">{formatDateLong(row.date)}</td>
                  <td className="px-1.5 py-2.5 font-semibold">{row.desc}</td>
                  <td className="px-1.5 py-2.5">{row.debit ? formatMoney(row.debit) : '—'}</td>
                  <td className="px-1.5 py-2.5">{row.credit ? formatMoney(row.credit) : '—'}</td>
                  <td className="px-1.5 py-2.5 font-bold">{formatMoney(Math.abs(row.balance))}</td>
                </tr>
              ))
            ) : (
              <tr>
                <td colSpan={5} className="py-6 text-center text-faint">
                  {STATEMENT.noRows}
                </td>
              </tr>
            )}
          </tbody>
        </table>

        <div className="mt-5 flex justify-end">
          <div className="min-w-[260px] rounded-[10px] bg-sand px-5 py-3.5">
            <div className="flex items-center justify-between">
              <span className="text-[13.5px] font-bold text-muted">{STATEMENT.finalBalance}</span>
              <span className="text-base font-bold text-teal-dark">{finalBalance.amountLabel}</span>
            </div>
          </div>
        </div>

        <div className="mt-16 flex justify-between gap-10">
          <div className="flex-1 border-t border-ink pt-2 text-center text-[12.5px] text-muted">
            {STATEMENT.clientSignature}
          </div>
          <div className="flex-1 border-t border-ink pt-2 text-center text-[12.5px] text-muted">
            {STATEMENT.accountantSignature}
          </div>
        </div>

        <div className="mt-8 hidden text-center text-[11px] text-faint print:block">{APP_NAME}</div>
      </div>
    </div>
  );
}
