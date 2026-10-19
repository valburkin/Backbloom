import type { Area, Edge, Segment, Strip } from '../layout';

/** Screen aspect presets for the canvas (view only; the layout itself is resolution independent). */
export const ASPECTS = [
  { id: '16:9', label: '16:9', w: 16, h: 9, ratio: 16 / 9 },
  { id: '21:9', label: '21:9', w: 21, h: 9, ratio: 21 / 9 },
  { id: '32:9', label: '32:9', w: 32, h: 9, ratio: 32 / 9 },
  { id: '16:10', label: '16:10', w: 16, h: 10, ratio: 16 / 10 },
] as const;
export type AspectId = (typeof ASPECTS)[number]['id'];

export function aspectRatio(id: AspectId): number {
  return ASPECTS.find((a) => a.id === id)?.ratio ?? 16 / 9;
}

export function aspectSize(id: AspectId): { w: number; h: number } {
  const a = ASPECTS.find((x) => x.id === id) ?? ASPECTS[0];
  return { w: a.w, h: a.h };
}

export interface ScreenBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface LedDot {
  x: number;
  y: number;
  /** Index within the strip (wiring order). */
  index: number;
  segmentIndex: number;
  kind: Segment['kind'];
}

/** Base distance of strip lane 0 from the screen edge, and extra distance per lane. */
export const LANE_BASE = 20;
export const LANE_STEP = 16;
const CLUSTER_SPACING = 9;

function edgePoint(box: ScreenBox, edge: Edge, t: number, off: number): { x: number; y: number } {
  switch (edge) {
    case 'top':
      return { x: box.x + t * box.w, y: box.y - off };
    case 'bottom':
      return { x: box.x + t * box.w, y: box.y + box.h + off };
    case 'left':
      return { x: box.x - off, y: box.y + t * box.h };
    case 'right':
      return { x: box.x + box.w + off, y: box.y + t * box.h };
  }
}

/** Edge closest to an area's center, and the center's position along that edge. */
export function nearestEdge(area: Area): { edge: Edge; t: number } {
  const cx = (area.hmin + area.hmax) / 2;
  const cy = (area.vmin + area.vmax) / 2;
  const d: Array<[Edge, number, number]> = [
    ['top', cy, cx],
    ['bottom', 1 - cy, cx],
    ['left', cx, cy],
    ['right', 1 - cx, cy],
  ];
  d.sort((a, b) => a[1] - b[1]);
  return { edge: d[0][0], t: d[0][2] };
}

/**
 * Where each LED of a strip is drawn: just outside the screen edge it samples, in wiring order.
 * Fixed-area LEDs cluster next to the area's nearest edge; "off" LEDs are spread between their neighbors
 * so the strip still reads as one continuous path.
 */
export function stripDots(strip: Strip, box: ScreenBox, lane = 0): LedDot[] {
  const off = LANE_BASE + lane * LANE_STEP;
  const dots: Array<LedDot & { anchored: boolean }> = [];
  let index = 0;
  strip.segments.forEach((seg, segmentIndex) => {
    const n = Math.max(0, Math.floor(seg.count));
    if (seg.kind === 'edge') {
      for (let k = 0; k < n; k++) {
        const t = seg.from + ((k + 0.5) * (seg.to - seg.from)) / n;
        dots.push({ ...edgePoint(box, seg.edge, t, off), index: index++, segmentIndex, kind: 'edge', anchored: true });
      }
    } else if (seg.kind === 'fixed') {
      const { edge, t } = nearestEdge(seg.area);
      const span = edge === 'top' || edge === 'bottom' ? box.w : box.h;
      for (let k = 0; k < n; k++) {
        const dt = ((k - (n - 1) / 2) * CLUSTER_SPACING) / span;
        dots.push({ ...edgePoint(box, edge, t + dt, off), index: index++, segmentIndex, kind: 'fixed', anchored: true });
      }
    } else {
      for (let k = 0; k < n; k++) dots.push({ x: NaN, y: NaN, index: index++, segmentIndex, kind: 'off', anchored: false });
    }
  });

  // Place unanchored ("off") LEDs by interpolating between the nearest anchored neighbors.
  for (let i = 0; i < dots.length; i++) {
    if (dots[i].anchored) continue;
    let j = i;
    while (j < dots.length && !dots[j].anchored) j++;
    const prev = i > 0 ? dots[i - 1] : undefined;
    const next = j < dots.length ? dots[j] : undefined;
    const runLen = j - i;
    for (let k = 0; k < runLen; k++) {
      const d = dots[i + k];
      if (prev && next) {
        const f = (k + 1) / (runLen + 1);
        d.x = prev.x + (next.x - prev.x) * f;
        d.y = prev.y + (next.y - prev.y) * f;
      } else if (prev || next) {
        // Trailing / leading dark LEDs: continue the strip's direction past the anchor.
        const a = (prev ?? next)!;
        const ai = prev ? i - 1 : j;
        const b = prev ? dots[ai - 1] : dots[ai + 1];
        let ux = 1;
        let uy = 0;
        if (b && b.anchored) {
          const len = Math.hypot(a.x - b.x, a.y - b.y) || 1;
          ux = (a.x - b.x) / len;
          uy = (a.y - b.y) / len;
        }
        const steps = prev ? k + 1 : runLen - k;
        d.x = a.x + ux * steps * CLUSTER_SPACING * 0.8;
        d.y = a.y + uy * steps * CLUSTER_SPACING * 0.8;
      } else {
        // Strip with only dark LEDs: lay them along the bottom-left.
        d.x = box.x + (i + k) * CLUSTER_SPACING;
        d.y = box.y + box.h + off;
      }
    }
    i = j - 1;
  }
  return dots.map(({ anchored: _a, ...d }) => d);
}

/** Typical distance between neighboring dots (used to size dots and arrows). */
export function typicalSpacing(dots: LedDot[]): number {
  const gaps: number[] = [];
  for (let i = 1; i < dots.length; i++) {
    const g = Math.hypot(dots[i].x - dots[i - 1].x, dots[i].y - dots[i - 1].y);
    if (g > 0.01) gaps.push(g);
  }
  if (!gaps.length) return 12;
  gaps.sort((a, b) => a - b);
  return gaps[Math.floor(gaps.length / 2)];
}
