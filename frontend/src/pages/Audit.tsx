import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataTable, type DataTableColumn } from '@/components/shared/DataTable';
import { ArabicDatePicker } from '@/components/shared/ArabicDatePicker';
import { formatMoney } from '@/components/shared/MoneyDisplay';
import { addDaysISO } from '@/services/reports';
import { todayISODate } from '@/lib/date';
import { listAudit, type AuditRow } from '@/services/audit';
import {
  AUDIT,
  AUDIT_ACTION_LABEL,
  AUDIT_ENTITY_LABEL,
  ROLE_LABEL,
  SO_STATUS_LABEL,
  PO_STATUS_LABEL,
} from '@/labels';
import type { AppRole, PoStatus, SoStatus } from '@/types/database';

const ACTION_CLASS: Record<string, string> = {
  insert: 'bg-success-soft text-success-text',
  update: 'bg-teal-soft text-teal',
  delete: 'bg-[#FBE7E1] text-[#B3402C]',
};

const inputClass =
  'rounded-[10px] border border-border bg-white px-3 py-2 text-[13px] text-ink outline-none focus:border-teal';

// Turn a raw audit row into a readable Arabic sentence. Defensive — if a field
// is missing it falls back to the generic verb + entity label.
function describe(row: AuditRow): string {
  const d = row.data as Record<string, unknown>;
  const verb = AUDIT_ACTION_LABEL[row.action] ?? row.action;
  const entity = AUDIT_ENTITY_LABEL[row.entity] ?? row.entity;
  const num = (k: string) => (typeof d[k] === 'number' ? (d[k] as number) : 0);
  const str = (k: string) => (d[k] == null ? '' : String(d[k]));

  switch (row.entity) {
    case 'payments':
      return `${row.action === 'delete' ? 'حذف' : 'تسجيل'} دفعة ${formatMoney(num('amount'))}`;
    case 'sales_orders': {
      const soNo = num('order_seq') ? num('order_seq').toLocaleString('ar-EG') : '';
      if (row.action === 'insert') return `إنشاء أمر بيع${soNo ? ' ' + soNo : ''}`.trim();
      return `أمر البيع${soNo ? ' ' + soNo : ''} ← ${
        SO_STATUS_LABEL[d.status as SoStatus] ?? str('status')
      }`.trim();
    }
    case 'purchase_orders': {
      const poNo = num('order_seq') ? num('order_seq').toLocaleString('ar-EG') : '';
      if (row.action === 'insert') return `إنشاء أمر شراء${poNo ? ' ' + poNo : ''}`.trim();
      return `أمر الشراء${poNo ? ' ' + poNo : ''} ← ${
        PO_STATUS_LABEL[d.status as PoStatus] ?? str('status')
      }`.trim();
    }
    case 'po_conversions':
      return `${row.action === 'delete' ? 'عكس تحويل' : 'تحويل'} مشتريات ${num('kg_consumed')} كجم`;
    case 'stock_transfers':
      return `نقل ${num('qty')} بين الفروع`;
    case 'stock_movements':
      return `تسوية مخزون ${num('qty_delta') > 0 ? '+' : ''}${num('qty_delta')}${
        str('note') ? ` — ${str('note')}` : ''
      }`;
    case 'cash_movements':
      return `حركة خزينة ${formatMoney(num('amount_delta'))}${str('reason') ? ` — ${str('reason')}` : ''}`;
    case 'client_credits': {
      const delta = num('amount_delta');
      const src =
        { overpayment: 'دفعة زائدة', deposit: 'إيداع رصيد', applied: 'استخدام رصيد', refund: 'استرداد رصيد', adjustment: 'تسوية رصيد' }[
          str('source_type')
        ] ?? 'حركة رصيد';
      return `${src}: ${delta > 0 ? '+' : '−'}${formatMoney(Math.abs(delta))}`;
    }
    case 'items':
      return `${verb} صنف: ${str('name_ar')}`;
    case 'contacts':
      return `${verb} جهة اتصال: ${str('name')}`;
    case 'profiles': {
      const role = ROLE_LABEL[d.role as AppRole] ?? str('role');
      if (row.action === 'insert') return `إنشاء مستخدم: ${str('full_name')} (${role})`;
      if (row.action === 'delete') return `حذف مستخدم: ${str('full_name')}`;
      return `تعديل مستخدم: ${str('full_name')} (${role}${d.active === false ? '، موقوف' : ''})`;
    }
    case 'sites':
      return `${verb} فرع: ${str('name_ar')}`;
    case 'organization':
      return `تعديل بيانات النشاط: ${str('business_name')}`;
    default:
      return `${verb} ${entity}`;
  }
}

