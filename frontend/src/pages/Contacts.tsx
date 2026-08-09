import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { PermGate } from '@/components/shared/PermGate';
import { ContactFormModal } from '@/components/contacts/ContactFormModal';
import { contactBalanceInfo } from '@/lib/contactBalance';
import { CONTACTS, CONTACT_TYPE_LABEL } from '@/labels';
import { listContacts, type ContactListRow } from '@/services/contacts';
import type { ContactType } from '@/types/database';

const TYPE_BADGE_CLASS: Record<ContactType, string> = {
  client: 'bg-teal-soft text-teal',
  vendor: 'bg-amber-soft text-amber-soft-text',
  both: 'bg-success-soft text-success-text',
};

const TYPE_FILTERS: (ContactType | 'all')[] = ['all', 'client', 'vendor', 'both'];

export function Contacts() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [typeFilter, setTypeFilter] = useState<ContactType | 'all'>('all');
  const [showAdd, setShowAdd] = useState(false);

  const { data: contacts = [], isLoading } = useQuery({
    queryKey: ['contacts'],
    queryFn: listContacts,
  });

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return contacts.filter((c) => {
      if (typeFilter !== 'all' && c.type !== typeFilter) return false;
      if (!q) return true;
      return c.name.toLowerCase().includes(q) || (c.phone ?? '').includes(q);
    });
  }, [contacts, search, typeFilter]);

  const columns: DataTableColumn<ContactListRow>[] = [
    {
      key: 'name',
      header: CONTACTS.colName,
      render: (c) => (
        <span className="flex min-w-0 items-center gap-2.5">
          <span className="truncate text-[13.5px] font-semibold">{c.name}</span>
          <span
            className={`flex-shrink-0 rounded-pill px-2.5 py-1 text-[11px] font-bold ${TYPE_BADGE_CLASS[c.type]}`}
          >
            {CONTACT_TYPE_LABEL[c.type]}
          </span>
        </span>
      ),
    },
    {
      key: 'phone',
      header: CONTACTS.colPhone,
      width: '150px',
      render: (c) => <span dir="ltr" className="text-[12.5px] text-muted">{c.phone ?? '—'}</span>,
    },
    {
      key: 'balance',
      header: CONTACTS.colBalance,
      width: '170px',
      render: (c) => {
        const info = contactBalanceInfo(c.balance);
        const toneClass =
          info.tone === 'positive' ? 'text-success-text' : info.tone === 'negative' ? 'text-amber-text' : 'text-muted';
        return <span className={`text-[13.5px] font-bold ${toneClass}`}>{info.amountLabel}</span>;
      },
    },
  ];

  return (
    <div>
      <PageHeader
        title={CONTACTS.title}
        subtitle={CONTACTS.subtitle}
        actions={
          <PermGate need="contacts.manage">
            <button
              onClick={() => setShowAdd(true)}
              className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
            >
              + {CONTACTS.addContact}
            </button>
          </PermGate>
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-3">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={CONTACTS.searchPlaceholder}
          className="w-full max-w-xs rounded-pill border border-border bg-white px-4 py-2 text-[13.5px] outline-none focus:border-teal"
        />
        <div className="flex gap-1.5">
          {TYPE_FILTERS.map((t) => (
            <button
              key={t}
              onClick={() => setTypeFilter(t)}
              className={`rounded-pill px-3.5 py-1.5 text-[12.5px] font-bold ${
                typeFilter === t ? 'bg-teal text-white' : 'bg-white text-muted border border-border'
              }`}
            >
              {t === 'all' ? CONTACTS.allTypes : CONTACT_TYPE_LABEL[t]}
            </button>
          ))}
        </div>
      </div>

      <DataTable
        columns={columns}
        data={filtered}
        rowKey={(c) => c.id}
        onRowClick={(c) => navigate(`/contacts/${c.id}`)}
        emptyMessage={CONTACTS.noContacts}
        isLoading={isLoading}
        minWidth="560px"
      />

      <ContactFormModal
        open={showAdd}
        onClose={() => setShowAdd(false)}
        onSaved={(id) => {
          queryClient.invalidateQueries({ queryKey: ['contacts'] });
          navigate(`/contacts/${id}`);
        }}
      />
    </div>
  );
}
