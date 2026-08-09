import type { ReactNode } from 'react';
import { NAV } from '@/labels';
import type { PermissionKey } from '@/types/database';

export interface NavItem {
  path: string;
  label: string;
  icon: ReactNode;
  /** Permission required to see this item; omit to show it to everyone.
   *  Hiding a page here is courtesy, not security — the page's own data is
   *  refused by RLS regardless of whether its nav entry was drawn. */
  need?: PermissionKey;
}

const iconProps = {
  width: 18,
  height: 18,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
};

export const MAIN_NAV: NavItem[] = [
  {
    path: '/dashboard',
    label: NAV.dashboard,
    icon: (
      <svg {...iconProps}>
        <rect x="3" y="3" width="8" height="8" rx="2" />
        <rect x="13" y="3" width="8" height="8" rx="2" />
        <rect x="3" y="13" width="8" height="8" rx="2" />
        <rect x="13" y="13" width="8" height="8" rx="2" />
      </svg>
    ),
  },
  {
    path: '/purchases',
    label: NAV.purchases,
    need: 'purchases.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <path d="M6 8h12l-1.2 11.2a1 1 0 0 1-1 .8H8.2a1 1 0 0 1-1-.8L6 8Z" />
        <path d="M9 8V6.5a3 3 0 0 1 6 0V8" />
      </svg>
    ),
  },
  {
    path: '/inventory',
    label: NAV.inventory,
    need: 'inventory.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <polyline points="3,7 12,3 21,7 12,11 3,7" />
        <polyline points="3,7 3,17 12,21 12,11" />
        <polyline points="21,7 21,17 12,21" />
      </svg>
    ),
  },
  {
    path: '/sales',
    label: NAV.sales,
    need: 'sales.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <rect x="5" y="3" width="14" height="18" rx="2" />
        <line x1="8" y1="8" x2="16" y2="8" />
        <line x1="8" y1="12" x2="16" y2="12" />
        <line x1="8" y1="16" x2="13" y2="16" />
      </svg>
    ),
  },
  {
    path: '/contacts',
    label: NAV.contacts,
    need: 'contacts.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="8" r="3.2" />
        <path d="M5 19c1.5-3.5 4.5-5 7-5s5.5 1.5 7 5" />
      </svg>
    ),
  },
];

/** Below MAIN_NAV for every role — reports are read-only (rule 9: staff view everything). */
export const SECONDARY_NAV: NavItem[] = [
  {
    path: '/reports',
    label: NAV.reports,
    need: 'reports.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round">
        <rect x="4" y="11" width="4" height="8" rx="1" />
        <rect x="10" y="6" width="4" height="13" rx="1" />
        <rect x="16" y="14" width="4" height="5" rx="1" />
      </svg>
    ),
  },
];

/** Manager + admin (rule 9: audit is for oversight, hidden from staff). */
export const MANAGER_NAV: NavItem[] = [
  {
    path: '/audit',
    label: NAV.audit,
    need: 'audit.view',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <path d="M9 5h6a1 1 0 0 1 1 1v0H8v0a1 1 0 0 1 1-1Z" />
        <rect x="5" y="4" width="14" height="17" rx="2" />
        <line x1="8.5" y1="10" x2="15.5" y2="10" />
        <line x1="8.5" y1="14" x2="15.5" y2="14" />
        <line x1="8.5" y1="18" x2="12.5" y2="18" />
      </svg>
    ),
  },
];

export const ADMIN_NAV: NavItem[] = [
  {
    path: '/users',
    label: NAV.users,
    need: 'users.manage',
    icon: (
      <svg {...iconProps} strokeLinecap="round" strokeLinejoin="round">
        <circle cx="9" cy="8" r="3" />
        <path d="M3.5 19c1-3 3-4.5 5.5-4.5s4.5 1.5 5.5 4.5" />
        <circle cx="17" cy="9" r="2.4" />
        <path d="M15.5 14.2c2.6.3 4 1.8 4.8 4.3" />
      </svg>
    ),
  },
  {
    path: '/settings',
    label: NAV.settings,
    need: 'settings.manage',
    icon: (
      <svg {...iconProps} strokeLinecap="round">
        <circle cx="12" cy="12" r="3" />
        <line x1="12" y1="2" x2="12" y2="5" />
        <line x1="12" y1="19" x2="12" y2="22" />
        <line x1="2" y1="12" x2="5" y2="12" />
        <line x1="19" y1="12" x2="22" y2="12" />
        <line x1="4.9" y1="4.9" x2="7" y2="7" />
        <line x1="17" y1="17" x2="19.1" y2="19.1" />
        <line x1="4.9" y1="19.1" x2="7" y2="17" />
        <line x1="17" y1="7" x2="19.1" y2="4.9" />
      </svg>
    ),
  },
];

/** Bottom tab bar (mobile) shows the 4 most-used items directly; everything else
 *  (purchases, reports, and admin-only items) lives behind the 5th "المزيد" tab —
 *  otherwise those pages have no route at all on mobile (no sidebar below `lg`). */
export const MOBILE_NAV: NavItem[] = [MAIN_NAV[0], MAIN_NAV[2], MAIN_NAV[3], MAIN_NAV[4]];

export const MOBILE_MORE_NAV: NavItem[] = [MAIN_NAV[1], ...SECONDARY_NAV];
