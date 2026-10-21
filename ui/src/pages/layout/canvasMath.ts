import {
  outlinePoint,
  fromUnfolded,
  onSurface,
  SURFACES,
  surfaceOrigin,
  surfaceSize,
  toUnfolded,
  touchingInUnfolded,
  unfoldedSize,
  type PathPoint,
  type Room,
  type RoomPoint,
  type Surface,
} from '../../layout';
import { furnitureSnaps } from './furniture';

// View and snapping math of the room canvas (unfolded room coordinates, meters).

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** What the canvas shows: the unfolded-room point in the middle and the zoom (pixels per meter). */
export interface View {
  cx: number;
  cy: number;
  scale: number;
}

export const MIN_SCALE = 20;
export const MAX_SCALE = 2000;

export function clampScale(s: number): number {
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, s));
}

/** The view that fits `r` (plus `pad` pixels around it) into a canvas of `w` x `h` pixels. */
export function fitView(r: Rect, w: number, h: number, pad = 24): View {
  const scale = clampScale(Math.min((w - 2 * pad) / Math.max(r.w, 1e-3), (h - 2 * pad) / Math.max(r.h, 1e-3)));
  return { cx: r.x + r.w / 2, cy: r.y + r.h / 2, scale };
}

/** The visible part of the unfolded room. */
export function viewBox(v: View, w: number, h: number): Rect {
  return { x: v.cx - w / 2 / v.scale, y: v.cy - h / 2 / v.scale, w: w / v.scale, h: h / v.scale };
}

/** Zoom by `factor`, keeping the unfolded-room point under the pixel (px, py) in place. */
export function zoomAt(v: View, factor: number, px: number, py: number, w: number, h: number): View {
  const scale = clampScale(v.scale * factor);
  const box = viewBox(v, w, h);
  const ux = box.x + px / v.scale;
  const uy = box.y + py / v.scale;
  return { scale, cx: ux - (px - w / 2) / scale, cy: uy - (py - h / 2) / scale };
}

/** A surface's rectangle in the unfolded room. */
export function surfaceRect(room: Room, s: Surface): Rect {
  const o = surfaceOrigin(room, s);
  const { w, h } = surfaceSize(room, s);
  return { x: o.x, y: o.y, w, h };
}

/** The front wall with a margin that shows a bit of the surfaces around it. */
export function frontFocus(room: Room): Rect {
  const f = surfaceRect(room, 'front');
  const m = Math.min(0.6, room.depth / 3);
  return { x: f.x - m, y: f.y - m, w: f.w + 2 * m, h: f.h + 2 * m };
}

export function wholeRoom(room: Room): Rect {
  const { w, h } = unfoldedSize(room);
  return { x: 0, y: 0, w, h };
}

/** The screen's rectangle in the unfolded room. */
export function screenRect(room: Room): Rect {
  const p = toUnfolded(room, { surface: 'front', x: room.screen.x, y: room.screen.y });
  return { x: p.x, y: p.y, w: room.screen.width, h: room.screen.height };
}

// ---- snapping ----

interface Segment2 {
  ax: number;
  ay: number;
  bx: number;
  by: number;
}

function nearestOnSegment(px: number, py: number, s: Segment2): { x: number; y: number; d: number } {
  const dx = s.bx - s.ax;
  const dy = s.by - s.ay;
  const len2 = dx * dx + dy * dy;
  const t = len2 > 0 ? Math.min(1, Math.max(0, ((px - s.ax) * dx + (py - s.ay) * dy) / len2)) : 0;
  const x = s.ax + t * dx;
  const y = s.ay + t * dy;
  return { x, y, d: Math.hypot(px - x, py - y) };
}

/** How much farther than lines a corner reaches: corners win near them, so they are easy to hit. */
export const CORNER_REACH = 1.5;

interface Candidates {
  lines: Segment2[];
  corners: Array<{ x: number; y: number }>;
}

