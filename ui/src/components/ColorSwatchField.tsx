import { useState } from 'react';
import { RotateCcw } from 'lucide-react';
import { contrastInk, rgbToHex, type Rgb } from '../color/color';
import { ColorPicker } from './ColorPicker';
import { Button, Dialog } from './ui';

/** A color setting shown as a swatch; clicking opens a picker. */
export function ColorSwatchField({
  label,
  value,
  onChange,
  defaultValue,
  hint,
}: {
  label: string;
  value: Rgb;
  onChange: (rgb: Rgb) => void;
  defaultValue?: Rgb;
  hint?: string;
}) {
  const [open, setOpen] = useState(false);
  const hex = rgbToHex(value);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="flex w-full items-center gap-3 rounded-2xl border border-line bg-white/[0.03] p-2.5 text-left hover:border-line-strong"
        aria-label={`${label}: ${hex}. Change`}
      >
        <span className="flex h-9 w-12 shrink-0 items-center justify-center rounded-lg font-mono text-[10px]" style={{ background: hex, color: contrastInk(value) }}>
          {hex.slice(1).toUpperCase()}
        </span>
        <span className="min-w-0">
          <span className="block text-[13px] font-medium">{label}</span>
          {hint && <span className="block truncate text-xs text-ink-faint">{hint}</span>}
        </span>
      </button>
      <Dialog
        open={open}
        title={label}
        onClose={() => setOpen(false)}
        footer={
          <>
            {defaultValue && (
              <Button variant="ghost" icon={<RotateCcw size={14} />} onClick={() => onChange(defaultValue)}>
                Reset
              </Button>
            )}
            <Button variant="primary" onClick={() => setOpen(false)}>
              Done
            </Button>
          </>
        }
      >
        <ColorPicker label={label} value={value} onChange={onChange} swatches={false} />
      </Dialog>
    </>
  );
}
