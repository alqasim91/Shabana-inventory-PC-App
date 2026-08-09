import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PermGate } from '@/components/shared/PermGate';
import { MoneyDisplay } from '@/components/shared/MoneyDisplay';
import { ContactFormModal } from '@/components/contacts/ContactFormModal';
import { CreditCard } from '@/components/contacts/CreditCard';
import { VendorCreditCard } from '@/components/contacts/VendorCreditCard';
import { contactBalanceInfo } from '@/lib/contactBalance';
import { formatDateShort } from '@/lib/date';
import { CONTACTS, CONTACT_TYPE_LABEL, PAYMENT_METHOD_LABEL, COMMON } from '@/labels';
import { getContact, getContactLedger } from '@/services/contacts';
import type { ContactType, PaymentMethod } from '@/types/database';

const TYPE_BADGE_CLASS: Record<ContactType, string> = {
  client: 'bg-teal-soft text-teal',
  vendor: 'bg-amber-soft text-amber-soft-text',
  both: 'bg-success-soft text-success-text',
};

const BALANCE_BG_CLASS = {
  receivable: 'bg-[#FBE7E1]',
  payable: 'bg-teal-soft',
  settled: 'bg-row-alt',
};

const BALANCE_TEXT_CLASS = {
  receivable: 'text-[#B3402C]',
  payable: 'text-teal',
  settled: 'text-muted',
};

function methodBadgeLabel(m: { method: PaymentMethod; instapay_number: string | null; bank_name: string | null; account_number: string | null }) {
  if (m.method === 'instapay' && m.instapay_number) return `${PAYMENT_METHOD_LABEL.instapay} — ${m.instapay_number}`;
  if (m.method === 'bank_transfer' && m.bank_name) return `${PAYMENT_METHOD_LABEL.bank_transfer} — ${m.bank_name}`;
  return PAYMENT_METHOD_LABEL[m.method];
}

