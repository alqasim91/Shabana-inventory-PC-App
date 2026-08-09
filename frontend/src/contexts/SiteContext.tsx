import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { supabase } from '@/lib/supabase';
import { useAuth } from '@/contexts/AuthContext';
import { SITE } from '@/labels';
import type { Site } from '@/types/database';

const STORAGE_KEY = 'shabana:selectedSite';
export const ALL_SITES = 'all' as const;
export type SiteSelection = string | typeof ALL_SITES;

interface SiteContextValue {
  sites: Site[];
  isLoading: boolean;
  selectedSiteId: SiteSelection;
  setSelectedSiteId: (id: SiteSelection) => void;
  selectedSiteName: string;
  /** null when "all sites" is selected — matches the get_stock/get_cash_balance/get_dashboard RPC convention. */
  selectedSiteIdForQuery: string | null;
}

const SiteContext = createContext<SiteContextValue | undefined>(undefined);

async function fetchSites(): Promise<Site[]> {
  const { data, error } = await supabase.from('sites').select('*').eq('active', true).order('name_ar');
  if (error) throw error;
  return data as Site[];
}

export function SiteProvider({ children }: { children: ReactNode }) {
  const { session } = useAuth();
  // Gated on `session`: firing before the session is restored hits RLS as
  // `anon`, which returns an empty result (not an error, since every sites
  // policy is `to authenticated`) — react-query would otherwise cache that
  // empty read and never retry once signed in.
  const { data: sites = [], isLoading } = useQuery({
    queryKey: ['sites'],
    queryFn: fetchSites,
    enabled: !!session,
  });

  const [selectedSiteId, setSelectedSiteIdState] = useState<SiteSelection>(() => {
    return (localStorage.getItem(STORAGE_KEY) as SiteSelection) || ALL_SITES;
  });

  function setSelectedSiteId(id: SiteSelection) {
    setSelectedSiteIdState(id);
    localStorage.setItem(STORAGE_KEY, id);
  }

  // If the stored site id no longer exists (e.g. deleted), fall back to "all".
  useEffect(() => {
    if (selectedSiteId === ALL_SITES || isLoading) return;
    if (sites.length > 0 && !sites.some((s) => s.id === selectedSiteId)) {
      setSelectedSiteId(ALL_SITES);
    }
  }, [sites, isLoading, selectedSiteId]);

  const selectedSiteName = useMemo(() => {
    if (selectedSiteId === ALL_SITES) return SITE.allSites;
    return sites.find((s) => s.id === selectedSiteId)?.name_ar ?? SITE.allSites;
  }, [selectedSiteId, sites]);

  const value: SiteContextValue = {
    sites,
    isLoading,
    selectedSiteId,
    setSelectedSiteId,
    selectedSiteName,
    selectedSiteIdForQuery: selectedSiteId === ALL_SITES ? null : selectedSiteId,
  };

  return <SiteContext.Provider value={value}>{children}</SiteContext.Provider>;
}

export function useSite() {
  const ctx = useContext(SiteContext);
  if (!ctx) throw new Error('useSite must be used within SiteProvider');
  return ctx;
}
