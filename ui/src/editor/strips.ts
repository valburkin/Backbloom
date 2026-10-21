import { clampToSurface, darkMask, litLength, pathLength, pathStations, stripLedCount, surfaceSize, type LedRange, type PathPoint, type Room, type Section, type Strip } from '../layout';

/** Common LED densities of strips, per meter. */
export const DENSITIES = [30, 60, 96, 144] as const;

/** A section run the other way (an outline stretch swaps its ends). */
function reverseSection(section: Section | undefined): Section | undefined {
  return section?.kind === 'edge' ? { kind: 'edge', from: section.to, to: section.from } : section;
}

/** The strip run the other way: its path reversed with its sections, matched points and dark parts mirrored. */
export function reverseStrip(strip: Strip): Pick<Strip, 'path' | 'dark'> {
  const n = stripLedCount(strip);
  const last = strip.path.length - 1;
  const path = strip.path
    .map((_, i) => {
      // Point i of the reversed path is point last - i; its section is the one that ended there.
      const { next: _own, ...p } = strip.path[last - i];
      const next = i < last ? reverseSection(strip.path[last - i - 1].next) : undefined;
      return { ...p, ...(p.at === undefined ? {} : { at: n - p.at }), ...(next ? { next } : {}) };
    });
  const dark = strip.dark?.map((r) => ({ start: n - r.start - r.count, count: r.count })).sort((a, b) => a.start - b.start);
  return { path, dark };
}

/** What a strip's sections do (a profile's list for it), for the strip run the other way (see reverseStrip). */
export function reverseSections(sections: ReadonlyArray<Section | null>): Array<Section | null> {
  return sections.map((_, i) => reverseSection(sections[sections.length - 1 - i] ?? undefined) ?? null);
}

/** Dark parts as ranges from a per-LED mask. */
export function rangesFromMask(mask: readonly boolean[]): LedRange[] {
  const out: LedRange[] = [];
  for (let i = 0; i < mask.length; i++) {
    if (!mask[i]) continue;
    const start = i;
    while (i + 1 < mask.length && mask[i + 1]) i++;
    out.push({ start, count: i - start + 1 });
  }
  return out;
}

/** Toggle one LED between lit and dark. */
export function toggleDark(strip: Strip, index: number): LedRange[] | undefined {
  const mask = darkMask(strip);
  if (index < 0 || index >= mask.length) return strip.dark;
  mask[index] = !mask[index];
  const ranges = rangesFromMask(mask);
  return ranges.length ? ranges : undefined;
}

/** Drop matched positions that no longer fit a strip of `ledCount` LEDs. */
export function fitMatchedPoints(path: readonly PathPoint[], ledCount: number): PathPoint[] {
  return path.map((p) => {
    if (p.at === undefined || p.at <= ledCount) return p;
    const { at: _drop, ...rest } = p;
    return rest;
  });
}

/** LEDs for the drawn length (gaps left out) at a density, at least 1. */
export function ledsForLength(room: Room, path: readonly PathPoint[], perMeter: number): number {
  return Math.max(1, Math.round(litLength(room, path) * perMeter));
}

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** How far around the screen a point counts as being on it (meters): strips on the back of a monitor or TV. */
const SCREEN_MARGIN = 0.08;

/** Moves the front-wall points on or close to the old screen along with it, scaled to its new size. */
export function carryWithScreen(path: readonly PathPoint[], from: Box, to: Box): PathPoint[] {
  const inside = (p: PathPoint) =>
    p.surface === 'front' &&
    p.x >= from.x - SCREEN_MARGIN &&
    p.x <= from.x + from.width + SCREEN_MARGIN &&
    p.y >= from.y - SCREEN_MARGIN &&
    p.y <= from.y + from.height + SCREEN_MARGIN;
  if (!path.some(inside)) return path as PathPoint[];
  const sx = from.width > 0 ? to.width / from.width : 1;
  const sy = from.height > 0 ? to.height / from.height : 1;
  return path.map((p) => {
    if (!inside(p)) return p;
    // Inside the screen: scale with it; in the margin around it: keep the distance to the edge.
    const rx = p.x < from.x ? to.x - (from.x - p.x) : p.x > from.x + from.width ? to.x + to.width + (p.x - from.x - from.width) : to.x + (p.x - from.x) * sx;
    const ry = p.y < from.y ? to.y - (from.y - p.y) : p.y > from.y + from.height ? to.y + to.height + (p.y - from.y - from.height) : to.y + (p.y - from.y) * sy;
    return { ...p, x: rx, y: ry };
  });
}

/** Keeps the screen on the front wall after a change. */
export function fitRoom(room: Room): Room {
  const screen = { ...room.screen };
  screen.width = Math.min(screen.width, room.width);
  screen.height = Math.min(screen.height, room.height);
  screen.x = Math.min(Math.max(0, screen.x), room.width - screen.width);
  screen.y = Math.min(Math.max(0, screen.y), room.height - screen.height);
  return { ...room, screen };
}

/** Pins dropped: every point spaced by length again. */
export function unpinned(path: readonly PathPoint[]): PathPoint[] {
  return path.map(({ at: _at, ...p }) => p);
}

/** The path with every point inside its surface (the same array when nothing had to move). */
export function clampPath(room: Room, path: readonly PathPoint[]): PathPoint[] {
  const outside = (p: PathPoint) => {
    const { w, h } = surfaceSize(room, p.surface);
    return p.x < 0 || p.y < 0 || p.x > w || p.y > h;
  };
  if (!path.some(outside)) return path as PathPoint[];
  return path.map((p) => (outside(p) ? { ...p, ...clampToSurface(room, p) } : p));
}

/**
 * How uneven pins make a strip: the densest part's LEDs per meter over the sparsest's (parts of at least 5 cm with
 * LEDs, gaps left out); 1 when even or without pins.
 */
export function pinUnevenness(room: Room, strip: Strip): number {
  if (!strip.path.some((p) => p.at !== undefined)) return 1;
  const stations = pathStations(room, strip.path, stripLedCount(strip));
  const densities: number[] = [];
  for (let i = 0; i < strip.path.length - 1; i++) {
    if (strip.path[i].next?.kind === 'gap') continue;
    const length = pathLength(room, [strip.path[i], strip.path[i + 1]]);
    if (length >= 0.05) densities.push((stations[i + 1] - stations[i]) / length);
  }
  if (densities.length < 2) return 1;
  const min = Math.min(...densities);
  return min > 0 ? Math.max(...densities) / min : Infinity;
}

/** The path with point `index` no longer pinned to an LED. */
export function unpinPoint(path: readonly PathPoint[], index: number): PathPoint[] {
  return path.map((p, i) => {
    if (i !== index) return p;
    const { at: _at, ...rest } = p;
    return rest;
  });
}