/**
 * What a point snaps to on a surface (its coordinates): its edges and corners, the screen's border, corners and the
 * middle of its bottom edge, the furniture as drawn and the baseboard (furniture.ts), and every place two of these
 * lines meet.
 */
function snapCandidates(room: Room, surface: Surface): Candidates {
  const { w, h } = surfaceSize(room, surface);
  const lines: Segment2[] = [
    { ax: 0, ay: 0, bx: w, by: 0 },
    { ax: 0, ay: h, bx: w, by: h },
    { ax: 0, ay: 0, bx: 0, by: h },
    { ax: w, ay: 0, bx: w, by: h },
  ];
  const corners = [
    { x: 0, y: 0 },
    { x: w, y: 0 },
    { x: w, y: h },
    { x: 0, y: h },
  ];
  if (surface === 'front') {
    const s = room.screen;
    lines.push(
      { ax: s.x, ay: s.y, bx: s.x + s.width, by: s.y },
      { ax: s.x, ay: s.y + s.height, bx: s.x + s.width, by: s.y + s.height },
      { ax: s.x, ay: s.y, bx: s.x, by: s.y + s.height },
      { ax: s.x + s.width, ay: s.y, bx: s.x + s.width, by: s.y + s.height },
    );
    corners.push(
      { x: s.x, y: s.y },
      { x: s.x + s.width, y: s.y },
      { x: s.x + s.width, y: s.y + s.height },
      { x: s.x, y: s.y + s.height },
      { x: s.x + s.width / 2, y: s.y + s.height },
    );
  }
  const furniture = furnitureSnaps(room, surface);
  lines.push(...furniture.lines);
  corners.push(...furniture.corners);
  // Where lines cross or one ends on another (the baseboard meets a desk leg, the desk top a wall edge).
  for (let i = 0; i < lines.length; i++) {
    for (let j = i + 1; j < lines.length; j++) {
      const c = crossing(lines[i], lines[j]);
      if (c) corners.push(c);
    }
  }
  return { lines, corners };
}

/** Where two segments meet, or null (parallel or apart). */
function crossing(a: Segment2, b: Segment2): { x: number; y: number } | null {
  const rx = a.bx - a.ax;
  const ry = a.by - a.ay;
  const sx = b.bx - b.ax;
  const sy = b.by - b.ay;
  const den = rx * sy - ry * sx;
  if (Math.abs(den) < 1e-12) return null;
  const qx = b.ax - a.ax;
  const qy = b.ay - a.ay;
  const t = (qx * sy - qy * sx) / den;
  const v = (qx * ry - qy * rx) / den;
  const eps = 1e-9;
  if (t < -eps || t > 1 + eps || v < -eps || v > 1 + eps) return null;
  return { x: a.ax + t * rx, y: a.ay + t * ry };
}

export interface SnapResult {
  point: RoomPoint;
  /** What it snapped to, for a hint. */
  to: 'point' | 'corner' | 'line' | 'align' | null;
}

/**
 * The surface point under an unfolded-room position, snapped to the nearest of: other points (`points`, within
 * `tolerance` meters) and corners (surfaces, screen, furniture, where lines meet; within CORNER_REACH x `tolerance`);
 * else onto the nearest line (surface edges, the screen's border, the furniture, the baseboard), sliding along it to
 * line up with an `align` point; else to the x or y of `align` points on the same surface. Corners and lines of the
 * surfaces next to the one under the cursor count too, so snapping works the same right at a fold. Positions outside
 * every surface go to the nearest point of `fallback`'s surface.
 */
