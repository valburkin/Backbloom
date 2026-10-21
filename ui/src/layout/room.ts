import type { OutlinePosition, PathPoint, Room, RoomPoint, Section, Strip, Surface } from './types';

// Room geometry: surfaces, 3D positions, LEDs along a strip's path, and the stretch of the screen's outline each
// section repeats.
// compile.ts turns this into sampling rects; the backend's LayoutSpecification does the same math (keep them equal).

/** A position in the room, in meters: x from the left wall, y from the front wall, z up from the floor. */
export interface Vec3 {
  x: number;
  y: number;
  z: number;
}

export const SURFACES: readonly Surface[] = ['front', 'left', 'right', 'back', 'ceiling', 'floor'];

export const SURFACE_LABELS: Record<Surface, string> = {
  front: 'Front wall',
  left: 'Left wall',
  right: 'Right wall',
  back: 'Back wall',
  ceiling: 'Ceiling',
  floor: 'Floor',
};

/** A desk with a 27" monitor in a 4 x 4 m room. */
export function defaultRoom(furniture: Room['furniture'] = 'desk'): Room {
  if (furniture === 'tv') {
    // A 65" TV on a low stand.
    return { width: 4.5, depth: 5, height: 2.6, screen: { x: 1.525, y: 0.95, width: 1.45, height: 0.82 }, furniture };
  }
  return { width: 4, depth: 4, height: 2.6, screen: { x: 1.7, y: 1.31, width: 0.6, height: 0.34 }, furniture };
}

/** Width and height of a surface as drawn. */
export function surfaceSize(room: Room, surface: Surface): { w: number; h: number } {
  switch (surface) {
    case 'front':
    case 'back':
      return { w: room.width, h: room.height };
    case 'left':
    case 'right':
      return { w: room.depth, h: room.height };
    case 'ceiling':
    case 'floor':
      return { w: room.width, h: room.depth };
  }
}

/** The 3D position of a point on a surface. */
export function toWorld(room: Room, p: RoomPoint): Vec3 {
  const { width: W, depth: D, height: H } = room;
  switch (p.surface) {
    case 'front':
      return { x: p.x, y: 0, z: H - p.y };
    case 'left':
      return { x: 0, y: D - p.x, z: H - p.y };
    case 'right':
      return { x: W, y: p.x, z: H - p.y };
    case 'back':
      return { x: W - p.x, y: D, z: H - p.y };
    case 'ceiling':
      return { x: p.x, y: D - p.y, z: H };
    case 'floor':
      return { x: p.x, y: p.y, z: 0 };
  }
}

/** Surface coordinates of a room position (projected onto that surface's plane). */
export function fromWorld(room: Room, v: Vec3, surface: Surface): RoomPoint {
  const { width: W, depth: D, height: H } = room;
  switch (surface) {
    case 'front':
      return { surface, x: v.x, y: H - v.z };
    case 'left':
      return { surface, x: D - v.y, y: H - v.z };
    case 'right':
      return { surface, x: v.y, y: H - v.z };
    case 'back':
      return { surface, x: W - v.x, y: H - v.z };
    case 'ceiling':
      return { surface, x: v.x, y: D - v.y };
    case 'floor':
      return { surface, x: v.x, y: v.y };
  }
}

/** Distance of a room position from a surface's plane. */
function planeDistance(room: Room, v: Vec3, surface: Surface): number {
  switch (surface) {
    case 'front':
      return Math.abs(v.y);
    case 'back':
      return Math.abs(room.depth - v.y);
    case 'left':
      return Math.abs(v.x);
    case 'right':
      return Math.abs(room.width - v.x);
    case 'ceiling':
      return Math.abs(room.height - v.z);
    case 'floor':
      return Math.abs(v.z);
  }
}

const EDGE_EPS = 1e-6;

/** The point expressed on another surface when it lies on that surface too (on the edge they share), else null. */
export function onSurface(room: Room, p: RoomPoint, surface: Surface, eps = EDGE_EPS): RoomPoint | null {
  if (p.surface === surface) return p;
  const v = toWorld(room, p);
  return planeDistance(room, v, surface) <= eps ? fromWorld(room, v, surface) : null;
}

