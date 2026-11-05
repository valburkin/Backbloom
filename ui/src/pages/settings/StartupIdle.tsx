import { Segmented } from '../../components/ui';
import { NumberField, Select, Toggle } from '../../components/form';
import { ColorPicker } from '../../components/ColorPicker';
import type { PageSettings } from '../../app/config';
import { effectLabel } from '../../control/modes';
import { isRgb } from '../../color/color';

type Obj = Record<string, unknown>;

/** Labels of the startup and idle effect values (the save bar and problems name them). */
export const STARTUP_LABELS: Record<string, string> = {
  'foregroundEffect.duration_ms': 'Startup effect duration',
  'foregroundEffect.effect': 'Startup effect',
  'backgroundEffect.effect': 'Idle effect',
};

/** The startup effect plays this long when its duration is 0 or -1 (the backend's default). */
const DEFAULT_STARTUP_MS = 3000;

/** The startup or idle effect: on/off, then an effect or a color (and for the startup effect, how long). */
function SourceEffectForm({
  title,
  description,
  toggle,
  value,
  effects,
  set,
  withDuration,
}: {
  title: string;
  description: string;
  toggle: string;
  value: Obj;
  effects: string[];
  set: (key: string, v: unknown) => void;
  withDuration?: boolean;
}) {
  const enabled = value.enable === true;
  const ms = Number(value.duration_ms ?? DEFAULT_STARTUP_MS);
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold">{title}</h3>
        <p className="text-xs text-ink-faint">{description}</p>
      </div>
      <Toggle label={toggle} checked={enabled} onChange={(v) => set('enable', v)} />
      {enabled && (
        <>
          <Segmented
            label={`${title}: effect or color`}
            value={String(value.type ?? 'effect')}
            options={[
              { value: 'effect', label: 'Effect' },
              { value: 'color', label: 'Color' },
            ]}
            onChange={(v) => set('type', v)}
          />
          {value.type === 'color' ? (
            <div className="max-w-sm">
              <ColorPicker label={`${title} color`} compact value={isRgb(value.color) ? value.color : [255, 138, 0]} onChange={(rgb) => set('color', [...rgb])} />
            </div>
          ) : (
            <Select
              className="max-w-xs"
              label="Effect"
              value={String(value.effect ?? '')}
              options={effects.map((n) => ({ value: n, label: effectLabel(n) }))}
              onChange={(v) => set('effect', v)}
            />
          )}
          {withDuration && (
            <NumberField
              label="Duration"
              unit="s"
              min={0.5}
              step={0.5}
              value={(ms > 0 ? ms : DEFAULT_STARTUP_MS) / 1000}
              onChange={(v) => set('duration_ms', Math.round(v * 1000))}
              hint="How long it plays when Backbloom starts."
            />
          )}
        </>
      )}
    </div>
  );
}

/** Settings → General: the startup and idle effects (their own settings sections, `foregroundEffect`, `backgroundEffect`). */
export function StartupAndIdle({ ps, effects }: { ps: PageSettings; effects: string[] }) {
  return (
    <div className="grid gap-6 md:grid-cols-2">
      <SourceEffectForm
        title="Startup effect"
        description="Plays briefly when Backbloom starts, so you know it’s running."
        toggle="Play a startup effect"
        value={ps.value<Obj>('foregroundEffect')}
        effects={effects}
        set={(k, v) => ps.setPath('foregroundEffect', [k], v)}
        withDuration
      />
      <SourceEffectForm
        title="Idle effect"
        description="Shown when nothing else is, for example while screen sync is off."
        toggle="Show something when idle"
        value={ps.value<Obj>('backgroundEffect')}
        effects={effects}
        set={(k, v) => ps.setPath('backgroundEffect', [k], v)}
      />
    </div>
  );
}
