import { useState } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { MOBILE_NAV, MOBILE_MORE_NAV, ADMIN_NAV } from './navConfig';
import { MoreSheet } from './MoreSheet';
import { NAV } from '@/labels';

const moreIcon = (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
    <circle cx="5" cy="12" r="1.8" />
    <circle cx="12" cy="12" r="1.8" />
    <circle cx="19" cy="12" r="1.8" />
  </svg>
);

const morePaths = new Set([...MOBILE_MORE_NAV, ...ADMIN_NAV].map((item) => item.path));

/** Mobile bottom tab bar; shown below the `lg` breakpoint in place of the sidebar. */
export function BottomTabBar() {
  const [moreOpen, setMoreOpen] = useState(false);
  const location = useLocation();
  const isMoreActive = morePaths.has(location.pathname);

  return (
    <>
      <nav className="fixed bottom-0 left-0 right-0 z-20 flex justify-around border-t border-border bg-white px-1 py-1 lg:hidden">
        {MOBILE_NAV.map((item) => (
          <NavLink
            key={item.path}
            to={item.path}
            className={({ isActive }) =>
              `flex min-h-[48px] min-w-[48px] flex-1 flex-col items-center justify-center gap-0.5 ${isActive ? 'text-teal' : 'text-faint'}`
            }
          >
            {({ isActive }) => (
              <>
                {item.icon}
                <span className={`text-[10px] ${isActive ? 'font-bold' : ''}`}>{item.label}</span>
              </>
            )}
          </NavLink>
        ))}
        <button
          onClick={() => setMoreOpen(true)}
          className={`flex min-h-[48px] min-w-[48px] flex-1 flex-col items-center justify-center gap-0.5 border-none bg-transparent ${isMoreActive ? 'text-teal' : 'text-faint'}`}
        >
          {moreIcon}
          <span className={`text-[10px] ${isMoreActive ? 'font-bold' : ''}`}>{NAV.more}</span>
        </button>
      </nav>
      <MoreSheet open={moreOpen} onClose={() => setMoreOpen(false)} />
    </>
  );
}
