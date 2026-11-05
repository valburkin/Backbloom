// Presets (the layout's profiles): what plays while one is in use, writing a "What plays" choice into it, new presets,
// the default presets a layout starts with, the guided setup's "Add your game to Gaming", and whether one is in use.
// The logic without React (Presets page, Home, Effects, Welcome).

import { applyProfile, nextProfileId, programName, stripInProfile, type LayoutSpec, type Profile, type ProfileLook, type Rgb, type Section, type Strip } from '../layout';
import type { ActiveProfile, DismissedProfile } from '../api/types';
import type { EditorAction } from '../editor/state';
import { detectProfileLook, LOOKS, profileLook } from '../settings/intensity';
import { effectLabel, isMusicEffect } from './modes';

/** What a preset plays on its strips, as the Presets page offers it; customized when its strips differ (set in Layout). */
export type WhatPlays = { kind: 'edges' } | { kind: 'average' } | { kind: 'effect'; effect: string } | { kind: 'color'; color: Rgb } | { kind: 'customized' };

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** A part's role in "What plays" terms, or null for a part that doesn't count (a gap, or off as in the layout). */
function playOf(section: Section | undefined, base: Section | undefined): WhatPlays | null {
  if (base?.kind === 'gap' || section?.kind === 'gap') return null;
  if (!section || section.kind === 'edge') return { kind: 'edges' };
  if (section.kind === 'whole' || section.kind === 'area') return { kind: 'average' };
  if (section.kind === 'none') return base?.kind === 'none' ? null : { kind: 'customized' };
  if (section.kind === 'effect') return { kind: 'effect', effect: section.effect };
  return { kind: 'color', color: section.color };
}

/**
 * What the preset plays (without one: what Everyday, the layout's own strips, plays): the same on every part with LEDs
 * (gaps and the layout's Off parts don't count), else customized. A preset that changes nothing plays what the
 * layout's strips play.
 */
export function whatPlays(spec: LayoutSpec, profile?: Profile): WhatPlays {
  let found: WhatPlays | null = null;
  for (const strip of spec.strips) {
    const applied = stripInProfile(strip, profile);
    for (let i = 0; i < strip.path.length - 1; i++) {
      const play = playOf(applied.path[i].next, strip.path[i].next);
      if (!play) continue;
      if (play.kind === 'customized' || (found && !same(found, play))) return { kind: 'customized' };
      found = play;
    }
  }
  return found ?? { kind: 'edges' };
}

/** The section a part gets for a choice; the layout's Off parts stay off, a part on a screen edge keeps its mapping. */
function sectionFor(play: Exclude<WhatPlays, { kind: 'customized' }>, base: Section | undefined): Section | null {
  if (base?.kind === 'gap') return null;
  if (base?.kind === 'none') return base;
  switch (play.kind) {
    case 'edges':
      return base?.kind === 'edge' ? base : null;
    case 'average':
      return { kind: 'whole' };
    case 'effect':
      return { kind: 'effect', effect: play.effect };
    case 'color':
      return { kind: 'color', color: [play.color[0], play.color[1], play.color[2]] };
  }
}

/** Every strip's sections for a "What plays" choice (stored as a preset's `sections`). */
export function sectionsFor(spec: LayoutSpec, play: Exclude<WhatPlays, { kind: 'customized' }>): Profile['sections'] {
  return Object.fromEntries(spec.strips.map((s) => [s.id, s.path.slice(0, -1).map((p) => sectionFor(play, p.next))]));
}

/** The sections of the layout as it shows now (with the preset that is on, `profileId`), for a new preset. */
export function currentSections(spec: LayoutSpec, profileId: string | null): Profile['sections'] {
  const shown = applyProfile(spec, profileId);
  return Object.fromEntries(shown.strips.map((s) => [s.id, s.path.slice(0, -1).map((p): Section | null => (p.next && p.next.kind !== 'gap' ? p.next : null))]));
}

/** A name not taken by another preset: "Gaming", else "Gaming 2", "Gaming 3"… */
export function freeName(spec: LayoutSpec, base: string): string {
  const taken = new Set((spec.profiles ?? []).map((p) => p.name.trim().toLowerCase()));
  if (!taken.has(base.toLowerCase())) return base;
  let n = 2;
  while (taken.has(`${base} ${n}`.toLowerCase())) n++;
  return `${base} ${n}`;
}

/** New preset: what the setup in use plays (`profileId`, null: Everyday) and its Look, used when picked until programs are added. */
export function newPreset(spec: LayoutSpec, now: { profileId: string | null; look: ProfileLook | null }): Profile {
  return {
    id: nextProfileId(spec),
    name: freeName(spec, `Preset ${(spec.profiles?.length ?? 0) + 1}`),
    apps: [],
    sections: currentSections(spec, now.profileId),
    ...(now.look ? { look: now.look } : {}),
  };
}

/** What adding a preset or a program does: the edits (one undo step), and what to tell the user. */
export interface PresetChange {
  actions: EditorAction[];
  message: string;
}

