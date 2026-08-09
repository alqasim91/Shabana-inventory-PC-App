import { supabase } from '@/lib/supabase';
import type { AuditLog } from '@/types/database';

export interface AuditRow extends AuditLog {
  actorName: string | null;
}

export interface AuditFilter {
  from: string; // ISO date (inclusive)
  to: string; // ISO date (inclusive)
  entity?: string; // '' = all
  action?: string; // '' = all
  limit?: number;
}

// Reads the audit trail (manager+ only, enforced by RLS) and resolves each
// actor id to a display name via a single profiles lookup — the audit_log
// table intentionally stores only the actor uuid, not a denormalized name.
export async function listAudit(filter: AuditFilter): Promise<AuditRow[]> {
  let q = supabase
    .from('audit_log')
    .select('*')
    .gte('created_at', `${filter.from}T00:00:00`)
    .lte('created_at', `${filter.to}T23:59:59`)
    .order('created_at', { ascending: false })
    .limit(filter.limit ?? 500);

  if (filter.entity) q = q.eq('entity', filter.entity);
  if (filter.action) q = q.eq('action', filter.action);

  const { data, error } = await q;
  if (error) throw error;
  const rows = (data ?? []) as AuditLog[];

  const actorIds = [...new Set(rows.map((r) => r.actor).filter(Boolean))] as string[];
  const names = new Map<string, string>();
  if (actorIds.length) {
    const { data: profs } = await supabase
      .from('profiles')
      .select('user_id, full_name')
      .in('user_id', actorIds);
    for (const p of profs ?? []) names.set(p.user_id as string, p.full_name as string);
  }

  return rows.map((r) => ({ ...r, actorName: r.actor ? names.get(r.actor) ?? null : null }));
}
