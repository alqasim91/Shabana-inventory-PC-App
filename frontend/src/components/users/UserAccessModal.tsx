import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { useAuth } from '@/contexts/AuthContext';
import { useSite } from '@/contexts/SiteContext';
import { ACCESS, PERM_AREA, PERM_LABEL, ROLE_LABEL, USERS, COMMON } from '@/labels';
import {
  getUserAccess,
  listPermissionKeys,
  presetPermissions,
  saveUserAccess,
} from '@/services/access';
import type { AppRole, PermissionKey, Profile, UUID } from '@/types/database';

interface UserAccessModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  /** The user being edited. */
  user: Profile | null;
  /** Everyone else, for "copy permissions from…". */
  allUsers: Profile[];
}

const ROLES: AppRole[] = ['admin', 'manager', 'staff'];

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

function sameSet(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  const s = new Set(a);
  return b.every((x) => s.has(x));
}

export function UserAccessModal({ open, onClose, onSaved, user, allUsers }: UserAccessModalProps) {
  const { show } = useToast();
  const { profile: me } = useAuth();
  const { sites } = useSite();

  const [fullName, setFullName] = useState('');
  const [role, setRole] = useState<AppRole>('staff');
  const [active, setActive] = useState(true);
  const [perms, setPerms] = useState<Set<PermissionKey>>(new Set());
  const [allSites, setAllSites] = useState(true);
  const [siteIds, setSiteIds] = useState<Set<UUID>>(new Set());
  const [copyFrom, setCopyFrom] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const { data: catalog = [] } = useQuery({
    queryKey: ['permission-keys'],
    queryFn: listPermissionKeys,
    enabled: open,
    staleTime: Infinity, // the vocabulary only changes with a migration
  });

  const { data: access } = useQuery({
    queryKey: ['user-access', user?.user_id],
    queryFn: () => getUserAccess(user!.user_id, user!.all_sites ?? true),
    enabled: open && !!user,
  });

  // Presets come from the database so the editor and the seed-on-create trigger
  // can never disagree about what "مدير" means.
  const { data: managerPreset = [] } = useQuery({
    queryKey: ['preset', 'manager'],
    queryFn: () => presetPermissions('manager'),
    enabled: open,
    staleTime: Infinity,
  });
  const { data: staffPreset = [] } = useQuery({
    queryKey: ['preset', 'staff'],
    queryFn: () => presetPermissions('staff'),
    enabled: open,
    staleTime: Infinity,
  });

  useEffect(() => {
    if (!open || !user) return;
    setFullName(user.full_name);
    setRole(user.role);
    setActive(user.active);
    setCopyFrom('');
  }, [open, user]);

  useEffect(() => {
    if (!access) return;
    setPerms(new Set(access.permissions));
    setAllSites(access.allSites);
    setSiteIds(new Set(access.siteIds));
  }, [access]);

  const isSelf = user?.user_id === me?.user_id;
  const isAdminRole = role === 'admin';

  // Which preset, if any, the current toggles happen to match. Purely a label —
  // presets are copied, not linked, so this is describing the set, not binding
  // the user to it.
  const presetLabel = useMemo(() => {
    if (isAdminRole) return ACCESS.presetAdmin;
    const current = [...perms];
    if (sameSet(current, managerPreset)) return ACCESS.presetManager;
    if (sameSet(current, staffPreset)) return ACCESS.presetStaff;
    return ACCESS.presetCustom;
  }, [perms, managerPreset, staffPreset, isAdminRole]);

  const grouped = useMemo(() => {
    const byArea = new Map<string, typeof catalog>();
    for (const row of catalog) {
      if (!byArea.has(row.area)) byArea.set(row.area, []);
      byArea.get(row.area)!.push(row);
    }
    return [...byArea.entries()];
  }, [catalog]);

  function applyPreset(next: AppRole) {
    setRole(next);
    if (next === 'admin') return; // admins bypass the rows entirely
    setPerms(new Set(next === 'manager' ? managerPreset : staffPreset));
    setCopyFrom('');
  }

  async function applyCopy(sourceId: string) {
    setCopyFrom(sourceId);
    if (!sourceId) return;
    const source = allUsers.find((u) => u.user_id === sourceId);
    if (!source) return;
    try {
      const a = await getUserAccess(source.user_id, source.all_sites ?? true);
      setPerms(new Set(a.permissions));
      setAllSites(a.allSites);
      setSiteIds(new Set(a.siteIds));
      show(ACCESS.copied, 'success');
    } catch (err) {
      show(err instanceof Error ? err.message : ACCESS.saveError, 'error');
    }
  }

  function toggle(key: PermissionKey) {
    setPerms((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  function setArea(area: string, on: boolean) {
    const keys = catalog.filter((c) => c.area === area).map((c) => c.key);
    setPerms((prev) => {
      const next = new Set(prev);
      for (const k of keys) {
        if (on) next.add(k);
        else next.delete(k);
      }
      return next;
    });
  }

  function toggleSite(id: UUID) {
    setSiteIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function handleSave() {
    if (!user) return;
    if (!fullName.trim()) {
      show(USERS.nameRequired, 'error');
      return;
    }
    if (!allSites && siteIds.size === 0) {
      show(ACCESS.branchesRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      await saveUserAccess({
        userId: user.user_id,
        fullName,
        role,
        active,
        // An admin's rows are irrelevant to what they can do, but keeping the
        // full set stored means demoting them later starts from something sane
        // rather than from nothing.
        permissions: isAdminRole ? (catalog.map((c) => c.key) as PermissionKey[]) : [...perms],
        allSites: isAdminRole ? true : allSites,
        siteIds: [...siteIds],
      });
      show(ACCESS.saved, 'success');
      onSaved();
      onClose();
    } catch (err) {
      show(err instanceof Error ? err.message : ACCESS.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={USERS.editUser}
      width="580px"
      footer={
        <>
          <button
            onClick={onClose}
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
      {isSelf && (
        <div className="rounded-[10px] bg-amber-soft px-3.5 py-2.5 text-[12.5px] font-semibold text-amber-soft-text">
          {ACCESS.selfWarning}
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{USERS.fullName}</label>
        <input value={fullName} onChange={(e) => setFullName(e.target.value)} className={inputClass} />
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
          {ACCESS.preset} — <span className="font-bold text-teal">{presetLabel}</span>
        </label>
        <div className="flex overflow-hidden rounded-[10px] border border-border">
          {ROLES.map((r) => (
            <button
              key={r}
              type="button"
              disabled={isSelf}
              onClick={() => applyPreset(r)}
              className={`flex-1 px-3 py-2 text-[12.5px] font-bold disabled:opacity-50 ${
                role === r ? 'bg-teal text-white' : 'bg-white text-muted hover:bg-row-alt'
              }`}
            >
              {ROLE_LABEL[r]}
            </button>
          ))}
        </div>
        <p className="m-0 mt-1 text-[11.5px] text-faint">{ACCESS.presetHint}</p>
      </div>

      {!isAdminRole && (
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ACCESS.copyFrom}</label>
          <select value={copyFrom} onChange={(e) => applyCopy(e.target.value)} className={inputClass}>
            <option value="">{ACCESS.copyFromPlaceholder}</option>
            {allUsers
              .filter((u) => u.user_id !== user?.user_id)
              .map((u) => (
                <option key={u.user_id} value={u.user_id}>
                  {u.full_name} — {ROLE_LABEL[u.role]}
                </option>
              ))}
          </select>
        </div>
      )}

      {isAdminRole ? (
        <div className="rounded-[10px] bg-teal-soft px-3.5 py-3 text-[12.5px] font-semibold leading-relaxed text-teal-dark">
          {ACCESS.adminAllAccess}
        </div>
      ) : (
        <>
          <div>
            <div className="mb-1.5 flex items-center justify-between">
              <label className="text-[12.5px] font-semibold text-muted">{ACCESS.title}</label>
              <span className="text-[11.5px] text-faint">
                {perms.size.toLocaleString('ar-EG')} {ACCESS.countSelected}
              </span>
            </div>

            <div className="flex flex-col gap-2.5">
              {grouped.map(([area, rows]) => {
                const on = rows.filter((r) => perms.has(r.key)).length;
                return (
                  <div key={area} className="rounded-[10px] border border-border p-3">
                    <div className="mb-2 flex items-center justify-between">
                      <span className="text-[13px] font-bold">{PERM_AREA[area] ?? area}</span>
                      <button
                        type="button"
                        onClick={() => setArea(area, on < rows.length)}
                        className="border-none bg-transparent text-[11.5px] font-bold text-teal"
                      >
                        {on < rows.length ? ACCESS.selectAll : ACCESS.clearAll}
                      </button>
                    </div>
                    <div className="flex flex-col gap-1.5">
                      {rows.map((r) => (
                        <label key={r.key} className="flex items-center gap-2.5 text-[12.5px]">
                          <input
                            type="checkbox"
                            checked={perms.has(r.key)}
                            onChange={() => toggle(r.key)}
                            className="h-4 w-4 flex-shrink-0 accent-teal"
                          />
                          <span>{PERM_LABEL[r.key] ?? r.key}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>

          <div>
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{ACCESS.branches}</label>
            <div className="mb-2 flex overflow-hidden rounded-[10px] border border-border">
              <button
                type="button"
                onClick={() => setAllSites(true)}
                className={`flex-1 px-3 py-2 text-[12.5px] font-bold ${
                  allSites ? 'bg-teal text-white' : 'bg-white text-muted hover:bg-row-alt'
                }`}
              >
                {ACCESS.allBranches}
              </button>
              <button
                type="button"
                onClick={() => setAllSites(false)}
                className={`flex-1 px-3 py-2 text-[12.5px] font-bold ${
                  !allSites ? 'bg-teal text-white' : 'bg-white text-muted hover:bg-row-alt'
                }`}
              >
                {ACCESS.pickBranches}
              </button>
            </div>
            {!allSites && (
              <div className="flex flex-col gap-1.5 rounded-[10px] border border-border p-3">
                {sites.map((s) => (
                  <label key={s.id} className="flex items-center gap-2.5 text-[12.5px]">
                    <input
                      type="checkbox"
                      checked={siteIds.has(s.id)}
                      onChange={() => toggleSite(s.id)}
                      className="h-4 w-4 flex-shrink-0 accent-teal"
                    />
                    <span>{s.name_ar}</span>
                  </label>
                ))}
              </div>
            )}
          </div>
        </>
      )}

      <label className="flex items-center gap-2.5 text-[13.5px] font-semibold">
        <input
          type="checkbox"
          checked={active}
          onChange={(e) => setActive(e.target.checked)}
          disabled={isSelf}
          className="h-4 w-4 accent-teal"
        />
        <span>{USERS.activeLabel}</span>
      </label>
    </Modal>
  );
}
