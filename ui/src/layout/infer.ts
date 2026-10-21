import { DEFAULT_BLEND, DEFAULT_DEPTH, DISABLED_RECT } from './compile';
import { defaultRoom } from './room';
import type { Area, Controller, LayoutSpec, LedRange, LedRect, PathPoint, Room, Strip } from './types';

// Import of per-LED rects (HyperHDR's `leds` setting, or a running configuration without a saved layout): runs of
// rects are fitted as HyperHDR-style segments along the screen edges, then laid out as paths along the screen's
// border on the front wall of a default room.

/** Screen edge. Positions along an edge run left→right (top/bottom) or top→bottom (left/right). */
export type Edge = 'top' | 'bottom' | 'left' | 'right';

/** A run of LEDs that mirrors part of a screen edge (from/to: 0..1 along the edge, from > to = backwards). */
export interface LegacyEdge {
  kind: 'edge';
  count: number;
  edge: Edge;
  from: number;
  to: number;
  /** How far into the screen each LED samples (0..1], a fraction of the screen side across the edge. */
  depth: number;
  /** Along-edge sampling width as a multiple of LED spacing. */
  blend?: number;
}

/** LEDs that all show one area. */
export interface LegacyFixed {
  kind: 'fixed';
  count: number;
  area: Area;
}

/** Dark LEDs. */
export interface LegacyOff {
  kind: 'off';
  count: number;
}

export type LegacySegment = LegacyEdge | LegacyFixed | LegacyOff;

function legacyCoord(x: number): number {
  if (!Number.isFinite(x)) return 0;
  return Math.round(Math.min(1, Math.max(0, x)) * 1e4) / 1e4;
}

function legacyRect(hmin: number, hmax: number, vmin: number, vmax: number): LedRect {
  return { hmin: legacyCoord(hmin), hmax: legacyCoord(hmax), vmin: legacyCoord(vmin), vmax: legacyCoord(vmax), group: 0 };
}

/** The rects a HyperHDR-style segment stands for (used to check a fit). */
export function compileLegacySegment(seg: LegacySegment): LedRect[] {
  const n = Number.isFinite(seg.count) ? Math.max(0, Math.floor(seg.count)) : 0;
  if (seg.kind === 'off') return Array.from({ length: n }, () => ({ ...DISABLED_RECT }));
  if (seg.kind === 'fixed') return Array.from({ length: n }, () => legacyRect(seg.area.hmin, seg.area.hmax, seg.area.vmin, seg.area.vmax));
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
        out.push(legacyRect(a0, a1, 0, d));
        break;
      case 'bottom':
        out.push(legacyRect(a0, a1, 1 - d, 1));
        break;
      case 'left':
        out.push(legacyRect(0, d, a0, a1));
        break;
      case 'right':
        out.push(legacyRect(1 - d, 1, a0, a1));
        break;
    }
  }
  return out;
}

/** Rects closer than this on every coordinate count as identical (legacy configs use 4 decimals). */
const SAME_TOL = 5e-5;
/** Tolerance for "hugs the same edge with the same depth". */
const BAND_TOL = 1e-4;
/** A fitted edge segment is accepted when every compiled coordinate is within this of the input. */
const FIT_TOL = 6e-4;
/** How many further LEDs to try after a failed fit before giving up on growing a run. */
const LOOKAHEAD = 2;
/** Values at/below this (or at/above 1 - this) are treated as clamped to the screen border. */
const BORDER_EPS = 1e-6;

const COORDS = ['hmin', 'hmax', 'vmin', 'vmax'] as const;

function areaOf(r: LedRect): Area {
  return { hmin: r.hmin, hmax: r.hmax, vmin: r.vmin, vmax: r.vmax };
}

function sameRect(a: LedRect, b: LedRect): boolean {
  return !a.disabled && !b.disabled && COORDS.every((k) => Math.abs(a[k] - b[k]) <= SAME_TOL);
}

