import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { formatDateShort } from '@/lib/date';
import { isValidOrgSlug, isValidUsername } from '@/lib/username';
import { COMMON, PLATFORM } from '@/labels';
import { createOrganization, isPlatformAdmin, listPlatformOrgs } from '@/services/platform';

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

const EMPTY = {
  slug: '',
  business_name: '',
  owner_name: '',
  owner_username: '',
  owner_password: '',
  site_name: '',
  address_line: '',
  phone_line: '',
};

/**
 * لوحة المشغّل — the operator console for onboarding CLIENT BUSINESSES.
 *
 * Not part of any tenant's app: this creates the businesses themselves. Access
 * is decided server-side by `platform_admins`; the check below only hides the
 * UI, and hiding is not security — the Edge Function refuses non-platform
 * callers regardless of what the browser renders.
 *
 * Deliberately shows metadata only (name, code, user/site counts). A platform
 * admin belongs to no organization, so current_org() returns null for them and
 * every tenant table is empty from their session — they cannot read a client's
 * contacts, orders, or money even if they wanted to.
 */
export function Platform() {
  const { show } = useToast();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState(EMPTY);

  const { data: allowed, isLoading: checking } = useQuery({
    queryKey: ['is-platform-admin'],
    queryFn: isPlatformAdmin,
  });

  const { data: orgs = [], isLoading } = useQuery({
    queryKey: ['platform-orgs'],
    queryFn: listPlatformOrgs,
    enabled: allowed === true,
  });

  const create = useMutation({
    mutationFn: () => createOrganization(form),
    onSuccess: (res) => {
      if (!res.ok) {
        show(res.code === 'slug_exists' ? PLATFORM.slugExists : PLATFORM.genericError, 'error');
        return;
      }
      queryClient.invalidateQueries({ queryKey: ['platform-orgs'] });
      setOpen(false);
      setForm(EMPTY);
      show(`${PLATFORM.created} — ${res.login_url}`, 'success');
    },
    onError: () => show(PLATFORM.genericError, 'error'),
  });

  function set<K extends keyof typeof EMPTY>(key: K, value: string) {
    setForm((f) => ({ ...f, [key]: value }));
  }

  function submit() {
    const slug = form.slug.trim().toLowerCase();
    if (!isValidOrgSlug(slug)) return show(PLATFORM.invalidSlug, 'error');
    if (!form.business_name.trim() || !form.owner_name.trim()) return show(COMMON.noData, 'error');
    if (!isValidUsername(form.owner_username.trim().toLowerCase()))
      return show(PLATFORM.invalidSlug, 'error');
    if (form.owner_password.length < 6) return show(PLATFORM.genericError, 'error');
    create.mutate();
  }

  if (checking) return <p className="text-sm text-faint">{COMMON.loading}</p>;
  if (!allowed) return <p className="text-sm text-faint">{PLATFORM.forbidden}</p>;

  return (
    <div className="max-w-[900px]">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="m-0 text-xl font-bold">{PLATFORM.title}</h1>
          <p className="m-0 mt-0.5 text-[12.5px] text-muted">{PLATFORM.subtitle}</p>
        </div>
        <button
          onClick={() => setOpen(true)}
          className="rounded-[10px] border-none bg-teal px-4 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover"
        >
          + {PLATFORM.create}
        </button>
      </div>

      <div className="rounded-card border border-border bg-white p-5">
        {isLoading ? (
          <p className="text-sm text-faint">{COMMON.loading}</p>
        ) : orgs.length === 0 ? (
          <div className="py-6 text-center text-[13px] text-faint">{PLATFORM.noOrgs}</div>
        ) : (
          <>
            <div className="flex items-center gap-3 border-b border-border pb-2.5 text-xs font-bold text-muted">
              <span className="w-28 flex-shrink-0">{PLATFORM.colSlug}</span>
              <span className="flex-1">{PLATFORM.colName}</span>
              <span className="w-20 flex-shrink-0">{PLATFORM.colUsers}</span>
              <span className="w-20 flex-shrink-0">{PLATFORM.colSites}</span>
              <span className="w-24 flex-shrink-0">{PLATFORM.colCreated}</span>
              <span className="w-20 flex-shrink-0">{PLATFORM.colStatus}</span>
            </div>
            {orgs.map((o) => (
              <div
                key={o.id}
                className="flex items-center gap-3 border-b border-border-soft py-2.5 text-[13px] last:border-b-0"
              >
                <span dir="ltr" className="w-28 flex-shrink-0 text-right font-mono text-[12px] text-teal">
                  /{o.slug}
                </span>
                <span className="flex-1 font-semibold">{o.business_name}</span>
                <span className="w-20 flex-shrink-0">{o.user_count.toLocaleString('ar-EG')}</span>
                <span className="w-20 flex-shrink-0">{o.site_count.toLocaleString('ar-EG')}</span>
                <span className="w-24 flex-shrink-0 text-[12px] text-muted">
                  {formatDateShort(o.created_at)}
                </span>
                <span className="w-20 flex-shrink-0">
                  <span
                    className={`rounded-pill px-2 py-1 text-[11.5px] font-bold ${
                      o.active ? 'bg-success-soft text-success-text' : 'bg-row-alt text-muted'
                    }`}
                  >
                    {o.active ? PLATFORM.active : PLATFORM.inactive}
                  </span>
                </span>
              </div>
            ))}
          </>
        )}
      </div>

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={PLATFORM.createTitle}
        width="440px"
        footer={
          <>
            <button
              onClick={() => setOpen(false)}
              className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
            >
              {COMMON.cancel}
            </button>
            <button
              onClick={submit}
              disabled={create.isPending}
              className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
            >
              {PLATFORM.create}
            </button>
          </>
        }
      >
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.slug}</label>
          <input
            dir="ltr"
            autoCapitalize="none"
            autoCorrect="off"
            value={form.slug}
            onChange={(e) => set('slug', e.target.value)}
            className={`${inputClass} text-left font-mono`}
            placeholder="acme"
          />
          <p className="m-0 mt-1 text-[11.5px] text-faint">{PLATFORM.slugHint}</p>
          {isValidOrgSlug(form.slug.trim().toLowerCase()) && (
            <p dir="ltr" className="m-0 mt-1 text-left text-[11.5px] font-semibold text-teal">
              /{form.slug.trim().toLowerCase()}/login · {form.owner_username.trim().toLowerCase() || 'user'}@
              {form.slug.trim().toLowerCase()}.local
            </p>
          )}
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.businessName}</label>
          <input value={form.business_name} onChange={(e) => set('business_name', e.target.value)} className={inputClass} />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.ownerName}</label>
          <input value={form.owner_name} onChange={(e) => set('owner_name', e.target.value)} className={inputClass} />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.ownerUsername}</label>
          <input
            dir="ltr"
            autoCapitalize="none"
            autoCorrect="off"
            value={form.owner_username}
            onChange={(e) => set('owner_username', e.target.value)}
            className={`${inputClass} text-left font-mono`}
          />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.ownerPassword}</label>
          <input
            type="password"
            dir="ltr"
            value={form.owner_password}
            onChange={(e) => set('owner_password', e.target.value)}
            className={`${inputClass} text-left`}
          />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.siteName}</label>
          <input
            value={form.site_name}
            onChange={(e) => set('site_name', e.target.value)}
            className={inputClass}
            placeholder="الفرع الرئيسي"
          />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.addressLine}</label>
          <input value={form.address_line} onChange={(e) => set('address_line', e.target.value)} className={inputClass} />
        </div>
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{PLATFORM.phoneLine}</label>
          <input value={form.phone_line} onChange={(e) => set('phone_line', e.target.value)} className={inputClass} />
        </div>
      </Modal>
    </div>
  );
}
