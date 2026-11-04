// Home's Brightness slider and the stored `scaleOutput`. The backend scales the light by `scaleOutput` before it encodes
// the colors as sRGB and applies the contrast (`gamma`, 1.5 by default) to the encoded values (as HyperHDR does), and
// LED strips show the result as it is, so a linear slider put most of the full output into its lower half. The slider
// follows a curve instead: at position p the strip gives about p² of its full output for the screen and effects (a
// quarter at the middle), close to how bright it looks. Static colors skip the contrast, so they come out a little
// brighter at the same position. The backend's power limits follow each one's share (ColorProcessor.OutputShare).
//
// Each mode has its own brightness: Ambient's is `color.channelAdjustment[0].scaleOutput`, Game's the `game` section's
// `scaleOutput`, which replaces it while Game mode is on (Engine/GameTuning.cs); until it is set, Game uses Ambient's.

import type { BackendConfig } from '../api/types';

/** sRGB encoding of linear light (values above 1 follow the same curve, for a boosted `scaleOutput`). */
function encode(linear: number): number {
  return linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.pow(linear, 1 / 2.4) - 0.055;
}

/** sRGB decoding to linear light. */
function decode(encoded: number): number {
  return encoded <= 0.04045 ? encoded / 12.92 : Math.pow((encoded + 0.055) / 1.055, 2.4);
}

/** The contrast (`gamma`) of the default color settings. */
export const DEFAULT_GAMMA = 1.5;

const usable = (gamma: number | undefined) => (gamma !== undefined && Number.isFinite(gamma) && gamma > 0 ? gamma : DEFAULT_GAMMA);

/** The `scaleOutput` for a slider position 0..1 with the contrast `gamma`. */
export function scaleForBrightness(position: number, gamma?: number): number {
  const p = Math.max(0, position);
  return decode(Math.pow(p * p, 1 / usable(gamma)));
}

/** The slider position for a stored `scaleOutput` with the contrast `gamma` (above 1 when boosted on the Colors page). */
export function brightnessForScale(scale: number, gamma?: number): number {
  return Math.sqrt(Math.pow(encode(Math.max(0, scale)), usable(gamma)));
}

/** Game mode's own `scaleOutput`, or undefined while it uses Ambient's. */
export function gameScale(config: BackendConfig | null | undefined): number | undefined {
  const game = config?.game;
  const scale = game && typeof game === 'object' ? (game as Record<string, unknown>).scaleOutput : undefined;
  return typeof scale === 'number' ? scale : undefined;
}

/** The `game` section with its own `scaleOutput` (the rest as saved). */
export function withGameScale(config: BackendConfig, scale: number): BackendConfig {
  const game = config.game && typeof config.game === 'object' ? (config.game as Record<string, unknown>) : {};
  return { game: { ...game, scaleOutput: scale } };
}
