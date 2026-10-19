import { compileSegment } from './compile';
import type { Area, Controller, Edge, EdgeSegment, LayoutSpec, LedRect, Segment, Strip } from './types';

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
function maxError(seg: Segment, rects: LedRect[], start: number): number {
  const compiled = compileSegment(seg);
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
function fitEdge(rects: LedRect[], start: number, n: number, band: Band): EdgeSegment | null {
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
function polish(seg: EdgeSegment, rects: LedRect[], start: number): EdgeSegment {
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
function longestEdgeRun(rects: LedRect[], i: number): EdgeSegment | null {
  let best: EdgeSegment | null = null;
  for (const band of bandsOf(rects[i])) {
    let end = i + 1;
    while (end < rects.length && inBand(rects[end], band)) end++;
    let fit: EdgeSegment | null = null;
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
export function inferSegments(rects: LedRect[]): Segment[] {
  const out: Segment[] = [];
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

/**
 * Legacy import: turn a controller-wide rect list into a spec with one strip per controller
 * output ("Strip 1", "Strip 2"...). Outputs with no rects in range get no strip. A controller
 * without outputs is given a single output covering every rect.
 */
export function specFromLegacy(rects: LedRect[], controller: Controller): LayoutSpec {
  const outputs = controller.outputs.length
    ? controller.outputs
    : [{ index: 0, start: 0, length: rects.length }];
  const strips: Strip[] = [];
  for (const o of outputs) {
    const slice = rects.slice(o.start, o.start + o.length);
    if (slice.length === 0) continue;
    const n = strips.length + 1;
    strips.push({
      id: `strip-${n}`,
      name: `Strip ${n}`,
      controllerId: controller.id,
      output: o.index,
      segments: inferSegments(slice),
    });
  }
  return { version: 1, controllers: [{ ...controller, outputs }], strips };
}