export function snapAt(
  room: Room,
  ux: number,
  uy: number,
  tolerance: number,
  opts: { points?: readonly RoomPoint[]; align?: readonly RoomPoint[]; fallback?: Surface; free?: boolean } = {},
): SnapResult {
  const under = fromUnfolded(room, ux, uy);
  let p = under;
  if (!p) {
    const surface = opts.fallback ?? nearestSurface(room, ux, uy);
    const r = surfaceRect(room, surface);
    p = { surface, x: Math.min(r.w, Math.max(0, ux - r.x)), y: Math.min(r.h, Math.max(0, uy - r.y)) };
  }
  if (opts.free) return { point: p, to: null };
  const here = p;
  const at = toUnfolded(room, here);

  // The nearest of the other points (on any surface, compared in the unfolded room) and the corners.
  let best: { point: RoomPoint; d: number; to: 'point' | 'corner' } | null = null;
  for (const q of opts.points ?? []) {
    const onThis = onSurface(room, q, here.surface, 1e-4);
    if (!onThis) continue;
    const d = Math.hypot(onThis.x - here.x, onThis.y - here.y);
    if (d <= tolerance && (!best || d < best.d)) best = { point: { surface: onThis.surface, x: onThis.x, y: onThis.y }, d, to: 'point' };
  }
  const surfaces = under ? [here.surface, ...SURFACES.filter((s) => touchingInUnfolded(here.surface, s))] : [here.surface];
  const candidates = surfaces.map((surface) => ({ surface, origin: surfaceOrigin(room, surface), ...snapCandidates(room, surface) }));
  for (const { surface, origin, corners } of candidates) {
    for (const c of corners) {
      const d = Math.hypot(origin.x + c.x - at.x, origin.y + c.y - at.y);
      if (d <= CORNER_REACH * tolerance && (!best || d < best.d - 1e-9)) best = { point: { surface, x: c.x, y: c.y }, d, to: 'corner' };
    }
  }
  if (best) return { point: best.point, to: best.to };

  let line: { surface: Surface; seg: Segment2; x: number; y: number; d: number } | null = null;
  for (const { surface, origin, lines } of candidates) {
    for (const seg of lines) {
      const n = nearestOnSegment(at.x - origin.x, at.y - origin.y, seg);
      if (n.d <= tolerance && (!line || n.d < line.d - 1e-9)) line = { surface, seg, ...n };
    }
  }
  if (line) {
    // Slide along the line to line up with a neighbor.
    let { x, y } = line;
    const { seg } = line;
    for (const q of opts.align ?? []) {
      const onThis = onSurface(room, q, line.surface, 1e-4);
      if (!onThis) continue;
      if (seg.ay === seg.by && Math.abs(onThis.x - x) <= tolerance && within(onThis.x, seg.ax, seg.bx)) x = onThis.x;
      if (seg.ax === seg.bx && Math.abs(onThis.y - y) <= tolerance && within(onThis.y, seg.ay, seg.by)) y = onThis.y;
    }
    return { point: { surface: line.surface, x, y }, to: 'line' };
  }

  let x = here.x;
  let y = here.y;
  let aligned = false;
  for (const q of opts.align ?? []) {
    const onThis = onSurface(room, q, here.surface, 1e-4);
    if (!onThis) continue;
    if (Math.abs(onThis.x - x) <= tolerance) {
      x = onThis.x;
      aligned = true;
    }
    if (Math.abs(onThis.y - y) <= tolerance) {
      y = onThis.y;
      aligned = true;
    }
  }
  return { point: { surface: here.surface, x, y }, to: aligned ? 'align' : null };
}

function within(v: number, a: number, b: number): boolean {
  return v >= Math.min(a, b) && v <= Math.max(a, b);
}

/** The surface whose rectangle is closest to an unfolded-room position. */
export function nearestSurface(room: Room, ux: number, uy: number): Surface {
  let best: Surface = 'front';
  let bestD = Infinity;
  for (const s of SURFACES) {
    const r = surfaceRect(room, s);
    const dx = Math.max(r.x - ux, 0, ux - (r.x + r.w));
    const dy = Math.max(r.y - uy, 0, uy - (r.y + r.h));
    const d = Math.hypot(dx, dy);
    if (d < bestD) {
      bestD = d;
      best = s;
    }
  }
  return best;
}

