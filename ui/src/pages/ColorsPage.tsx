import { useCallback, useEffect, useId, useRef } from 'react';
import { Palette, Waves } from 'lucide-react';
import { rangeFill, SliderField } from '../components/ui';
import { Advanced, Expert, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { useConfig, usePageSettings, type PageSettings } from '../app/config';
import { useEditor } from '../app/editor';
import type { Profile, ProfileLook } from '../layout';
import { setupLookValues, withLookValues, withPresetPatch } from '../control/setup';
import { useSetupInUse } from '../control/usePick';
import { mirrorId } from '../editor/mirror';
import { useExpertTools } from '../app/expert';
import { hrefFor } from '../app/router';
import { dependenciesMet, propertyAt, rangeOf } from '../schema/schema';
import { detectPreset, detectProfileLook, formatSeconds, LOOKS, savedTuning, type Preset } from '../settings/intensity';
import { MIN_SPRING_MS, springFor, SPRING_TYPES, withTransition } from '../settings/smoothing';
import { adjustmentOf, changedKeys, useLiveAdjustment, withDefaultsFor, type Obj } from './colorAdjustment';

const LABELS: Record<string, string> = {
  'color.channelAdjustment.0.scaleOutput': 'Brightness boost',
  'color.channelAdjustment.0.saturationGain': 'Saturation',
  'color.channelAdjustment.0.luminanceGain': 'Color brightness',
  'color.channelAdjustment.0.backlightThreshold': 'Minimum glow',
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

const TRANSITION_MORE = ['updateFrequency', 'antiFlickeringFilter', 'type'];

type Range = { min: number; max: number; step?: number };

/**
 * Transition time with "Instant" at the left end: Instant turns smoothing off, any time turns it on with that time (and
 * the matching spring of the Natural styles).
 */
function TransitionSlider({
  label = 'Transition time',
  hint,
  enabled,
  ms,
  min,
  max,
  withInstant = true,
  onChange,
}: {
  label?: string;
  hint: string;
  enabled: boolean;
  ms: number;
  min: number;
  max: number;
  /** Offer Instant (smoothing off); a preset's Look is always smoothed. */
  withInstant?: boolean;
  onChange: (ms: number | null) => void;
}) {
  const id = useId();
  const step = 50;
  const instant = withInstant ? min - step : min;
  const value = enabled ? Math.max(min, ms) : instant;
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-3">
        <label htmlFor={id} className="text-sm font-medium text-ink">
          {label}
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
        style={rangeFill(value, instant, max)}
        aria-valuetext={enabled ? formatSeconds(ms) : 'Instant'}
        onChange={(e) => {
          const v = Number(e.target.value);
          onChange(v < min ? null : v);
        }}
      />
      <div className="flex justify-between text-[11px] text-ink-faint" aria-hidden>
        <span>{withInstant ? 'Instant' : formatSeconds(min)}</span>
        <span>Relaxed</span>
      </div>
      <p className="text-xs text-ink-faint">{hint}</p>
    </div>
  );
}

/** Which of Home's Looks the values match, with a link there: this page fine-tunes the same values. */
function PresetLine<Id extends string>({ what, setup, presets, current, focus }: { what: string; setup: string; presets: ReadonlyArray<Preset<Id>>; current: Id | 'custom' | null; focus: string }) {
  if (!current) return null;
  const label = presets.find((p) => p.id === current)?.label;
  return (
    <p className="rounded-xl border border-line bg-surface-2 px-4 py-3 text-[13px] text-ink-muted">
      {label ? (
        <>
          {setup}’s {what} matches <span className="font-medium text-ink">{label}</span>.
        </>
      ) : (
        <>
          {setup}’s {what} is your own: none of the five matches it.
        </>
      )}{' '}
      <a className="text-accent-strong underline-offset-2 hover:underline" href={hrefFor('home', null, focus)}>
        {what} on Home
      </a>
    </p>
  );
}

function GainSliders({
  saturation,
  luminance,
  onSaturation,
  onLuminance,
  range,
}: {
  saturation: number;
  luminance: number;
  onSaturation: (v: number) => void;
  onLuminance: (v: number) => void;
  range: (key: string) => Range;
}) {
  const sat = range('saturationGain');
  const lum = range('luminanceGain');
  return (
    <>
      <SliderField label="Saturation" unit="%" scale={100} min={sat.min} max={Math.min(sat.max, 3)} step={0.05} value={saturation} onChange={onSaturation} minLabel="Gray" maxLabel="Vivid" />
      <SliderField label="Color brightness" unit="%" scale={100} min={lum.min} max={Math.min(lum.max, 3)} step={0.05} value={luminance} onChange={onLuminance} minLabel="Darker" maxLabel="Brighter" />
    </>
  );
}

/**
 * Fine-tuning of the Look (Home's Look sets the same values): the saved color and smoothing. What makes the LEDs match
 * the screen is on Setup → Calibration.
 */
export function ColorsPage() {
  const ps = usePageSettings('colors', LABELS, true);
  const rollback = useLiveAdjustment(ps);
  return <LookColors ps={ps} rollback={rollback} />;
}

/**
 * The Look values of a preset in use: its own Look (made from Everyday's when it has none yet), changed one value at a
 * time in the edited layout and saved shortly after the last change to the layout saved last (the backend applies it
 * at once), so Layout edits that aren't saved stay out of it. The edits are a mirror (not counted as unsaved edits
 * while they wait). Leaving the page saves a change still waiting; a failed save puts the Look back to the saved one.
 */
function usePresetLook(preset: Profile | undefined): { look: ProfileLook | null; edit(patch: Partial<ProfileLook>): void } {
  const { config } = useConfig();
  const { dispatch, quietSave } = useEditor();
  const pending = useRef<{ id: string; mirror: string; look: ProfileLook } | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const flush = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    const p = pending.current;
    if (!p) return;
    pending.current = null;
    void quietSave((saved) => withPresetPatch(saved, p.id, { look: p.look })).then((ok) => dispatch({ type: 'mirrorDone', id: p.mirror, saved: ok }));
  }, [dispatch, quietSave]);
  const flushRef = useRef(flush);
  flushRef.current = flush;
  useEffect(() => () => flushRef.current(), []);
  const look = preset ? setupLookValues(config, preset) : null;
  const edit = useCallback(
    (patch: Partial<ProfileLook>) => {
      if (!preset) return;
      const next = withLookValues(preset, setupLookValues(config, undefined), patch);
      if (!next) return;
      if (pending.current && pending.current.id !== preset.id) flushRef.current();
      const mirror = pending.current?.mirror ?? mirrorId();
      dispatch({ type: 'mirror', id: mirror, actions: [{ type: 'updateProfile', profileId: preset.id, patch: { look: next } }], coalesce: `look-${preset.id}` });
      pending.current = { id: preset.id, mirror, look: next };
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(() => flushRef.current(), 300);
    },
    [preset, config, dispatch],
  );
  return { look, edit };
}

