// The setup in use: Everyday (the layout's own strips with the saved Look) or a preset (its sections, its Look). One
// is always in use. What it plays is stored only in its strip sections, so a pick is written into them (never started
// for a while); its Look is the saved one for Everyday and the preset's own for a preset. Also: a source another app
// shows over the setup. The logic without React (usePick, Home, Effects, Colors, Presets).

import type { LayoutSpec, Profile, ProfileLook } from '../layout';
import type { ActiveProfile, BackendConfig, ComponentInfo, PriorityInfo } from '../api/types';
import type { EditorAction } from '../editor/state';
import { detectLook, detectProfileLook, LOOKS, profileLook, savedLook, type LookId } from '../settings/intensity';
import { partChanges, undoChanges, type PartChange } from '../pages/layout/keepShow';
import { UI_ORIGIN } from '../api/rpcApi';
import { currentSource, isMusicEffect } from './modes';
import { EVERYDAY, sectionsFor, whatPlays, type WhatPlays } from './presets';
import { pickPaths, withPaths, type MainPick } from './show';

/** The preset in use as the layout has it; undefined for Everyday (or a preset the layout doesn't have yet). */
export function presetInUse(spec: LayoutSpec, active: Pick<ActiveProfile, 'id'> | null | undefined): Profile | undefined {
  return active ? spec.profiles?.find((p) => p.id === active.id) : undefined;
}

/** The setup's name: the preset's, or Everyday. */
export function setupName(preset: Profile | undefined): string {
  return preset ? preset.name.trim() || 'Untitled' : EVERYDAY;
}

/** What the setup plays: the preset's sections, or Everyday's (the layout's own strips). */
export function setupPlays(spec: LayoutSpec, preset: Profile | undefined): WhatPlays {
  return whatPlays(spec, preset);
}

/** What puts the setup back as it was before a pick (the toast's Undo). */
export type PickUndo = { kind: 'preset'; id: string; sections: Profile['sections'] } | { kind: 'everyday'; changes: PartChange[] };

/** A pick written into the setup in use. */
export interface PickWrite {
  /** The layout with the pick (save it). */
  spec: LayoutSpec;
  /** The edits that make it (dispatch them as one undo step); none when the setup plays it already. */
  actions: EditorAction[];
  /** The setup's strips were customized (a mix, set in Layout): the pick replaced that. */
  replaced: boolean;
  undo: PickUndo;
}

/**
 * Writes a pick into the setup in use: a preset's sections (every strip, sectionsFor), or Everyday's strips (pickPaths).
 * Off parts and gaps are kept; anything the setup's strips were customized with is replaced.
 */
export function writePick(spec: LayoutSpec, preset: Profile | undefined, pick: MainPick): PickWrite {
  const replaced = setupPlays(spec, preset).kind === 'customized';
  if (preset) {
    const sections = sectionsFor(spec, pick);
    const same = JSON.stringify(sections) === JSON.stringify(preset.sections);
    return {
      spec: same ? spec : { ...spec, profiles: spec.profiles?.map((p) => (p.id === preset.id ? { ...p, sections } : p)) },
      actions: same ? [] : [{ type: 'updateProfile', profileId: preset.id, patch: { sections } }],
      replaced,
      undo: { kind: 'preset', id: preset.id, sections: preset.sections },
    };
  }
  const paths = pickPaths(spec, pick);
  return {
    spec: withPaths(spec, paths),
    actions: [...paths].map(([stripId, path]) => ({ type: 'updateStrip', stripId, patch: { path } })),
    replaced,
    undo: { kind: 'everyday', changes: partChanges(spec.strips, paths) },
  };
}

/**
 * The setup with that id in a layout: the preset, or undefined for Everyday (null). Throws when the layout doesn't have
 * the preset (`saved`: the layout saved last, else the edited one), so a pick never lands in Everyday instead.
 */
export function setupById(spec: LayoutSpec, presetId: string | null, saved: boolean): Profile | undefined {
  if (presetId === null) return undefined;
  const preset = spec.profiles?.find((p) => p.id === presetId);
  if (!preset)
    throw new Error(saved ? 'That preset isn’t saved yet. Save your changes on Layout first.' : 'That preset was removed on Layout. Save or discard that change first.');
  return preset;
}

/** The layout with a preset changed (its Look, its name…); throws when the layout doesn't have it (setupById). */
export function withPresetPatch(spec: LayoutSpec, presetId: string, patch: Partial<Omit<Profile, 'id'>>, saved = true): LayoutSpec {
  setupById(spec, presetId, saved);
  return { ...spec, profiles: spec.profiles!.map((p) => (p.id === presetId ? { ...p, ...patch } : p)) };
}

