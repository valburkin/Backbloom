import { Thermometer, Zap } from 'lucide-react';
import { Segmented, SliderField } from '../components/ui';
import { Advanced, Section, Toggle } from '../components/form';
import { ColorSwatchField } from '../components/ColorSwatchField';
import { PageFrame } from '../components/PageFrame';
import { usePageSettings } from '../app/config';
import { isRgb, type Rgb } from '../color/color';
import { propertyAt, rangeOf } from '../schema/schema';
import { adjustmentOf, changedKeys, useLiveAdjustment, withDefaultsFor, type Obj } from './colorAdjustment';

const LABELS: Record<string, string> = {
  'color.channelAdjustment.0.gamma': 'Contrast',
  'color.channelAdjustment.0.powerLimit': 'Power limit',
};

const TEMPERATURES = [
  { value: 'disabled', label: 'Off' },
  { value: 'cold', label: 'Cool' },
  { value: 'neutral', label: 'Neutral' },
  { value: 'warm', label: 'Warm' },
  { value: 'custom', label: 'Custom' },
] as const;

const CALIBRATION: Array<{ key: string; label: string; classic: boolean }> = [
  { key: 'red', label: 'Red', classic: true },
  { key: 'green', label: 'Green', classic: true },
  { key: 'blue', label: 'Blue', classic: true },
  { key: 'cyan', label: 'Cyan', classic: false },
  { key: 'magenta', label: 'Magenta', classic: false },
  { key: 'yellow', label: 'Yellow', classic: false },
  { key: 'white', label: 'White', classic: false },
  { key: 'black', label: 'Black', classic: false },
];
const CALIBRATION_KEYS = ['classic_config', ...CALIBRATION.map((c) => c.key)];

/**
 * Setup → Calibration: what makes the strips match the screen and the power supply, the same in Ambient and Game
 * (white balance, contrast, per-color calibration, power limit). How the colors feel is the mode's, on Colors.
 */
export function CalibrationPage() {
  const ps = usePageSettings('calibration', LABELS, true);
  const rollback = useLiveAdjustment(ps);
  const adj = adjustmentOf(ps.value<Obj>('color'));
  const adjSchema = propertyAt(ps.schemaOf('color'), 'channelAdjustment', '[]');
  const setAdj = (key: string) => (v: unknown) => ps.setPath('color', ['channelAdjustment', 0, key], v);
  const setAdjAll = (next: Obj) => ps.setPath('color', ['channelAdjustment', 0], next);
  const classic = adj.classic_config !== false;
  const gamma = rangeOf(propertyAt(adjSchema, 'gamma'), { min: 0.1, max: 5, step: 0.05 });
  const calibrationMore = changedKeys(adjSchema, adj, CALIBRATION_KEYS);

  return (
    <PageFrame title="Calibration" subtitle="Make your LEDs match your screen. The same in Ambient and Game." settings={ps} onDiscard={rollback}>
      <div id="white-balance">
        <Section title="White balance and contrast" icon={<Thermometer size={18} />} description="Make white and dark scenes on the LEDs match your screen.">
          <Segmented label="Color temperature" value={String(adj.temperatureSetting ?? 'disabled')} options={TEMPERATURES} onChange={setAdj('temperatureSetting')} />
          {adj.temperatureSetting === 'custom' && (
            <div className="grid gap-4 sm:grid-cols-3">
              {(['temperatureRed', 'temperatureGreen', 'temperatureBlue'] as const).map((k) => (
                <SliderField key={k} label={k.replace('temperature', '')} unit="%" scale={100} min={0} max={1} step={0.01} value={Number(adj[k] ?? 1)} onChange={setAdj(k)} />
              ))}
            </div>
          )}
          <div id="contrast">
            <SliderField label="Contrast" min={gamma.min} max={Math.min(gamma.max, 5)} step={0.05} value={Number(adj.gamma ?? 1.5)} onChange={setAdj('gamma')} hint="Higher makes dark scenes darker and colors deeper. 1.5 suits most strips; 1.0 is neutral." />
          </div>
          <Advanced label="Per-color calibration" changed={calibrationMore.length} onReset={() => setAdjAll(withDefaultsFor(adjSchema, adj, calibrationMore))}>
            <p className="text-[13px] text-ink-muted">Pick what each pure color should look like on your strip. For example, if full red looks orange, lower its green part.</p>
            <Toggle label="Also calibrate cyan, magenta, yellow, white and black" checked={!classic} onChange={(v) => setAdj('classic_config')(!v)} />
            <div className="grid gap-2 sm:grid-cols-2">
              {CALIBRATION.filter((c) => c.classic || !classic).map((c) => {
                const def = propertyAt(adjSchema, c.key)?.default;
                return (
                  <ColorSwatchField
                    key={c.key}
                    label={c.label}
                    value={isRgb(adj[c.key]) ? (adj[c.key] as Rgb) : [255, 255, 255]}
                    defaultValue={isRgb(def) ? def : undefined}
                    onChange={(rgb) => setAdj(c.key)([...rgb])}
                  />
                );
              })}
            </div>
          </Advanced>
        </Section>
      </div>

      <div id="power">
        <Section title="Power" icon={<Zap size={18} />} description="Keep the strips within what your power supply can deliver.">
          <SliderField
            label="Power limit"
            unit="%"
            scale={100}
            min={0}
            max={1}
            step={0.01}
            value={Number(adj.powerLimit ?? 1)}
            onChange={setAdj('powerLimit')}
            hint="Dims bright scenes so the strip never draws more than this share of full power. Not a replacement for a big enough power supply."
          />
        </Section>
      </div>
    </PageFrame>
  );
}
