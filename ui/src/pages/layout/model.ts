import type { LayoutSpec, Strip } from '../../layout';

export interface LedLocation {
  strip: Strip;
  stripIndex: number;
  /** LED index within the strip. */
  local: number;
}

/** Map a controller-wide LED index (what "blink" takes) to the strip LED it belongs to. */
export function locateLed(spec: LayoutSpec, controllerId: string, index: number): LedLocation | null {
  const ctrl = spec.controllers.find((c) => c.id === controllerId);
  if (!ctrl) return null;
  const out = ctrl.outputs.find((o) => index >= o.start && index < o.start + o.length);
  if (!out) return null;
  const stripIndex = spec.strips.findIndex((s) => s.controllerId === controllerId && s.output === out.index);
  if (stripIndex < 0) return null;
  return { strip: spec.strips[stripIndex], stripIndex, local: index - out.start };
}

/** Controller-wide index of a strip's LED, or null when the strip isn't wired to a known output. */
export function controllerIndex(spec: LayoutSpec, strip: Strip, local: number): number | null {
  const out = spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output);
  return out ? out.start + local : null;
}
