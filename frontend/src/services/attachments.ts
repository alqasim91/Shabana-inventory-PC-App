import { supabase } from '@/lib/supabase';
import { compressImage } from '@/lib/image';
import type { OrderAttachment, OrderDocType, UUID } from '@/types/database';

/**
 * PC EDITION. The cloud build of this file talks to Supabase Storage; this one
 * keeps the bytes in Postgres and reaches them through the pc_file_* functions
 * in migration 0101. See that migration's header for why there is no storage
 * service on a shop PC.
 *
 * The exported surface is deliberately identical to the cloud version, so
 * AttachmentsPanel and everything above it are unchanged.
 */

/**
 * Object URLs, keyed by storage path.
 *
 * Two jobs. It stops us re-fetching (and re-decoding) the same photo every time
 * the list re-renders or the query refetches, and it stops us leaking a new
 * blob URL each time - browsers hold the whole blob alive until the URL is
 * revoked, so an un-cached version would accumulate megabytes over an
 * afternoon of scrolling through orders.
 */
const objectUrls = new Map<string, string>();

export interface AttachmentRow extends OrderAttachment {
  /** Local object URL for the file's bytes; null if it could not be read. */
  url: string | null;
}

function toObjectUrl(path: string, b64: string, mime: string | null): string {
  const cached = objectUrls.get(path);
  if (cached) return cached;

  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  const url = URL.createObjectURL(new Blob([bytes], { type: mime ?? 'application/octet-stream' }));
  objectUrls.set(path, url);
  return url;
}

function forgetObjectUrl(path: string): void {
  const url = objectUrls.get(path);
  if (url) {
    URL.revokeObjectURL(url);
    objectUrls.delete(path);
  }
}

async function fileToBase64(file: Blob): Promise<string> {
  const dataUrl: string = await new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error);
    reader.onload = () => resolve(reader.result as string);
    reader.readAsDataURL(file);
  });
  // readAsDataURL gives "data:<mime>;base64,<payload>" - we want the payload.
  return dataUrl.slice(dataUrl.indexOf(',') + 1);
}

export async function listAttachments(
  orderType: OrderDocType,
  orderId: UUID,
): Promise<AttachmentRow[]> {
  const { data, error } = await supabase
    .from('order_attachments')
    .select('*')
    .eq('order_type', orderType)
    .eq('order_id', orderId)
    .order('created_at', { ascending: true });
  if (error) throw error;

  const rows = (data ?? []) as OrderAttachment[];
  if (rows.length === 0) return [];

  // One call per file. On the cloud this was one signed-URL request for the
  // whole set, but here every request is a loopback round trip to a Postgres
  // on the same machine - and an order carries a handful of downscaled photos,
  // not hundreds. Fetched in parallel, and cached above.
  return Promise.all(
    rows.map(async (r) => {
      const cached = objectUrls.get(r.storage_path);
      if (cached) return { ...r, url: cached };
      const { data: b64, error: fileErr } = await supabase.rpc('pc_file_get', {
        p_path: r.storage_path,
      });
      // A missing file must not break the whole list - the row still shows,
      // with no preview, which is the same thing a dead signed URL did.
      if (fileErr || !b64) return { ...r, url: null };
      return { ...r, url: toObjectUrl(r.storage_path, b64 as string, r.mime_type) };
    }),
  );
}

/**
 * Upload one file and register it against the order.
 *
 * Bytes first, row second - the same order the cloud version used, and for the
 * same reason: a failed insert leaves orphaned bytes (invisible, and cleaned up
 * below) whereas the reverse leaves a row pointing at nothing, which the user
 * sees as a broken thumbnail they cannot explain.
 */
export async function uploadAttachment(
  orderType: OrderDocType,
  orderId: UUID,
  file: File,
  orgId?: UUID,
): Promise<void> {
  const prepared = await compressImage(file);
  const ext = (prepared.name.split('.').pop() || 'bin').toLowerCase().slice(0, 8);
  // Org id first, exactly as on the cloud: pc_file_put rejects a path outside
  // the caller's own org folder, mirroring the storage INSERT policy in 0028.
  // Paths therefore stay identical in shape between the two editions.
  const prefix = orgId ? `${orgId}/` : '';
  const path = `${prefix}${orderType}/${orderId}/${crypto.randomUUID()}.${ext}`;

  const b64 = await fileToBase64(prepared);
  const { data: put, error: putErr } = await supabase.rpc('pc_file_put', {
    p_path: path,
    p_mime: prepared.type,
    p_b64: b64,
  });
  if (putErr) throw putErr;
  if (!put?.ok) throw new Error(`pc_file_put: ${put?.code ?? 'unknown_error'}`);

  const { error: rowErr } = await supabase.from('order_attachments').insert({
    order_type: orderType,
    order_id: orderId,
    storage_path: path,
    file_name: file.name,
    mime_type: prepared.type,
    byte_size: prepared.size,
  });
  if (rowErr) {
    await supabase.rpc('pc_file_delete', { p_path: path });
    throw rowErr;
  }
}

/**
 * Bytes first, row second.
 *
 * pc_file_delete authorizes against the caller's org (not against the
 * attachment row), so either order would work here - but deleting the bytes
 * first keeps the failure mode mild: a row left behind shows a broken
 * thumbnail the user can retry, rather than bytes nobody can ever see or
 * reclaim.
 */
export async function deleteAttachment(row: OrderAttachment): Promise<void> {
  const { data: del, error: delErr } = await supabase.rpc('pc_file_delete', {
    p_path: row.storage_path,
  });
  if (delErr) throw delErr;
  if (!del?.ok) throw new Error(`pc_file_delete: ${del?.code ?? 'unknown_error'}`);

  const { error } = await supabase.from('order_attachments').delete().eq('id', row.id);
  if (error) throw error;

  forgetObjectUrl(row.storage_path);
}
