import { useEffect, useRef } from 'react';
import { Palette, SlidersHorizontal, Thermometer, Waves } from 'lucide-react';
import { Segmented, SliderField } from '../components/ui';
import { Advanced, Callout, Section, Select, Toggle } from '../components/form';
import { ColorSwatchField } from '../components/ColorSwatchField';
import { PageFrame } from '../components/PageFrame';
import { usePageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useThrottled } from '../app/useThrottled';
import { hrefFor } from '../app/router';
import { isRgb, type Rgb } from '../color/color';
import { dependenciesMet, propertyAt, rangeOf } from '../schema/schema';
import { deepEqual } from '../settings/drafts';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'color.channelAdjustment.0.scaleOutput': 'Brightness',
  'color.channelAdjustment.0.saturationGain': 'Saturation',
  'color.channelAdjustment.0.luminanceGain': 'Luminance',
  'color.channelAdjustment.0.gamma': 'Gamma',
  'color.channelAdjustment.0.backlightThreshold': 'Minimum backlight',
  'color.channelAdjustment.0.powerLimit': 'Power limit',
  'smoothing.time_ms': 'Transition time',
  'smoothing.updateFrequency': 'Update frequency',
  'smoothing.smoothingFactor': 'Smoothing factor',
  'smoothing.stiffness': 'Stiffness',
  'smoothing.damping': 'Damping',
  'smoothing.y_limit': 'Brightness change limit',
};

