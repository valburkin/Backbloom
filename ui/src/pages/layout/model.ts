import { compileStrip, type LayoutSpec, type LedRect, type Segment, type Strip } from '../../layout';

/** Cumulative [start, end) LED ranges of each segment within its strip. */
export function segmentRanges(segments: Segment[]): Array<[number, number]> {
  const out: Array<[number, number]> = [];
  let n = 0;
  for (const s of segments) {
    out.push([n, n + s.count]);
    n += s.count;
  }
  return out;
}

export interface LedLocation {
  strip: Strip;
  stripIndex: number;
  /** LED index within the strip. */
  local: number;
  segmentIndex: number | null;
}

/** Map a controller-wide LED index (what "blink" takes) to the strip LED it belongs to. */
export function locateLed(spec: LayoutSpec, controllerId: string, index: number): LedLocation | null {
  const ctrl = spec.controllers.find((c) => c.id === controllerId);
  if (!ctrl) return null;
  const out = ctrl.outputs.find((o) => index >= o.start && index < o.start + o.length);
  if (!out) return null;
  const stripIndex = spec.strips.findIndex((s) => s.controllerId === controllerId && s.output === out.index);
  if (stripIndex < 0) return null;
  const strip = spec.strips[stripIndex];
  const local = index - out.start;
  const seg = segmentRanges(strip.segments).findIndex(([a, b]) => local >= a && local < b);
  return { strip, stripIndex, local, segmentIndex: seg >= 0 ? seg : null };
}

/** Controller-wide index of a strip's LED, or null when the strip isn't wired to a known output. */
export function controllerIndex(spec: LayoutSpec, strip: Strip, local: number): number | null {
  const out = spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output);
  return out ? out.start + local : null;
}

/** compileStrip that reports failure instead of throwing (the compiler may be unavailable). */
export function safeCompileStrip(strip: Strip): { rects: LedRect[] | null; error: string | null } {
  try {
    return { rects: compileStrip(strip), error: null };
  } catch (e) {
    return { rects: null, error: e instanceof Error ? e.message : String(e) };
  }
}
