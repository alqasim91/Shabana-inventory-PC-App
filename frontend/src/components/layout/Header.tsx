import { useEffect, useState } from 'react';
import { useAuth } from '@/contexts/AuthContext';
import { useSite, ALL_SITES } from '@/contexts/SiteContext';
import { GlobalSearch } from '@/components/shared/GlobalSearch';
import { AUTH, ROLE_LABEL, SEARCH, SITE } from '@/labels';

function initials(fullName: string): string {
  const parts = fullName.trim().split(/\s+/);
  return parts.slice(0, 2).map((p) => p[0]).join('.');
}

export function Header() {
  const { profile, signOut } = useAuth();
  const { sites, selectedSiteId, setSelectedSiteId, selectedSiteName } = useSite();
  const [siteMenuOpen, setSiteMenuOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);

  // ⌘K / Ctrl+K opens search from anywhere, the way the staff expect from other
  // tools. Bound on window so it works with focus anywhere on the page.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        setSearchOpen(true);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <header className="sticky top-0 z-20 flex items-center gap-4 border-b border-border bg-white px-4 py-3.5 lg:px-7">
      <div className="relative">
        <button
          onClick={() => setSiteMenuOpen((v) => !v)}
          className="flex items-center gap-2 rounded-pill border border-border bg-sand px-3.5 py-2 text-[13.5px] font-semibold text-ink"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 21s7-6.2 7-11.5A7 7 0 0 0 5 9.5C5 14.8 12 21 12 21Z" />
            <circle cx="12" cy="9.5" r="2.3" />
          </svg>
          <span>{selectedSiteName}</span>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="6,9 12,15 18,9" />
          </svg>
        </button>

        {siteMenuOpen && (
          <>
            <div className="fixed inset-0 z-10" onClick={() => setSiteMenuOpen(false)} />
            <div className="absolute top-[calc(100%+6px)] right-0 z-20 min-w-[210px] rounded-xl border border-border bg-white p-1.5 shadow-lg">
              <div
                onClick={() => {
                  setSelectedSiteId(ALL_SITES);
                  setSiteMenuOpen(false);
                }}
                className={`cursor-pointer rounded-lg px-3 py-2 text-[13.5px] font-semibold ${
                  selectedSiteId === ALL_SITES ? 'bg-teal-soft text-teal' : 'text-ink hover:bg-sand'
                }`}
              >
                {SITE.allSites}
              </div>
              {sites.map((s) => (
                <div
                  key={s.id}
                  onClick={() => {
                    setSelectedSiteId(s.id);
                    setSiteMenuOpen(false);
                  }}
                  className={`cursor-pointer rounded-lg px-3 py-2 text-[13.5px] font-semibold ${
                    selectedSiteId === s.id ? 'bg-teal-soft text-teal' : 'text-ink hover:bg-sand'
                  }`}
                >
                  {s.name_ar}
                </div>
              ))}
            </div>
          </>
        )}
      </div>

      <div className="flex-1" />

      <button
        onClick={() => setSearchOpen(true)}
        aria-label={SEARCH.open}
        title={`${SEARCH.open} (Ctrl+K)`}
        className="flex h-[38px] w-[38px] items-center justify-center rounded-[10px] border border-border bg-white text-muted hover:bg-sand hover:text-ink"
      >
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
          <circle cx="11" cy="11" r="6.5" />
          <line x1="20" y1="20" x2="16" y2="16" />
        </svg>
      </button>
      <button className="hidden h-[38px] w-[38px] items-center justify-center rounded-[10px] border border-border bg-white text-muted sm:flex">
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
          <path d="M6 10a6 6 0 0 1 12 0v4l1.5 3h-15L6 14v-4Z" />
          <path d="M10 20a2 2 0 0 0 4 0" />
        </svg>
      </button>

      <div className="relative border-r border-border pr-4">
        <button onClick={() => setUserMenuOpen((v) => !v)} className="flex items-center gap-2.5">
          <div className="flex h-9 w-9 flex-shrink-0 items-center justify-center rounded-full bg-teal text-[13px] font-bold text-white">
            {profile ? initials(profile.full_name) : '؟'}
          </div>
          <div className="hidden text-right sm:block">
            <div className="text-[13px] font-semibold">{profile?.full_name}</div>
            <div className="text-[11.5px] text-muted">{profile ? ROLE_LABEL[profile.role] : ''}</div>
          </div>
        </button>

        {userMenuOpen && (
          <>
            <div className="fixed inset-0 z-10" onClick={() => setUserMenuOpen(false)} />
            <div className="absolute top-[calc(100%+6px)] left-0 z-20 min-w-[160px] rounded-xl border border-border bg-white p-1.5 shadow-lg">
              <button
                onClick={() => void signOut()}
                className="w-full rounded-lg px-3 py-2 text-right text-[13.5px] font-semibold text-ink hover:bg-sand"
              >
                {AUTH.logout}
              </button>
            </div>
          </>
        )}
      </div>

      <GlobalSearch open={searchOpen} onClose={() => setSearchOpen(false)} />
    </header>
  );
}
