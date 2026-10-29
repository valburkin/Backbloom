import { useEffect, useId, useRef } from 'react';
import { Palette, Thermometer, Waves } from 'lucide-react';
import { Segmented, SliderField } from '../components/ui';
import { Advanced, Expert, Section, Select, Toggle } from '../components/form';
import { ColorSwatchField } from '../components/ColorSwatchField';
import { PageFrame } from '../components/PageFrame';
import { usePageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useExpertTools } from '../app/expert';
import { useThrottled } from '../app/useThrottled';
import { isRgb, type Rgb } from '../color/color';
import { dependenciesMet, differsFromDefault, propertyAt, rangeOf, withDefaults } from '../schema/schema';
import { deepEqual } from '../settings/drafts';
import { applyIntensity, detectIntensity, formatSeconds } from '../settings/intensity';
import { MIN_SPRING_MS, SPRING_TYPES, withTransition } from '../settings/smoothing';
import { IntensityPicker } from '../control/IntensityPicker';
import type { JsonSchema } from '../api/types';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'color.channelAdjustment.0.scaleOutput': 'Brightness boost',
  'color.channelAdjustment.0.saturationGain': 'Saturation',
  'color.channelAdjustment.0.luminanceGain': 'Color brightness',
  'color.channelAdjustment.0.gamma': 'Contrast',
  'color.channelAdjustment.0.backlightThreshold': 'Minimum glow',
  'color.channelAdjustment.0.powerLimit': 'Power limit',
  'smoothing.time_ms': 'Transitions',
  'smoothing.updateFrequency': 'LED updates',
  'smoothing.smoothingFactor': 'Smoothing factor',
  'smoothing.stiffness': 'Stiffness',
  'smoothing.damping': 'Damping',
  'smoothing.y_limit': 'Brightness change limit',
};

/** Friendly names for the backend's smoothing algorithms; only types the backend offers are listed. */
const SMOOTHING_TYPES: Record<string, { label: string; hint: string }> = {
  HybridRgbInterpolator: { label: 'Natural (recommended)', hint: 'Colors glide like they’re on a soft spring: quick to react, no harsh jumps.' },
  HybridInterpolator: { label: 'Natural, no flashes', hint: 'Like Natural, and also holds back sudden flashes of brightness.' },
  Stepper: { label: 'Linear', hint: 'Moves at a constant speed toward each new color.' },
  RgbInterpolator: { label: 'Fluid', hint: 'Smooth transitions with an adjustable amount of easing.' },
  YuvInterpolator: { label: 'Fluid, no flashes', hint: 'Fluid transitions that hold back sudden changes of brightness.' },
  ExponentialInterpolator: { label: 'Classic', hint: 'Reacts fast to big changes and slows down near the target.' },
};

const TEMPERATURES = [
  { value: 'disabled', label: 'None' },
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

/** Fields the `adjustment` command accepts (it applies them immediately). */
const LIVE_KEYS = [
  'classic_config', 'red', 'green', 'blue', 'cyan', 'magenta', 'yellow', 'white', 'black', 'scaleOutput', 'gamma', 'backlightThreshold',
  'backlightColored', 'luminanceGain', 'saturationGain', 'temperatureSetting', 'temperatureRed', 'temperatureGreen', 'temperatureBlue', 'powerLimit',
];

/** The Picture section's More options; saturation and color brightness count only when no Intensity preset sets them. */
const PICTURE_MORE = ['saturationGain', 'luminanceGain', 'gamma', 'backlightThreshold', 'backlightColored', 'powerLimit'];
const TRANSITION_MORE = ['updateFrequency', 'antiFlickeringFilter', 'type'];
const CALIBRATION_KEYS = ['classic_config', ...CALIBRATION.map((c) => c.key)];

function liveAdjustment(adj: Obj): Obj {
  const out: Obj = {};
  for (const k of LIVE_KEYS) if (adj[k] !== undefined) out[k] = adj[k];
  if (typeof out.gamma === 'number') out.gamma = Math.min(5, out.gamma);
  return out;
}

/** Keys of `value` that differ from their defaults in `schema` (an object schema). */
function changedKeys(schema: JsonSchema | undefined, value: Obj, keys: readonly string[]): string[] {
  return keys.filter((k) => {
    const s = propertyAt(schema, k);
    return !!s && differsFromDefault(s, value[k]);
  });
}

function withDefaultsFor(schema: JsonSchema | undefined, value: Obj, keys: readonly string[]): Obj {
  return { ...value, ...Object.fromEntries(keys.map((k) => [k, withDefaults(propertyAt(schema, k), undefined)])) };
}

/**
 * Transition time with "Instant" at the left end: Instant turns smoothing off, any time turns it on with that time
 * (and the matching spring of the Natural styles).
 */
function TransitionSlider({ enabled, ms, min, max, onChange }: { enabled: boolean; ms: number; min: number; max: number; onChange: (ms: number | null) => void }) {
  const id = useId();
  const step = 50;
  const instant = min - step;
  const value = enabled ? Math.max(min, ms) : instant;
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-3">
        <label htmlFor={id} className="text-sm font-medium text-ink">
          Transition time
        </label>
        <span className="text-sm tabular-nums text-ink-muted">{enabled ? formatSeconds(ms) : 'Instant'}</span>
      </div>
      <input
        id={id}
        type="range"
        className="w-full"
        min={instant}
        max={max}
        step={step}
        value={value}
        aria-valuetext={enabled ? formatSeconds(ms) : 'Instant'}
        onChange={(e) => {
          const v = Number(e.target.value);
          onChange(v < min ? null : v);
        }}
      />
      <div className="flex justify-between text-[11px] text-ink-faint" aria-hidden>
        <span>Instant</span>
        <span>Relaxed</span>
      </div>
      <p className="text-xs text-ink-faint">How gently colors change. Instant can flicker in fast scenes.</p>
    </div>
  );
}

