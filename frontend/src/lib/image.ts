/**
 * Shrink a camera photo before uploading it.
 *
 * A phone snap of an A4 vendor order is typically 3–8 MB at 4000px wide. That's
 * a slow upload on a shop's connection and pointless storage — the paper only
 * has to stay legible, not archival. Downscaling the long edge to 2000px and
 * re-encoding as JPEG typically lands under 500 KB while keeping handwriting
 * readable.
 *
 * PDFs and anything non-image pass through untouched (a PDF is already a
 * document; re-encoding it here would be lossy and pointless).
 */

const MAX_EDGE = 2000;
const QUALITY = 0.82;

export async function compressImage(file: File): Promise<File> {
  if (!file.type.startsWith('image/')) return file;

  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    // HEIC and other formats the browser can't decode — upload the original
    // rather than failing the attachment outright.
    return file;
  }

  const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));
  // Already small enough and already a JPEG: nothing to gain from a re-encode.
  if (scale === 1 && file.type === 'image/jpeg') {
    bitmap.close();
    return file;
  }

  const canvas = document.createElement('canvas');
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  const ctx = canvas.getContext('2d');
  if (!ctx) {
    bitmap.close();
    return file;
  }
  ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();

  const blob = await new Promise<Blob | null>((resolve) =>
    canvas.toBlob(resolve, 'image/jpeg', QUALITY),
  );
  // Keep whichever is actually smaller — a re-encode can inflate a
  // well-compressed source.
  if (!blob || blob.size >= file.size) return file;

  const name = file.name.replace(/\.[^.]+$/, '') || 'photo';
  return new File([blob], `${name}.jpg`, { type: 'image/jpeg', lastModified: file.lastModified });
}

/** Human-readable size for the attachment list, in Arabic numerals. */
export function formatBytes(bytes: number): string {
  const mb = bytes / (1024 * 1024);
  if (mb >= 1) return `${mb.toLocaleString('ar-EG', { maximumFractionDigits: 1 })} م.ب`;
  return `${Math.max(1, Math.round(bytes / 1024)).toLocaleString('ar-EG')} ك.ب`;
}