/** What a pick needs turned on: screen sync for Screen edges and Screen average (when it is off), sound input for music. */
export function pickNeeds(pick: MainPick, components: readonly ComponentInfo[] | undefined): { screen: boolean; sound: boolean } {
  const screenOn = components?.find((c) => c.name === 'SYSTEMGRABBER')?.enabled === true;
  return {
    screen: (pick.kind === 'edges' || pick.kind === 'average') && !screenOn,
    sound: pick.kind === 'effect' && isMusicEffect(pick.effect),
  };
}

/** Undo of a pick: the edits and the layout that put the setup back (a part changed since keeps its new role). */
export function undoPick(spec: LayoutSpec, undo: PickUndo): { spec: LayoutSpec; actions: EditorAction[] } {
  if (undo.kind === 'preset') {
    if (!spec.profiles?.some((p) => p.id === undo.id)) return { spec, actions: [] };
    return {
      spec: { ...spec, profiles: spec.profiles.map((p) => (p.id === undo.id ? { ...p, sections: undo.sections } : p)) },
      actions: [{ type: 'updateProfile', profileId: undo.id, patch: { sections: undo.sections } }],
    };
  }
  const paths = undoChanges(spec, undo.changes);
  return { spec: withPaths(spec, paths), actions: [...paths].map(([stripId, path]) => ({ type: 'updateStrip', stripId, patch: { path } })) };
}

/**
 * The setup's Look as values: a preset's own (one without its own has Everyday's), else the saved one; null before the
 * settings are loaded.
 */
export function setupLookValues(config: BackendConfig | null, preset: Profile | undefined): ProfileLook | null {
  return preset?.look ?? savedLook(config);
}

/** Which of the five the setup's Look is, or "custom"; null before the settings are loaded. */
export function setupLook(config: BackendConfig | null, preset: Profile | undefined): LookId | 'custom' | null {
  return preset?.look ? detectProfileLook(preset.look) : detectLook(config);
}

/** A Look in a word: "Intense", or "Custom". */
export function lookLabel(look: LookId | 'custom' | null): string | null {
  if (look === null) return null;
  return look === 'custom' ? 'Custom' : (LOOKS.find((l) => l.id === look)?.label ?? null);
}

/** Picking one of the five for a preset: its own Look (the preset is saved with the layout). */
export function presetLookAction(preset: Profile, id: LookId): EditorAction {
  return { type: 'updateProfile', profileId: preset.id, patch: { look: profileLook(id) } };
}

/**
 * A preset's Look with some values changed (the Colors page): from its own, or from Everyday's (`saved`) when it has
 * none yet.
 */
export function withLookValues(preset: Profile, saved: ProfileLook | null, patch: Partial<ProfileLook>): ProfileLook | null {
  const base = preset.look ?? saved;
  return base ? { ...base, ...patch } : null;
}

/** Something another app shows over the setup (a color, an effect, a picture): what, from whom, at which priority. */
export interface Overlay {
  what: string;
  /** The app's name (the origin before '@'); null when unknown. */
  app: string | null;
  /** Backbloom's own: a color tried on the Effects page (shown while its picker is dragged). */
  own: boolean;
  priority: number;
}

/**
 * The visible source when it isn't the setup: not the screen (with the zones), not Backbloom's own startup or idle
 * effect (origin "System"), and with `ignoreOwn` not a color the UI tries while its picker is dragged. Null when the
 * setup shows.
 */
export function overlayOf(priorities: readonly PriorityInfo[] | undefined, ignoreOwn = false): Overlay | null {
  const { source } = currentSource(priorities);
  if (!source || source.componentId === 'SYSTEMGRABBER') return null;
  const app = source.origin.split('@')[0].trim();
  const own = app === UI_ORIGIN;
  if (app === 'System' || (own && ignoreOwn)) return null;
  const what =
    source.componentId === 'EFFECT'
      ? source.owner && isMusicEffect(source.owner)
        ? 'A music effect'
        : 'An effect'
      : source.componentId === 'COLOR'
        ? 'A color'
        : source.componentId === 'IMAGE'
          ? 'A picture'
          : 'Something';
  return { what, app: own ? null : app || null, own, priority: source.priority };
}

/** "An effect from Home Assistant shows over Gaming"; "A color you tried shows over Gaming". */
export function overlayText(overlay: Overlay, setup: string): string {
  if (overlay.own) return `${overlay.what} you tried shows over ${setup}`;
  return `${overlay.what} from ${overlay.app ?? 'another app'} shows over ${setup}`;
}
