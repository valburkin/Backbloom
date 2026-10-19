// Intensity presets: one choice on the Home page that sets how calm or punchy the lights feel. Each preset maps to
// concrete settings — smoothing (on + transition time) and the saturation / luminance gains of the color section.
// Anything else stays as the user configured it; a config that matches no preset shows as "Custom".

import type { BackendConfig } from '../api/types';

export interface IntensityPreset {
  id: 'subtle' | 'moderate' | 'high' | 'intense';
  label: string;
  description: string;
  /** smoothing.time_ms */
  smoothingMs: number;
  /** color.channelAdjustment[0].saturationGain */
  saturation: number;
  /** color.channelAdjustment[0].luminanceGain */
  luminance: number;
}

export const INTENSITY_PRESETS: readonly IntensityPreset[] = [
  { id: 'subtle', label: 'Subtle', description: 'Soft colors that drift slowly. Easy on the eyes for movies and long sessions.', smoothingMs: 400, saturation: 0.9, luminance: 0.9 },
  { id: 'moderate', label: 'Moderate', description: 'True-to-screen colors with gentle transitions. A good default.', smoothingMs: 250, saturation: 1, luminance: 1 },
  { id: 'high', label: 'High', description: 'Richer colors that follow the action quickly.', smoothingMs: 150, saturation: 1.25, luminance: 1.1 },
  { id: 'intense', label: 'Intense', description: 'Vivid colors with near-instant reactions. Made for fast games.', smoothingMs: 80, saturation: 1.5, luminance: 1.2 },
];

export type IntensityId = IntensityPreset['id'] | 'custom';

type Obj = Record<string, unknown>;

function adjustmentOf(config: BackendConfig): Obj {
  const list = (config.color as Obj | undefined)?.channelAdjustment;
  return Array.isArray(list) && list[0] && typeof list[0] === 'object' ? (list[0] as Obj) : {};
}

const close = (a: unknown, b: number) => typeof a === 'number' && Math.abs(a - b) < 0.005;

/** Which preset the current settings match, or "custom". */
export function detectIntensity(config: BackendConfig | null): IntensityId | null {
  if (!config) return null;
  const smoothing = (config.smoothing ?? {}) as Obj;
  const adj = adjustmentOf(config);
  const match = INTENSITY_PRESETS.find(
    (p) => smoothing.enable === true && smoothing.time_ms === p.smoothingMs && close(adj.saturationGain, p.saturation) && close(adj.luminanceGain, p.luminance),
  );
  return match?.id ?? 'custom';
}

/** The `smoothing` and `color` sections with the preset applied (send them with setconfig). */
export function applyIntensity(config: BackendConfig, id: IntensityPreset['id']): BackendConfig {
  const p = INTENSITY_PRESETS.find((x) => x.id === id);
  if (!p) throw new Error(`Unknown preset ${id}`);
  const color = (config.color ?? {}) as Obj;
  const list = Array.isArray(color.channelAdjustment) ? (color.channelAdjustment as Obj[]) : [{}];
  return {
    smoothing: { ...((config.smoothing ?? {}) as Obj), enable: true, time_ms: p.smoothingMs },
    color: {
      ...color,
      channelAdjustment: [{ ...list[0], saturationGain: p.saturation, luminanceGain: p.luminance }, ...list.slice(1)],
    },
  };
}

/** "What it changes" rows for a preset. */
export function describeIntensity(p: IntensityPreset): Array<[string, string]> {
  return [
    ['Smoothing', `${p.smoothingMs} ms transitions`],
    ['Saturation', `${Math.round(p.saturation * 100)}%`],
    ['Luminance', `${Math.round(p.luminance * 100)}%`],
  ];
}
