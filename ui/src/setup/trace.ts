// "Trace your strip" (the first-run setup's What each light does, TraceStrip.tsx): the user puts the board where it is
// and draws the strip's route on a front view of the monitor and the desk (its start, each corner or turn, its end);
// the strip's LEDs are spread evenly along it, as Layout spreads them along a drawn strip (pathStations/placeLeds in
// layout/room.ts). The board lights the LEDs that land on the corners and ends (`wled-test` `points`); where one is
// off, the user moves it onto its corner, which matches that corner to its LED (PathPoint.at) and spreads the rest
// evenly between matched corners. Pure helpers, tested without a board.

import { furnitureTop, pathStations, placeLeds, type PathPoint, type Room } from '../layout';

/** A point of the drawn route, on the front wall (meters, as Room's front-wall coordinates). */
export interface TracePoint {
  x: number;
  y: number;
  /** Where it is in words ("the monitor’s top-left corner"). */
  where: string;
}

/** A spot on the front wall. */
export interface FrontSpot {
  x: number;
  y: number;
}

/** A part of the front wall in front-wall coordinates (meters). */
export interface FrontRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** At most this many corners and ends: `points` lights 64 LEDs at most. */
export const MAX_ROUTE_POINTS = 64;

/** What the drawing shows: the screen and the desk (or TV stand) under it, with some wall around them. */
export function traceCrop(room: Room): FrontRect {
  const s = room.screen;
  const top = furnitureTop(room);
  const x0 = Math.max(0, Math.min(s.x, top?.x ?? s.x) - 0.2);
  const x1 = Math.min(room.width, Math.max(s.x + s.width, top ? top.x + top.width : s.x + s.width) + 0.2);
  const y0 = Math.max(0, s.y - Math.max(0.2, s.height * 0.5));
  const y1 = Math.min(room.height, top ? top.y + 0.15 : s.y + s.height + 0.3);
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

/** Where the board starts on the drawing (the user drags it to where it is): below the screen's middle, on the desk. */
export function boardStart(room: Room): FrontSpot {
  const s = room.screen;
  const top = furnitureTop(room);
  return { x: s.x + s.width / 2, y: top ? top.y : Math.min(room.height, s.y + s.height + 0.1) };
}

interface Target {
  x: number;
  y: number;
  where: string;
}

interface Line {
  ax: number;
  ay: number;
  bx: number;
  by: number;
  where: string;
}

function names(room: Room): { screen: string; desk: string } {
  return room.furniture === 'tv' ? { screen: 'TV', desk: 'TV stand' } : { screen: 'monitor', desk: 'desk' };
}

/** What a click snaps to: the screen's corners and edges (and its bottom's middle), the desk's back edge and its ends. */
function targets(room: Room): { corners: Target[]; lines: Line[] } {
  const { x, y, width: w, height: h } = room.screen;
  const n = names(room);
  const corners: Target[] = [
    { x, y, where: `the ${n.screen}’s top-left corner` },
    { x: x + w, y, where: `the ${n.screen}’s top-right corner` },
    { x: x + w, y: y + h, where: `the ${n.screen}’s bottom-right corner` },
    { x, y: y + h, where: `the ${n.screen}’s bottom-left corner` },
    { x: x + w / 2, y: y + h, where: `the middle of the ${n.screen}’s bottom edge` },
  ];
  const lines: Line[] = [
    { ax: x, ay: y, bx: x + w, by: y, where: `the ${n.screen}’s top edge` },
    { ax: x + w, ay: y, bx: x + w, by: y + h, where: `the ${n.screen}’s right edge` },
    { ax: x, ay: y + h, bx: x + w, by: y + h, where: `the ${n.screen}’s bottom edge` },
    { ax: x, ay: y, bx: x, by: y + h, where: `the ${n.screen}’s left edge` },
  ];
  const top = furnitureTop(room);
  if (top) {
    corners.push({ x: top.x, y: top.y, where: `the ${n.desk}’s left end` }, { x: top.x + top.width, y: top.y, where: `the ${n.desk}’s right end` });
    lines.push({ ax: top.x, ay: top.y, bx: top.x + top.width, by: top.y, where: `the ${n.desk}’s back edge` });
  }
  return { corners, lines };
}

/** How much farther than edges a corner reaches, so corners are easy to hit (as on Layout). */
const CORNER_REACH = 1.5;

/**
 * Where a click on the front wall lands: on a corner of the screen or the desk (or the board, `board`, for the
 * route's start) within 1.5 × `tolerance` (meters), else on the nearest screen edge or the desk's back edge within
 * `tolerance`, else where it was clicked (on the wall).
 */
export function snapFront(room: Room, x: number, y: number, tolerance: number, board?: FrontSpot): TracePoint {
  const { corners, lines } = targets(room);
  if (board) corners.unshift({ ...board, where: 'the board' });
  let best: { t: Target; d: number } | null = null;
  for (const c of corners) {
    const d = Math.hypot(c.x - x, c.y - y);
    if (d <= CORNER_REACH * tolerance && (!best || d < best.d)) best = { t: c, d };
  }
  if (best) return { ...best.t };
  let line: { t: Target; d: number } | null = null;
  for (const l of lines) {
    const dx = l.bx - l.ax;
    const dy = l.by - l.ay;
    const len2 = dx * dx + dy * dy;
    const k = len2 > 0 ? Math.min(1, Math.max(0, ((x - l.ax) * dx + (y - l.ay) * dy) / len2)) : 0;
    const px = l.ax + k * dx;
    const py = l.ay + k * dy;
    const d = Math.hypot(px - x, py - y);
    if (d <= tolerance && (!line || d < line.d)) line = { t: { x: px, y: py, where: l.where }, d };
  }
  if (line) return { ...line.t };
  return { x, y, where: 'the wall' };
}

/** The route with LED 1 at the end nearest the board (the board's wire goes into the strip's start). */
export function fromBoard(points: readonly TracePoint[], board: FrontSpot): TracePoint[] {
  if (points.length < 2) return [...points];
  const d = (p: FrontSpot) => Math.hypot(p.x - board.x, p.y - board.y);
  return d(points[points.length - 1]) < d(points[0]) ? [...points].reverse() : [...points];
}

/**
 * The strip's path from the route: on the front wall, every part following the screen next to it (no section:
 * 'auto'). Its LEDs are spread evenly along it; a corner in `pinned` (point index → LED, from 1) is matched to that LED
 * (`at`: its middle, as `at` counts: LED k spans [k, k + 1)) and the LEDs between matched corners are spread evenly.
 */
export function routePath(points: readonly TracePoint[], pinned: Readonly<Record<number, number>> = {}): PathPoint[] {
  return points.map((p, i) => (pinned[i] !== undefined ? { surface: 'front', x: p.x, y: p.y, at: pinned[i] - 0.5 } : { surface: 'front', x: p.x, y: p.y }));
}

/** The LED (from 1) that lands on each point of the path, as Layout places a strip's LEDs. */
export function pointLeds(room: Room, path: readonly PathPoint[], leds: number): number[] {
  return pathStations(room, path, leds).map((s) => Math.min(leds, Math.max(1, Math.floor(s + 1e-9) + 1)));
}

/** The LEDs to light bright for the check: those on the corners and ends, each once, at most 64. */
export function checkLeds(ledsAt: readonly number[]): number[] {
  return [...new Set(ledsAt)].slice(0, MAX_ROUTE_POINTS);
}

/** How far a corner's LED can move: past the LED of the point before it and before the one after it. */
export function pinRange(ledsAt: readonly number[], i: number, leds: number): { min: number; max: number } {
  return { min: i > 0 ? ledsAt[i - 1] + 1 : 1, max: i < ledsAt.length - 1 ? ledsAt[i + 1] - 1 : leds };
}

/** Where each LED of the strip lands on the front wall, for the drawing. */
export function ledSpots(room: Room, path: readonly PathPoint[], leds: number): FrontSpot[] {
  if (path.length < 2) return [];
  // placeLeds gives world positions: on the front wall x is the same, y is measured down from the ceiling
  return placeLeds(room, { path: [...path], ledCount: leds }).map((l) => ({ x: l.world.x, y: room.height - l.world.z }));
}
