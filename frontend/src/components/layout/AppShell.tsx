import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Header } from './Header';
import { BottomTabBar } from './BottomTabBar';
import { OfflineBanner } from '@/components/shared/OfflineBanner';

export function AppShell() {
  return (
    <div dir="rtl" className="flex min-h-screen bg-sand print:block print:bg-white">
      <div className="print:hidden">
        <Sidebar />
      </div>
      <div className="flex min-h-screen min-w-0 flex-1 flex-col print:block">
        <div className="print:hidden">
          <OfflineBanner />
          <Header />
        </div>
        <main className="flex-1 overflow-x-auto px-4 pb-24 pt-6 lg:px-7 lg:pb-14 print:overflow-visible print:p-0">
          <Outlet />
        </main>
      </div>
      <div className="print:hidden">
        <BottomTabBar />
      </div>
    </div>
  );
}
