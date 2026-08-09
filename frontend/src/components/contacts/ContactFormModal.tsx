import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { CONTACT_FORM, CONTACT_TYPE_LABEL, PAYMENT_METHOD_LABEL, COMMON, CONTACTS } from '@/labels';
import { isValidEgyptianPhone } from '@/lib/phone';
import {
  createContact,
  updateContact,
  type ContactFormInput,
  type ContactPaymentMethodInput,
  type ContactPhoneInput,
  type ContactWithDetails,
} from '@/services/contacts';
import type { ContactType, PaymentMethod, UUID } from '@/types/database';

interface ContactFormModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: (id: UUID) => void;
  /** Omit (or null) for create mode; pass an existing contact to edit it. */
  contact?: ContactWithDetails | null;
}

const CONTACT_TYPES: ContactType[] = ['client', 'vendor', 'both'];
const PAYMENT_METHODS: PaymentMethod[] = ['cash', 'instapay', 'bank_transfer'];

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

function emptyPhones(): ContactPhoneInput[] {
  return [{ phone: '', is_primary: true }];
}

export function ContactFormModal({ open, onClose, onSaved, contact }: ContactFormModalProps) {
  const { show } = useToast();
  const isEdit = !!contact;

  const [type, setType] = useState<ContactType>('client');
  const [name, setName] = useState('');
  const [address, setAddress] = useState('');
  const [notes, setNotes] = useState('');
  const [phones, setPhones] = useState<ContactPhoneInput[]>(emptyPhones());
  const [methods, setMethods] = useState<ContactPaymentMethodInput[]>([]);
  const [errors, setErrors] = useState<string[]>([]);
  const [submitting, setSubmitting] = useState(false);

  // Reset to the contact's current data (or a blank form) each time the modal opens.
  useEffect(() => {
    if (!open) return;
    setType(contact?.type ?? 'client');
    setName(contact?.name ?? '');
    setAddress(contact?.address ?? '');
    setNotes(contact?.notes ?? '');
    setPhones(
      contact && contact.phones.length > 0
        ? contact.phones.map((p) => ({ phone: p.phone, is_primary: p.is_primary }))
        : emptyPhones(),
    );
    setMethods(
      contact?.paymentMethods.map((m) => ({
        method: m.method,
        instapay_number: m.instapay_number,
        bank_name: m.bank_name,
        account_number: m.account_number,
      })) ?? [],
    );
    setErrors([]);
  }, [open, contact]);

  function updatePhone(index: number, phone: string) {
    setPhones((prev) => prev.map((p, i) => (i === index ? { ...p, phone } : p)));
  }

  function addPhone() {
    setPhones((prev) => [...prev, { phone: '', is_primary: false }]);
  }

  function removePhone(index: number) {
    setPhones((prev) => prev.filter((_, i) => i !== index));
  }

  function updateMethod(index: number, patch: Partial<ContactPaymentMethodInput>) {
    setMethods((prev) => prev.map((m, i) => (i === index ? { ...m, ...patch } : m)));
  }

  function addMethod() {
    setMethods((prev) => [...prev, { method: 'cash', instapay_number: null, bank_name: null, account_number: null }]);
  }

  function removeMethod(index: number) {
    setMethods((prev) => prev.filter((_, i) => i !== index));
  }

  function validate(): string[] {
    const errs: string[] = [];
    if (!name.trim()) errs.push(CONTACT_FORM.nameRequired);

    const primaryPhone = phones[0]?.phone.trim() ?? '';
    if (!primaryPhone) errs.push(CONTACT_FORM.primaryPhoneRequired);
    else if (!isValidEgyptianPhone(primaryPhone)) errs.push(CONTACT_FORM.invalidPhone);

    for (const p of phones.slice(1)) {
      if (p.phone.trim() && !isValidEgyptianPhone(p.phone.trim())) errs.push(CONTACT_FORM.invalidPhone);
    }

    for (const m of methods) {
      if (m.method === 'instapay' && !m.instapay_number?.trim()) errs.push(CONTACT_FORM.instapayNumberRequired);
      if (m.method === 'bank_transfer' && (!m.bank_name?.trim() || !m.account_number?.trim())) {
        errs.push(CONTACT_FORM.bankDetailsRequired);
      }
    }

    return errs;
  }

  async function handleSubmit() {
    const validationErrors = validate();
    setErrors(validationErrors);
    if (validationErrors.length > 0) return;

    const input: ContactFormInput = {
      type,
      name: name.trim(),
      address: address.trim() || null,
      notes: notes.trim() || null,
      phones: [
        { phone: phones[0].phone.trim(), is_primary: true },
        ...phones.slice(1).filter((p) => p.phone.trim()).map((p) => ({ phone: p.phone.trim(), is_primary: false })),
      ],
      paymentMethods: methods,
    };

    setSubmitting(true);
    try {
      const id = isEdit ? contact!.id : await createContact(input);
      if (isEdit) await updateContact(contact!.id, input);
      show(isEdit ? CONTACT_FORM.savedEdit : CONTACT_FORM.savedNew, 'success');
      onSaved(id);
      onClose();
    } catch {
      show(CONTACT_FORM.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={isEdit ? CONTACTS.editContact : CONTACTS.addContact}
      width="480px"
      footer={
        <>
          <button
            onClick={onClose}
            className="flex-1 rounded-[10px] border border-border bg-white py-2.5 text-[13.5px] font-bold text-muted"
          >
            {COMMON.cancel}
          </button>
          <button
            onClick={handleSubmit}
            disabled={submitting}
            className="flex-1 rounded-[10px] border-none bg-teal py-2.5 text-[13.5px] font-bold text-white hover:bg-teal-hover disabled:opacity-60"
          >
            {COMMON.save}
          </button>
        </>
      }
    >
      {/* Scrolling is handled by Modal's body now. */}
      <div className="pl-1">
        {errors.length > 0 && (
          <div className="mb-3 rounded-[10px] bg-[#FBE7E1] px-3.5 py-2.5 text-[12.5px] font-semibold text-[#B3402C]">
            {errors.map((e) => (
              <div key={e}>{e}</div>
            ))}
          </div>
        )}

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.name}</label>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={CONTACT_FORM.namePlaceholder}
            className={inputClass}
          />
        </div>

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.type}</label>
          <select value={type} onChange={(e) => setType(e.target.value as ContactType)} className={inputClass}>
            {CONTACT_TYPES.map((t) => (
              <option key={t} value={t}>
                {CONTACT_TYPE_LABEL[t]}
              </option>
            ))}
          </select>
        </div>

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.primaryPhone}</label>
          <input
            value={phones[0]?.phone ?? ''}
            onChange={(e) => updatePhone(0, e.target.value)}
            dir="ltr"
            placeholder="01012345678 / 0572401180"
            className={`${inputClass} text-right`}
          />
        </div>

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.extraPhones}</label>
          {phones.slice(1).map((p, i) => (
            <div key={i} className="mb-2 flex gap-2">
              <input
                value={p.phone}
                onChange={(e) => updatePhone(i + 1, e.target.value)}
                dir="ltr"
                placeholder="01012345678 / 0572401180"
                className={`${inputClass} text-right`}
              />
              <button
                type="button"
                onClick={() => removePhone(i + 1)}
                aria-label={CONTACT_FORM.removePhone}
                className="flex-shrink-0 rounded-[10px] border border-border px-3 text-muted"
              >
                ×
              </button>
            </div>
          ))}
          <button
            type="button"
            onClick={addPhone}
            className="text-[12.5px] font-bold text-teal hover:text-teal-hover"
          >
            + {CONTACT_FORM.addPhone}
          </button>
        </div>

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.address}</label>
          <input value={address} onChange={(e) => setAddress(e.target.value)} className={inputClass} />
        </div>

        <div className="mb-3">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.notes}</label>
          <textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            rows={2}
            className={`${inputClass} resize-none`}
          />
        </div>

        <div>
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{CONTACT_FORM.paymentMethod}</label>
          {methods.map((m, i) => (
            <div key={i} className="mb-2 rounded-[10px] border border-border p-2.5">
              <div className="mb-2 flex gap-2">
                <select
                  value={m.method}
                  onChange={(e) => updateMethod(i, { method: e.target.value as PaymentMethod })}
                  className={inputClass}
                >
                  {PAYMENT_METHODS.map((pm) => (
                    <option key={pm} value={pm}>
                      {PAYMENT_METHOD_LABEL[pm]}
                    </option>
                  ))}
                </select>
                <button
                  type="button"
                  onClick={() => removeMethod(i)}
                  aria-label={CONTACT_FORM.removePaymentMethod}
                  className="flex-shrink-0 rounded-[10px] border border-border px-3 text-muted"
                >
                  ×
                </button>
              </div>
              {m.method === 'instapay' && (
                <input
                  value={m.instapay_number ?? ''}
                  onChange={(e) => updateMethod(i, { instapay_number: e.target.value })}
                  placeholder={CONTACT_FORM.instapayNumber}
                  dir="ltr"
                  className={`${inputClass} text-right`}
                />
              )}
              {m.method === 'bank_transfer' && (
                <div className="flex gap-2">
                  <input
                    value={m.bank_name ?? ''}
                    onChange={(e) => updateMethod(i, { bank_name: e.target.value })}
                    placeholder={CONTACT_FORM.bankName}
                    className={inputClass}
                  />
                  <input
                    value={m.account_number ?? ''}
                    onChange={(e) => updateMethod(i, { account_number: e.target.value })}
                    placeholder={CONTACT_FORM.accountNumber}
                    dir="ltr"
                    className={`${inputClass} text-right`}
                  />
                </div>
              )}
            </div>
          ))}
          <button
            type="button"
            onClick={addMethod}
            className="text-[12.5px] font-bold text-teal hover:text-teal-hover"
          >
            + {CONTACT_FORM.addPaymentMethod}
          </button>
        </div>
      </div>
    </Modal>
  );
}
