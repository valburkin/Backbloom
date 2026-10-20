import type { PriorityInfo } from '../api/types';

/** What the lights are doing, as offered by the Home page's quick switch. */
export type Mode = 'screen' | 'music' | 'effect' | 'color';

const MUSIC_PREFIX = 'Music: ';

export function isMusicEffect(name: string): boolean {
  return name.startsWith(MUSIC_PREFIX);
}

/** Effect name without the "Music: " prefix the backend uses to mark sound-reactive effects. */
export function effectLabel(name: string): string {
  return isMusicEffect(name) ? name.slice(MUSIC_PREFIX.length) : name;
}

export function splitEffects(effects: ReadonlyArray<{ name: string }> | undefined): { music: string[]; regular: string[] } {
  const names = (effects ?? []).map((e) => e.name).sort((a, b) => a.localeCompare(b));
  return { music: names.filter(isMusicEffect), regular: names.filter((n) => !isMusicEffect(n)) };
}

/** The visible source, and which mode it corresponds to (null when nothing is showing). */
export function currentSource(priorities: readonly PriorityInfo[] | undefined): { mode: Mode | null; source: PriorityInfo | null } {
  const source = priorities?.find((p) => p.visible) ?? null;
  if (!source) return { mode: null, source };
  switch (source.componentId) {
    case 'COLOR':
      return { mode: 'color', source };
    case 'EFFECT':
      return { mode: source.owner && isMusicEffect(source.owner) ? 'music' : 'effect', source };
    case 'SYSTEMGRABBER':
      return { mode: 'screen', source };
    default:
      return { mode: null, source };
  }
}

/** Plain-language description of a source for the Home page. */
export function describeSource(source: PriorityInfo | null): string {
  if (!source) return 'Nothing — the lights are idle';
  switch (source.componentId) {
    case 'SYSTEMGRABBER':
      return 'Your screen';
    case 'COLOR': {
      const rgb = source.value?.RGB;
      return rgb ? `Static color (${rgb.join(', ')})` : 'Static color';
    }
    case 'EFFECT':
      return source.owner ? `${isMusicEffect(source.owner) ? 'Music' : 'Effect'}: ${effectLabel(source.owner)}` : 'An effect';
    case 'IMAGE':
      return 'A picture';
    default: {
      const origin = source.origin.split('@')[0];
      return origin || source.componentId;
    }
  }
}
