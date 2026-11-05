// The first-run setup's "make it ready" steps (What each light does, Look), the logic without React.
// docs/guided-setup.md describes them.

import type { ScreenInfo } from '../../api/types';
import { defaultRoom, stripLedCount, type Controller, type LayoutSpec, type Room } from '../../layout';
import { isTestDevice } from '../devices/drivers';
import { stripColor } from '../../editor/colors';
import { stripLedRanges } from '../../live/layoutView';

/** The setup's steps; What each light does only once there are lights. */
export const STEPS = ['Welcome', 'Your lights', 'What each does', 'Look', 'Turn on'] as const;
export type Step = 0 | 1 | 2 | 3 | 4;

/** The steps shown: without lights (the user skipped adding them) nothing to give a role. */
export function shownSteps(hasLights: boolean): Step[] {
  return hasLights ? [0, 1, 2, 3, 4] : [0, 1, 3, 4];
}

/** The step after (`by` 1) or before (-1) `step` among the shown ones. */
export function stepAfter(step: Step, hasLights: boolean, by: 1 | -1 = 1): Step {
  const shown = shownSteps(hasLights);
  const i = shown.indexOf(step);
  if (i < 0) return by > 0 ? (shown.find((s) => s > step) ?? 4) : ([...shown].reverse().find((s) => s < step) ?? 0);
  return shown[Math.min(shown.length - 1, Math.max(0, i + by))];
}

/**
 * The setup added devices itself (a board, room lights, another device): some real device isn't one of those the
 * layout had when it loaded. Only then does the setup save the layout before the user changed anything in it (an
 * imported layout stays as it was until then).
 */
export function addedThisSession(loaded: ReadonlySet<string> | null, controllers: readonly Controller[]): boolean {
  return !!loaded && controllers.some((c) => !isTestDevice(c) && !loaded.has(c.id));
}

/** The monitor Backbloom reads, with its size: the primary one with a size, else the first with one. */
export function sizedMonitor(screens: readonly ScreenInfo[]): (ScreenInfo & { widthMm: number; heightMm: number }) | null {
  const sized = screens.filter((s): s is ScreenInfo & { widthMm: number; heightMm: number } => !!s.widthMm && !!s.heightMm);
  return sized.find((s) => s.primary) ?? sized[0] ?? null;
}

/** A monitor in words: "DELL U2723QE · 27 inches (60 × 34 cm)". */
export function monitorText(s: { name: string; widthMm: number; heightMm: number }): string {
  const inches = Math.round(Math.hypot(s.widthMm, s.heightMm) / 25.4);
  return `${s.name} · ${inches} inches (${Math.round(s.widthMm / 10)} × ${Math.round(s.heightMm / 10)} cm)`;
}

/**
 * The room's screen at the monitor's real size, standing where it stood (the bottom's middle kept), or null when the
 * screen has a size of its own already (a strip plan set it, or the user did on Layout): only the default size is
 * replaced.
 */
export function fittedScreen(room: Room, monitor: { widthMm: number; heightMm: number }): Room['screen'] | null {
  const d = defaultRoom(room.furniture).screen;
  const isDefault = Math.abs(room.screen.width - d.width) < 0.001 && Math.abs(room.screen.height - d.height) < 0.001;
  const width = monitor.widthMm / 1000;
  const height = monitor.heightMm / 1000;
  if (!isDefault || (Math.abs(width - room.screen.width) < 0.005 && Math.abs(height - room.screen.height) < 0.005)) return null;
  const cx = room.screen.x + room.screen.width / 2;
  const bottom = room.screen.y + room.screen.height;
  return { x: cx - width / 2, y: Math.max(0, bottom - height), width, height };
}

function rgbOf(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

/** A still frame for the room picture before live colors arrive: each strip's LEDs in its color on Layout. */
export function stripColorFrame(spec: LayoutSpec): number[] {
  const ranges = stripLedRanges(spec);
  const total = Math.max(0, ...[...ranges.values()].map((r) => r.first + r.length));
  const frame = new Array<number>(total * 3).fill(0);
  spec.strips.forEach((s, i) => {
    const r = ranges.get(s.id);
    if (!r) return;
    const [red, green, blue] = rgbOf(stripColor(i));
    for (let k = 0; k < Math.min(r.length, stripLedCount(s)); k++) frame.splice((r.first + k) * 3, 3, red, green, blue);
  });
  return frame;
}

/**
 * The room picture's colors with one strip picked out: its LEDs as they are, every other LED at a quarter, so the
 * light being chosen stands out. `frame`: the live colors (else each strip in its Layout color).
 */
export function highlightFrame(spec: LayoutSpec, frame: readonly number[] | null, stripId: string | null): number[] {
  // live colors once they show something (a black screen would leave nothing to see)
  const base = frame && frame.some((v) => v > 8) ? [...frame] : stripColorFrame(spec);
  const range = stripId ? stripLedRanges(spec).get(stripId) : undefined;
  if (!range) return base;
  const first = range.first * 3;
  const end = (range.first + range.length) * 3;
  return base.map((v, i) => (i >= first && i < end ? v : Math.round(v * 0.25)));
}
