/**
 * Arabic-Indic digits → the ASCII digits the app stores and computes with.
 *
 * A shop in دمياط types with an Arabic keyboard, which produces ٠١٢٣٤٥٦٧٨٩
 * (U+0660–0669) — and `<input type="number">` silently refuses them, so the
 * field simply stays empty and the user thinks the app is broken. Persian
 * digits (U+06F0–06F9) come from some Arabic keyboard layouts too, so they are
 * handled alongside.
 *
 * The separators matter as much as the digits: ٫ is the Arabic decimal mark and
 * ٬ the thousands mark, and a European keyboard's comma means a decimal point
 * to most people here. All three are folded to what Number() understands.
 */

const ARABIC_INDIC = 0x0660; // ٠
const EXTENDED_ARABIC_INDIC = 0x06f0; // ۰

/** Converts any Arabic/Persian digits and separators in `input` to ASCII. */
export function toLatinDigits(input: string): string {
  let out = '';
  for (const ch of input) {
    const code = ch.codePointAt(0)!;
    if (code >= ARABIC_INDIC && code <= ARABIC_INDIC + 9) {
      out += String(code - ARABIC_INDIC);
    } else if (code >= EXTENDED_ARABIC_INDIC && code <= EXTENDED_ARABIC_INDIC + 9) {
      out += String(code - EXTENDED_ARABIC_INDIC);
    } else if (ch === '٫' || ch === ',' || ch === '،') {
      out += '.';
    } else if (ch === '٬' || ch === ' ' || ch === ' ') {
      // thousands separators — drop them rather than break the number
    } else {
      out += ch;
    }
  }
  return out;
}

/**
 * Normalizes a numeric field's raw value: Arabic digits folded to ASCII, then
 * anything that isn't part of a number removed.
 *
 * Deliberately tolerant of half-finished input — "12." and "-" and "" are all
 * returned as-is, because a person typing 12.5 passes through "12." on the way
 * and a field that erases the dot the moment you type it is unusable.
 */
export function normalizeNumericInput(raw: string): string {
  const latin = toLatinDigits(raw);
  let out = '';
  let seenDot = false;
  for (let i = 0; i < latin.length; i++) {
    const ch = latin[i];
    if (ch >= '0' && ch <= '9') {
      out += ch;
    } else if (ch === '.' && !seenDot) {
      seenDot = true;
      out += ch;
    } else if (ch === '-' && out.length === 0) {
      // Only meaningful in first position — تسوية can be a negative delta.
      out += ch;
    }
  }
  return out;
}