/** Keep a point inside its surface. */
export function clampToSurface(room: Room, p: RoomPoint): RoomPoint {
  const { w, h } = surfaceSize(room, p.surface);
  return { ...p, x: Math.min(w, Math.max(0, p.x)), y: Math.min(h, Math.max(0, p.y)) };
}

function distance(a: Vec3, b: Vec3): number {
  return Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
}

/** Total length of a strip's path in meters (straight lines between its points). */
export function pathLength(room: Room, path: readonly RoomPoint[]): number {
  let total = 0;
  for (let i = 1; i < path.length; i++) total += distance(toWorld(room, path[i - 1]), toWorld(room, path[i]));
  return total;
}

function ledCountOf(strip: Strip): number {
  return Number.isFinite(strip.ledCount) ? Math.max(0, Math.floor(strip.ledCount)) : 0;
}

/** True when the section from point `i` to the next is a gap (no LEDs). */
export function isGap(path: readonly PathPoint[], i: number): boolean {
  return path[i]?.next?.kind === 'gap';
}

/** Length of the parts of a path that have LEDs (gaps left out), in meters. */
export function litLength(room: Room, path: readonly PathPoint[]): number {
  let total = 0;
  for (let i = 1; i < path.length; i++) if (!isGap(path, i - 1)) total += distance(toWorld(room, path[i - 1]), toWorld(room, path[i]));
  return total;
}

/**
 * Position of every path point along the strip, in LEDs (see PathPoint.at): matched points keep theirs, the first
 * and last default to 0 and the LED count, and the rest are spaced by length between their known neighbors. Gaps
 * count no length, so both ends of a gap are at the same position.
 */
export function pathStations(room: Room, path: readonly PathPoint[], ledCount: number): number[] {
  const n = path.length;
  if (n === 0) return [];
  const world = path.map((p) => toWorld(room, p));
  const cumulative = [0];
  for (let i = 1; i < n; i++) cumulative.push(cumulative[i - 1] + (isGap(path, i - 1) ? 0 : distance(world[i - 1], world[i])));
  const known: Array<number | undefined> = path.map((p) => (p.at !== undefined && Number.isFinite(p.at) ? p.at : undefined));
  known[0] ??= 0;
  if (n > 1) known[n - 1] ??= ledCount;
  const out = known.slice() as number[];
  let prev = 0;
  for (let i = 1; i < n; i++) {
    if (known[i] === undefined) continue;
    const span = cumulative[i] - cumulative[prev];
    for (let j = prev + 1; j < i; j++) {
      const f = span > 0 ? (cumulative[j] - cumulative[prev]) / span : (j - prev) / (i - prev);
      out[j] = known[prev]! + f * (known[i]! - known[prev]!);
    }
    prev = i;
  }
  return out;
}

/** One LED of a strip placed in the room. */
export interface PlacedLed {
  /** Index on the strip (wiring order). */
  index: number;
  world: Vec3;
  /** Index of the path piece (between point `piece` and `piece + 1`) the LED is on. */
  piece: number;
  /** Position within that piece, 0..1. */
  t: number;
}

/**
 * Where each LED of a strip is: LED k sits at station k + 0.5 along the path, interpolated between the stations of
 * the points around it (never in a gap). LEDs before the first point or past the last stay at that point.
 */
export function placeLeds(room: Room, strip: Pick<Strip, 'path' | 'ledCount'>): PlacedLed[] {
  const count = ledCountOf(strip as Strip);
  const path = strip.path;
  if (path.length === 0 || count === 0) return [];
  const world = path.map((p) => toWorld(room, p));
  if (path.length === 1) return Array.from({ length: count }, (_, index) => ({ index, world: world[0], piece: 0, t: 0 }));
  const stations = pathStations(room, path, count);
  const out: PlacedLed[] = [];
  let piece = 0;
  for (let k = 0; k < count; k++) {
    const s = k + 0.5;
    // Move on past pieces that end before s, and past empty ones (gaps) that end at it.
    while (piece < path.length - 2 && (s > stations[piece + 1] || (s === stations[piece + 1] && stations[piece + 1] === stations[piece]))) piece++;
    const s0 = stations[piece];
    const s1 = stations[piece + 1];
    const t = s1 !== s0 ? Math.min(1, Math.max(0, (s - s0) / (s1 - s0))) : 0;
    const a = world[piece];
    const b = world[piece + 1];
    out.push({ index: k, piece, t, world: { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t } });
  }
  return out;
}

