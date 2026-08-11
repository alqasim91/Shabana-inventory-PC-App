import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getOrganization } from '@/services/organization';
import { ORG_NAME, ORG_INFO } from '@/labels';
import { setOrgLocale } from '@/lib/locale';

export interface OrgIdentity {
  businessName: string;
  addressLine: string;
  phoneLine: string;
  currency: string;
  timezone: string;
}

// Live business identity for the sidebar / invoice / statement headers. Falls
// back to the labels.ts constants so headers still render before the query
// resolves, offline, or on a fresh project with no row yet. Cached long — the
// business name changes almost never.
export function useOrganization(): OrgIdentity {
  const { data } = useQuery({
    queryKey: ['organization'],
    queryFn: getOrganization,
    staleTime: 5 * 60 * 1000,
  });

  // Publish the currency and timezone to the module-level formatters as soon as
  // the business is known. formatMoney and todayISODate are called from far
  // outside the React tree (printed invoices, statements), which is why this is
  // pushed rather than provided.
  useEffect(() => {
    setOrgLocale(data?.currency, data?.timezone);
  }, [data?.currency, data?.timezone]);

  return {
    businessName: data?.business_name || ORG_NAME,
    addressLine: data?.address_line ?? ORG_INFO.addressLine,
    phoneLine: data?.phone_line ?? ORG_INFO.phoneLine,
    currency: data?.currency ?? 'EGP',
    timezone: data?.timezone ?? 'Africa/Cairo',
  };
}
