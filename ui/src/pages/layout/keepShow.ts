import type { PathPoint, Section, Strip, ZoneRole } from '../../layout';
import type { PriorityInfo } from '../../api/types';
import { followsShow } from '../../editor/mapping';

/**
 * What plays on every light, as a part's own role: the effect or music of an EFFECT source, the color of a COLOR one;
 * null for the screen, nothing, or a source whose effect or color isn't known.
 */
export function showRole(source: PriorityInfo | null): ZoneRole | null {
  if (source?.componentId === 'EFFECT' && source.owner) return { kind: 'effect', effect: source.owner };
  const rgb = source?.componentId === 'COLOR' ? source.value?.RGB : undefined;
  return rgb ? { kind: 'color', color: [rgb[0], rgb[1], rgb[2]] } : null;
}

/**
 * The path with every part that follows the Show (not a gap, not one of `except`) playing `role` as its own, so it
 * keeps playing what shows when the layout comes back; `parts` are those parts.
 */
export function keepPlaying(path: readonly PathPoint[], role: ZoneRole, except: (part: number) => boolean = () => false): { path: PathPoint[]; parts: number[] } {
  const parts: number[] = [];
  const next = path.map((p, i) => {
    if (i === path.length - 1 || p.next?.kind === 'gap' || except(i) || !followsShow(p.next)) return p;
    parts.push(i);
    return { ...p, next: role };
  });
  return { path: next, parts };
}

/** One part a change gave another role: what it was and what it became. */
export interface PartChange {
  stripId: string;
  part: number;
  before: Section | undefined;
  after: Section | undefined;
}

const same = (a: Section | undefined, b: Section | undefined) => JSON.stringify(a) === JSON.stringify(b);

/** The parts whose role differs between `strips` and the new paths in `next` (by strip id). */
export function partChanges(strips: readonly Strip[], next: ReadonlyMap<string, readonly PathPoint[]>): PartChange[] {
  const out: PartChange[] = [];
  for (const [stripId, path] of next) {
    const old = strips.find((s) => s.id === stripId);
    path.forEach((p, part) => {
      const before = old?.path[part]?.next;
      if (!same(before, p.next)) out.push({ stripId, part, before, after: p.next });
    });
  }
  return out;
}

/** The paths that put `changes` back in `spec`, for the parts that still have the role the change gave them. */
export function undoChanges(spec: { strips: readonly Strip[] }, changes: readonly PartChange[]): Map<string, PathPoint[]> {
  const out = new Map<string, PathPoint[]>();
  for (const c of changes) {
    const strip = spec.strips.find((s) => s.id === c.stripId);
    const path = out.get(c.stripId) ?? strip?.path.map((p) => ({ ...p }));
    const point = path?.[c.part];
    if (!path || !point || !same(point.next, c.after)) continue;
    if (c.before) point.next = c.before;
    else delete point.next;
    out.set(c.stripId, path);
  }
  return out;
}