function LookColors({ ps, rollback }: { ps: PageSettings; rollback: () => void }) {
  const expert = useExpertTools();
  const { preset, name } = useSetupInUse();
  const presetLook = usePresetLook(preset);
  const own = preset ? presetLook.look : null;
  const color = ps.value<Obj>('color');
  const smoothing = ps.value<Obj>('smoothing');
  const adj = adjustmentOf(color);
  const adjSchema = propertyAt(ps.schemaOf('color'), 'channelAdjustment', '[]');
  const smoothSchema = ps.schemaOf('smoothing');
  const r = (key: string, fb: Range = { min: 0, max: 10, step: 0.05 }) => rangeOf(propertyAt(adjSchema, key), fb);
  const sr = (key: string, fb: Range) => rangeOf(propertyAt(smoothSchema, key), fb);
  const setAdj = (key: string) => (v: unknown) => ps.setPath('color', ['channelAdjustment', 0, key], v);
  const setSmooth = (key: string) => (v: unknown) => ps.setPath('smoothing', [key], v);
  // the spring belongs to the Look: a preset with its own Look keeps it there (whole numbers, as the layout schema wants)
  const setSpring = (key: 'stiffness' | 'damping') => (v: unknown) => (own ? presetLook.edit({ [key]: Math.round(Number(v)) }) : setSmooth(key)(v));
  // A transition time also sets the spring of the Natural styles, so it changes the speed whichever style is chosen.
  const setTransition = (ms: number | null) => {
    if (own) {
      if (ms !== null) presetLook.edit({ time_ms: ms, ...springFor(ms) });
      return;
    }
    if (ms === null) {
      if (smoothing.enable !== false) setSmooth('enable')(false);
      return;
    }
    const next = withTransition(smoothing.enable === true ? smoothing : { ...smoothing, enable: true }, ms);
    if (next !== smoothing) ps.set('smoothing', next);
  };
  const shown = (key: string) => !!smoothSchema?.properties?.[key] && dependenciesMet(smoothSchema.properties[key], smoothing);

  const types = (smoothSchema?.properties?.type?.enum ?? Object.keys(SMOOTHING_TYPES)).map(String);
  const scale = r('scaleOutput', { min: 0, max: 2 });
  const time = sr('time_ms', { min: 25, max: 5000, step: 25 });
  // the spring doesn't get faster below MIN_SPRING_MS, so the slider doesn't offer it there
  const timeMin = SPRING_TYPES.includes(String(smoothing.type)) ? Math.max(time.min, MIN_SPRING_MS) : Math.max(time.min, 50);
  const timeMs = own ? own.time_ms : Number(smoothing.time_ms ?? 350);
  const freq = sr('updateFrequency', { min: 20, max: 200, step: 5 });
  const smooth = own ? true : smoothing.enable === true;
  const saturation = own ? own.saturationGain : Number(adj.saturationGain ?? 1);
  const luminance = own ? own.luminanceGain : Number(adj.luminanceGain ?? 1);
  const minGlow = own ? own.backlightThreshold : Number(adj.backlightThreshold ?? 0);
  const lookMatch = own ? detectProfileLook(own) : detectPreset(LOOKS, savedTuning({ smoothing, color }));
  const scaleOutput = Number(adj.scaleOutput ?? 1);
  const transitionMore = changedKeys(smoothSchema, smoothing, TRANSITION_MORE);
  const showExpert = (on: boolean) => expert || on;

  return (
    <PageFrame title="Colors" subtitle={`How your room glows with the picture, shown on your lights right away. The Look values save to ${name}; the rest is the same for every preset.`} settings={ps} onDiscard={rollback}>
      <PresetLine what="Look" setup={name} presets={LOOKS} current={lookMatch} focus="look" />
      <Section title="Colors" icon={<Palette size={18} />} description="How rich the colors are, and how much the room glows in dark scenes.">
        <GainSliders
          saturation={saturation}
          luminance={luminance}
          onSaturation={own ? (v) => presetLook.edit({ saturationGain: v }) : setAdj('saturationGain')}
          onLuminance={own ? (v) => presetLook.edit({ luminanceGain: v }) : setAdj('luminanceGain')}
          range={(k) => r(k)}
        />
        <SliderField
          label="Minimum glow"
          unit="%"
          scale={100}
          min={0}
          max={0.2}
          step={0.005}
          value={minGlow}
          onChange={own ? (v) => presetLook.edit({ backlightThreshold: v }) : setAdj('backlightThreshold')}
          hint="Keeps the LEDs glowing at least this much in dark scenes."
        />
        {minGlow > 0 && (
          <Toggle label="Tinted glow" hint="Tint the minimum glow with the scene’s color instead of white." checked={adj.backlightColored === true} onChange={setAdj('backlightColored')} />
        )}
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
      </Section>

      <div id="transitions">
        <Section title="Transitions" icon={<Waves size={18} />} description="How gently the LEDs move from one color to the next.">
          <TransitionSlider
            hint={own ? 'How gently colors change.' : 'How gently colors change. Instant can flicker in fast scenes.'}
            enabled={smooth}
            ms={timeMs}
            min={timeMin}
            max={timeMs > 1000 ? time.max : Math.min(time.max, 1000)}
            withInstant={!own}
            onChange={setTransition}
          />
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
                  <SliderField label="Stiffness" {...pick(sr('stiffness', { min: 0, max: 1000, step: 10 }))} value={own ? own.stiffness : Number(smoothing.stiffness ?? 150)} onChange={setSpring('stiffness')} hint="How strongly colors are pulled toward the target. Transitions set it." />
                )}
                {shown('damping') && (
                  <SliderField label="Damping" {...pick(sr('damping', { min: 0, max: 1000, step: 2 }))} value={own ? own.damping : Number(smoothing.damping ?? 26)} onChange={setSpring('damping')} hint="Reduces overshoot and wobble. Transitions set it." />
                )}
                {shown('y_limit') && (
                  <SliderField label="Brightness change limit" {...pick(sr('y_limit', { min: 0, max: 1, step: 0.01 }))} value={Number(smoothing.y_limit ?? 0.03)} onChange={setSmooth('y_limit')} hint="Caps how much brightness may change per update." />
                )}
              </Expert>
            )}
          </Advanced>
        </Section>
      </div>
    </PageFrame>
  );
}

function pick(r: { min: number; max: number; step: number }) {
  return { min: r.min, max: r.max, step: r.step };
}
