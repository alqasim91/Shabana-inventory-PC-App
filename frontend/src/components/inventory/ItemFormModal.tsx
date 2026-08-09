import { useEffect, useState } from 'react';
import { Modal } from '@/components/shared/Modal';
import { useToast } from '@/components/shared/Toast';
import { INVENTORY, UNIT_LABEL, COMMON } from '@/labels';
import { createItem, updateItem, type ItemFormInput } from '@/services/inventory';
import type { Item, UnitType, UUID } from '@/types/database';

interface ItemFormModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: (id: UUID) => void;
  /** Omit (or null) for create mode; pass an existing item to edit it. */
  item?: Item | null;
}

const UNIT_TYPES: UnitType[] = ['kg', 'unit'];

const inputClass =
  'w-full rounded-[10px] border border-border bg-white px-3 py-2 text-[13.5px] text-ink outline-none focus:border-teal';

export function ItemFormModal({ open, onClose, onSaved, item }: ItemFormModalProps) {
  const { show } = useToast();
  const isEdit = !!item;

  const [name, setName] = useState('');
  const [unitType, setUnitType] = useState<UnitType>('unit');
  const [threshold, setThreshold] = useState('0');
  const [salePrice, setSalePrice] = useState('0');
  const [active, setActive] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setName(item?.name_ar ?? '');
    setUnitType(item?.unit_type ?? 'unit');
    setThreshold(String(item?.low_stock_threshold ?? 0));
    setSalePrice(String(item?.sale_price ?? 0));
    setActive(item?.active ?? true);
    setError(null);
  }, [open, item]);

  async function handleSubmit() {
    if (!name.trim()) {
      setError(INVENTORY.nameRequired);
      return;
    }
    setError(null);

    const input: ItemFormInput = {
      name_ar: name.trim(),
      unit_type: unitType,
      low_stock_threshold: Number(threshold) || 0,
      sale_price: Number(salePrice) || 0,
      active,
    };

    setSubmitting(true);
    try {
      const id = isEdit ? item!.id : await createItem(input);
      if (isEdit) await updateItem(item!.id, input);
      show(isEdit ? INVENTORY.savedEdit : INVENTORY.savedNew, 'success');
      onSaved(id);
      onClose();
    } catch {
      show(INVENTORY.saveError, 'error');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={isEdit ? INVENTORY.editItem : INVENTORY.addItem}
      width="440px"
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
      {error && (
        <div className="rounded-[10px] bg-[#FBE7E1] px-3.5 py-2.5 text-[12.5px] font-semibold text-[#B3402C]">
          {error}
        </div>
      )}

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{INVENTORY.itemName}</label>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={INVENTORY.itemNamePlaceholder}
          className={inputClass}
        />
      </div>

      <div>
        <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{INVENTORY.unitType}</label>
        <select value={unitType} onChange={(e) => setUnitType(e.target.value as UnitType)} className={inputClass}>
          {UNIT_TYPES.map((u) => (
            <option key={u} value={u}>
              {UNIT_LABEL[u]}
            </option>
          ))}
        </select>
      </div>

      <div className="flex gap-3">
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">
            {INVENTORY.lowStockThreshold}
          </label>
          <input
            type="number"
            inputMode="decimal"
            value={threshold}
            onChange={(e) => setThreshold(e.target.value)}
            className={inputClass}
          />
        </div>
        <div className="flex-1">
          <label className="mb-1.5 block text-[12.5px] font-semibold text-muted">{INVENTORY.salePrice}</label>
          <input
            type="number"
            inputMode="decimal"
            value={salePrice}
            onChange={(e) => setSalePrice(e.target.value)}
            className={inputClass}
          />
        </div>
      </div>

      {isEdit && (
        <label className="flex items-center gap-2 text-[13px] font-semibold text-ink">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
          {INVENTORY.active}
        </label>
      )}
    </Modal>
  );
}
