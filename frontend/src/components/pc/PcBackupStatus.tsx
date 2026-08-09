import { useQuery } from '@tanstack/react-query';
import { PC_BACKUP } from '@/labels';

// PC EDITION ONLY. A thin banner on the Dashboard showing when the last
// database backup ran, so a silently-broken nightly job gets noticed instead
// of being discovered the day a drive dies (BUILD_PLAN.md item #2).
//
// Fed by /pc/last-backup.json, a fixed file Caddy serves from InstallDir\public
// and backup.ps1 rewrites after every run. On the cloud build this endpoint
// doesn't exist; the fetch simply fails and the banner renders nothing, so the
// component is inert anywhere but a PC install.

interface BackupStatus {
  timestamp: string;
  file: string;
  success: boolean;
}

function daysBetween(then: Date, now: Date): number {
  const ms = now.setHours(0, 0, 0, 0) - new Date(then).setHours(0, 0, 0, 0);
  return Math.round(ms / (1000 * 60 * 60 * 24));
}

function relativeLabel(iso: string): string {
  const d = daysBetween(new Date(iso), new Date());
  if (d <= 0) return PC_BACKUP.today;
  if (d === 1) return PC_BACKUP.yesterday;
  return PC_BACKUP.daysAgo(d);
}

export function PcBackupStatus() {
  const { data, isError } = useQuery<BackupStatus>({
    queryKey: ['pc-last-backup'],
    queryFn: async () => {
      const res = await fetch('/pc/last-backup.json', { cache: 'no-store' });
      if (!res.ok) throw new Error('no-status');
      return (await res.json()) as BackupStatus;
    },
    staleTime: 1000 * 60 * 30,
    retry: false,
  });

  // Not a PC install (endpoint absent) or no backup taken yet → say nothing
  // rather than nag. The "never" case only shows once we know we're on PC but
  // there's genuinely no status file — which we can't distinguish from "cloud
  // build" via a failed fetch, so failed fetch = render nothing.
  if (isError || !data) return null;

  const stale = daysBetween(new Date(data.timestamp), new Date()) > 2;
  const bad = !data.success || stale;

  const tone = bad
    ? 'border-amber bg-amber-soft text-amber-soft-text'
    : 'border-border bg-white text-muted';
  const message = !data.success
    ? PC_BACKUP.failed
    : stale
      ? PC_BACKUP.stale
      : `${PC_BACKUP.last}: ${relativeLabel(data.timestamp)}`;

  return (
    <div className={`mb-3 rounded-[10px] border px-3.5 py-2 text-[12.5px] font-semibold ${tone}`}>
      {message}
    </div>
  );
}
