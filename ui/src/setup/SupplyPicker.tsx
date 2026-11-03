import { useState } from 'react';
import { Usb } from 'lucide-react';
import { ChoiceCard, Segmented } from '../components/ui';
import { NumberField } from '../components/form';
import type { Volts } from './plan';
import { COMMON_SUPPLIES, USB_SAFE_MA, supplyAmps, supplyLimitMa, type SupplyChoice } from './wled';

const same = (a: SupplyChoice | null, volts: number, amps: number) => a?.kind === 'printed' && a.volts === volts && a.amps === amps;

/** What the limit means, in one line. */
export function supplyHint(supply: SupplyChoice | null): string | null {
  if (!supply) return null;
  if (supply.kind === 'usb') return `The board keeps the LEDs under ${USB_SAFE_MA / 1000} A, safe for a USB port or phone charger.`;
  if (!(supply.amps > 0)) return null;
  return `The board keeps the LEDs under ${(supplyLimitMa(supply) / 1000).toFixed(1)} A (85%), so the supply never runs at its limit.`;
}

/**
 * "What’s printed on the power supply?" (a required answer): how its label reads, the common sizes in one click,
 * Other (volts and amps or watts), and Powered over USB (WLED's USB-safe limit).
 */
export function SupplyPicker({ value, onChange }: { value: SupplyChoice | null; onChange(s: SupplyChoice): void }) {
  const common = value?.kind === 'printed' && COMMON_SUPPLIES.some((c) => same(value, c.volts, c.amps));
  const [other, setOther] = useState(value?.kind === 'printed' && !common);
  const [unit, setUnit] = useState<'A' | 'W'>('A');
  const volts: Volts = value?.kind === 'printed' ? value.volts : 5;
  const amps = value?.kind === 'printed' && value.amps > 0 ? value.amps : undefined;
  return (
    <section aria-label="Power supply" className="space-y-3">
      <p className="text-sm font-medium">What’s printed on the power supply?</p>
      <div className="flex flex-wrap items-center gap-3">
        <span className="rounded-md border border-line-strong bg-surface-2 px-2.5 py-1 font-mono text-xs text-ink-muted" aria-hidden>
          INPUT 100-240V~ 50/60Hz · <span className="font-semibold text-ink">OUTPUT 5V ⎓ 4A</span>
        </span>
        <span className="text-xs text-ink-faint">Look for OUTPUT on its label: volts (V) and amps (A), or watts (W).</span>
      </div>
      <div role="radiogroup" aria-label="Power supply" className="grid gap-2 sm:grid-cols-4">
        {COMMON_SUPPLIES.map((c) => (
          <ChoiceCard
            key={`${c.volts}-${c.amps}`}
            active={!other && same(value, c.volts, c.amps)}
            title={`${c.volts} V · ${c.amps} A`}
            onClick={() => {
              setOther(false);
              onChange({ kind: 'printed', ...c });
            }}
          />
        ))}
        <ChoiceCard
          active={other}
          title="Other"
          onClick={() => {
            setOther(true);
            onChange({ kind: 'printed', volts, amps: amps ?? 0 });
          }}
        />
        <ChoiceCard
          active={value?.kind === 'usb'}
          title="Powered over USB"
          description="From a USB port or a phone charger"
          icon={<Usb size={15} />}
          className="sm:col-span-2"
          onClick={() => {
            setOther(false);
            onChange({ kind: 'usb' });
          }}
        />
      </div>
      {other && (
        <div className="flex flex-wrap items-end gap-3">
          <Segmented<string>
            label="Supply volts"
            value={String(volts)}
            options={['5', '12', '24'].map((v) => ({ value: v, label: `${v} V` }))}
            onChange={(v) => onChange({ kind: 'printed', volts: Number(v) as Volts, amps: amps ?? 0 })}
          />
          <NumberField
            label={unit === 'A' ? 'Amps' : 'Watts'}
            unit={unit}
            value={amps ? (unit === 'A' ? amps : Math.round(amps * volts)) : undefined}
            min={0.5}
            max={2000}
            onChange={(v) => v > 0 && onChange({ kind: 'printed', volts, amps: supplyAmps(volts, v, unit) })}
          />
          <Segmented<'A' | 'W'>
            label="Unit"
            size="sm"
            value={unit}
            options={[
              { value: 'A', label: 'Amps' },
              { value: 'W', label: 'Watts' },
            ]}
            onChange={setUnit}
          />
        </div>
      )}
      {supplyHint(value) && <p className="text-xs text-ink-faint">{supplyHint(value)}</p>}
    </section>
  );
}
