import { Cpu, Lightbulb } from 'lucide-react';
import type { Controller, LayoutSpec } from '../layout';
import { Badge } from '../components/ui';
import { deviceLine } from '../pages/welcome/summary';
import { isLampDevice, isTestDevice } from '../pages/devices/drivers';
import { SetupChoices, type AddStart } from './SetupChoices';

/** A device's strips (or lights) in one line: "Desk and monitor · 91 LEDs, Ceiling · 77 LEDs"; one-LED lights by name. */
export function stripsLine(spec: LayoutSpec, c: Controller): string {
  const strips = spec.strips.filter((s) => s.controllerId === c.id);
  if (!strips.length) {
    const leds = c.outputs.reduce((n, o) => n + o.length, 0);
    return `${leds} LED${leds === 1 ? '' : 's'}`;
  }
  return strips.map((s) => (s.ledCount > 1 ? `${s.name} · ${s.ledCount} LEDs` : s.name)).join(', ');
}

/** The real devices (not a new install's test device) of the layout. */
export function realDevices(spec: LayoutSpec): Controller[] {
  return spec.controllers.filter((c) => !isTestDevice(c));
}

/**
 * Everything added so far, one device per row with its strips or lights; `fresh` marks the devices added just now.
 * `withAddress`: where each is reached (expert tools).
 */
export function LightsList({ spec, fresh, withAddress = false }: { spec: LayoutSpec; fresh?: ReadonlySet<string>; withAddress?: boolean }) {
  return (
    <ul className="divide-y divide-line border-y border-line">
      {realDevices(spec).map((c) => {
        const lamps = isLampDevice(String(c.device?.type ?? c.kind)) || c.outputs.every((o) => o.length === 1);
        return (
          <li key={c.id} className="flex items-center gap-3 py-3">
            {lamps ? <Lightbulb size={20} className="shrink-0 text-ink-faint" aria-hidden /> : <Cpu size={20} className="shrink-0 text-ink-faint" aria-hidden />}
            <div className="min-w-0 flex-1">
              <p className="flex items-center gap-2 text-sm font-medium">
                <span className="truncate">{deviceLine(c, withAddress)}</span>
                {fresh?.has(c.id) && <Badge>New</Badge>}
              </p>
              <p className="text-xs text-ink-faint">{stripsLine(spec, c)}</p>
            </div>
          </li>
        );
      })}
    </ul>
  );
}

/**
 * "Your lights": what is added so far, and the three ways to add more (LED strips on a WLED board, room lights,
 * another kind of device). The caller puts the one primary to go on next to it. After each board or set of room lights
 * the setup comes back here (the first-run setup's Your lights, and Add device on Devices).
 */
export function YourLights({
  spec,
  fresh,
  withAddress,
  onAdd,
}: {
  spec: LayoutSpec;
  fresh?: ReadonlySet<string>;
  withAddress?: boolean;
  onAdd(kind: Exclude<AddStart, 'choose'>): void;
}) {
  return (
    <div className="space-y-5">
      <LightsList spec={spec} fresh={fresh} withAddress={withAddress} />
      <section aria-labelledby="your-lights-more" className="space-y-2">
        <h3 id="your-lights-more" className="text-sm font-semibold">
          Add more
        </h3>
        <SetupChoices onPick={onAdd} />
      </section>
    </div>
  );
}
