import { supabase } from '@/lib/supabase';
import { compressImage } from '@/lib/image';
import type { OrderAttachment, OrderDocType, UUID } from '@/types/database';

const BUCKET = 'order-docs';
/** Signed-URL lifetime. Long enough to view/print, short enough that a copied link dies quickly. */
const SIGNED_URL_TTL_SECONDS = 60 * 60;

export interface AttachmentRow extends OrderAttachment {
  /** Short-lived signed URL — the bucket is private, there is no public URL. */
  url: string | null;
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

  // One round trip for every file's signed URL rather than N.
  const { data: signed } = await supabase.storage
    .from(BUCKET)
    .createSignedUrls(rows.map((r) => r.storage_path), SIGNED_URL_TTL_SECONDS);

  const urlByPath = new Map((signed ?? []).map((s) => [s.path, s.signedUrl]));
  return rows.map((r) => ({ ...r, url: urlByPath.get(r.storage_path) ?? null }));
}

/**
 * Upload one file and register it against the order.
 *
 * Storage first, row second: a failed insert leaves an orphan object (invisible,
 * costs a few KB) whereas the reverse would leave a row pointing at nothing —
 * a broken thumbnail the user can see and can't explain. If the insert does
 * fail we remove the object so the orphan doesn't linger.
 */
export async function uploadAttachment(
  orderType: OrderDocType,
  orderId: UUID,
  file: File,
  orgId?: UUID,
): Promise<void> {
  const prepared = await compressImage(file);
  const ext = (prepared.name.split('.').pop() || 'bin').toLowerCase().slice(0, 8);
  // The org id MUST be the first path segment: the storage INSERT policy
  // (migration 0028) checks `(storage.foldername(name))[1] = current_org()`.
  // It cannot check the attachment row instead, because the file is uploaded
  // before that row exists. Files uploaded before 0028 keep their old,
  // unprefixed paths and stay readable via their row — nothing is moved.
  //
  // orgId is optional purely to make the release order safe: this bundle ships
  // BEFORE the migrations, so for a few minutes profiles have no org_id yet.
  // Falling back to the legacy shape keeps uploads working against the old
  // policy in that window, instead of writing a literal "undefined/" prefix.
  const prefix = orgId ? `${orgId}/` : '';
  const path = `${prefix}${orderType}/${orderId}/${crypto.randomUUID()}.${ext}`;

  const { error: upErr } = await supabase.storage
    .from(BUCKET)
    .upload(path, prepared, { contentType: prepared.type, upsert: false });
  if (upErr) throw upErr;

  const { error: rowErr } = await supabase.from('order_attachments').insert({
    order_type: orderType,
    order_id: orderId,
    storage_path: path,
    file_name: file.name,
    mime_type: prepared.type,
    byte_size: prepared.size,
  });
  if (rowErr) {
    await supabase.storage.from(BUCKET).remove([path]);
    throw rowErr;
  }
}

/**
 * Object first, row second — the reverse of what this used to do.
 *
 * Migration 0028 made the bucket's DELETE policy require an order_attachments
 * row that belongs to your org. Deleting the row first therefore removes the
 * very thing that authorizes removing the object: the storage delete is denied
 * and the file is orphaned in the bucket forever. Unreadable (read needs the
 * row too) but never reclaimed.
 *
 * The failure mode of this order is the milder one: if the row delete fails
 * after the object is gone, the user sees a broken thumbnail and can retry,
 * rather than the app silently accumulating paid-for storage nobody can see.
 */
export async function deleteAttachment(row: OrderAttachment): Promise<void> {
  const { error: objErr } = await supabase.storage.from(BUCKET).remove([row.storage_path]);
  if (objErr) throw objErr;
  const { error } = await supabase.from('order_attachments').delete().eq('id', row.id);
  if (error) throw error;
}