/** Friendly names for the backend's smoothing algorithms; only types the backend offers are listed. */
const SMOOTHING_TYPES: Record<string, { label: string; hint: string }> = {
  HybridRgbInterpolator: { label: 'Natural (recommended)', hint: 'Colors glide like they’re on a soft spring: quick to react, no harsh jumps.' },
  HybridInterpolator: { label: 'Natural, brightness-safe', hint: 'Like Natural, and also limits sudden brightness flashes.' },
  Stepper: { label: 'Linear', hint: 'Moves at a constant speed toward each new color.' },
  RgbInterpolator: { label: 'Fluid', hint: 'Smooth transitions with an adjustable amount of easing.' },
  YuvInterpolator: { label: 'Fluid, brightness-safe', hint: 'Fluid transitions that limit sudden brightness changes.' },
  ExponentialInterpolator: { label: 'Classic', hint: 'Reacts fast to big changes and slows down near the target.' },
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

/** Fields the `adjustment` command accepts (it applies them immediately). */
const LIVE_KEYS = [
  'classic_config', 'red', 'green', 'blue', 'cyan', 'magenta', 'yellow', 'white', 'black', 'scaleOutput', 'gamma', 'backlightThreshold',
  'backlightColored', 'luminanceGain', 'saturationGain', 'temperatureSetting', 'temperatureRed', 'temperatureGreen', 'temperatureBlue', 'powerLimit',
];

function liveAdjustment(adj: Obj): Obj {
  const out: Obj = {};
  for (const k of LIVE_KEYS) if (adj[k] !== undefined) out[k] = adj[k];
  if (typeof out.gamma === 'number') out.gamma = Math.min(5, out.gamma);
  return out;
}

export function ColorsPage() {
  const ps = usePageSettings('colors', LABELS);
  const { api } = useConnection();
  const color = ps.value<Obj>('color');
  const smoothing = ps.value<Obj>('smoothing');
  const adj = ((color.channelAdjustment as Obj[] | undefined)?.[0] ?? {}) as Obj;
  const adjSchema = propertyAt(ps.schemaOf('color'), 'channelAdjustment', '[]');
  const smoothSchema = ps.schemaOf('smoothing');
  const r = (key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(adjSchema, key), fb);
  const sr = (key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(smoothSchema, key), fb);
  const setAdj = (key: string) => (v: unknown) => ps.setPath('color', ['channelAdjustment', 0, key], v);
  const setSmooth = (key: string) => (v: unknown) => ps.setPath('smoothing', [key], v);
  const shown = (key: string) => !smoothSchema?.properties?.[key] || dependenciesMet(smoothSchema.properties[key], smoothing);

  // Live preview: color changes go to the lights right away; Discard rolls them back.
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
  const time = sr('time_ms', { min: 25, max: 5000, step: 25 });
  const freq = sr('updateFrequency', { min: 20, max: 200, step: 5 });
  const classic = adj.classic_config !== false;

  return (
    <PageFrame title="Colors" subtitle="How the colors on your screen turn into LED colors. Changes show on your lights right away." settings={ps} onDiscard={rollback}>
      <Section title="Picture" icon={<Palette size={18} />} description={<>Saturation and luminance are also set by the Intensity choice on <a className="text-accent-strong hover:underline" href={hrefFor('home')}>Home</a>.</>}>
        <SliderField label="Brightness" unit="%" scale={100} min={0} max={r('scaleOutput', { min: 0, max: 2 }).max} step={0.01} value={Number(adj.scaleOutput ?? 1)} onChange={setAdj('scaleOutput')} hint="Above 100% lifts dim colors; colors already at full brightness can’t get brighter." />
        <SliderField label="Saturation" unit="%" scale={100} min={sat.min} max={Math.min(sat.max, 3)} step={0.05} value={Number(adj.saturationGain ?? 1)} onChange={setAdj('saturationGain')} minLabel="Gray" maxLabel="Vivid" />
        <SliderField label="Luminance" unit="%" scale={100} min={lum.min} max={Math.min(lum.max, 3)} step={0.05} value={Number(adj.luminanceGain ?? 1)} onChange={setAdj('luminanceGain')} minLabel="Darker" maxLabel="Brighter" />
        <SliderField label="Gamma" min={gamma.min} max={Math.min(gamma.max, 5)} step={0.05} value={Number(adj.gamma ?? 1.5)} onChange={setAdj('gamma')} hint="Higher values make dark scenes darker and colors deeper; 1.0 is neutral." />
      </Section>

      <Section title="White balance" icon={<Thermometer size={18} />} description="Make white on the LEDs match white on your screen.">
        <Segmented
          label="Color temperature"
          value={String(adj.temperatureSetting ?? 'disabled')}
          options={TEMPERATURES}
          onChange={setAdj('temperatureSetting')}
        />
        {adj.temperatureSetting === 'custom' && (
          <div className="grid gap-4 sm:grid-cols-3">
            {(['temperatureRed', 'temperatureGreen', 'temperatureBlue'] as const).map((k) => (
              <SliderField key={k} label={k.replace('temperature', '')} unit="%" scale={100} min={0} max={1} step={0.01} value={Number(adj[k] ?? 1)} onChange={setAdj(k)} />
            ))}
          </div>
        )}
      </Section>

      <Section title="Smoothness" icon={<Waves size={18} />} description="How gently the LEDs move from one color to the next.">
        <Toggle label="Smooth transitions" hint="Without smoothing, colors jump instantly (can flicker in fast scenes)." checked={smoothing.enable === true} onChange={setSmooth('enable')} />
        {smoothing.enable === true && (
          <>
            {types.length > 1 && (
              <Select
                label="Style"
                value={String(smoothing.type)}
                hint={SMOOTHING_TYPES[String(smoothing.type)]?.hint}
                options={types.map((t) => ({ value: t, label: SMOOTHING_TYPES[t]?.label ?? t }))}
                onChange={setSmooth('type')}
              />
            )}
            <SliderField label="Transition time" unit="ms" min={time.min} max={Number(smoothing.time_ms ?? 150) > 1000 ? time.max : Math.min(time.max, 1000)} step={time.step} value={Number(smoothing.time_ms ?? 150)} onChange={setSmooth('time_ms')} minLabel="Snappy" maxLabel="Relaxed" />
            <Toggle label="Anti-flicker filter" hint="Hides tiny flickers in dark, slowly changing colors. Not needed for Philips Hue or LIFX." checked={smoothing.antiFlickeringFilter === true} onChange={setSmooth('antiFlickeringFilter')} />
          </>
        )}
        <SliderField label="Update frequency" unit="Hz" min={freq.min} max={freq.max} step={freq.step} value={Number(smoothing.updateFrequency ?? 50)} onChange={setSmooth('updateFrequency')} hint="How often new colors are sent to the controller. Most controllers handle 50–60 Hz." />
        <Advanced>
          <Toggle label="Keep sending when nothing changes" hint="For controllers that switch to their own effects when they stop receiving data." checked={smoothing.continuousOutput === true} onChange={setSmooth('continuousOutput')} />
          {smoothing.enable === true && shown('smoothingFactor') && smoothSchema?.properties?.smoothingFactor && (
            <SliderField label="Smoothing factor" {...pick(sr('smoothingFactor', { min: 0, max: 1, step: 0.05 }))} value={Number(smoothing.smoothingFactor ?? 0)} onChange={setSmooth('smoothingFactor')} hint="0 = straight line to the new color; higher = softer easing." />
          )}
          {smoothing.enable === true && shown('stiffness') && smoothSchema?.properties?.stiffness && (
            <SliderField label="Stiffness" {...pick(sr('stiffness', { min: 0, max: 1000, step: 10 }))} value={Number(smoothing.stiffness ?? 150)} onChange={setSmooth('stiffness')} hint="How strongly colors are pulled toward the target." />
          )}
          {smoothing.enable === true && shown('damping') && smoothSchema?.properties?.damping && (
            <SliderField label="Damping" {...pick(sr('damping', { min: 0, max: 1000, step: 2 }))} value={Number(smoothing.damping ?? 26)} onChange={setSmooth('damping')} hint="Reduces overshoot and wobble." />
          )}
          {smoothing.enable === true && shown('y_limit') && smoothSchema?.properties?.y_limit && (
            <SliderField label="Brightness change limit" {...pick(sr('y_limit', { min: 0, max: 1, step: 0.01 }))} value={Number(smoothing.y_limit ?? 0.03)} onChange={setSmooth('y_limit')} hint="Caps how much brightness may change per update." />
          )}
        </Advanced>
      </Section>

      <Section title="Fine-tuning" icon={<SlidersHorizontal size={18} />} description="Rarely needed. Calibrate each LED color, set a minimum glow, or limit power.">
        <Advanced label="Color processing">
          <Select
            label="LED colors"
            value={String(color.imageToLedMappingType ?? 'advanced')}
            options={[
              { value: 'advanced', label: 'Each LED follows its part of the screen' },
              { value: 'unicolor_mean', label: 'All LEDs show the screen’s average color' },
            ]}
            onChange={(v) => ps.setPath('color', ['imageToLedMappingType'], v)}
          />
          <Toggle label="Sparse processing" hint="Look at every second pixel only. Saves CPU with very large LED areas." checked={color.sparse_processing === true} onChange={(v) => ps.setPath('color', ['sparse_processing'], v)} />
        </Advanced>
        <Advanced label="Backlight and power">
          <SliderField label="Minimum backlight" min={0} max={0.2} step={1 / 255} scale={255} value={Number(adj.backlightThreshold ?? 0)} onChange={setAdj('backlightThreshold')} hint="Keeps the LEDs glowing at least this much (0–255 scale) in dark scenes." />
          {Number(adj.backlightThreshold ?? 0) > 0 && (
            <Toggle label="Colored backlight" hint="Tint the minimum glow with the scene’s color instead of white." checked={adj.backlightColored === true} onChange={setAdj('backlightColored')} />
          )}
          <SliderField label="Power limit" unit="%" scale={100} min={0} max={1} step={0.01} value={Number(adj.powerLimit ?? 1)} onChange={setAdj('powerLimit')} hint="Dims bright scenes so the strip never draws more than this share of full power. Not a replacement for a big enough power supply." />
        </Advanced>
        <Advanced label="Per-color calibration">
          <Toggle
            label="Calibrate red, green and blue only"
            hint="Turn off to also calibrate cyan, magenta, yellow, white and black."
            checked={classic}
            onChange={setAdj('classic_config')}
          />
          <Callout tone="info">Pick what each pure color should look like on your strip. For example, if full red looks orange, lower its green part.</Callout>
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
    </PageFrame>
  );
}

function pick(r: { min: number; max: number; step: number }) {
  return { min: r.min, max: r.max, step: r.step };
}
