import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useToast } from '@/components/shared/Toast';
import { ORG_SETTINGS } from '@/labels';
import { CURRENCIES, TIMEZONES } from '@/lib/locale';
import { getOrganization, updateOrganization } from '@/services/organization';

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

/** Business identity editor (name / address / phone) — printed on invoices &
 *  statements and shown in the sidebar. Admin-only screen (the /settings route
 *  is already role-gated). */
export function OrgIdentityCard() {
  const queryClient = useQueryClient();
  const { show } = useToast();

  const { data: org } = useQuery({ queryKey: ['organization'], queryFn: getOrganization });

  const [businessName, setBusinessName] = useState('');
  const [addressLine, setAddressLine] = useState('');
  const [phoneLine, setPhoneLine] = useState('');
  const [currency, setCurrency] = useState('EGP');
  const [timezone, setTimezone] = useState('Africa/Cairo');
  const [submitting, setSubmitting] = useState(false);

  // Hydrate the form once the row loads.
  useEffect(() => {
    if (!org) return;
    setBusinessName(org.business_name ?? '');
    setAddressLine(org.address_line ?? '');
    setPhoneLine(org.phone_line ?? '');
    setCurrency(org.currency ?? 'EGP');
    setTimezone(org.timezone ?? 'Africa/Cairo');
  }, [org]);

  async function handleSave() {
    if (!businessName.trim()) {
      show(ORG_SETTINGS.nameRequired, 'error');
      return;
    }
    setSubmitting(true);
    try {
      await updateOrganization({
        business_name: businessName.trim(),
        address_line: addressLine.trim() || null,
        phone_line: phoneLine.trim() || null,
        currency,
        timezone,
      });
      show(ORG_SETTINGS.saved, 'success');
      queryClient.invalidateQueries({ queryKey: ['organization'] });
    } catch (err) {
      show(err instanceof Error ? err.message : ORG_SETTINGS.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="mb-6 rounded-card border border-border bg-white p-5">
      <h3 className="m-0 text-[15px] font-bold text-ink">{ORG_SETTINGS.sectionTitle}</h3>
      <p className="mb-4 mt-1 text-[12px] leading-relaxed text-muted">{ORG_SETTINGS.sectionHint}</p>

      <div className="flex flex-col gap-3.5">
        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
            {ORG_SETTINGS.businessName}
          </label>
          <input
            value={businessName}
            onChange={(e) => setBusinessName(e.target.value)}
            placeholder={ORG_SETTINGS.businessNamePlaceholder}
            className={inputClass}
          />
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
            {ORG_SETTINGS.addressLine}
          </label>
          <input
            value={addressLine}
            onChange={(e) => setAddressLine(e.target.value)}
            placeholder={ORG_SETTINGS.addressPlaceholder}
            className={inputClass}
          />
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
            {ORG_SETTINGS.phoneLine}
          </label>
          <input
            value={phoneLine}
            onChange={(e) => setPhoneLine(e.target.value)}
            placeholder={ORG_SETTINGS.phonePlaceholder}
            className={inputClass}
          />
        </div>

        <div className="flex flex-wrap gap-3">
          <div className="min-w-[180px] flex-1">
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
              {ORG_SETTINGS.currency}
            </label>
            <select value={currency} onChange={(e) => setCurrency(e.target.value)} className={inputClass}>
              {CURRENCIES.map((c) => (
                <option key={c.code} value={c.code}>
                  {c.name} ({c.symbol})
                </option>
              ))}
            </select>
            <p className="m-0 mt-1 text-[11.5px] text-faint">{ORG_SETTINGS.currencyHint}</p>
          </div>

          <div className="min-w-[180px] flex-1">
            <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
              {ORG_SETTINGS.timezone}
            </label>
            <select value={timezone} onChange={(e) => setTimezone(e.target.value)} className={inputClass}>
              {TIMEZONES.map((z) => (
                <option key={z.zone} value={z.zone}>
                  {z.name}
                </option>
              ))}
            </select>
            <p className="m-0 mt-1 text-[11.5px] text-faint">{ORG_SETTINGS.timezoneHint}</p>
          </div>
        </div>

        <div className="flex justify-start">
          <button
            onClick={handleSave}
            disabled={submitting}
            className="rounded-[10px] border-none bg-teal px-5 py-2.5 text-[13px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {ORG_SETTINGS.save}
          </button>
        </div>
      </div>
    </div>
  );
}
