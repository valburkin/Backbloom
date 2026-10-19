import type {
  Area,
  CompiledController,
  EdgeSegment,
  LayoutSpec,
  LedRect,
  Segment,
  Strip,
} from './types';

/** Default along-edge sampling width, as a multiple of LED spacing. */
export const DEFAULT_BLEND = 1.2;

/**
 * Rect emitted for a dark LED. The engine skips sampling for disabled LEDs entirely
 * (ImageColorAveraging), and LedString accepts a zero-size rect, so a centered point is safe.
 */
export const DISABLED_RECT: Readonly<LedRect> = Object.freeze({
  hmin: 0.5,
  hmax: 0.5,
  vmin: 0.5,
  vmax: 0.5,
  group: 0,
  disabled: true,
});

/** Number of LEDs a segment produces. Invalid counts (negative, NaN) produce none; fractions are floored. */
export function segmentLedCount(segment: Segment): number {
  const n = segment.count;
  return Number.isFinite(n) ? Math.max(0, Math.floor(n)) : 0;
}

/** Number of LEDs a strip's segments add up to. */
export function stripLedCount(strip: Strip): number {
  return strip.segments.reduce((n, s) => n + segmentLedCount(s), 0);
}

/** Clamp to [0, 1] and round to 4 decimals (the precision the engine config is stored with). */
function coord(x: number): number {
  if (!Number.isFinite(x)) return 0;
  return Math.round(Math.min(1, Math.max(0, x)) * 1e4) / 1e4;
}

function rect(hmin: number, hmax: number, vmin: number, vmax: number): LedRect {
  return { hmin: coord(hmin), hmax: coord(hmax), vmin: coord(vmin), vmax: coord(vmax), group: 0 };
}

function areaRect(a: Area): LedRect {
  return rect(a.hmin, a.hmax, a.vmin, a.vmax);
}

function compileEdge(seg: EdgeSegment, n: number): LedRect[] {
  const step = (seg.to - seg.from) / n;
  const half = ((seg.blend ?? DEFAULT_BLEND) * Math.abs(step)) / 2;
  const d = seg.depth;
  const out: LedRect[] = [];
  for (let k = 0; k < n; k++) {
    const c = seg.from + (k + 0.5) * step;
    const a0 = c - half;
    const a1 = c + half;
    switch (seg.edge) {
      case 'top':
        out.push(rect(a0, a1, 0, d));
        break;
      case 'bottom':
        out.push(rect(a0, a1, 1 - d, 1));
        break;
      case 'left':
        out.push(rect(0, d, a0, a1));
        break;
      case 'right':
        out.push(rect(1 - d, 1, a0, a1));
        break;
    }
  }
  return out;
}

/** Compile one segment to per-LED rects, in wiring order. */
export function compileSegment(segment: Segment): LedRect[] {
  const n = segmentLedCount(segment);
  if (n === 0) return [];
  switch (segment.kind) {
    case 'edge':
      return compileEdge(segment, n);
    case 'fixed':
      return Array.from({ length: n }, () => areaRect(segment.area));
    case 'off':
      return Array.from({ length: n }, () => ({ ...DISABLED_RECT }));
    default:
      return [];
  }
}

/** Compile one strip to per-LED rects, in wiring order. */
export function compileStrip(strip: Strip): LedRect[] {
  return strip.segments.flatMap(compileSegment);
}

function nonNegativeInt(x: number): number {
  return Number.isFinite(x) ? Math.max(0, Math.floor(x)) : 0;
}

/**
 * Compile a whole layout: one rect array per controller, covering its full LED index space
 * (max over outputs of start + length). Each strip lands at its output's start; indices no
 * strip covers stay dark, and a strip longer than its output is truncated to fit.
 */
export function compileLayout(spec: LayoutSpec): CompiledController[] {
  return spec.controllers.map((controller) => {
    const size = controller.outputs.reduce(
      (m, o) => Math.max(m, nonNegativeInt(o.start) + nonNegativeInt(o.length)),
      0,
    );
    const leds: LedRect[] = Array.from({ length: size }, () => ({ ...DISABLED_RECT }));

    for (const strip of spec.strips) {
      if (strip.controllerId !== controller.id) continue;
      const output = controller.outputs.find((o) => o.index === strip.output);
      if (!output) continue;
      const start = nonNegativeInt(output.start);
      const rects = compileStrip(strip);
      const n = Math.min(rects.length, nonNegativeInt(output.length));
      for (let i = 0; i < n; i++) leds[start + i] = rects[i];
    }

    return { controllerId: controller.id, leds };
  });
}
