import type { ReactNode } from 'react';
import { useAuth } from '@/contexts/AuthContext';
import type { PermissionKey } from '@/types/database';

interface PermGateProps {
  /** Shown when the user holds this permission. */
  need: PermissionKey;
  children: ReactNode;
  /** Rendered instead when they don't (default: nothing at all). */
  fallback?: ReactNode;
}

/**
 * Draws `children` only for a user who holds `need` (migration 0031).
 *
 * The successor to RoleGate, and the same warning applies with more force: this
 * is a UI convenience, NOT the security boundary. Every action hidden here is
 * refused again by an RLS policy, a trigger, or an RPC gate — hiding a button
 * spares someone a pointless error, it does not stop anyone.
 *
 * Which is why `can()` fails closed: while permissions are still loading, or if
 * the fetch failed, the set is empty and nothing is drawn. A moment of missing
 * buttons is a far better failure than a moment of buttons that don't work.
 */
export function PermGate({ need, children, fallback = null }: PermGateProps) {
  const { can } = useAuth();
  return <>{can(need) ? children : fallback}</>;
}

/** The hook form, for disabling a control rather than removing it. */
export function usePerm(need: PermissionKey): boolean {
  const { can } = useAuth();
  return can(need);
}

/** True if the user holds ANY of these — for a panel that several actions share. */
export function useAnyPerm(...needs: PermissionKey[]): boolean {
  const { can } = useAuth();
  return needs.some(can);
}