/** The id of the default Gaming preset. */
export const GAMING_ID = 'gaming';

/** The Gaming preset: the default one (by its id), else one named Gaming. */
export function gamingPreset(spec: LayoutSpec): Profile | undefined {
  const list = spec.profiles ?? [];
  return list.find((p) => p.id === GAMING_ID) ?? list.find((p) => p.name.trim().toLowerCase() === 'gaming');
}

/**
 * The guided setup's "Add your game to Gaming": the program goes into Gaming's programs. Gaming is made again when it
 * was deleted (Screen edges, the Intense Look), and the default presets come first when the layout never had presets
 * (`effects`: the effect catalog, for the Music preset). Nothing to change when a preset is used with that program
 * already.
 */
export function addGameToGaming(spec: LayoutSpec, exe: string, effects: readonly string[]): PresetChange | null {
  const program = programName(exe);
  if (!program) return null;
  const existing = spec.profiles?.find((p) => p.apps.some((a) => a.toLowerCase() === program));
  if (existing) return { actions: [], message: `${existing.name || 'A preset'} is used while ${program} runs already` };
  const actions: EditorAction[] = [];
  let next = spec;
  if (spec.profiles === undefined) {
    for (const profile of defaultPresets(spec, effects)) actions.push({ type: 'addProfile', profile });
    next = { ...spec, profiles: defaultPresets(spec, effects) };
  }
  const gaming = gamingPreset(next);
  if (gaming) {
    actions.push({ type: 'updateProfile', profileId: gaming.id, patch: { apps: [...gaming.apps, program] } });
    return { actions, message: `${gaming.name} is used while ${program} runs` };
  }
  const name = freeName(next, 'Gaming');
  const taken = next.profiles?.some((p) => p.id === GAMING_ID);
  const profile: Profile = { id: taken ? nextProfileId(next) : GAMING_ID, name, apps: [program], sections: sectionsFor(next, { kind: 'edges' }), look: profileLook('intense') };
  actions.push({ type: 'addProfile', profile });
  return { actions, message: `${name} added: it's used while ${program} runs` };
}

/**
 * The music effect of the default Music preset: the first one in the catalog that plays along a strip in many colors
 * ("… for LED strip (MULTI COLOR)"), else the first in many colors, else the first; null without music effects.
 */
export function defaultMusicEffect(effects: readonly string[]): string | null {
  const music = effects.filter(isMusicEffect);
  return music.find((e) => e.includes('for LED strip') && e.includes('(MULTI COLOR)')) ?? music.find((e) => e.includes('(MULTI COLOR)')) ?? music[0] ?? null;
}

/** The warm color of the default Cozy preset. */
export const COZY_COLOR: Rgb = [255, 140, 40];

/**
 * The presets a layout starts with: Gaming (Screen edges, Intense), Movie (Screen edges, Relaxed), Music (a music
 * effect, Rich; only with music effects in `effects`) and Cozy (a warm color, Relaxed), none used by a program yet.
 */
export function defaultPresets(spec: LayoutSpec, effects: readonly string[]): Profile[] {
  const music = defaultMusicEffect(effects);
  const edges = sectionsFor(spec, { kind: 'edges' });
  return [
    { id: GAMING_ID, name: 'Gaming', apps: [], sections: edges, look: profileLook('intense') },
    { id: 'movie', name: 'Movie', apps: [], sections: edges, look: profileLook('relaxed') },
    ...(music ? [{ id: 'music', name: 'Music', apps: [], sections: sectionsFor(spec, { kind: 'effect', effect: music }), look: profileLook('rich') }] : []),
    { id: 'cozy', name: 'Cozy', apps: [], sections: sectionsFor(spec, { kind: 'color', color: COZY_COLOR }), look: profileLook('relaxed') },
  ];
}

/**
 * The layout as it is saved: a layout that never had presets (`profiles` absent) gets the default ones once it has
 * strips (what they play is stored per strip). Deleting them leaves an empty list, so they don't come back.
 */
export function withDefaultPresets(spec: LayoutSpec, effects: readonly string[]): LayoutSpec {
  if (spec.profiles !== undefined || spec.strips.length === 0) return spec;
  return { ...spec, profiles: defaultPresets(spec, effects) };
}

/**
 * What a preset plays the same on all its parts, as the strips are now and as they were saved last (`previous`), or
 * null when it is customized (or can't tell). A strip's list counts as it was made: for the strip as it is, else for
 * the strip as it was saved (reshaped since). A list made for neither shape counts as customized on a strip whose shape
 * didn't change; on a strip added or reshaped since, it was made for a shape the strip had while it was drawn (a pick
 * mirrored into the edited layout before it was saved), so what the preset saved last plays on the strip counts
 * instead, else the list doesn't count. A strip without a list plays what the layout's strip plays, unless it is new
 * since `previous` (it doesn't count). A preset without lists plays what the layout plays, whatever that is: null.
 */