export function ContactDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [showEdit, setShowEdit] = useState(false);

  const { data: contact, isLoading } = useQuery({
    queryKey: ['contact', id],
    queryFn: () => getContact(id!),
    enabled: !!id,
  });

  const { data: ledger } = useQuery({
    queryKey: ['contact-ledger', id],
    queryFn: () => getContactLedger(id!),
    enabled: !!id,
  });

  if (isLoading || !contact) {
    return <p className="text-sm text-faint">{COMMON.loading}</p>;
  }

  const balance = contactBalanceInfo(ledger?.finalBalance ?? 0);

  return (
    <div className="max-w-[760px]">
      <button
        onClick={() => navigate('/contacts')}
        className="mb-4 inline-flex items-center gap-2 border-none bg-transparent text-[13.5px] font-semibold text-muted"
      >
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <line x1="5" y1="12" x2="19" y2="12" />
          <polyline points="13,6 19,12 13,18" />
        </svg>
        <span>{CONTACTS.backToList}</span>
      </button>

      <div className="mb-4 rounded-card border border-border bg-white p-5">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <h1 className="m-0 text-xl font-bold">{contact.name}</h1>
            <span className={`rounded-pill px-2.5 py-1 text-xs font-bold ${TYPE_BADGE_CLASS[contact.type]}`}>
              {CONTACT_TYPE_LABEL[contact.type]}
            </span>
          </div>
          <div className="flex gap-2">
            <PermGate need="contacts.manage">
              <button
                onClick={() => setShowEdit(true)}
                className="rounded-[10px] border border-border bg-white px-4 py-2.5 text-[13px] font-bold text-ink"
              >
                {COMMON.edit}
              </button>
            </PermGate>
            <button
              onClick={() => navigate(`/contacts/${contact.id}/statement`)}
              className="flex items-center gap-2 rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                <rect x="6" y="9" width="12" height="7" rx="1" />
                <path d="M8 9V4h8v5" />
                <path d="M8 16v4h8v-4" />
              </svg>
              <span>{CONTACTS.printStatement}</span>
            </button>
          </div>
        </div>

        <div className="mb-4 flex flex-wrap gap-8">
          <div>
            <div className="mb-2 text-[11.5px] font-semibold text-muted">{CONTACTS.phones}</div>
            {contact.phones.map((p) => (
              <div key={p.id} dir="ltr" className="mb-1.5 flex items-center justify-end gap-1.5 text-[13.5px] font-semibold">
                <span>{p.phone}</span>
              </div>
            ))}
          </div>
          <div>
            <div className="mb-2 text-[11.5px] font-semibold text-muted">{CONTACTS.paymentMethods}</div>
            <div className="flex flex-wrap gap-2">
              {contact.paymentMethods.map((m) => (
                <span key={m.id} className="rounded-pill bg-amber-soft px-3 py-1.5 text-[12.5px] font-bold text-amber-soft-text">
                  {methodBadgeLabel(m)}
                </span>
              ))}
            </div>
          </div>
        </div>

        <div className={`rounded-xl px-5 py-4 ${BALANCE_BG_CLASS[balance.status]}`}>
          <div className="mb-1.5 text-[12.5px] font-semibold text-muted">{balance.sub}</div>
          <div className={`text-[26px] font-bold ${BALANCE_TEXT_CLASS[balance.status]}`}>{balance.amountLabel}</div>
        </div>
      </div>

      {(contact.type === 'client' || contact.type === 'both') && (
        <CreditCard
          contactId={contact.id}
          contactName={contact.name}
          onChanged={() => queryClient.invalidateQueries({ queryKey: ['contact-ledger', id] })}
        />
      )}

      {(contact.type === 'vendor' || contact.type === 'both') && (
        <VendorCreditCard
          contactId={contact.id}
          contactName={contact.name}
          onChanged={() => queryClient.invalidateQueries({ queryKey: ['contact-ledger', id] })}
        />
      )}

      <div className="rounded-card border border-border bg-white p-5">
        <h3 className="m-0 mb-3.5 text-[15px] font-bold">{CONTACTS.transactions}</h3>
        {ledger && ledger.rows.length > 0 ? (
          <>
            <div className="flex items-center gap-3 border-b border-border pb-2.5 text-xs font-bold text-muted">
              <span className="w-20 flex-shrink-0">{CONTACTS.colDate}</span>
              <span className="flex-1">{CONTACTS.colDesc}</span>
              <span className="w-28 flex-shrink-0">{CONTACTS.colDebit}</span>
              <span className="w-28 flex-shrink-0">{CONTACTS.colCredit}</span>
              <span className="w-28 flex-shrink-0">{CONTACTS.colBalance}</span>
            </div>
            {ledger.rows.map((row, i) => {
              const inner = (
                <>
                  <span className="w-20 flex-shrink-0 text-[12.5px] text-muted">{formatDateShort(row.date)}</span>
                  <span className={`flex-1 font-semibold ${row.link ? 'text-teal underline decoration-transparent transition-colors group-hover:decoration-teal' : ''}`}>
                    {row.desc}
                  </span>
                  <span className="w-28 flex-shrink-0 font-semibold">{row.debit ? <MoneyDisplay amount={row.debit} /> : '—'}</span>
                  <span className="w-28 flex-shrink-0 font-semibold">{row.credit ? <MoneyDisplay amount={row.credit} /> : '—'}</span>
                  <span className="w-28 flex-shrink-0 font-bold">
                    <MoneyDisplay amount={Math.abs(row.balance)} />
                  </span>
                </>
              );
              const rowClass = 'flex items-center gap-3 border-b border-border-soft py-2.5 text-[13px] last:border-b-0';
              return row.link ? (
                <Link key={i} to={row.link} className={`group ${rowClass} -mx-2 rounded-lg px-2 hover:bg-row-alt`}>
                  {inner}
                </Link>
              ) : (
                <div key={i} className={rowClass}>
                  {inner}
                </div>
              );
            })}
          </>
        ) : (
          <div className="py-6 text-center text-[13px] text-faint">{CONTACTS.noTransactions}</div>
        )}
      </div>

      <ContactFormModal
        open={showEdit}
        onClose={() => setShowEdit(false)}
        contact={contact}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ['contact', id] });
          queryClient.invalidateQueries({ queryKey: ['contacts'] });
        }}
      />
    </div>
  );
}
