import { forwardRef, type InputHTMLAttributes } from 'react';
import { normalizeNumericInput } from '@/lib/digits';

type NumberInputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'type'>;

/**
 * The app's numeric field. A drop-in for `<input type="number">`, which this
 * replaces everywhere for one reason: a native number input silently DISCARDS
 * Arabic-Indic digits. Someone typing ١٢٣ on an Arabic keyboard — the normal
 * way to type in this shop — watched the field stay empty.
 *
 * So it is a text field that accepts Arabic, Persian and ASCII digits and folds
 * them to ASCII before anyone sees the value. `inputMode="decimal"` keeps the
 * numeric keypad on phones, which is the only thing worth keeping from the
 * native type.
 *
 * The normalized value is written back onto the event before the caller's
 * onChange runs, so every existing `e.target.value` call site keeps working and
 * always reads ASCII — no caller needs to know any of this happened.
 */
export const NumberInput = forwardRef<HTMLInputElement, NumberInputProps>(
  function NumberInput({ onChange, ...rest }, ref) {
    return (
      <input
        {...rest}
        ref={ref}
        type="text"
        inputMode="decimal"
        autoComplete="off"
        dir="ltr"
        onChange={(e) => {
          const clean = normalizeNumericInput(e.target.value);
          if (clean !== e.target.value) {
            // Rewrite in place so the caret doesn't jump and the caller sees ASCII.
            e.target.value = clean;
          }
          onChange?.(e);
        }}
      />
    );
  },
);
