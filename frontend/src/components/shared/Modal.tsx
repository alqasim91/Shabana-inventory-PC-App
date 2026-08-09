import type { ReactNode } from 'react';

interface ModalProps {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ReactNode;
  footer?: ReactNode;
  width?: string;
}

export function Modal({ open, onClose, title, children, footer, width = '420px' }: ModalProps) {
  if (!open) return null;

  return (
    <div
      onClick={onClose}
      className="fixed inset-0 z-[100] flex items-center justify-center bg-[rgba(43,38,33,0.45)]"
    >
      {/* Column layout with a scrolling body: a tall form (e.g. an SO with many
          lines on a phone) scrolls internally instead of pushing the footer's
          Save button off-screen with no way to reach it. */}
      <div
        onClick={(e) => e.stopPropagation()}
        className="flex max-h-[90vh] max-w-[90vw] flex-col rounded-2xl bg-white p-6"
        style={{ width }}
      >
        <div className="mb-4 flex flex-shrink-0 items-center justify-between">
          <h3 className="m-0 text-base font-bold">{title}</h3>
          <button
            onClick={onClose}
            aria-label="إغلاق"
            className="flex h-[30px] w-[30px] items-center justify-center rounded-lg border-none bg-sand text-muted"
          >
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <line x1="6" y1="6" x2="18" y2="18" />
              <line x1="18" y1="6" x2="6" y2="18" />
            </svg>
          </button>
        </div>

        <div className="flex min-h-0 flex-col gap-3.5 overflow-y-auto">{children}</div>

        {footer && <div className="mt-5 flex flex-shrink-0 gap-2.5">{footer}</div>}
      </div>
    </div>
  );
}