export function ColorsPage() {
  const ps = usePageSettings('colors', LABELS, true);
  const { api } = useConnection();
  const expert = useExpertTools();
  const color = ps.value<Obj>('color');
  const smoothing = ps.value<Obj>('smoothing');
  const adj = ((color.channelAdjustment as Obj[] | undefined)?.[0] ?? {}) as Obj;
  const adjSchema = propertyAt(ps.schemaOf('color'), 'channelAdjustment', '[]');
  const smoothSchema = ps.schemaOf('smoothing');
  const r = (key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(adjSchema, key), fb);
  const sr = (key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(smoothSchema, key), fb);
  const setAdj = (key: string) => (v: unknown) => ps.setPath('color', ['channelAdjustment', 0, key], v);
  const setAdjAll = (next: Obj) => ps.setPath('color', ['channelAdjustment', 0], next);
  const setSmooth = (key: string) => (v: unknown) => ps.setPath('smoothing', [key], v);
  // A transition time also sets the spring of the Natural styles, so it changes the speed whichever style is chosen.
  const setTransition = (ms: number | null) => {
    if (ms === null) {
      if (smoothing.enable !== false) setSmooth('enable')(false);
      return;
    }
    const next = withTransition(smoothing.enable === true ? smoothing : { ...smoothing, enable: true }, ms);
    if (next !== smoothing) ps.set('smoothing', next);
  };
  const shown = (key: string) => !!smoothSchema?.properties?.[key] && dependenciesMet(smoothSchema.properties[key], smoothing);
  const intensity = detectIntensity({ smoothing, color });

  // Live preview: color changes go to the lights right away, ahead of the automatic save; Discard (offered for
  // values that need attention) rolls them back.
  const colorDirty = ps.dirtySections.includes('color');
  const sendLive = useThrottled((a: Obj) => void api.adjust(a).catch(() => undefined), 150);
  const lastSent = useRef<Obj | null>(null);
  useEffect(() => {
    if (!colorDirty) {
      lastSent.current = null;
      return;
    }
    const live = liveAdjustment(adj);
    if (lastSent.current && deepEqual(live, lastSent.current)) return;
    lastSent.current = live;
    sendLive(live);
  }, [adj, colorDirty, sendLive]);
  const rollback = () => {
    const base = (ps.baseline.color as { channelAdjustment?: Obj[] } | undefined)?.channelAdjustment?.[0];
    if (base) void api.adjust(liveAdjustment(base)).catch(() => undefined);
  };

  const types = (smoothSchema?.properties?.type?.enum ?? Object.keys(SMOOTHING_TYPES)).map(String);
  const gamma = r('gamma', { min: 0.1, max: 5, step: 0.05 });
  const sat = r('saturationGain', { min: 0, max: 10, step: 0.05 });
  const lum = r('luminanceGain', { min: 0, max: 10, step: 0.05 });
  const scale = r('scaleOutput', { min: 0, max: 2 });
  const time = sr('time_ms', { min: 25, max: 5000, step: 25 });
  // the spring doesn't get faster below MIN_SPRING_MS, so the slider doesn't offer it there
  const timeMin = SPRING_TYPES.includes(String(smoothing.type)) ? Math.max(time.min, MIN_SPRING_MS) : Math.max(time.min, 50);
  const timeMs = Number(smoothing.time_ms ?? 350);
  const freq = sr('updateFrequency', { min: 20, max: 200, step: 5 });
  const classic = adj.classic_config !== false;
  const smooth = smoothing.enable === true;
  const scaleOutput = Number(adj.scaleOutput ?? 1);

  const pictureMore = changedKeys(adjSchema, adj, intensity === 'custom' ? PICTURE_MORE : PICTURE_MORE.filter((k) => k !== 'saturationGain' && k !== 'luminanceGain'));
  const transitionMore = changedKeys(smoothSchema, smoothing, TRANSITION_MORE);
  const calibrationMore = changedKeys(adjSchema, adj, CALIBRATION_KEYS);
  const showExpert = (on: boolean) => expert || on;

  return (
    <PageFrame title="Colors" subtitle="How the colors on your screen turn into LED colors. Changes show on your lights right away." settings={ps} onDiscard={rollback}>
      <Section title="Picture" icon={<Palette size={18} />} description="How calm or punchy the colors are. Intensity on Home is the same choice.">
        <IntensityPicker
          current={intensity}
          disabled={!ps.canEdit}
          onPick={(id) => {
            const next = applyIntensity({ smoothing, color }, id);
            ps.set('smoothing', next.smoothing);
            ps.set('color', next.color);
          }}
          customHint="You’ve adjusted the colors yourself. Pick a preset to replace your changes, or keep tuning below."
        />
        <Advanced changed={pictureMore.length} onReset={() => setAdjAll(withDefaultsFor(adjSchema, adj, pictureMore))}>
          <SliderField label="Saturation" unit="%" scale={100} min={sat.min} max={Math.min(sat.max, 3)} step={0.05} value={Number(adj.saturationGain ?? 1)} onChange={setAdj('saturationGain')} minLabel="Gray" maxLabel="Vivid" />
          <SliderField label="Color brightness" unit="%" scale={100} min={lum.min} max={Math.min(lum.max, 3)} step={0.05} value={Number(adj.luminanceGain ?? 1)} onChange={setAdj('luminanceGain')} minLabel="Darker" maxLabel="Brighter" />
          <SliderField label="Contrast" min={gamma.min} max={Math.min(gamma.max, 5)} step={0.05} value={Number(adj.gamma ?? 1.5)} onChange={setAdj('gamma')} hint="Higher makes dark scenes darker and colors deeper; 1.0 is neutral." />
          <SliderField label="Minimum glow" unit="%" scale={100} min={0} max={0.2} step={0.005} value={Number(adj.backlightThreshold ?? 0)} onChange={setAdj('backlightThreshold')} hint="Keeps the LEDs glowing at least this much in dark scenes." />
          {Number(adj.backlightThreshold ?? 0) > 0 && (
            <Toggle label="Tinted glow" hint="Tint the minimum glow with the scene’s color instead of white." checked={adj.backlightColored === true} onChange={setAdj('backlightColored')} />
          )}
          <SliderField label="Power limit" unit="%" scale={100} min={0} max={1} step={0.01} value={Number(adj.powerLimit ?? 1)} onChange={setAdj('powerLimit')} hint="Dims bright scenes so the strip never draws more than this share of full power. Not a replacement for a big enough power supply." />
          {showExpert(scaleOutput > 1) && (
            <SliderField
              label="Brightness boost"
              unit="%"
              scale={100}
              min={1}
              max={Math.max(1.05, scale.max)}
              step={0.05}
              value={Math.max(1, scaleOutput)}
              onChange={(v) => setAdj('scaleOutput')(v <= 1 ? Math.min(1, scaleOutput) : v)}
              hint="Brightness above 100%: lifts dim colors; colors already at full brightness can’t get brighter. The Brightness on Home sets it back to 100% or less."
            />
          )}
          {showExpert(color.imageToLedMappingType === 'unicolor_mean') && (
            <Select
              className="max-w-md"
              label="LED colors"
              value={String(color.imageToLedMappingType ?? 'advanced')}
              hint="The Layout page’s “Screen average” role does this for single strips."
              options={[
                { value: 'advanced', label: 'Each LED follows its part of the screen' },
                { value: 'unicolor_mean', label: 'All LEDs show the screen’s average color' },
              ]}
              onChange={(v) => ps.setPath('color', ['imageToLedMappingType'], v)}
            />
          )}
          {showExpert(color.sparse_processing === true) && (
            <Toggle label="Sparse processing" hint="Look at every second pixel only. Saves CPU with very large LED areas." checked={color.sparse_processing === true} onChange={(v) => ps.setPath('color', ['sparse_processing'], v)} />
          )}
        </Advanced>
      </Section>

      <div id="transitions">
        <Section title="Transitions" icon={<Waves size={18} />} description="How gently the LEDs move from one color to the next.">
          <TransitionSlider enabled={smooth} ms={timeMs} min={timeMin} max={timeMs > 1000 ? time.max : Math.min(time.max, 1000)} onChange={setTransition} />
          <Advanced changed={transitionMore.length} onReset={() => ps.set('smoothing', withDefaultsFor(smoothSchema, smoothing, transitionMore))}>
            <SliderField
              label="LED updates"
              unit="/s"
              min={freq.min}
              max={freq.max}
              step={freq.step}
              value={Number(smoothing.updateFrequency ?? 50)}
              onChange={setSmooth('updateFrequency')}
              hint="How often new colors are sent to your devices, per second. Most handle 50–60."
            />
            {smooth && (
              <>
                <Toggle label="Anti-flicker" hint="Hides tiny flickers in dark, slowly changing colors. Not needed for Philips Hue or LIFX." checked={smoothing.antiFlickeringFilter === true} onChange={setSmooth('antiFlickeringFilter')} />
                {types.length > 1 && (
                  <Select
                    className="max-w-xs"
                    label="Style"
                    value={String(smoothing.type)}
                    hint={SMOOTHING_TYPES[String(smoothing.type)]?.hint}
                    options={types.map((t) => ({ value: t, label: SMOOTHING_TYPES[t]?.label ?? t }))}
                    onChange={setSmooth('type')}
                  />
                )}
              </>
            )}
            <Expert always={smoothing.continuousOutput === true}>
              <Toggle label="Keep sending when nothing changes" hint="For devices that switch to their own effects when they stop receiving colors." checked={smoothing.continuousOutput === true} onChange={setSmooth('continuousOutput')} />
            </Expert>
            {smooth && (
              <Expert>
                {shown('smoothingFactor') && (
                  <SliderField label="Smoothing factor" {...pick(sr('smoothingFactor', { min: 0, max: 1, step: 0.05 }))} value={Number(smoothing.smoothingFactor ?? 0)} onChange={setSmooth('smoothingFactor')} hint="0 = straight line to the new color; higher = softer easing." />
                )}
                {shown('stiffness') && (
                  <SliderField label="Stiffness" {...pick(sr('stiffness', { min: 0, max: 1000, step: 10 }))} value={Number(smoothing.stiffness ?? 150)} onChange={setSmooth('stiffness')} hint="How strongly colors are pulled toward the target. Transitions set it." />
                )}
                {shown('damping') && (
                  <SliderField label="Damping" {...pick(sr('damping', { min: 0, max: 1000, step: 2 }))} value={Number(smoothing.damping ?? 26)} onChange={setSmooth('damping')} hint="Reduces overshoot and wobble. Transitions set it." />
                )}
                {shown('y_limit') && (
                  <SliderField label="Brightness change limit" {...pick(sr('y_limit', { min: 0, max: 1, step: 0.01 }))} value={Number(smoothing.y_limit ?? 0.03)} onChange={setSmooth('y_limit')} hint="Caps how much brightness may change per update." />
                )}
              </Expert>
            )}
          </Advanced>
        </Section>
      </div>

      <div id="white-balance">
        <Section title="White balance" icon={<Thermometer size={18} />} description="Make white on the LEDs match white on your screen.">
          <Segmented label="Color temperature" value={String(adj.temperatureSetting ?? 'disabled')} options={TEMPERATURES} onChange={setAdj('temperatureSetting')} />
          {adj.temperatureSetting === 'custom' && (
            <div className="grid gap-4 sm:grid-cols-3">
              {(['temperatureRed', 'temperatureGreen', 'temperatureBlue'] as const).map((k) => (
                <SliderField key={k} label={k.replace('temperature', '')} unit="%" scale={100} min={0} max={1} step={0.01} value={Number(adj[k] ?? 1)} onChange={setAdj(k)} />
              ))}
            </div>
          )}
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
    </PageFrame>
  );
}

function pick(r: { min: number; max: number; step: number }) {
  return { min: r.min, max: r.max, step: r.step };
}
