// The previews (Home, Effects, Layout's live colors) show the colors as at full brightness: the LED stream carries the
// colors after Home's Brightness, so at 20% they would look nearly off. The Brightness slider (settings/brightness.ts)
// is a curve: at position p the screen and effects come out at about p² of their full output, so that is what the
// colors are divided by (capped at 255).

import { brightnessForScale } from '../settings/brightness';

/** Below this share the colors are scaled up (and the previews say so); at full brightness nothing changes. */
const FULL = 0.995;

/**
 * The share of their full output the LEDs get at the brightness `scaleOutput` with the contrast `gamma`: 1 at full or
 * boosted brightness, and while the lights are off or the brightness is 0 (nothing to bring back).
 */
export function outputShare(scaleOutput: number | undefined, gamma: number | undefined, lightsOn: boolean): number {
  if (!lightsOn || scaleOutput === undefined || !Number.isFinite(scaleOutput) || scaleOutput <= 0) return 1;
  const position = Math.min(1, brightnessForScale(scaleOutput, gamma));
  const share = position * position;
  return share >= FULL || share <= 0 ? 1 : share;
}

/** Whether the previews scale the colors up (and say "Shown at full brightness"). */
export function shownBrighter(share: number): boolean {
  return share < FULL;
}

/** The frame as at full brightness: every channel divided by `share`, capped at 255 (the same frame at full). */
export function atFullBrightness(frame: number[] | null, share: number): number[] | null {
  if (!frame || !shownBrighter(share)) return frame;
  return frame.map((v) => Math.min(255, Math.round(v / share)));
}
