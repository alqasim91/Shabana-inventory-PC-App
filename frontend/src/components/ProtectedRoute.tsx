import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { COMMON } from '@/labels';
import type { AppRole } from '@/types/database';

export function ProtectedRoute() {
  const { session, loading } = useAuth();

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-sand text-sm text-muted">
        {COMMON.loading}
      </div>
    );
  }

  if (!session) return <Navigate to="/login" replace />;

  return <Outlet />;
}

/** Route-level role gate (defense in depth alongside RLS) — redirects rather than just hiding. */
export function RequireRole({ allow }: { allow: AppRole[] }) {
  const { profile, loading } = useAuth();

  if (loading) return null;
  if (!profile || !allow.includes(profile.role)) return <Navigate to="/dashboard" replace />;

  return <Outlet />;
}
