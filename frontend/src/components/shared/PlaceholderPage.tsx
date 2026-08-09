import { PageHeader } from './PageHeader';

interface PlaceholderPageProps {
  title: string;
  subtitle: string;
  note: string;
}

/** Stand-in body for tabs whose real content lands in a later build-plan phase. */
export function PlaceholderPage({ title, subtitle, note }: PlaceholderPageProps) {
  return (
    <div>
      <PageHeader title={title} subtitle={subtitle} />
      <div className="flex min-h-[240px] items-center justify-center rounded-card border border-dashed border-border bg-white text-center">
        <p className="m-0 max-w-sm px-6 text-sm text-muted">{note}</p>
      </div>
    </div>
  );
}
