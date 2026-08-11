import { NavLink } from 'react-router-dom';
import { APP_NAME, NAV } from '@/labels';
import { MAIN_NAV, SECONDARY_NAV, MANAGER_NAV, ADMIN_NAV } from './navConfig';
import { useAuth } from '@/contexts/AuthContext';
import { useOrganization } from '@/hooks/useOrganization';

/** Desktop right-hand sidebar. Hidden below the `lg` breakpoint in favor of the bottom tab bar. */
export function Sidebar() {
  const { can } = useAuth();
  const { businessName } = useOrganization();
  // A page whose permission the user lacks is simply not listed. The lists are
  // still ordered by importance rather than by who may see them, so someone
  // with narrow access sees a short menu, not a menu full of gaps.
  const visible = (item: { need?: Parameters<typeof can>[0] }) => !item.need || can(item.need);
  const items = MAIN_NAV.filter(visible);
  const otherItems = [...SECONDARY_NAV, ...MANAGER_NAV, ...ADMIN_NAV].filter(visible);

  return (
    <aside className="sticky top-0 hidden h-screen w-[248px] flex-shrink-0 flex-col overflow-y-auto bg-teal-dark px-4 py-5.5 text-[#EAF3F0] lg:flex">
      <div className="mb-4 flex items-center gap-2.5 border-b border-white/10 px-1.5 pb-5">
        <div className="flex h-[38px] w-[38px] flex-shrink-0 items-center justify-center rounded-[10px] bg-amber text-[17px] font-bold text-teal-dark">
          ش
        </div>
        <div className="min-w-0">
          <div className="whitespace-nowrap text-[15px] font-bold text-white">{APP_NAME}</div>
          <div className="overflow-hidden text-ellipsis whitespace-nowrap text-[11.5px] text-teal-light">
            {businessName}
          </div>
        </div>
      </div>

      <div className="px-2.5 pb-2 text-[11px] font-semibold tracking-wide text-teal-faint">
        {NAV.mainSection}
      </div>

      {items.map((item) => (
        <NavLink
          key={item.path}
          to={item.path}
          className={({ isActive }) =>
            `mb-1 flex w-full items-center gap-3 rounded-[10px] px-3 py-2.5 text-sm font-medium ${
              isActive ? 'bg-white/10 font-bold text-white' : 'text-teal-light hover:bg-white/5'
            }`
          }
        >
          {item.icon}
          <span>{item.label}</span>
        </NavLink>
      ))}

      <div className="px-2.5 pb-2 pt-4 text-[11px] font-semibold tracking-wide text-teal-faint">
        {NAV.otherSection}
      </div>
      {otherItems.map((item) => (
        <NavLink
          key={item.path}
          to={item.path}
          className={({ isActive }) =>
            `mb-1 flex w-full items-center gap-3 rounded-[10px] px-3 py-2.5 text-sm font-medium ${
              isActive ? 'bg-white/10 font-bold text-white' : 'text-teal-light hover:bg-white/5'
            }`
          }
        >
          {item.icon}
          <span>{item.label}</span>
        </NavLink>
      ))}

      <div className="mt-auto border-t border-white/10 pt-4 text-[11.5px] text-teal-faint">
        {/* The APP's name, not the business's — the tenant's own name is already
            at the top of this sidebar, and hardcoding one shop's name here read
            as if every business were running مركز شبانة التجاري. */}
        {APP_NAME} &middot; الإصدار 0.1.0
      </div>
    </aside>
  );
}
