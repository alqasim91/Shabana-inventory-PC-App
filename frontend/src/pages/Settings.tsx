import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { Modal } from '@/components/shared/Modal';
import { MoneyDisplay } from '@/components/shared/MoneyDisplay';
import { useToast } from '@/components/shared/Toast';
import { formatDateShort } from '@/lib/date';
import { SITES_ADMIN, COMMON } from '@/labels';
import { createSite, listAllSites, updateSite } from '@/services/admin';
import { getCashBalance } from '@/services/cash';
import { OrgIdentityCard } from '@/components/settings/OrgIdentityCard';
import type { Site } from '@/types/database';

interface SiteRow extends Site {
  drawer: number;
}

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

async function listSiteRows(): Promise<SiteRow[]> {
  const sites = await listAllSites();
  const drawers = await Promise.all(sites.map((s) => getCashBalance(s.id)));
  return sites.map((s, i) => ({ ...s, drawer: drawers[i] }));
}

export function Settings() {
  const queryClient = useQueryClient();
  const { show } = useToast();
  // null = closed · 'new' = create · Site = edit
  const [editing, setEditing] = useState<Site | 'new' | null>(null);

  const [name, setName] = useState('');
  const [active, setActive] = useState(true);
  const [submitting, setSubmitting] = useState(false);

  const { data: sites = [], isLoading } = useQuery({ queryKey: ['sites-admin'], queryFn: listSiteRows });

  const isEdit = editing !== null && editing !== 'new';

  useEffect(() => {
    if (editing === null) return;
    setName(isEdit ? (editing as Site).name_ar : '');
    setActive(isEdit ? (editing as Site).active : true);
  }, [editing, isEdit]);

  async function handleSave() {
    if (!name.trim()) {
      show(SITES_ADMIN.nameRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      const input = { name_ar: name.trim(), active };
      if (isEdit) {
        await updateSite((editing as Site).id, input);
        show(SITES_ADMIN.savedEdit, 'success');
      } else {
        await createSite(input);
        show(SITES_ADMIN.savedNew, 'success');
      }
      queryClient.invalidateQueries({ queryKey: ['sites-admin'] });
      queryClient.invalidateQueries({ queryKey: ['sites'] }); // header switcher
      setEditing(null);
    } catch (err) {
      show(err instanceof Error ? err.message : SITES_ADMIN.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  const columns: DataTableColumn<SiteRow>[] = [
    {
      key: 'name',
      header: SITES_ADMIN.colName,
      render: (s) => <span className="truncate text-[13.5px] font-semibold">{s.name_ar}</span>,
    },
    {
      key: 'status',
      header: SITES_ADMIN.colStatus,
      width: '90px',
      render: (s) => (
        <span
          className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${
            s.active ? 'bg-success-soft text-success-text' : 'bg-[#FBE7E1] text-[#B3402C]'
          }`}
        >
          {s.active ? SITES_ADMIN.active : SITES_ADMIN.inactive}
        </span>
      ),
    },
    {
      key: 'drawer',
      header: SITES_ADMIN.colDrawer,
      width: '140px',
      render: (s) => <MoneyDisplay amount={s.drawer} className="text-[13.5px]" />,
    },
    {
      key: 'since',
      header: SITES_ADMIN.colSince,
      width: '90px',
      render: (s) => <span className="text-[12.5px] text-muted">{formatDateShort(s.created_at)}</span>,
    },
  ];

  return (
    <div>
      <PageHeader
        title={SITES_ADMIN.title}
        subtitle={SITES_ADMIN.subtitle}
        actions={
          <button
            onClick={() => setEditing('new')}
            className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
          >
            + {SITES_ADMIN.addSite}
          </button>
        }
      />

      <OrgIdentityCard />

      <DataTable
        columns={columns}
        data={sites}
        rowKey={(s) => s.id}
        onRowClick={(s) => setEditing(s)}
        emptyMessage={SITES_ADMIN.noSites}
        isLoading={isLoading}
        minWidth="560px"
      />

      <Modal
        open={editing !== null}
        onClose={() => setEditing(null)}
        title={isEdit ? SITES_ADMIN.editSite : SITES_ADMIN.addSite}
        width="400px"
        footer={
          <>
            <button
              onClick={() => setEditing(null)}
              className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
            >
              {COMMON.cancel}
            </button>
            <button
              onClick={handleSave}
              disabled={submitting}
              className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
            >
              {COMMON.save}
            </button>
          </>
        }
      >
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{SITES_ADMIN.name}</label>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={SITES_ADMIN.namePlaceholder}
            className={inputClass}
          />
        </div>

        <label className="flex items-center gap-2.5 text-[13.5px] font-semibold">
          <input
            type="checkbox"
            checked={active}
            onChange={(e) => setActive(e.target.checked)}
            className="h-4 w-4 accent-teal"
          />
          <span>{SITES_ADMIN.activeLabel}</span>
        </label>
        <p className="m-0 text-[11.5px] text-faint">{SITES_ADMIN.activeHint}</p>
      </Modal>
    </div>
  );
}
