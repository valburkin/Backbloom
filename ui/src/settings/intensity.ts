// The presets on Home, one set per mode: Ambient's Mood (how the room feels) and Game's Intensity (how punchy the
// lights are while playing). Each preset maps to concrete settings: the transition time with its spring, and the
// saturation and color brightness (luminance) gains. Ambient's go into the saved `smoothing` and `color` sections
// (smoothing on); Game's into the `game` section, which the backend puts in place of those values while Game mode is on
// (Engine/GameTuning.cs). Anything else stays as the user configured it; values that match no preset show as "Custom".
// The Colors page fine-tunes the same values one by one.

import type { BackendConfig } from '../api/types';
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
}

export type MoodId = 'relaxed' | 'natural' | 'rich';
export type GameIntensityId = 'subtle' | 'vivid' | 'intense';

/** Ambient's Mood: slower transitions, from softer to richer colors. Natural is the default settings. */
export const MOOD_PRESETS: ReadonlyArray<Preset<MoodId>> = [
  { id: 'relaxed', label: 'Relaxed', description: 'Soft colors that drift slowly. Easy on the eyes for movies and evenings.', smoothingMs: 550, saturation: 0.9, luminance: 0.9 },
  { id: 'natural', label: 'Natural', description: 'True-to-screen colors with gentle transitions.', smoothingMs: 350, saturation: 1, luminance: 1 },
  { id: 'rich', label: 'Rich', description: 'Deeper colors that follow the picture a little quicker. Great with music and colorful films.', smoothingMs: 300, saturation: 1.25, luminance: 1.1 },
];

/** Game's Intensity: quick in all three, from true-to-game to vivid colors. Intense is the `game` section's default. */
export const GAME_PRESETS: ReadonlyArray<Preset<GameIntensityId>> = [
  { id: 'subtle', label: 'Subtle', description: 'True-to-game colors with quick reactions. Less to catch your eye at the edge of your view.', smoothingMs: 250, saturation: 1, luminance: 1 },
  { id: 'vivid', label: 'Vivid', description: 'Richer colors that keep up with the action.', smoothingMs: 200, saturation: 1.25, luminance: 1.1 },
  { id: 'intense', label: 'Intense', description: 'Vivid colors with the quickest reactions. Made for fast games.', smoothingMs: 150, saturation: 1.5, luminance: 1.2 },
];

type Obj = Record<string, unknown>;

/** The values a preset sets, as they apply. */
export interface Tuning {
  /** Smoothing is on (Game mode always smooths). */
  smooth: boolean;
  timeMs: unknown;
  stiffness: unknown;
  damping: unknown;
  saturation: unknown;
  luminance: unknown;
}

function adjustmentOf(config: BackendConfig): Obj {
  const list = (config.color as Obj | undefined)?.channelAdjustment;
  return Array.isArray(list) && list[0] && typeof list[0] === 'object' ? (list[0] as Obj) : {};
}

function gameOf(config: BackendConfig): Obj {
  const game = config.game;
  return game && typeof game === 'object' ? (game as Obj) : {};
}

/** Ambient's values: the saved `smoothing` and `color` sections. */
export function ambientTuning(config: BackendConfig): Tuning {
  const smoothing = (config.smoothing ?? {}) as Obj;
  const adj = adjustmentOf(config);
  return {
    smooth: smoothing.enable === true,
    timeMs: smoothing.time_ms,
    stiffness: smoothing.stiffness,
    damping: smoothing.damping,
    saturation: adj.saturationGain,
    luminance: adj.luminanceGain,
  };
}

/** Game's values: the `game` section. */
export function gameTuning(config: BackendConfig): Tuning {
  const game = gameOf(config);
  return { smooth: true, timeMs: game.time_ms, stiffness: game.stiffness, damping: game.damping, saturation: game.saturationGain, luminance: game.luminanceGain };
}

const close = (a: unknown, b: number) => typeof a === 'number' && Math.abs(a - b) < 0.005;

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
      close(tuning.luminance, p.luminance)
    );
  });
  return match?.id ?? 'custom';
}

/** Ambient's Mood, or "custom". */
export function detectMood(config: BackendConfig | null): MoodId | 'custom' | null {
  return detectPreset(MOOD_PRESETS, config && ambientTuning(config));
}

/** Game's Intensity, or "custom". */
export function detectGameIntensity(config: BackendConfig | null): GameIntensityId | 'custom' | null {
  return detectPreset(GAME_PRESETS, config && gameTuning(config));
}

function find<Id extends string>(presets: ReadonlyArray<Preset<Id>>, id: Id): Preset<Id> {
  const p = presets.find((x) => x.id === id);
  if (!p) throw new Error(`Unknown preset ${id}`);
  return p;
}

/** The `smoothing` and `color` sections with the Mood applied (send them with setconfig). */
export function applyMood(config: BackendConfig, id: MoodId): BackendConfig {
  const p = find(MOOD_PRESETS, id);
  const color = (config.color ?? {}) as Obj;
  const list = Array.isArray(color.channelAdjustment) ? (color.channelAdjustment as Obj[]) : [{}];
  return {
    smoothing: { ...((config.smoothing ?? {}) as Obj), enable: true, time_ms: p.smoothingMs, ...springFor(p.smoothingMs) },
    color: {
      ...color,
      channelAdjustment: [{ ...list[0], saturationGain: p.saturation, luminanceGain: p.luminance }, ...list.slice(1)],
    },
  };
}

/** The `game` section with the Intensity applied, keeping its other values (send it with setconfig). */
export function applyGameIntensity(config: BackendConfig, id: GameIntensityId): BackendConfig {
  const p = find(GAME_PRESETS, id);
  return { game: { ...gameOf(config), time_ms: p.smoothingMs, ...springFor(p.smoothingMs), saturationGain: p.saturation, luminanceGain: p.luminance } };
}

/** A duration in seconds as the UI shows it: 350 → "0.35 s". */
export function formatSeconds(ms: number): string {
  return `${Number((ms / 1000).toFixed(2))} s`;
}

/** "What it changes" rows for a preset; Game's transition time is its Reaction time (as on Colors in Game mode). */
export function describePreset(p: Preset): Array<[string, string]> {
  return [
    [(GAME_PRESETS as ReadonlyArray<Preset>).includes(p) ? 'Reaction time' : 'Transitions', formatSeconds(p.smoothingMs)],
    ['Saturation', `${Math.round(p.saturation * 100)}%`],
    ['Color brightness', `${Math.round(p.luminance * 100)}%`],
  ];
}
