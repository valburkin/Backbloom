import { pathLength, type PathPoint, type Room, type Section } from '../layout';

// Sections of a strip (from one bend point to the next): keeping them right when bend points are added or removed.

function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

/**
 * The path with a bend point inserted on section `i` (between points i and i + 1). The new point splits the section:
 * both halves keep what it does; an outline stretch is split where the point is along it.
 */
export function insertPoint(room: Room, path: readonly PathPoint[], i: number, point: PathPoint): PathPoint[] {
  const a = path[i];
  const b = path[i + 1];
  const own = a.next;
  const { next: _drop, at: _at, ...plain } = point;
  if (own?.kind !== 'edge') return [...path.slice(0, i + 1), own ? { ...plain, next: own } : plain, ...path.slice(i + 1)];
  const whole = pathLength(room, [a, b]);
  const t = whole > 0 ? Math.min(1, Math.max(0, pathLength(room, [a, point]) / (pathLength(room, [a, point]) + pathLength(room, [point, b])))) : 0.5;
  const mid = lerp(own.from, own.to, t);
  return [
    ...path.slice(0, i),
    { ...a, next: { kind: 'edge', from: own.from, to: mid } },
    { ...plain, next: { kind: 'edge', from: mid, to: own.to } },
    ...path.slice(i + 1),
  ];
}

/** The path without point `i`: the sections on both sides become one (an outline stretch runs from the first's start to the second's end). */
export function removePoint(path: readonly PathPoint[], i: number): PathPoint[] {
  if (path.length <= 2 || i < 0 || i >= path.length) return path as PathPoint[];
  if (i === 0) return path.slice(1);
  if (i === path.length - 1) {
    const out = path.slice(0, -1);
    const { next: _drop, ...last } = out[out.length - 1];
    return [...out.slice(0, -1), last];
  }
  const before = path[i - 1].next;
  const after = path[i].next;
  const merged: Section | undefined =
    before?.kind === 'edge' && after?.kind === 'edge' ? { kind: 'edge', from: before.from, to: after.to } : before;
  const prev = { ...path[i - 1] };
  if (merged) prev.next = merged;
  else delete prev.next;
  return [...path.slice(0, i - 1), prev, ...path.slice(i + 1)];
}

/** The path with section `i` doing `section` ('auto' when undefined). */
export function setSection(path: readonly PathPoint[], i: number, section: Section | undefined): PathPoint[] {
  return path.map((p, k) => {
    if (k !== i) return p;
    const { next: _drop, ...rest } = p;
    return section ? { ...rest, next: section } : rest;
  });
}
