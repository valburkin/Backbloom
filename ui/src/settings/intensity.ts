// The Look on Home: one list of five presets, from calm to quick and vivid. Each maps to concrete settings: the
// transition time with its spring, the saturation and color brightness (luminance) gains, and the minimum glow
// (backlightThreshold). They go into the saved `smoothing` and `color` sections (smoothing on); anything else stays as
// the user configured it, and values that match no Look show as "Custom". The Colors page fine-tunes the same values
// one by one. A preset (layout profile) can carry a Look of its own (`Profile.look`), which the backend applies on top
// of the saved values while the preset is on, without saving it (Engine/LookTuning).

import type { BackendConfig } from '../api/types';
import type { ProfileLook } from '../layout';
import { springFor } from './smoothing';

export interface Preset<Id extends string = string> {
  id: Id;
  label: string;
  description: string;
  /** The transition time (ms); the spring (stiffness, damping) follows from it (springFor) */
  smoothingMs: number;
  /** saturationGain */
  saturation: number;
  /** luminanceGain */
  luminance: number;
  /** backlightThreshold: how much the LEDs keep glowing in dark scenes (0: dark scenes stay dark) */
  minGlow: number;
}

export type LookId = 'relaxed' | 'natural' | 'rich' | 'vivid' | 'intense';

/** The minimum glow of the settings' defaults (`color.channelAdjustment[].backlightThreshold`, 1/255). */
export const DEFAULT_MIN_GLOW = 0.0039;

/** The five Looks. Natural is the default settings. */
export const LOOKS: ReadonlyArray<Preset<LookId>> = [
  { id: 'relaxed', label: 'Relaxed', description: 'Soft colors that drift slowly, for movies and evenings.', smoothingMs: 550, saturation: 0.9, luminance: 0.9, minGlow: DEFAULT_MIN_GLOW },
  { id: 'natural', label: 'Natural', description: 'True to the screen, with gentle transitions.', smoothingMs: 350, saturation: 1, luminance: 1, minGlow: DEFAULT_MIN_GLOW },
  { id: 'rich', label: 'Rich', description: 'Deeper colors that follow a little quicker, for music and colorful films.', smoothingMs: 300, saturation: 1.25, luminance: 1.1, minGlow: DEFAULT_MIN_GLOW },
  { id: 'vivid', label: 'Vivid', description: 'Richer colors that keep up with the action.', smoothingMs: 200, saturation: 1.25, luminance: 1.1, minGlow: 0 },
  { id: 'intense', label: 'Intense', description: 'The quickest reactions, for fast games; dark scenes stay dark.', smoothingMs: 150, saturation: 1.5, luminance: 1.2, minGlow: 0 },
];

type Obj = Record<string, unknown>;

/** The values a Look sets, as they apply. */
export interface Tuning {
  smooth: boolean;
  timeMs: unknown;
  stiffness: unknown;
  damping: unknown;
  saturation: unknown;
  luminance: unknown;
  minGlow: unknown;
}

function adjustmentOf(config: BackendConfig): Obj {
  const list = (config.color as Obj | undefined)?.channelAdjustment;
  return Array.isArray(list) && list[0] && typeof list[0] === 'object' ? (list[0] as Obj) : {};
}

/** The saved values: the `smoothing` and `color` sections. */
export function savedTuning(config: BackendConfig): Tuning {
  const smoothing = (config.smoothing ?? {}) as Obj;
  const adj = adjustmentOf(config);
  return {
    smooth: smoothing.enable === true,
    timeMs: smoothing.time_ms,
    stiffness: smoothing.stiffness,
    damping: smoothing.damping,
    saturation: adj.saturationGain,
    luminance: adj.luminanceGain,
    minGlow: adj.backlightThreshold,
  };
}

/** A preset's own Look as tuning values (always smoothed). */
export function profileTuning(look: ProfileLook): Tuning {
  return {
    smooth: true,
    timeMs: look.time_ms,
    stiffness: look.stiffness,
    damping: look.damping,
    saturation: look.saturationGain,
    luminance: look.luminanceGain,
    minGlow: look.backlightThreshold,
  };
}