// ---- the screen's outline ----

export type ScreenEdge = 'top' | 'right' | 'bottom' | 'left';

/** Where a point is on the front wall when the room is looked at straight on (depth dropped). */
export function onFrontWall(room: Room, p: RoomPoint): { x: number; y: number } {
  const v = toWorld(room, p);
  return { x: v.x, y: room.height - v.z };
}

/** The outline position closest to a front-wall position (meters). */
export function nearestOutline(room: Room, x: number, y: number): OutlinePosition {
  const { x: sx, y: sy, width: w, height: h } = room.screen;
  let px = Math.min(sx + w, Math.max(sx, x));
  let py = Math.min(sy + h, Math.max(sy, y));
  if (px === x && py === y) {
    // Inside the screen: out to the closest edge.
    const d = [y - sy, sx + w - x, sy + h - y, x - sx];
    const i = d.indexOf(Math.min(...d));
    if (i === 0) py = sy;
    else if (i === 1) px = sx + w;
    else if (i === 2) py = sy + h;
    else px = sx;
  }
  // On the border now; a corner counts to the edge that starts there, going clockwise.
  if (py === sy && px < sx + w) return (px - sx) / w;
  if (px === sx + w && py < sy + h) return 1 + (py - sy) / h;
  if (py === sy + h && px > sx) return 2 + (sx + w - px) / w;
  return 3 + (sy + h - py) / h;
}

/** `to` moved by whole turns so it is as close to `from` as possible (a stretch never goes the long way round). */
export function shortWay(from: OutlinePosition, to: OutlinePosition): OutlinePosition {
  return to + 4 * Math.round((from - to) / 4);
}

/**
 * The stretch of the outline a section faces: its two ends seen straight on against the front wall, each at the
 * closest point of the screen's outline. So a strip around the screen repeats its own edges, a strip behind the desk
 * the bottom edge from the corner it is closest to, and one across the ceiling the whole top edge.
 */
export function autoSection(room: Room, a: RoomPoint, b: RoomPoint): { kind: 'edge'; from: OutlinePosition; to: OutlinePosition } {
  const pa = onFrontWall(room, a);
  const pb = onFrontWall(room, b);
  const from = nearestOutline(room, pa.x, pa.y);
  return { kind: 'edge', from, to: shortWay(from, nearestOutline(room, pb.x, pb.y)) };
}

/** What the section from point `i` to the next does, with 'auto' worked out. */
export function sectionAt(room: Room, path: readonly PathPoint[], i: number): Section {
  const own = path[i]?.next;
  if (own) return own;
  const a = path[i];
  const b = path[Math.min(path.length - 1, i + 1)];
  return autoSection(room, a, b);
}

/** The edge and the position along it (0..1, left to right or top to bottom) of an outline position. */
export function outlineEdge(p: OutlinePosition): { edge: ScreenEdge; pos: number } {
  const q = ((p % 4) + 4) % 4;
  if (q < 1) return { edge: 'top', pos: q };
  if (q < 2) return { edge: 'right', pos: q - 1 };
  if (q < 3) return { edge: 'bottom', pos: 1 - (q - 2) };
  return { edge: 'left', pos: 1 - (q - 3) };
}

/** A front-wall position (meters) of an outline position. */
export function outlinePoint(room: Room, p: OutlinePosition): { x: number; y: number } {
  const { x, y, width: w, height: h } = room.screen;
  const { edge, pos } = outlineEdge(p);
  switch (edge) {
    case 'top':
      return { x: x + pos * w, y };
    case 'right':
      return { x: x + w, y: y + pos * h };
    case 'bottom':
      return { x: x + pos * w, y: y + h };
    case 'left':
      return { x, y: y + pos * h };
  }
}