/** Largest coordinate error between a segment's compiled rects and rects[start...]. */
function maxError(seg: LegacySegment, rects: LedRect[], start: number): number {
  const compiled = compileLegacySegment(seg);
  let err = 0;
  for (let k = 0; k < compiled.length; k++) {
    const want = rects[start + k];
    if (!want || Boolean(want.disabled) !== Boolean(compiled[k].disabled)) return Infinity;
    for (const c of COORDS) err = Math.max(err, Math.abs(compiled[k][c] - want[c]));
  }
  return err;
}

/** A screen edge plus how deep a rect reaches into the screen from it. */
interface Band {
  edge: Edge;
  depth: number;
}

function bandsOf(r: LedRect): Band[] {
  const bands: Band[] = [];
  if (r.vmin <= BORDER_EPS && r.vmax > 0) bands.push({ edge: 'top', depth: r.vmax });
  if (r.vmax >= 1 - BORDER_EPS && r.vmin < 1) bands.push({ edge: 'bottom', depth: 1 - r.vmin });
  if (r.hmin <= BORDER_EPS && r.hmax > 0) bands.push({ edge: 'left', depth: r.hmax });
  if (r.hmax >= 1 - BORDER_EPS && r.hmin < 1) bands.push({ edge: 'right', depth: 1 - r.hmin });
  return bands;
}

function inBand(r: LedRect, b: Band): boolean {
  if (r.disabled) return false;
  return bandsOf(r).some((x) => x.edge === b.edge && Math.abs(x.depth - b.depth) <= BAND_TOL);
}

function isHorizontal(edge: Edge): boolean {
  return edge === 'top' || edge === 'bottom';
}

/**
 * Fit an edge segment to rects[start, start + n). Along the edge, LED k spans
 * [a + k*s, b + k*s] before clamping; unclamped bounds give a least-squares fit of the shared
 * slope s and the two intercepts. Bounds that are clamped on every LED only constrain the
 * intercept, so the narrowest consistent value is used. Returns null when underdetermined.
 */
function fitEdge(rects: LedRect[], start: number, n: number, band: Band): LegacyEdge | null {
  const lo: [number, number][] = [];
  const hi: [number, number][] = [];
  const loClamped: [number, number][] = [];
  const hiClamped: [number, number][] = [];
  const horiz = isHorizontal(band.edge);
  for (let k = 0; k < n; k++) {
    const r = rects[start + k];
    const l = horiz ? r.hmin : r.vmin;
    const h = horiz ? r.hmax : r.vmax;
    (l > BORDER_EPS && l < 1 - BORDER_EPS ? lo : loClamped).push([k, l]);
    (h > BORDER_EPS && h < 1 - BORDER_EPS ? hi : hiClamped).push([k, h]);
  }

  // Shared-slope regression with one intercept per bound.
  let sxy = 0;
  let sxx = 0;
  const means = (pts: [number, number][]) => {
    const mk = pts.reduce((t, p) => t + p[0], 0) / pts.length;
    const my = pts.reduce((t, p) => t + p[1], 0) / pts.length;
    for (const [k, y] of pts) {
      sxy += (k - mk) * (y - my);
      sxx += (k - mk) ** 2;
    }
    return [mk, my] as const;
  };
  const loMean = lo.length ? means(lo) : undefined;
  const hiMean = hi.length ? means(hi) : undefined;
  if (sxx === 0) return null;
  const s = sxy / sxx;
  if (Math.abs(s) < 1e-9) return null;

  // Clamped bounds: true value <= 0 when stored as 0, >= 1 when stored as 1.
  const fromClamped = (pts: [number, number][]): number | undefined => {
    const atZero = pts.filter((p) => p[1] <= BORDER_EPS).map(([k]) => -s * k);
    const atOne = pts.filter((p) => p[1] > BORDER_EPS).map(([k]) => 1 - s * k);
    if (atZero.length) return Math.min(...atZero);
    if (atOne.length) return Math.max(...atOne);
    return undefined;
  };
  const a = loMean ? loMean[1] - s * loMean[0] : fromClamped(loClamped);
  const b = hiMean ? hiMean[1] - s * hiMean[0] : fromClamped(hiClamped);
  if (a === undefined || b === undefined || b - a <= 0) return null;

  const from = (a + b) / 2 - s / 2;
  return {
    kind: 'edge',
    count: n,
    edge: band.edge,
    from,
    to: from + n * s,
    depth: band.depth,
    blend: (b - a) / Math.abs(s),
  };
}

