import { Modal } from './Modal';
import { COMMON } from '@/labels';

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: string;
  onConfirm: () => void;
  onCancel: () => void;
  confirmLabel?: string;
  /** Red-tinted confirm button for destructive actions (delete/reverse). */
  danger?: boolean;
  isSubmitting?: boolean;
}

export function ConfirmDialog({
  open,
  title,
  message,
  onConfirm,
  onCancel,
  confirmLabel = COMMON.confirm,
  danger = false,
  isSubmitting = false,
}: ConfirmDialogProps) {
  return (
    <Modal
      open={open}
      onClose={onCancel}
      title={title}
      width="380px"
      footer={
        <>
          <button
            onClick={onCancel}
            className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
          >
            {COMMON.cancel}
          </button>
          <button
            onClick={onConfirm}
            disabled={isSubmitting}
            className={`flex-1 rounded-[10px] border-none py-2.5 text-[13.5px] font-bold text-white disabled:opacity-60 ${
              danger ? 'bg-red-600 hover:bg-red-700' : 'bg-teal hover:bg-teal-hover'
            }`}
          >
            {confirmLabel}
          </button>
        </>
      }
    >
      <p className="m-0 text-sm text-muted">{message}</p>
    </Modal>
  );
}
