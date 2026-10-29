import { pathStations, sectionAt, shortWay, stripLedCount, type OutlinePosition, type PathPoint, type Room, type Section, type Strip } from '../layout';
import { setSection } from './sections';
import { effectLabel, isMusicEffect } from '../control/modes';

// How a strip maps onto the screen, made visible and editable: rainbow colors along the strip, the dots on the screen's
// outline where its parts start and end, and one simple choice for the whole strip.

/** Color of LED k of n: a rainbow from the first LED (red) to the last (violet), the same on the strip and the screen. */
export function ledColor(k: number, n: number): string {
  return `hsl(${Math.round((n > 1 ? k / (n - 1) : 0) * 280)} 95% 60%)`;
}

/** Color at a position along the strip, in LEDs (see PathPoint.at). */
export function stationColor(station: number, n: number): string {
  return ledColor(Math.min(n - 1, Math.max(0, station - 0.5)), n);
}

export interface MappingEnd {
  /** Section index (from point `index` to the next). */
  index: number;
  end: 'from' | 'to';
}

/** A dot on the screen's outline: where parts of the strip start or end (several when they meet there). */
export interface Joint {
  pos: OutlinePosition;
  /** Position along the strip, in LEDs, of the first end (for its color). */
  station: number;
  ends: MappingEnd[];
}

function samePos(a: OutlinePosition, b: OutlinePosition): boolean {
  const d = Math.abs(a - b) % 4;
  return Math.min(d, 4 - d) < 1e-3;
}

/**
 * The dots of a strip's mapping: the ends of every section that repeats a stretch of the outline, in strip order;
 * ends at the same place next to each other (where one part continues the next) share a dot. Sections that don't
 * repeat the outline (whole, area, off, gap) break the chain.
 */
export function mappingJoints(room: Room, strip: Strip): Joint[] {
  const stations = pathStations(room, strip.path, stripLedCount(strip));
  const joints: Joint[] = [];
  let open: Joint | null = null;
  for (let i = 0; i < strip.path.length - 1; i++) {
    const s = sectionAt(room, strip.path, i);
    if (s.kind !== 'edge') {
      open = null;
      continue;
    }
    for (const [end, pos, station] of [
      ['from', s.from, stations[i]],
      ['to', s.to, stations[i + 1]],
    ] as const) {
      if (open && samePos(open.pos, pos)) open.ends.push({ index: i, end });
      else {
        open = { pos, station, ends: [{ index: i, end }] };
        joints.push(open);
      }
    }
  }
  return joints;
}

/** The path with every end of a joint moved to `pos` on the outline (sections become explicit stretches). */
export function moveJoint(room: Room, path: readonly PathPoint[], ends: readonly MappingEnd[], pos: OutlinePosition): PathPoint[] {
  let out = path as PathPoint[];
  for (const { index, end } of ends) {
    const s = sectionAt(room, out, index);
    if (s.kind !== 'edge') continue;
    const moved = { ...s, [end]: shortWay(s[end], pos) };
    moved.to = shortWay(moved.from, moved.to);
    out = setSection(out, index, moved);
  }
  return out;
}

/** What a whole strip shows, in the simple terms the panel offers. */
export type StripShows = 'screen' | 'whole' | 'effect' | 'color' | 'none' | 'custom';

/** A section's role in the panel's terms ('screen' for one that repeats the outline). */
function roleOf(section: Section): Exclude<StripShows, 'custom'> | 'gap' | 'area' {
  return section.kind === 'edge' ? 'screen' : section.kind;
}

/**
 * 'screen' when every part with LEDs repeats the outline; 'whole', 'none', 'effect' (one effect) or 'color' (one
 * color) when all of them do that; else 'custom'. With it, the effect or color they share.
 */
export function stripShows(room: Room, strip: Strip): { shows: StripShows; section?: Section } {
  let shows: StripShows | null = null;
  let shared: Section | undefined;
  for (let i = 0; i < strip.path.length - 1; i++) {
    const section = sectionAt(room, strip.path, i);
    const role = roleOf(section);
    if (role === 'gap') continue;
    if (role === 'area') return { shows: 'custom' };
    if (shows === null) {
      shows = role;
      shared = section;
    } else if (shows !== role || ((role === 'effect' || role === 'color') && JSON.stringify(section) !== JSON.stringify(shared))) {
      return { shows: 'custom' };
    }
  }
  if (shows === null) return { shows: 'screen' };
  return shows === 'effect' || shows === 'color' ? { shows, section: shared } : { shows };
}

/** True when some part repeats a stretch the user chose instead of the one it faces. */
export function hasAdjustedMapping(strip: Strip): boolean {
  return strip.path.slice(0, -1).some((p) => p.next?.kind === 'edge');
}

/** Every part with LEDs set to `section` (undefined = back to the stretch each part faces); gaps stay gaps. */
export function setStripShows(path: readonly PathPoint[], section: Section | undefined): PathPoint[] {
  let out = path as PathPoint[];
  for (let i = 0; i < path.length - 1; i++) if (path[i].next?.kind !== 'gap') out = setSection(out, i, section);
  return out;
}

/** A short name for what a part does, for labels on the canvas and the strip list (null for the screen edge). */
export function roleLabel(section: Section): string | null {
  switch (section.kind) {
    case 'whole':
      return 'Screen average';
    case 'area':
      return 'One area';
    case 'none':
      return 'Off';
    case 'gap':
      return 'Gap';
    case 'effect':
      return `${isMusicEffect(section.effect) ? '♪ ' : ''}${effectLabel(section.effect)}`;
    case 'color':
      return 'Color';
    default:
      return null;
  }
}