function roundTo(x: number, decimals: number): number {
  const f = 10 ** decimals;
  return Math.round(x * f) / f || 0; // no -0
}

/** Round each parameter to the fewest decimals that don't make the fit worse. */
function polish(seg: LegacyEdge, rects: LedRect[], start: number): LegacyEdge {
  const limit = Math.max(maxError(seg, rects, start), 1e-4) + 1e-9;
  let best = seg;
  for (const key of ['depth', 'blend', 'from', 'to'] as const) {
    for (let d = 1; d <= 6; d++) {
      const cand = { ...best, [key]: roundTo(best[key] as number, d) };
      if (maxError(cand, rects, start) <= limit) {
        best = cand;
        break;
      }
    }
  }
  return best;
}

/** Longest edge segment starting at rects[i], over every edge that rect hugs. */
function longestEdgeRun(rects: LedRect[], i: number): LegacyEdge | null {
  let best: LegacyEdge | null = null;
  for (const band of bandsOf(rects[i])) {
    let end = i + 1;
    while (end < rects.length && inBand(rects[end], band)) end++;
    let fit: LegacyEdge | null = null;
    let misses = 0;
    for (let n = 2; i + n <= end; n++) {
      // Leave the first LED of a following run of identical rects to a fixed segment.
      const last = i + n - 1;
      if (last + 1 < rects.length && sameRect(rects[last], rects[last + 1])) break;
      const seg = fitEdge(rects, i, n, band);
      if (seg && maxError(seg, rects, i) <= FIT_TOL) {
        fit = seg;
        misses = 0;
      } else if (++misses > LOOKAHEAD) {
        break;
      }
    }
    if (fit && (!best || fit.count > best.count)) best = fit;
  }
  return best && polish(best, rects, i);
}

/**
 * Legacy import: fit segments to an existing per-LED rect list. compileStrip() of the result
 * reproduces the input within 0.001. Runs of disabled LEDs become `off`, runs of identical rects
 * become `fixed`, evenly spaced runs along one edge become `edge`; any LED that fits none of these
 * becomes a single-LED `fixed` segment. LED groups are not preserved.
 */
export function inferSegments(rects: LedRect[]): LegacySegment[] {
  const out: LegacySegment[] = [];
  let i = 0;
  while (i < rects.length) {
    const r = rects[i];
    let j = i + 1;

    if (r.disabled) {
      while (j < rects.length && rects[j].disabled) j++;
      out.push({ kind: 'off', count: j - i });
    } else {
      while (j < rects.length && sameRect(rects[j], r)) j++;
      const edge = j - i === 1 ? longestEdgeRun(rects, i) : null;
      if (edge) {
        out.push(edge);
        j = i + edge.count;
      } else {
        out.push({ kind: 'fixed', count: j - i, area: areaOf(r) });
      }
    }
    i = j;
  }
  return out;
}


/** Aspect HyperHDR layouts are assumed to be made for (their depths on the sides are fractions of the width). */
const LEGACY_ASPECT = 16 / 9;
/** Spacing of the LEDs of a fixed-area run along the edge it's drawn on, in meters (60 LEDs/m). */
const FIXED_SPACING = 1 / 60;

