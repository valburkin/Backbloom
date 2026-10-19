import { DEFAULT_BLEND, type Area, type Edge, type EdgeSegment, type FixedSegment, type OffSegment, type Segment } from '../layout';

export { DEFAULT_BLEND };

export type SegmentKind = Segment['kind'];

export const DEFAULT_DEPTH = 0.1;

export const EDGE_LABELS: Record<Edge, string> = { top: 'Top', right: 'Right', bottom: 'Bottom', left: 'Left' };
export const KIND_LABELS: Record<SegmentKind, string> = { edge: 'Along an edge', fixed: 'Fixed area', off: 'Off' };

/** Area of the screen strip nearest to an edge segment (used when converting edge → fixed). */
function edgeArea(seg: EdgeSegment): Area {
  const lo = Math.max(0, Math.min(seg.from, seg.to));
  const hi = Math.min(1, Math.max(seg.from, seg.to));
  const d = Math.min(1, Math.max(0.01, seg.depth));
  switch (seg.edge) {
    case 'top':
      return { hmin: lo, hmax: hi, vmin: 0, vmax: d };
    case 'bottom':
      return { hmin: lo, hmax: hi, vmin: 1 - d, vmax: 1 };
    case 'left':
      return { hmin: 0, hmax: d, vmin: lo, vmax: hi };
    case 'right':
      return { hmin: 1 - d, hmax: 1, vmin: lo, vmax: hi };
  }
}

export function newEdgeSegment(count = 10, edge: Edge = 'bottom'): EdgeSegment {
  return { kind: 'edge', count, edge, from: 0, to: 1, depth: DEFAULT_DEPTH, blend: DEFAULT_BLEND };
}

export function newFixedSegment(count = 3): FixedSegment {
  return { kind: 'fixed', count, area: { hmin: 0.4, hmax: 0.6, vmin: 0.85, vmax: 1 } };
}

export function newOffSegment(count = 1): OffSegment {
  return { kind: 'off', count };
}

/** Convert a segment to another kind, keeping its LED count and as much placement as makes sense. */
export function convertSegment(seg: Segment, kind: SegmentKind): Segment {
  if (seg.kind === kind) return seg;
  switch (kind) {
    case 'off':
      return newOffSegment(seg.count);
    case 'fixed':
      return { kind: 'fixed', count: seg.count, area: seg.kind === 'edge' ? edgeArea(seg) : newFixedSegment().area };
    case 'edge':
      return newEdgeSegment(seg.count);
  }
}

/** Short description in plain language, e.g. "18 LEDs · Bottom, right → left". */
export function describeSegment(seg: Segment): string {
  const leds = `${seg.count} LED${seg.count === 1 ? '' : 's'}`;
  if (seg.kind === 'off') return `${leds} · kept dark`;
  if (seg.kind === 'fixed') return `${leds} · fixed area`;
  const horizontal = seg.edge === 'top' || seg.edge === 'bottom';
  const fwd = seg.to >= seg.from;
  const dir = horizontal ? (fwd ? 'left → right' : 'right → left') : fwd ? 'top → bottom' : 'bottom → top';
  return `${leds} · ${EDGE_LABELS[seg.edge]}, ${dir}`;
}
