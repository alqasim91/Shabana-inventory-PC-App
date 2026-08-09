import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { formatDateShort } from '@/lib/date';
import { USERS, ROLE_LABEL, COMMON } from '@/labels';
import { createUser, listProfiles } from '@/services/admin';
import { UserAccessModal } from '@/components/users/UserAccessModal';
import { isValidUsername } from '@/lib/username';
import type { AppRole, Profile } from '@/types/database';

const ROLES: AppRole[] = ['admin', 'manager', 'staff'];

const ROLE_BADGE_CLASS: Record<AppRole, string> = {
  admin: 'bg-teal-soft text-teal',
  manager: 'bg-amber-soft text-amber-soft-text',
  staff: 'bg-row-alt text-muted',
};

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function Users() {
  const queryClient = useQueryClient();
  const { show } = useToast();
  const [editing, setEditing] = useState<Profile | null>(null);

  const [submitting, setSubmitting] = useState(false);

  // Create-user modal state.
  const [creating, setCreating] = useState(false);
  const [newUsername, setNewUsername] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [newFullName, setNewFullName] = useState('');
  const [newRole, setNewRole] = useState<AppRole>('staff');

  const { data: profiles = [], isLoading } = useQuery({ queryKey: ['profiles'], queryFn: listProfiles });

  function openCreate() {
    setNewUsername('');
    setNewPassword('');
    setNewFullName('');
    setNewRole('staff');
    setCreating(true);
  }

  async function handleCreate() {
    const username = newUsername.trim().toLowerCase();
    if (!username) {
      show(USERS.usernameRequired, 'error');
      return;
    }
    if (!isValidUsername(username)) {
      show(USERS.usernameInvalid, 'error');
      return;
    }
    if (newPassword.length < 6) {
      show(USERS.passwordRequired, 'error');
      return;
    }
    if (!newFullName.trim()) {
      show(USERS.nameRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      const result = await createUser({
        username,
        password: newPassword,
        full_name: newFullName.trim(),
        role: newRole,
      });
      if (result.ok) {
        show(USERS.createdUser, 'success');
        queryClient.invalidateQueries({ queryKey: ['profiles'] });
        setCreating(false);
      } else {
        const map: Record<string, string> = {
          email_exists: USERS.emailExists,
          forbidden: USERS.forbidden,
          invalid_input: USERS.createError,
        };
        show(map[result.code] ?? result.detail ?? USERS.createError, 'error');
      }
    } catch (err) {
      show(err instanceof Error ? err.message : USERS.createError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  const columns: DataTableColumn<Profile>[] = [
    {
      key: 'name',
      header: USERS.colName,
      render: (p) => (
        <span className="flex items-center gap-2.5">
          <span className="flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full bg-teal text-[12px] font-bold text-white">
            {p.full_name.trim().charAt(0) || '؟'}
          </span>
          <span className="truncate text-[13.5px] font-semibold">{p.full_name}</span>
        </span>
      ),
    },
    {
      key: 'username',
      header: USERS.colUsername,
      width: '130px',
      render: (p) => (
        <span dir="ltr" className="block text-right text-[12.5px] text-muted">
          {p.username ?? '—'}
        </span>
      ),
    },
    {
      key: 'role',
      header: USERS.colRole,
      width: '120px',
      render: (p) => (
        <span className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${ROLE_BADGE_CLASS[p.role]}`}>
          {ROLE_LABEL[p.role]}
        </span>
      ),
    },
    {
      key: 'status',
      header: USERS.colStatus,
      width: '90px',
      render: (p) => (
        <span
          className={`rounded-pill px-2.5 py-1 text-[12px] font-bold ${
            p.active ? 'bg-success-soft text-success-text' : 'bg-[#FBE7E1] text-[#B3402C]'
          }`}
        >
          {p.active ? USERS.active : USERS.inactive}
        </span>
      ),
    },
    {
      key: 'since',
      header: USERS.colSince,
      width: '90px',
      render: (p) => <span className="text-[12.5px] text-muted">{formatDateShort(p.created_at)}</span>,
    },
  ];

  return (
    <div>
      <PageHeader
        title={USERS.title}
        subtitle={USERS.subtitle}
        actions={
          <button
            onClick={openCreate}
            className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
          >
            + {USERS.addUser}
          </button>
        }
      />

      <div className="mb-4 rounded-card border border-border bg-teal-soft px-4 py-3 text-[12.5px] leading-relaxed text-teal-dark">
        {USERS.createNote}
      </div>

      <DataTable
        columns={columns}
        data={profiles}
        rowKey={(p) => p.user_id}
        onRowClick={(p) => setEditing(p)}
        emptyMessage={USERS.noUsers}
        isLoading={isLoading}
        minWidth="560px"
      />

      <UserAccessModal
        open={editing !== null}
        onClose={() => setEditing(null)}
        onSaved={() => queryClient.invalidateQueries({ queryKey: ['profiles'] })}
        user={editing}
        allUsers={profiles}
      />

      <Modal
        open={creating}
        onClose={() => setCreating(false)}
        title={USERS.createUser}
        width="400px"
        footer={
          <>
            <button
              onClick={() => setCreating(false)}
              className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
            >
              {COMMON.cancel}
            </button>
            <button
              onClick={handleCreate}
              disabled={submitting}
              className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
            >
              {USERS.create}
            </button>
          </>
        }
      >
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{USERS.fullName}</label>
          <input value={newFullName} onChange={(e) => setNewFullName(e.target.value)} className={inputClass} />
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{USERS.username}</label>
          <input
            type="text"
            dir="ltr"
            autoCapitalize="none"
            autoCorrect="off"
            value={newUsername}
            onChange={(e) => setNewUsername(e.target.value)}
            placeholder={USERS.usernamePlaceholder}
            className={`${inputClass} text-left`}
          />
          <p className="m-0 mt-1 text-[11.5px] text-faint">{USERS.usernameHint}</p>
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{USERS.password}</label>
          <input
            type="text"
            dir="ltr"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            className={`${inputClass} text-left`}
          />
          <p className="m-0 mt-1 text-[11.5px] text-faint">{USERS.passwordHint}</p>
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{USERS.role}</label>
          <select
            value={newRole}
            onChange={(e) => setNewRole(e.target.value as AppRole)}
            className={inputClass}
          >
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {ROLE_LABEL[r]}
              </option>
            ))}
          </select>
        </div>
      </Modal>
    </div>
  );
}
