// The main picks (Screen edges, Screen average, an effect, music, a color) and what picking one does to Everyday's
// strips (docs/ui-guidelines.md, "The Show"). A pick is stored in the strips of the setup in use (control/setup.ts);
// usePick (control/usePick.ts) runs it.

import type { LayoutSpec, PathPoint, Rgb, Section } from '../layout';
import type { WhatPlays } from './presets';

/** A main effect the user picks: it plays on every light (Screen edges and Screen average through the layout). */
export type MainPick = { kind: 'edges' } | { kind: 'average' } | { kind: 'effect'; effect: string } | { kind: 'color'; color: Rgb };

/** Screen average needs strips placed in Layout (without them the LEDs only have their imported positions). */
export function canShowAverage(spec: Pick<LayoutSpec, 'strips'>): boolean {
  return spec.strips.length > 0;
}

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/**
 * The section a part gets for a pick: Off parts and gaps are kept (hardware facts), a part on a screen edge keeps its
 * mapping for Screen edges, else the screen's average, the effect or the color.
 */
function sectionForPick(section: Section | undefined, pick: MainPick): Section | undefined {
  if (section?.kind === 'none' || section?.kind === 'gap') return section;
  switch (pick.kind) {
    case 'edges':
      return section?.kind === 'edge' ? section : undefined;
    case 'average':
      return section?.kind === 'whole' ? section : { kind: 'whole' };
    case 'effect': {
      const next: Section = { kind: 'effect', effect: pick.effect };
      return same(section, next) ? section : next;
    }
    case 'color': {
      const next: Section = { kind: 'color', color: [pick.color[0], pick.color[1], pick.color[2]] };
      return same(section, next) ? section : next;
    }
  }
}

/**
 * Everyday's strips with a pick: every part that isn't Off or a gap plays it (Screen edges: its screen edge, Screen
 * average: the screen's average, an effect or a color: that, all such parts forming one zone in the backend, so an
 * effect flows across all strips as one picture). The new paths of the strips that change (none: nothing changes).
 */
export function pickPaths(spec: Pick<LayoutSpec, 'strips'>, pick: MainPick): Map<string, PathPoint[]> {
  const out = new Map<string, PathPoint[]>();
  for (const strip of spec.strips) {
    let changed = false;
    const path = strip.path.map((p, i) => {
      if (i === strip.path.length - 1) return p;
      const next = sectionForPick(p.next, pick);
      if (next === p.next) return p;
      changed = true;
      const { next: _old, ...rest } = p;
      return next ? { ...rest, next } : rest;
    });
    if (changed) out.set(strip.id, path);
  }
  return out;
}

/** The layout with the strips' new paths. */
export function withPaths<T extends Pick<LayoutSpec, 'strips'>>(spec: T, paths: ReadonlyMap<string, PathPoint[]>): T {
  return paths.size ? { ...spec, strips: spec.strips.map((s) => (paths.has(s.id) ? { ...s, path: paths.get(s.id)! } : s)) } : spec;
}

/** Whether the setup plays `pick` (a color counts whatever its value: the Color section is one choice). */
export function isPicked(plays: WhatPlays, pick: MainPick): boolean {
  if (pick.kind === 'effect') return plays.kind === 'effect' && plays.effect === pick.effect;
  return plays.kind === pick.kind;
}
