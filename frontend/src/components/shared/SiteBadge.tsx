interface SiteBadgeProps {
  name: string;
  className?: string;
}

/** Small teal pill used to mark which site/branch a row belongs to. */
export function SiteBadge({ name, className = '' }: SiteBadgeProps) {
  return (
    <span
      className={`inline-block flex-shrink-0 rounded-pill bg-teal-soft px-3 py-1 text-xs font-bold text-teal ${className}`}
    >
      {name}
    </span>
  );
}
