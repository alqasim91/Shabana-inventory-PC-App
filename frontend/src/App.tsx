import { lazy, Suspense } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient } from '@tanstack/react-query';
import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client';
import { createSyncStoragePersister } from '@tanstack/query-sync-storage-persister';
import { ErrorBoundary } from '@/components/shared/ErrorBoundary';
import { AuthProvider } from '@/contexts/AuthContext';
import { SiteProvider } from '@/contexts/SiteContext';
import { ToastProvider } from '@/components/shared/Toast';
import { ProtectedRoute, RequireRole } from '@/components/ProtectedRoute';
import { AppShell } from '@/components/layout/AppShell';
import { Login } from '@/pages/Login';
import { COMMON } from '@/labels';

// Route-level code-splitting: each page (plus its dependencies — Recharts on
// the Dashboard, react-to-print on the printable pages) lands in its own
// chunk instead of inflating the initial bundle everyone downloads at login.
const Dashboard = lazy(() => import('@/pages/Dashboard').then((m) => ({ default: m.Dashboard })));
const Purchases = lazy(() => import('@/pages/Purchases').then((m) => ({ default: m.Purchases })));
const PurchaseOrderDetail = lazy(() =>
  import('@/pages/PurchaseOrderDetail').then((m) => ({ default: m.PurchaseOrderDetail })),
);
const Inventory = lazy(() => import('@/pages/Inventory').then((m) => ({ default: m.Inventory })));
const ItemDetail = lazy(() => import('@/pages/ItemDetail').then((m) => ({ default: m.ItemDetail })));
const Sales = lazy(() => import('@/pages/Sales').then((m) => ({ default: m.Sales })));
const SalesOrderDetail = lazy(() =>
  import('@/pages/SalesOrderDetail').then((m) => ({ default: m.SalesOrderDetail })),
);
const SalesOrderInvoice = lazy(() =>
  import('@/pages/SalesOrderInvoice').then((m) => ({ default: m.SalesOrderInvoice })),
);
const Contacts = lazy(() => import('@/pages/Contacts').then((m) => ({ default: m.Contacts })));
const ContactDetail = lazy(() => import('@/pages/ContactDetail').then((m) => ({ default: m.ContactDetail })));
const ContactStatement = lazy(() =>
  import('@/pages/ContactStatement').then((m) => ({ default: m.ContactStatement })),
);
const Settings = lazy(() => import('@/pages/Settings').then((m) => ({ default: m.Settings })));
const Reports = lazy(() => import('@/pages/Reports').then((m) => ({ default: m.Reports })));
const Users = lazy(() => import('@/pages/Users').then((m) => ({ default: m.Users })));
const Audit = lazy(() => import('@/pages/Audit').then((m) => ({ default: m.Audit })));
const Platform = lazy(() => import('@/pages/Platform').then((m) => ({ default: m.Platform })));

const queryClient = new QueryClient({
  defaultOptions: {
    // Keep the last-fetched data around for a day so read-only views still
    // render from cache after a reload with no connection (rule: offline
    // views are read-only — writes still require a live Supabase session).
    queries: { gcTime: 1000 * 60 * 60 * 24 },
  },
});

const persister = createSyncStoragePersister({ storage: window.localStorage, key: 'shabana:queryCache' });

function RouteFallback() {
  return <p className="p-6 text-sm text-faint">{COMMON.loading}</p>;
}

export function App() {
  return (
    <ErrorBoundary>
      {/* `buster` invalidates any cache persisted by an older schema — bump it
          whenever a list row gains a required field (e.g. order_seq) so warm
          clients don't hydrate stale rows that predate it. */}
      <PersistQueryClientProvider
        client={queryClient}
        persistOptions={{ persister, maxAge: 1000 * 60 * 60 * 24, buster: 'order-seq-v1' }}
      >
        <ToastProvider>
          <AuthProvider>
            <SiteProvider>
              <BrowserRouter>
                <Suspense fallback={<RouteFallback />}>
                  <Routes>
                    {/* Two doors, same page. `/:orgSlug/login` is the link a
                        client is given (/shabana/login); `/login` asks for the
                        business code instead. The slug only picks which email
                        the username maps to — org membership comes from the
                        session, so a wrong slug just fails to sign in. Static
                        segments outrank dynamic ones in React Router, so this
                        cannot shadow /purchases/:id and friends. */}
                    <Route path="/login" element={<Login />} />
                    <Route path="/:orgSlug/login" element={<Login />} />

                    <Route element={<ProtectedRoute />}>
                      <Route element={<AppShell />}>
                        <Route path="/dashboard" element={<Dashboard />} />
                        <Route path="/purchases" element={<Purchases />} />
                        <Route path="/purchases/:id" element={<PurchaseOrderDetail />} />
                        <Route path="/inventory" element={<Inventory />} />
                        <Route path="/inventory/:id" element={<ItemDetail />} />
                        <Route path="/sales" element={<Sales />} />
                        <Route path="/sales/:id" element={<SalesOrderDetail />} />
                        <Route path="/sales/:id/invoice" element={<SalesOrderInvoice />} />
                        <Route path="/contacts" element={<Contacts />} />
                        <Route path="/contacts/:id" element={<ContactDetail />} />
                        <Route path="/contacts/:id/statement" element={<ContactStatement />} />
                        <Route path="/reports" element={<Reports />} />

                        <Route element={<RequireRole allow={['admin', 'manager']} />}>
                          <Route path="/audit" element={<Audit />} />
                        </Route>

                        <Route element={<RequireRole allow={['admin']} />}>
                          <Route path="/settings" element={<Settings />} />
                          <Route path="/users" element={<Users />} />
                        </Route>

                        {/* Operator console — creating CLIENT BUSINESSES, which
                            is a different authority from tenant admin, so it is
                            NOT behind RequireRole. The page checks
                            platform_admins server-side, and the Edge Function
                            refuses non-platform callers regardless. */}
                        <Route path="/platform" element={<Platform />} />
                      </Route>
                    </Route>

                    <Route path="/" element={<Navigate to="/dashboard" replace />} />
                    <Route path="*" element={<Navigate to="/dashboard" replace />} />
                  </Routes>
                </Suspense>
              </BrowserRouter>
            </SiteProvider>
          </AuthProvider>
        </ToastProvider>
      </PersistQueryClientProvider>
    </ErrorBoundary>
  );
}
