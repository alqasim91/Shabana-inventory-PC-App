import { useQuery } from '@tanstack/react-query';
import { getOrganization } from '@/services/organization';
import { ORG_NAME, ORG_INFO } from '@/labels';

export interface OrgIdentity {
  businessName: string;
  addressLine: string;
  phoneLine: string;
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

  return {
    businessName: data?.business_name || ORG_NAME,
    addressLine: data?.address_line ?? ORG_INFO.addressLine,
    phoneLine: data?.phone_line ?? ORG_INFO.phoneLine,
  };
}