/** Edge closest to an area's middle, and the middle's position along that edge. */
function nearestEdge(area: Area): { edge: Edge; t: number } {
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

/** A point on the screen's border (on the front wall), `t` along the edge. */
function borderPoint(room: Room, edge: Edge, t: number): PathPoint {
  const s = room.screen;
  const clampX = (x: number) => Math.min(room.width, Math.max(0, x));
  const clampY = (y: number) => Math.min(room.height, Math.max(0, y));
  switch (edge) {
    case 'top':
      return { surface: 'front', x: clampX(s.x + t * s.width), y: clampY(s.y) };
    case 'bottom':
      return { surface: 'front', x: clampX(s.x + t * s.width), y: clampY(s.y + s.height) };
    case 'left':
      return { surface: 'front', x: clampX(s.x), y: clampY(s.y + t * s.height) };
    case 'right':
      return { surface: 'front', x: clampX(s.x + s.width), y: clampY(s.y + t * s.height) };
  }
}

function round(x: number, decimals: number): number {
  const f = 10 ** decimals;
  return Math.round(x * f) / f || 0;
}

/**
 * A strip running along the screen's border the way `segments` describe: each edge run from its start to its end,
 * fixed-area runs as a short run on the edge nearest their area, dark runs kept dark between their neighbors.
 * LEDs are spread evenly by length (nothing is pinned, so reshaping the strip keeps them even); depth and blend
 * are the LED-weighted averages (depth in screen heights).
 */
export function stripFromSegments(
  segments: LegacySegment[],
  room: Room,
  base: Pick<Strip, 'id' | 'name' | 'controllerId' | 'output'>,
): Strip {
  const path: PathPoint[] = [];
  const dark: LedRange[] = [];
  let at = 0;
  let depthSum = 0;
  let blendSum = 0;
  let weight = 0;
  const add = (p: PathPoint) => {
    const last = path[path.length - 1];
    const point: PathPoint = { ...p, x: round(p.x, 4), y: round(p.y, 4) };
    if (!(last && last.surface === point.surface && Math.abs(last.x - point.x) < 1e-4 && Math.abs(last.y - point.y) < 1e-4)) path.push(point);
  };
  for (const seg of segments) {
    const n = Number.isFinite(seg.count) ? Math.max(0, Math.floor(seg.count)) : 0;
    if (n === 0) continue;
    if (seg.kind === 'off') {
      dark.push({ start: at, count: n });
    } else if (seg.kind === 'edge') {
      add(borderPoint(room, seg.edge, seg.from));
      add(borderPoint(room, seg.edge, seg.to));
      const horizontal = seg.edge === 'top' || seg.edge === 'bottom';
      depthSum += n * (horizontal ? seg.depth : seg.depth * LEGACY_ASPECT);
      blendSum += n * (seg.blend ?? DEFAULT_BLEND);
      weight += n;
    } else {
      const { edge, t } = nearestEdge(seg.area);
      const length = edge === 'top' || edge === 'bottom' ? room.screen.width : room.screen.height;
      const half = (n * FIXED_SPACING) / 2 / length;
      add(borderPoint(room, edge, t + half));
      add(borderPoint(room, edge, t - half));
    }
    at += n;
  }
  if (path.length === 0) {
    // Nothing to place: a dark strip along the bottom edge.
    path.push(borderPoint(room, 'bottom', 1), borderPoint(room, 'bottom', 0));
  } else if (path.length === 1) {
    path.push({ ...path[0], x: Math.min(room.width, path[0].x + 0.01) });
  }
  const strip: Strip = { ...base, ledCount: at, path };
  if (dark.length) strip.dark = dark;
  if (weight > 0) {
    const depth = Math.min(1, round(depthSum / weight, 3));
    const blend = round(blendSum / weight, 2);
    if (depth !== DEFAULT_DEPTH) strip.depth = depth;
    if (blend !== DEFAULT_BLEND) strip.blend = blend;
  }
  return strip;
}

/**
 * Legacy import: turn a controller-wide rect list into a spec with one strip per controller output ("Strip 1",
 * "Strip 2"...) in a default room. Outputs with no rects in range get no strip. A controller without outputs is
 * given a single output covering every rect.
 */
export function specFromLegacy(rects: LedRect[], controller: Controller, room: Room = defaultRoom()): LayoutSpec {
  const outputs = controller.outputs.length
    ? controller.outputs
    : [{ index: 0, start: 0, length: rects.length }];
  const strips: Strip[] = [];
  for (const o of outputs) {
    const slice = rects.slice(o.start, o.start + o.length);
    if (slice.length === 0) continue;
    const n = strips.length + 1;
    strips.push(stripFromSegments(inferSegments(slice), room, { id: `strip-${n}`, name: `Strip ${n}`, controllerId: controller.id, output: o.index }));
  }
  return { version: 1, room, controllers: [{ ...controller, outputs }], strips };
}
