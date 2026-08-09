import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useAuth } from '@/contexts/AuthContext';
import { PermGate } from './PermGate';
import { ConfirmDialog } from './ConfirmDialog';
import { useToast } from './Toast';
import { formatBytes } from '@/lib/image';
import { ATTACHMENTS, COMMON } from '@/labels';
import {
  deleteAttachment,
  listAttachments,
  uploadAttachment,
  type AttachmentRow,
} from '@/services/attachments';
import type { OrderDocType, UUID } from '@/types/database';

const MAX_BYTES = 10 * 1024 * 1024; // matches the bucket's server-side limit
const ACCEPT = 'image/*,application/pdf';

const isPdf = (row: AttachmentRow) => row.mime_type === 'application/pdf';

/**
 * المرفقات — the vendor's paper order, photographed or attached as a file.
 *
 * Two entry points on purpose: تصوير carries `capture="environment"`, which on a
 * phone jumps straight to the rear camera (the common case — the paper is on the
 * counter), while إرفاق ملف opens the normal picker for a scan, a PDF, or a
 * photo already in the gallery. On desktop both fall back to the file picker.
 */
export function AttachmentsPanel({
  orderType,
  orderId,
}: {
  orderType: OrderDocType;
  orderId: UUID;
}) {
  const queryClient = useQueryClient();
  const toast = useToast();
  // Storage paths are org-prefixed so the bucket's INSERT policy can verify
  // ownership before any attachment row exists (migration 0028).
  const { profile } = useAuth();
  const cameraRef = useRef<HTMLInputElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const [pendingDelete, setPendingDelete] = useState<AttachmentRow | null>(null);
  const [preview, setPreview] = useState<AttachmentRow | null>(null);

  const queryKey = ['attachments', orderType, orderId];
  const { data: rows = [], isLoading } = useQuery({
    queryKey,
    queryFn: () => listAttachments(orderType, orderId),
  });

  const upload = useMutation({
    mutationFn: (files: File[]) =>
      // Sequential, not parallel: several 3 MB photos at once on a shop
      // connection is how uploads time out.
      files.reduce(
        (chain, f) =>
          chain.then(() => uploadAttachment(orderType, orderId, f, profile?.org_id)),
        Promise.resolve(),
      ),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey });
      toast.show(ATTACHMENTS.uploaded, 'success');
    },
    onError: () => toast.show(ATTACHMENTS.uploadError, 'error'),
  });

  const remove = useMutation({
    mutationFn: (row: AttachmentRow) => deleteAttachment(row),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey });
      setPendingDelete(null);
      toast.show(ATTACHMENTS.deleted, 'success');
    },
    onError: () => toast.show(ATTACHMENTS.uploadError, 'error'),
  });

  function onPicked(e: React.ChangeEvent<HTMLInputElement>) {
    const picked = [...(e.target.files ?? [])];
    e.target.value = ''; // let the same file be re-picked after a failure
    if (picked.length === 0) return;

    // Check before uploading — the size limit is also enforced by the bucket,
    // but a local message beats a failed request on a slow connection.
    const tooBig = picked.find((f) => f.size > MAX_BYTES && f.type === 'application/pdf');
    if (tooBig) return toast.show(ATTACHMENTS.tooLarge, 'error');
    const badType = picked.find((f) => !f.type.startsWith('image/') && f.type !== 'application/pdf');
    if (badType) return toast.show(ATTACHMENTS.badType, 'error');

    upload.mutate(picked);
  }

  return (
    <div className="rounded-card border border-border bg-white p-5">
      <div className="mb-3.5 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="m-0 text-[15px] font-bold">{ATTACHMENTS.title}</h3>
          <p className="mt-0.5 text-[12px] text-muted">{ATTACHMENTS.subtitle}</p>
        </div>

        <PermGate need="attachments.manage">
          <div className="flex gap-2">
            <input
              ref={cameraRef}
              type="file"
              accept="image/*"
              capture="environment"
              multiple
              onChange={onPicked}
              className="hidden"
            />
            <input ref={fileRef} type="file" accept={ACCEPT} multiple onChange={onPicked} className="hidden" />

            <button
              onClick={() => cameraRef.current?.click()}
              disabled={upload.isPending}
              className="flex items-center gap-2 rounded-[10px] border-none bg-teal px-3.5 py-2 text-[13px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                <path d="M4 8h3l1.5-2h7L17 8h3v11H4V8Z" />
                <circle cx="12" cy="13" r="3.2" />
              </svg>
              <span>{ATTACHMENTS.camera}</span>
            </button>

            <button
              onClick={() => fileRef.current?.click()}
              disabled={upload.isPending}
              className="flex items-center gap-2 rounded-[10px] border border-border bg-white px-3.5 py-2 text-[13px] font-bold text-ink hover:bg-row-alt disabled:opacity-60"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                <path d="M20 11.5 12.3 19a4.5 4.5 0 0 1-6.4-6.4l7.8-7.7a3 3 0 0 1 4.2 4.2l-7.7 7.8a1.5 1.5 0 0 1-2.2-2.1l7-7" />
              </svg>
              <span>{ATTACHMENTS.pickFile}</span>
            </button>
          </div>
        </PermGate>
      </div>

      {upload.isPending && <p className="mb-3 text-[12.5px] font-semibold text-teal">{ATTACHMENTS.uploading}</p>}

      {isLoading && <p className="py-6 text-center text-[13px] text-faint">{COMMON.loading}</p>}

      {!isLoading && rows.length === 0 && !upload.isPending && (
        <p className="py-6 text-center text-[13px] text-faint">{ATTACHMENTS.none}</p>
      )}

      {rows.length > 0 && (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4">
          {rows.map((row) => (
            <div key={row.id} className="group relative overflow-hidden rounded-xl border border-border bg-sand">
              <button
                onClick={() => (isPdf(row) ? window.open(row.url ?? '', '_blank') : setPreview(row))}
                className="block w-full"
                aria-label={`${ATTACHMENTS.open}: ${row.file_name}`}
              >
                <span className="flex h-[120px] w-full items-center justify-center overflow-hidden">
                  {isPdf(row) || !row.url ? (
                    <span className="flex flex-col items-center gap-1.5 text-muted">
                      <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5Z" />
                        <path d="M14 3v5h5" />
                      </svg>
                      <span className="text-[11px] font-bold">{ATTACHMENTS.pdfBadge}</span>
                    </span>
                  ) : (
                    <img src={row.url} alt={row.file_name} className="h-full w-full object-cover" loading="lazy" />
                  )}
                </span>
              </button>

              <div className="flex items-center justify-between gap-1 border-t border-border bg-white px-2 py-1.5">
                <span className="min-w-0 truncate text-[11px] text-muted" title={row.file_name}>
                  {row.file_name}
                </span>
                <span className="flex flex-shrink-0 items-center gap-1">
                  <span className="text-[10.5px] text-faint">{formatBytes(row.byte_size)}</span>
                  <PermGate need="attachments.manage">
                    <button
                      onClick={() => setPendingDelete(row)}
                      aria-label={ATTACHMENTS.delete}
                      className="flex h-6 w-6 items-center justify-center rounded-md text-faint hover:bg-sand hover:text-red-600"
                    >
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M5 7h14M10 7V5h4v2M6 7l1 13h10l1-13" />
                      </svg>
                    </button>
                  </PermGate>
                </span>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Lightbox: tap a photo to read the handwriting at full size. */}
      {preview?.url && (
        <div
          onClick={() => setPreview(null)}
          className="fixed inset-0 z-[100] flex items-center justify-center bg-[rgba(43,38,33,0.8)] p-4"
        >
          <img
            src={preview.url}
            alt={preview.file_name}
            onClick={(e) => e.stopPropagation()}
            className="max-h-full max-w-full rounded-xl object-contain"
          />
          <button
            onClick={() => setPreview(null)}
            aria-label={ATTACHMENTS.close}
            className="absolute left-4 top-4 flex h-10 w-10 items-center justify-center rounded-full bg-white/90 text-ink"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
              <line x1="6" y1="6" x2="18" y2="18" />
              <line x1="18" y1="6" x2="6" y2="18" />
            </svg>
          </button>
          <a
            href={preview.url}
            download={preview.file_name}
            onClick={(e) => e.stopPropagation()}
            className="absolute bottom-6 rounded-pill bg-white/90 px-4 py-2 text-[13px] font-bold text-ink"
          >
            {ATTACHMENTS.download}
          </a>
        </div>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        title={ATTACHMENTS.delete}
        message={ATTACHMENTS.deleteConfirm}
        confirmLabel={COMMON.delete}
        danger
        isSubmitting={remove.isPending}
        onConfirm={() => pendingDelete && remove.mutate(pendingDelete)}
        onCancel={() => setPendingDelete(null)}
      />
    </div>
  );
}
