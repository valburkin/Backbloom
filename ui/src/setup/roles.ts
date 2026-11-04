import type { LayoutSpec, PathPoint, Section, Strip } from '../layout';
import { sectionFor } from '../pages/layout/RoleChooser';
import { STRIP_PLACES } from './wled';

// What a new strip or light shows (the layout's roles, ROLE_CHOICES in pages/layout/RoleChooser.tsx). Every strip and
// light starts with the whole screen's average color (Screen average): it looks right whatever way the strip runs,
// and light at the edge of vision that changes sharply distracts (docs/immersion.md). The way a strip runs can't be
// guessed (from which end, which way round, across which places), so Screen edge is only set once the user traced it
// (the first-run setup's "Trace your strip", TraceStrip.tsx) or drew it on Layout. Set when the strip is added, from
// Set up a WLED board, Add lights or Another kind of device alike; the setup's "What each light does" and Layout
// change it.

/** The role a strip gets when it is added: the screen next to it, or the screen's average (the default). */
export type PlaceRole = 'screen' | 'soft';

/** The path with every part (not the gaps) showing `role`: the screen next to it, or the screen's average. */
export function withRole(path: readonly PathPoint[], role: PlaceRole): PathPoint[] {
  return path.map((p, i) => {
    if (i === path.length - 1 || p.next?.kind === 'gap') return { ...p };
    const { next: _old, ...rest } = p;
    return role === 'screen' ? rest : { ...rest, next: { kind: 'whole' } };
  });
}

/** The three choices of the setup's "What each light does"; the others (Off, a gap, parts that differ) are on Layout. */
export type SimpleRole = 'screen' | 'whole' | 'own';

export const SIMPLE_ROLES: ReadonlyArray<{ id: SimpleRole; label: string; description: string }> = [
  { id: 'whole', label: 'Screen average', description: 'A soft color from the whole screen, calm at the edge of your view.' },
  { id: 'screen', label: 'Screen edge', description: 'Follows the part of the screen next to it.' },
  { id: 'own', label: 'Its own effect or color', description: 'Its own effect or color: pick it on Layout.' },
];

/** A section's simple role, or null for one the setup doesn't offer (Off). */
export function simpleRoleOf(section: Section | undefined): SimpleRole | null {
  if (!section || section.kind === 'edge') return 'screen';
  if (section.kind === 'whole' || section.kind === 'area') return 'whole';
  if (section.kind === 'effect' || section.kind === 'color') return 'own';
  return null;
}

/** One row of "What each light does": a strip or a room light, as one light (its parts together). */
export interface RoleRow {
  key: string;
  stripId: string;
  /** The strip's or the light's name. */
  label: string;
  /** Where it runs ("Along the desk and behind the monitor"), else the device it is on. */
  description: string;
  /** The path points whose part (to the next point) the row sets: every part but the gaps. */
  segments: number[];
  /** What it shows now; null when its parts differ or show something the setup doesn't offer. */
  role: SimpleRole | null;
  /** A strip on a WLED board, which can be traced (the board lights one LED after the other): its board and output. */
  trace: { host: string; output: number; leds: number } | null;
}

/** The parts of a strip that have LEDs (not the gaps between its places). */
export function litSegments(strip: Strip): number[] {
  const out: number[] = [];
  for (let i = 0; i < strip.path.length - 1; i++) if (strip.path[i].next?.kind !== 'gap') out.push(i);
  return out;
}

function roleOfSegments(strip: Strip, segments: readonly number[]): SimpleRole | null {
  const roles = new Set(segments.map((i) => simpleRoleOf(strip.path[i].next)));
  return roles.size === 1 ? [...roles][0] : null;
}

const capitalized = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/**
 * Where a strip runs, from the name Set up a WLED board gave it by its places ("Desk and monitor" → "Along the desk and
 * behind the monitor"); null for other names.
 */
export function placesOfName(name: string): string | null {
  const parts = name
    .replace(/ \d+$/, '')
    .split(/\s+and\s+/i)
    .map((p) => STRIP_PLACES.find((x) => x.value !== 'other' && x.name.toLowerCase() === p.trim().toLowerCase()));
  if (!parts.length || parts.some((p) => !p)) return null;
  return capitalized(parts.map((p) => p!.words).join(' and '));
}

/** The rows: one per strip or room light (a strip along several places is one light). */
export function roleRows(spec: LayoutSpec): RoleRow[] {
  return spec.strips.map((strip): RoleRow => {
    const c = spec.controllers.find((x) => x.id === strip.controllerId);
    const output = c?.outputs.find((o) => o.index === strip.output);
    const leds = Math.min(strip.ledCount, output?.length ?? strip.ledCount);
    const host = c?.kind === 'wled' ? c.host || String(c.device?.host ?? '') : '';
    const segments = litSegments(strip);
    return {
      key: strip.id,
      stripId: strip.id,
      label: strip.name,
      description: placesOfName(strip.name) ?? (c ? (strip.ledCount > 1 ? `${strip.ledCount} LEDs on ${c.name}` : c.name) : ''),
      segments,
      role: roleOfSegments(strip, segments),
      trace: host && leds > 1 ? { host, output: strip.output, leds } : null,
    };
  });
}

/**
 * The strip's path with the row's parts set to `role` (Effect or color: the layout's default effect, keeping an effect
 * or color the part has).
 */
export function pathWithRole(strip: Strip, segments: readonly number[], role: SimpleRole, effects: readonly string[]): PathPoint[] {
  const set = new Set(segments);
  return strip.path.map((p, i) => {
    if (!set.has(i)) return p;
    const { next: current, ...rest } = p;
    const next = role === 'own' ? (current?.kind === 'color' ? current : sectionFor('effect', current, effects)) : sectionFor(role, current, effects);
    return next ? { ...rest, next } : rest;
  });
}