// ---- the unfolded room (drawing) ----

/** Top-left corner of a surface in the unfolded room: the walls in a row (left, front, right, back), the ceiling above the front wall, the floor below it. */
export function surfaceOrigin(room: Room, surface: Surface): { x: number; y: number } {
  const { width: W, depth: D, height: H } = room;
  switch (surface) {
    case 'ceiling':
      return { x: D, y: 0 };
    case 'left':
      return { x: 0, y: D };
    case 'front':
      return { x: D, y: D };
    case 'right':
      return { x: D + W, y: D };
    case 'back':
      return { x: 2 * D + W, y: D };
    case 'floor':
      return { x: D, y: D + H };
  }
}

/** Size of the whole unfolded room. */
export function unfoldedSize(room: Room): { w: number; h: number } {
  return { w: 2 * room.depth + 2 * room.width, h: 2 * room.depth + room.height };
}

/** A surface point's position in the unfolded room. */
export function toUnfolded(room: Room, p: RoomPoint): { x: number; y: number } {
  const o = surfaceOrigin(room, p.surface);
  return { x: o.x + p.x, y: o.y + p.y };
}

/** The surface under a position of the unfolded room, with the point on it, or null outside every surface. */
export function fromUnfolded(room: Room, x: number, y: number): RoomPoint | null {
  for (const surface of SURFACES) {
    const o = surfaceOrigin(room, surface);
    const { w, h } = surfaceSize(room, surface);
    if (x >= o.x && x <= o.x + w && y >= o.y && y <= o.y + h) return { surface, x: x - o.x, y: y - o.y };
  }
  return null;
}

/**
 * The two ends of a path piece as drawn in the unfolded room. Both on one surface (directly or through the edge they
 * share): a straight line on it. Otherwise, when the surfaces touch in the unfolded room: a straight line across the
 * fold. Else null (the piece jumps; draw it as a dashed connection between its ends).
 */
export function pieceInUnfolded(
  room: Room,
  a: RoomPoint,
  b: RoomPoint,
): { a: { x: number; y: number }; b: { x: number; y: number } } | null {
  if (a.surface === b.surface) return { a: toUnfolded(room, a), b: toUnfolded(room, b) };
  const bOnA = onSurface(room, b, a.surface, 1e-4);
  if (bOnA) return { a: toUnfolded(room, a), b: toUnfolded(room, bOnA) };
  const aOnB = onSurface(room, a, b.surface, 1e-4);
  if (aOnB) return { a: toUnfolded(room, aOnB), b: toUnfolded(room, b) };
  if (touchingInUnfolded(a.surface, b.surface)) return { a: toUnfolded(room, a), b: toUnfolded(room, b) };
  return null;
}

const UNFOLDED_NEIGHBORS: ReadonlyArray<readonly [Surface, Surface]> = [
  ['left', 'front'],
  ['front', 'right'],
  ['right', 'back'],
  ['ceiling', 'front'],
  ['front', 'floor'],
];

/** True when two surfaces share an edge in the unfolded room too (not just in the real room). */
export function touchingInUnfolded(a: Surface, b: Surface): boolean {
  return UNFOLDED_NEIGHBORS.some(([x, y]) => (x === a && y === b) || (x === b && y === a));
}

/** Where a placed LED is drawn in the unfolded room. */
export function ledInUnfolded(room: Room, path: readonly RoomPoint[], led: PlacedLed): { x: number; y: number } {
  const a = path[led.piece];
  const b = path[Math.min(path.length - 1, led.piece + 1)];
  const piece = pieceInUnfolded(room, a, b);
  if (piece) {
    return { x: piece.a.x + (piece.b.x - piece.a.x) * led.t, y: piece.a.y + (piece.b.y - piece.a.y) * led.t };
  }
  // A jump: show it on the nearer end's surface.
  return toUnfolded(room, clampToSurface(room, fromWorld(room, led.world, led.t < 0.5 ? a.surface : b.surface)));
}