const close = (a: unknown, b: number, within = 0.005) => typeof a === 'number' && Math.abs(a - b) < within;

/** Which of `presets` the values match, or "custom"; null before the config is loaded. */
export function detectPreset<Id extends string>(presets: ReadonlyArray<Preset<Id>>, tuning: Tuning | null): Id | 'custom' | null {
  if (!tuning) return null;
  const match = presets.find((p) => {
    const spring = springFor(p.smoothingMs);
    return (
      tuning.smooth &&
      tuning.timeMs === p.smoothingMs &&
      tuning.stiffness === spring.stiffness &&
      tuning.damping === spring.damping &&
      close(tuning.saturation, p.saturation) &&
      close(tuning.luminance, p.luminance) &&
      close(tuning.minGlow, p.minGlow, 0.0005)
    );
  });
  return match?.id ?? 'custom';
}

/** The saved Look, or "custom"; null before the config is loaded. */
export function detectLook(config: BackendConfig | null): LookId | 'custom' | null {
  return detectPreset(LOOKS, config && savedTuning(config));
}

/** A preset's Look: one of the five, "custom", or null when it keeps the saved Look. */
export function detectProfileLook(look: ProfileLook | null | undefined): LookId | 'custom' | null {
  return look ? detectPreset(LOOKS, profileTuning(look)) : null;
}

export function lookById(id: LookId): Preset<LookId> {
  const p = LOOKS.find((x) => x.id === id);
  if (!p) throw new Error(`Unknown Look ${id}`);
  return p;
}

/** The `smoothing` and `color` sections with the Look applied (send them with setconfig). */
export function applyLook(config: BackendConfig, id: LookId): BackendConfig {
  const p = lookById(id);
  const color = (config.color ?? {}) as Obj;
  const list = Array.isArray(color.channelAdjustment) ? (color.channelAdjustment as Obj[]) : [{}];
  return {
    smoothing: { ...((config.smoothing ?? {}) as Obj), enable: true, time_ms: p.smoothingMs, ...springFor(p.smoothingMs) },
    color: {
      ...color,
      channelAdjustment: [{ ...list[0], saturationGain: p.saturation, luminanceGain: p.luminance, backlightThreshold: p.minGlow }, ...list.slice(1)],
    },
  };
}

/** A Look as a preset stores it (`Profile.look`). */
export function profileLook(id: LookId): ProfileLook {
  const p = lookById(id);
  const spring = springFor(p.smoothingMs);
  return {
    time_ms: p.smoothingMs,
    stiffness: spring.stiffness,
    damping: spring.damping,
    saturationGain: p.saturation,
    luminanceGain: p.luminance,
    backlightThreshold: p.minGlow,
  };
}

/** The saved values as a preset's Look (New preset takes the current Look); null before the config is loaded. */
export function savedLook(config: BackendConfig | null): ProfileLook | null {
  if (!config) return null;
  const id = detectLook(config);
  if (id && id !== 'custom') return profileLook(id);
  const t = savedTuning(config);
  const num = (v: unknown, fallback: number) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);
  const time = Math.round(num(t.timeMs, 350));
  const spring = springFor(time);
  return {
    time_ms: time,
    stiffness: Math.round(num(t.stiffness, spring.stiffness)),
    damping: Math.round(num(t.damping, spring.damping)),
    saturationGain: num(t.saturation, 1),
    luminanceGain: num(t.luminance, 1),
    backlightThreshold: num(t.minGlow, DEFAULT_MIN_GLOW),
  };
}

/** A duration in seconds as the UI shows it: 350 → "0.35 s". */
export function formatSeconds(ms: number): string {
  return `${Number((ms / 1000).toFixed(2))} s`;
}

/** "What it changes" rows for a Look. */
export function describePreset(p: Preset): Array<[string, string]> {
  return [
    ['Transitions', formatSeconds(p.smoothingMs)],
    ['Saturation', `${Math.round(p.saturation * 100)}%`],
    ['Color brightness', `${Math.round(p.luminance * 100)}%`],
    ['Dark scenes', p.minGlow > 0 ? 'Faint glow' : 'Stay dark'],
  ];
}
