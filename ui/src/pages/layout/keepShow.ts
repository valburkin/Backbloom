import type { PathPoint, Section, Strip } from '../../layout';

// The parts a change gave other roles, and undoing exactly those (the Undo of a pick that replaced customized strips).

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