function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString('ar-EG', {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function Audit() {
  const [from, setFrom] = useState(() => addDaysISO(todayISODate(), -30));
  const [to, setTo] = useState(todayISODate());
  const [entity, setEntity] = useState('');
  const [action, setAction] = useState('');

  const { data: rows = [], isLoading } = useQuery({
    queryKey: ['audit', from, to, entity, action],
    queryFn: () => listAudit({ from, to, entity, action }),
  });

  const columns: DataTableColumn<AuditRow>[] = [
    {
      key: 'when',
      header: AUDIT.colWhen,
      width: '150px',
      render: (r) => <span className="text-[12.5px] text-muted">{formatWhen(r.created_at)}</span>,
    },
    {
      key: 'user',
      header: AUDIT.colUser,
      width: '150px',
      render: (r) => (
        <span className="text-[13px] font-semibold">
          {r.actorName ?? (r.actor ? AUDIT.unknownUser : AUDIT.system)}
        </span>
      ),
    },
    {
      key: 'action',
      header: AUDIT.colAction,
      width: '130px',
      render: (r) => (
        <span className={`rounded-pill px-2.5 py-1 text-[11.5px] font-bold ${ACTION_CLASS[r.action] ?? ''}`}>
          {AUDIT_ACTION_LABEL[r.action]} · {AUDIT_ENTITY_LABEL[r.entity] ?? r.entity}
        </span>
      ),
    },
    {
      key: 'details',
      header: AUDIT.colDetails,
      render: (r) => <span className="text-[13px] text-ink">{describe(r)}</span>,
    },
  ];

  return (
    <div>
      <PageHeader
        title={AUDIT.title}
        subtitle={AUDIT.subtitle}
        actions={
          <button
            onClick={() => window.print()}
            className="rounded-[10px] border border-border bg-white px-4 py-2.5 text-[13px] font-bold text-muted hover:bg-sand"
          >
            {AUDIT.print}
          </button>
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-2.5 print:hidden">
        <div className="flex items-center gap-1.5">
          <span className="text-[12.5px] text-muted">{AUDIT.from}</span>
          <ArabicDatePicker value={from} onChange={setFrom} />
        </div>
        <div className="flex items-center gap-1.5">
          <span className="text-[12.5px] text-muted">{AUDIT.to}</span>
          <ArabicDatePicker value={to} onChange={setTo} />
        </div>
        <select value={entity} onChange={(e) => setEntity(e.target.value)} className={inputClass}>
          <option value="">{AUDIT.allEntities}</option>
          {Object.entries(AUDIT_ENTITY_LABEL).map(([k, v]) => (
            <option key={k} value={k}>
              {v}
            </option>
          ))}
        </select>
        <select value={action} onChange={(e) => setAction(e.target.value)} className={inputClass}>
          <option value="">{AUDIT.allActions}</option>
          {Object.entries(AUDIT_ACTION_LABEL).map(([k, v]) => (
            <option key={k} value={k}>
              {v}
            </option>
          ))}
        </select>
      </div>

      <DataTable
        columns={columns}
        data={rows}
        rowKey={(r) => r.id}
        emptyMessage={AUDIT.noEvents}
        isLoading={isLoading}
        minWidth="620px"
      />
    </div>
  );
}