function uniformPlay(spec: LayoutSpec, profile: Profile, previous: LayoutSpec | null | undefined): Exclude<WhatPlays, { kind: 'customized' }> | null {
  if (!spec.strips.some((s) => profile.sections[s.id])) return null;
  let found: Exclude<WhatPlays, { kind: 'customized' }> | null = null;
  for (const now of spec.strips) {
    const before = previous?.strips.find((s) => s.id === now.id);
    const list = profile.sections[now.id];
    if (!list && previous && !before) continue;
    let strip: Strip | null = !list || list.length === now.path.length - 1 ? now : before && list.length === before.path.length - 1 ? before : null;
    let made = profile;
    if (!strip) {
      // the strip's shape didn't change since it was saved (or there is nothing to tell): customized
      if (!previous || (before && before.path.length === now.path.length)) return null;
      const saved = previous.profiles?.find((p) => p.id === profile.id);
      if (!before || saved?.sections[now.id]?.length !== before.path.length - 1) continue;
      strip = before;
      made = saved;
    }
    const applied = stripInProfile(strip, made);
    for (let i = 0; i < strip.path.length - 1; i++) {
      const play = playOf(applied.path[i].next, strip.path[i].next);
      if (!play) continue;
      if (play.kind === 'customized' || (found && !same(found, play))) return null;
      found = play;
    }
  }
  return found;
}

/**
 * The layout as it is saved: a preset that plays one thing on all its parts (Screen edges, Screen average, one
 * effect, one color) plays it on every strip, so strips added or reshaped since (`previous`: the layout saved last)
 * follow it; a list that fits its strip stays as it is. Presets customized in Layout are left alone.
 */
export function followStrips(spec: LayoutSpec, previous?: LayoutSpec | null): LayoutSpec {
  if (!spec.profiles?.length) return spec;
  let changed = false;
  const profiles = spec.profiles.map((profile) => {
    const play = uniformPlay(spec, profile, previous);
    if (!play) return profile;
    const all = sectionsFor(spec, play);
    const stale = spec.strips.filter((s) => profile.sections[s.id]?.length !== s.path.length - 1);
    if (stale.length === 0) return profile;
    changed = true;
    return { ...profile, sections: { ...profile.sections, ...Object.fromEntries(stale.map((s) => [s.id, all[s.id]])) } };
  });
  return changed ? { ...spec, profiles } : spec;
}

/** A "What plays" choice in words. */
export function playLabel(play: WhatPlays): string {
  switch (play.kind) {
    case 'edges':
      return 'Screen edges';
    case 'average':
      return 'Screen average';
    case 'effect':
      return `${isMusicEffect(play.effect) ? 'Music' : 'Effect'}: ${effectLabel(play.effect)}`;
    case 'color':
      return 'Color';
    default:
      return 'Customized in Layout';
  }
}

/** A preset in a few words: what plays and its Look ("Screen edges · Intense"). */
export function presetSummary(spec: LayoutSpec, profile: Profile): string {
  const look = detectProfileLook(profile.look);
  const lookText = look === null ? null : look === 'custom' ? 'its own Look' : LOOKS.find((l) => l.id === look)?.label;
  return [playLabel(whatPlays(spec, profile)), lookText].filter(Boolean).join(' · ');
}

/** Whether a preset is on, and how: by one of its programs, by hand, off until its programs quit, or off. */
export type PresetStatus = 'program' | 'hand' | 'dismissed' | 'off';

export function presetStatus(id: string, active: ActiveProfile | null | undefined, dismissed: DismissedProfile | null | undefined): PresetStatus {
  if (active?.id === id) return active.manual ? 'hand' : 'program';
  return dismissed?.id === id ? 'dismissed' : 'off';
}

/** Why the preset is in use, or stays unused, in words ("In use while witcher3.exe runs"); null otherwise. */
export function statusText(profile: Profile, status: PresetStatus): string | null {
  const one = profile.apps.length === 1;
  const program = one ? profile.apps[0] : 'one of its programs';
  switch (status) {
    case 'program':
      return `In use while ${program} runs`;
    case 'hand':
      return 'In use until you switch';
    case 'dismissed':
      return `Not used until ${one ? `${program} quits` : 'its programs quit'}`;
    default:
      return null;
  }
}

/** The setup without a preset: the layout's own strips, with the saved Look. One of the choices next to the presets. */
export const EVERYDAY = 'Everyday';

/**
 * Switches to Everyday: the preset in use goes off (`profile dismiss`; one its program turned on stays off until the
 * program quits). Another preset whose program runs could take over, so this dismisses until none is in use (a few
 * at most). Resolves with the state after it.
 */
export async function switchToEveryday(api: { dismissProfile(): Promise<{ profile: ActiveProfile | null; dismissed: DismissedProfile | null }> }) {
  let state = await api.dismissProfile();
  for (let i = 0; state.profile && i < 8; i++) state = await api.dismissProfile();
  return state;
}
