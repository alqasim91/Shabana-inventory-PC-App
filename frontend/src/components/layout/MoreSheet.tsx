import { NavLink } from 'react-router-dom';
import { MOBILE_MORE_NAV, MANAGER_NAV, ADMIN_NAV, type NavItem } from './navConfig';
import { useAuth } from '@/contexts/AuthContext';
import { NAV } from '@/labels';

interface MoreSheetProps {
  open: boolean;
  onClose: () => void;
}

/** Mobile-only bottom sheet listing the nav items that don't fit in the 4-tab bar. */
export function MoreSheet({ open, onClose }: MoreSheetProps) {
  const { can } = useAuth();
  const items: NavItem[] = [...MOBILE_MORE_NAV, ...MANAGER_NAV, ...ADMIN_NAV].filter(
    (item) => !item.need || can(item.need),
  );

  if (!open) return null;

  return (
    <div
      onClick={onClose}
      className="fixed inset-0 z-[100] flex items-end bg-[rgba(43,38,33,0.45)] lg:hidden"
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="w-full rounded-t-2xl bg-white p-4 pb-[max(1rem,env(safe-area-inset-bottom))]"
      >
        <div className="mx-auto mb-3 h-1 w-10 rounded-full bg-border" />
        <div className="mb-2 px-1 text-[13px] font-bold text-ink">{NAV.more}</div>
        {items.map((item) => (
          <NavLink
            key={item.path}
            to={item.path}
            onClick={onClose}
            className={({ isActive }) =>
              `mb-1 flex w-full items-center gap-3 rounded-[10px] px-3 py-3 text-[14px] font-medium ${
                isActive ? 'bg-sand font-bold text-teal' : 'text-ink hover:bg-sand'
              }`
            }
          >
            {item.icon}
            <span>{item.label}</span>
          </NavLink>
        ))}
      </div>
    </div>
  );
}
