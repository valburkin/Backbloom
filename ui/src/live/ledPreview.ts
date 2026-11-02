import type { ServerInfo } from '../api/types';

type Rect = NonNullable<ServerInfo['leds']>[number];

export interface PreviewPoint {
  /** 0..1 across the preview, including the frame around the screen. */
  x: number;
  y: number;
  index: number;
}

/** Fraction of the preview taken by the frame (LED band) on each side. */
export const PREVIEW_FRAME = 0.07;

type Side = 0 | 1 | 2 | 3; // top, bottom, left, right

/** Screen edges a sampling area touches. */
function touching(r: Rect): Side[] {
  const out: Side[] = [];
  if (r.vmin <= 0.001) out.push(0);
  if (r.vmax >= 0.999) out.push(1);
  if (r.hmin <= 0.001) out.push(2);
  if (r.hmax >= 0.999) out.push(3);
  return out;
}

function nearestSide(r: Rect): Side {
  const cx = (r.hmin + r.hmax) / 2;
  const cy = (r.vmin + r.vmax) / 2;
  const d = [cy, 1 - cy, cx, 1 - cx];
  return d.indexOf(Math.min(...d)) as Side;
}

/**
 * Where to draw each LED in the live preview: the center of its sampling area pushed out to the screen edge it
 * belongs to, so the colors form a glow around a screen like the real setup. Corner areas touch two edges; they
 * follow their neighbors in the strip. Disabled LEDs are skipped.
 */
export function previewPoints(rects: readonly Rect[]): PreviewPoint[] {
  const f = PREVIEW_FRAME;
  const inner = 1 - 2 * f;
  const touches = rects.map(touching);
  const single = (i: number): Side | null => (touches[i]?.length === 1 ? touches[i][0] : null);
  const sideOf = (i: number): Side => {
    const t = touches[i];
    if (t.length === 1) return t[0];
    if (t.length >= 2) {
      for (let k = 1; k < rects.length; k++) {
        for (const j of [i - k, i + k]) {
          const s = single(j);
          if (s !== null && t.includes(s)) return s;
        }
        if (k > 3) break;
      }
    }
    return nearestSide(rects[i]);
  };
  const out: PreviewPoint[] = [];
  rects.forEach((r, index) => {
    if (r.disabled) return;
    const px = f + inner * Math.min(1, Math.max(0, (r.hmin + r.hmax) / 2));
    const py = f + inner * Math.min(1, Math.max(0, (r.vmin + r.vmax) / 2));
    const side = sideOf(index);
    const p = side === 0 ? { x: px, y: f / 2 } : side === 1 ? { x: px, y: 1 - f / 2 } : side === 2 ? { x: f / 2, y: py } : { x: 1 - f / 2, y: py };
    out.push({ ...p, index });
  });
  return out;
}

/** CSS color of LED `index` in a flat [r,g,b,r,g,b,...] frame. */
export function ledColor(frame: ArrayLike<number> | null, index: number): string {
  if (!frame || frame.length < index * 3 + 3) return 'rgb(30,34,46)';
  return `rgb(${frame[index * 3]},${frame[index * 3 + 1]},${frame[index * 3 + 2]})`;
}

/**
 * The colors the lights show on the left, in the middle and on the right of the screen (each LED counted by the
 * middle of its sampling area), for the page glow. A plain average turns vivid colors muddy, so each keeps its hue
 * and is scaled to the LEDs' average brightness. Null without a frame or LEDs.
 */
export function glowColors(frame: ArrayLike<number> | null, rects: readonly Rect[] | undefined): [string, string, string] | null {
  if (!frame || !rects?.length) return null;
  const sums = [0, 1, 2, 3].map(() => ({ r: 0, g: 0, b: 0, peak: 0, n: 0 }));
  rects.forEach((rect, i) => {
    if (frame.length < i * 3 + 3) return;
    const r = frame[i * 3];
    const g = frame[i * 3 + 1];
    const b = frame[i * 3 + 2];
    const x = (rect.hmin + rect.hmax) / 2;
    for (const s of [sums[x < 1 / 3 ? 0 : x > 2 / 3 ? 2 : 1], sums[3]]) {
      s.r += r;
      s.g += g;
      s.b += b;
      s.peak += Math.max(r, g, b);
      s.n++;
    }
  });
  if (!sums[3].n) return null;
  const css = (s: (typeof sums)[number]) => {
    const max = Math.max(s.r, s.g, s.b);
    if (!max) return 'rgb(0,0,0)';
    // Average color, scaled so its strongest channel is the LEDs' average brightness.
    const k = s.peak / max / s.n;
    return `rgb(${Math.round(s.r * k)},${Math.round(s.g * k)},${Math.round(s.b * k)})`;
  };
  const all = css(sums[3]);
  return [sums[0].n ? css(sums[0]) : all, sums[1].n ? css(sums[1]) : all, sums[2].n ? css(sums[2]) : all];
}