/**
 * Where the line from `a` to `b` crosses the edge between their surfaces, when they touch in the unfolded room and
 * neither point already lies on that edge (the strip bends there). Null otherwise.
 */
export function foldCrossing(room: Room, a: RoomPoint, b: RoomPoint): PathPoint | null {
  if (a.surface === b.surface || !touchingInUnfolded(a.surface, b.surface)) return null;
  if (onSurface(room, a, b.surface, 1e-4) || onSurface(room, b, a.surface, 1e-4)) return null;
  const pa = toUnfolded(room, a);
  const pb = toUnfolded(room, b);
  const r = surfaceRect(room, a.surface);
  // The shared edge is one side of a's rectangle: find where the line leaves it.
  const candidates: Array<{ t: number; x: number; y: number }> = [];
  const dx = pb.x - pa.x;
  const dy = pb.y - pa.y;
  for (const xEdge of [r.x, r.x + r.w]) {
    if (dx !== 0) {
      const t = (xEdge - pa.x) / dx;
      const y = pa.y + t * dy;
      if (t > 0 && t < 1 && y >= r.y - 1e-9 && y <= r.y + r.h + 1e-9) candidates.push({ t, x: xEdge, y });
    }
  }
  for (const yEdge of [r.y, r.y + r.h]) {
    if (dy !== 0) {
      const t = (yEdge - pa.y) / dy;
      const x = pa.x + t * dx;
      if (t > 0 && t < 1 && x >= r.x - 1e-9 && x <= r.x + r.w + 1e-9) candidates.push({ t, x, y: yEdge });
    }
  }
  if (!candidates.length) return null;
  candidates.sort((u, v) => u.t - v.t);
  const c = candidates[0];
  const x = Math.min(r.w, Math.max(0, c.x - r.x));
  const y = Math.min(r.h, Math.max(0, c.y - r.y));
  const point: RoomPoint = { surface: a.surface, x, y };
  // Only a real fold: the crossing must lie on b's surface too.
  return onSurface(room, point, b.surface, 1e-6) ? point : null;
}

/** `next` appended to a path being drawn, with a bend point added where it crosses onto another surface. */
export function appendPoint(room: Room, path: readonly PathPoint[], next: PathPoint): PathPoint[] {
  const last = path[path.length - 1];
  if (!last) return [next];
  const fold = foldCrossing(room, last, next);
  return fold ? [...path, fold, next] : [...path, next];
}

/** Rounds a point to the millimeter, so stored layouts stay readable. */
export function tidy(p: PathPoint): PathPoint {
  const r = (x: number) => Math.round(x * 1000) / 1000;
  return { ...p, x: r(p.x), y: r(p.y) };
}

/**
 * The screen's outline from `from` to `to` (outline positions) in the unfolded room, through the corners on the way;
 * `inset` meters inside the screen.
 */
export function outlinePath(room: Room, from: number, to: number, inset = 0): Array<{ x: number; y: number }> {
  const steps = [from];
  if (to > from) for (let k = Math.floor(from) + 1; k < to; k++) steps.push(k);
  else for (let k = Math.ceil(from) - 1; k > to; k--) steps.push(k);
  steps.push(to);
  const front = surfaceRect(room, 'front');
  const d = Math.min(inset, room.screen.width / 4, room.screen.height / 4);
  const inner = { ...room, screen: { x: room.screen.x + d, y: room.screen.y + d, width: room.screen.width - 2 * d, height: room.screen.height - 2 * d } };
  return steps.map((p) => {
    const q = outlinePoint(inner, p);
    return { x: front.x + q.x, y: front.y + q.y };
  });
}

/** An outline position snapped to the nearest corner or middle of an edge within `tolerance` (outline units). */
export function snapOutline(p: number, tolerance: number): number {
  const half = Math.round(p * 2) / 2;
  return Math.abs(half - p) <= tolerance ? half : Math.round(p * 1000) / 1000;
}
