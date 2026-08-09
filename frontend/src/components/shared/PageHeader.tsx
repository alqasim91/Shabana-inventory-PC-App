import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  className?: string;
}

export function PageHeader({ title, subtitle, actions, className = '' }: PageHeaderProps) {
  return (
    <div className={`mb-5 flex flex-wrap items-end justify-between gap-3.5 ${className}`}>
      <div>
        <h1 className="m-0 text-2xl font-bold">{title}</h1>
        {subtitle && <p className="m-0 mt-1 text-[13.5px] text-muted">{subtitle}</p>}
      </div>
      {actions && <div className="flex items-center gap-2.5">{actions}</div>}
    </div>
  );
}
