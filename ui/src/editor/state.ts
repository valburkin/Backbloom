import { defaultRoom, nextProfileId, shapePath, type Controller, type LayoutSpec, type Profile, type Room, type ShapeId, type Strip } from '../layout';
import { followStrips } from '../control/presets';
import { carryWithScreen, clampPath, fitMatchedPoints, fitRoom, reverseSections, reverseStrip } from './strips';
import { mergeTouches, revertTouches, sameValue, touchesOf, type Mirror } from './mirror';

/**
 * Layout editor state. Every spec change goes through `commit`, which pushes the previous spec onto the
 * undo stack. Rapid edits sharing a `coalesce` key (handle drags, slider drags, typing) within COALESCE_MS collapse
 * into a single undo step. Selection and view settings are not part of history.
 */
export interface Selection {
  stripId: string | null;
  /** The selected bend point of the selected strip. */
  pointIndex: number | null;
  /** The selected section of the selected strip (from bend point `sectionIndex` to the next). */
  sectionIndex: number | null;
}

export interface EditorState {
  spec: LayoutSpec;
  /** Spec as last loaded from / saved to the backend; null = nothing loaded yet. */
  saved: LayoutSpec | null;
  past: LayoutSpec[];
  future: LayoutSpec[];
  selection: Selection;
  /** Coalescing bookkeeping for the most recent commit. */
  lastKey: string | null;
  lastAt: number;
  /** Mirrored changes whose save hasn't ended (`mirror`): what they wrote isn't counted as unsaved edits. */
  mirrors: Mirror[];
}

export const HISTORY_LIMIT = 100;
export const COALESCE_MS = 1000;

export const EMPTY_SPEC: LayoutSpec = { version: 1, room: defaultRoom(), controllers: [], strips: [] };

export function initialEditorState(spec: LayoutSpec = EMPTY_SPEC, saved: LayoutSpec | null = null): EditorState {
  return {
    spec,
    saved,
    past: [],
    future: [],
    selection: { stripId: spec.strips[0]?.id ?? null, pointIndex: null, sectionIndex: null },
    lastKey: null,
    lastAt: 0,
    mirrors: [],
  };
}

interface EditMeta {
  /** Edits with the same key within COALESCE_MS merge into one undo step. */
  coalesce?: string;
  /** Timestamp (ms); defaults to Date.now(). Injected by tests. */
  now?: number;
}

export type StripPatch = Partial<Omit<Strip, 'id'>>;

export type EditorAction =
  | { type: 'load'; spec: LayoutSpec }
  | { type: 'markSaved'; spec: LayoutSpec }
  | { type: 'undo' }
  | { type: 'redo' }
  | { type: 'selectStrip'; stripId: string | null }
  | { type: 'selectPoint'; index: number | null }
  | { type: 'selectSection'; index: number | null }
  | ({ type: 'addStrip'; controllerId?: string; output?: number; name?: string; shape?: ShapeId; path?: Strip['path'] } & EditMeta)
  | ({ type: 'removeStrip'; stripId: string } & EditMeta)
  | ({ type: 'updateStrip'; stripId: string; patch: StripPatch } & EditMeta)
  | ({ type: 'moveStrip'; stripId: string; to: number } & EditMeta)
  | ({ type: 'reverseStrip'; stripId: string } & EditMeta)
  | ({ type: 'updateRoom'; patch: Partial<Room> } & EditMeta)
  | ({ type: 'addProfile'; profile: Profile } & EditMeta)
  | ({ type: 'updateProfile'; profileId: string; patch: Partial<Omit<Profile, 'id'>> } & EditMeta)
  | ({ type: 'removeProfile'; profileId: string } & EditMeta)
  | ({ type: 'addController'; controller: Controller } & EditMeta)
  | ({ type: 'updateController'; controllerId: string; patch: Partial<Omit<Controller, 'id'>> } & EditMeta)
  | ({ type: 'removeController'; controllerId: string } & EditMeta)
  /**
   * A change saved by itself (a pick, a Look: made to the layout saved last and saved at once), mirrored into the edited
   * layout: `actions` (one undo step; given as a function, made from the edited layout as it is then). What it writes
   * belongs to mirror `id` (edits with the same id add to it) until `mirrorDone`.
   */
  | ({ type: 'mirror'; id: string; actions: EditorAction[] | ((spec: LayoutSpec) => EditorAction[]) } & EditMeta)
  /**
   * The save of mirror `id` ended. When it failed, what the mirror wrote goes back to the layout saved last, where the
   * edited layout still has what it wrote (a later change to the same value stays).
   */
  | { type: 'mirrorDone'; id: string; saved: boolean };

// ---- selectors ----

/** The edited layout differs from the saved one; what mirrored changes still being saved wrote doesn't count. */
export function isDirty(state: EditorState): boolean {
  if (!state.saved) return state.past.length > 0;
  const spec = state.mirrors.length ? revertTouches(state.spec, state.saved, state.mirrors.flatMap((m) => m.touches)) : state.spec;
  return !sameValue(spec, state.saved);
}

export const canUndo = (s: EditorState) => s.past.length > 0;
export const canRedo = (s: EditorState) => s.future.length > 0;

/** True when the last undo step added strip `stripId` and nothing changed since (undo then takes back just that). */
export function lastStepAddedStrip(state: EditorState, stripId: string): boolean {
  const prev = state.past[state.past.length - 1];
  const { spec } = state;
  return (
    !!prev &&
    spec.strips.length === prev.strips.length + 1 &&
    spec.strips[spec.strips.length - 1].id === stripId &&
    prev.strips.every((s, i) => s === spec.strips[i]) &&
    prev.room === spec.room &&
    prev.controllers === spec.controllers &&
    prev.profiles === spec.profiles
  );
}

export function selectedStrip(state: EditorState): Strip | undefined {
  return state.spec.strips.find((s) => s.id === state.selection.stripId);
}

// ---- helpers ----

function nextId(prefix: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  let n = 1;
  while (used.has(`${prefix}-${n}`)) n++;
  return `${prefix}-${n}`;
}

function moveItem<T>(arr: readonly T[], from: number, to: number): T[] {
  const out = arr.slice();
  if (from < 0 || from >= out.length) return out;
  const clamped = Math.max(0, Math.min(out.length - 1, to));
  const [item] = out.splice(from, 1);
  out.splice(clamped, 0, item);
  return out;
}

/** Drops keys patched to undefined (e.g. brightness back to full), so the spec stays plain JSON. */
function withoutUndefined<T extends object>(o: T): T {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined)) as T;
}

function mapStrip(spec: LayoutSpec, stripId: string, fn: (s: Strip) => Strip): LayoutSpec {
  return { ...spec, strips: spec.strips.map((s) => (s.id === stripId ? fn(s) : s)) };
}

/**
 * The spec with only `strips` left; the profiles forget the strips that are gone (a strip added later may get the same
 * id and must not inherit their sections).
 */
function withoutStrips(spec: LayoutSpec, strips: Strip[]): LayoutSpec {
  const kept = new Set(strips.map((s) => s.id));
  const profiles = spec.profiles?.map((p) => {
    const gone = Object.keys(p.sections).filter((id) => !kept.has(id));
    if (gone.length === 0) return p;
    return { ...p, sections: Object.fromEntries(Object.entries(p.sections).filter(([id]) => kept.has(id))) };
  });
  return { ...spec, strips, ...(profiles ? { profiles } : {}) };
}

/** Clamp a selection to what exists in the spec. */
function fixSelection(spec: LayoutSpec, sel: Selection): Selection {
  const strip = spec.strips.find((s) => s.id === sel.stripId) ?? null;
  if (!strip) return { stripId: spec.strips[0]?.id ?? null, pointIndex: null, sectionIndex: null };
  const pointIndex = sel.pointIndex !== null && sel.pointIndex < strip.path.length ? sel.pointIndex : null;
  const sectionIndex = sel.sectionIndex !== null && sel.sectionIndex < strip.path.length - 1 ? sel.sectionIndex : null;
  return { stripId: strip.id, pointIndex, sectionIndex };
}

function commit(state: EditorState, spec: LayoutSpec, meta: EditMeta, selection: Selection = state.selection): EditorState {
  if (spec === state.spec) return state;
  const now = meta.now ?? Date.now();
  const merge = !!meta.coalesce && meta.coalesce === state.lastKey && now - state.lastAt <= COALESCE_MS && state.past.length > 0;
  const past = merge ? state.past : [...state.past, state.spec].slice(-HISTORY_LIMIT);
  return {
    ...state,
    spec,
    past,
    future: [],
    selection: fixSelection(spec, selection),
    lastKey: meta.coalesce ?? null,
    lastAt: now,
  };
}

function freeOutput(spec: LayoutSpec, controllerId: string | undefined): { controllerId: string; output: number } {
  const ctrl = spec.controllers.find((c) => c.id === controllerId) ?? spec.controllers[0];
  if (!ctrl) return { controllerId: controllerId ?? '', output: 0 };
  const used = new Set(spec.strips.filter((s) => s.controllerId === ctrl.id).map((s) => s.output));
  const free = ctrl.outputs.find((o) => !used.has(o.index));
  return { controllerId: ctrl.id, output: (free ?? ctrl.outputs[0])?.index ?? 0 };
}

export { nextProfileId };

/** The id the next added strip gets. */
export function nextStripId(spec: LayoutSpec): string {
  return nextId('strip', spec.strips.map((s) => s.id));
}

/**
 * Shape for a strip added without one, the layout having `strips` already: around the screen first, then the ceiling,
 * then behind the furniture.
 */
export function defaultShape(strips: number): ShapeId {
  const order: ShapeId[] = ['around-screen', 'ceiling-front', 'behind-furniture', 'line'];
  return order[Math.min(strips, order.length - 1)];
}

// ---- reducer ----

export function editorReducer(state: EditorState, action: EditorAction): EditorState {
  const { spec } = state;
  switch (action.type) {
    case 'load':
      return { ...initialEditorState(action.spec, action.spec), selection: fixSelection(action.spec, state.selection) };

    case 'markSaved': {
      // what a save adds joins the edited layout too: the default presets a layout gets at its first save
      // (withDefaultPresets), and the strips presets that play one thing everywhere follow (followStrips)
      const joined = action.spec.profiles !== undefined && spec.profiles === undefined ? { ...spec, profiles: action.spec.profiles } : spec;
      return { ...state, spec: followStrips(joined, state.saved), saved: action.spec, lastKey: null };
    }

    case 'undo': {
      if (!state.past.length) return state;
      const prev = state.past[state.past.length - 1];
      return {
        ...state,
        spec: prev,
        past: state.past.slice(0, -1),
        future: [state.spec, ...state.future],
        selection: fixSelection(prev, state.selection),
        lastKey: null,
      };
    }

    case 'redo': {
      if (!state.future.length) return state;
      const [next, ...rest] = state.future;
      return {
        ...state,
        spec: next,
        past: [...state.past, state.spec],
        future: rest,
        selection: fixSelection(next, state.selection),
        lastKey: null,
      };
    }

    case 'selectStrip':
      return { ...state, selection: fixSelection(spec, { stripId: action.stripId, pointIndex: null, sectionIndex: null }), lastKey: null };

    case 'selectPoint':
      return { ...state, selection: fixSelection(spec, { ...state.selection, pointIndex: action.index }), lastKey: null };

    case 'selectSection':
      return { ...state, selection: fixSelection(spec, { ...state.selection, sectionIndex: action.index }), lastKey: null };

    case 'addStrip': {
      const id = nextStripId(spec);
      const target = action.controllerId !== undefined && action.output !== undefined
        ? { controllerId: action.controllerId, output: action.output }
        : freeOutput(spec, action.controllerId);
      // a new strip fills its output, so it starts out without a count warning
      const length = spec.controllers.find((c) => c.id === target.controllerId)?.outputs.find((o) => o.index === target.output)?.length;
      const path = action.path ?? shapePath(spec.room, action.shape ?? defaultShape(spec.strips.length));
      const strip: Strip = {
        id,
        name: action.name ?? `Strip ${spec.strips.length + 1}`,
        ...target,
        ledCount: length && length > 0 ? length : 60,
        path,
      };
      return commit(state, { ...spec, strips: [...spec.strips, strip] }, action, { stripId: id, pointIndex: null, sectionIndex: null });
    }

    case 'removeStrip': {
      const idx = spec.strips.findIndex((s) => s.id === action.stripId);
      if (idx < 0) return state;
      const strips = spec.strips.filter((s) => s.id !== action.stripId);
      const nextSel = strips[Math.min(idx, strips.length - 1)]?.id ?? null;
      return commit(state, withoutStrips(spec, strips), action, { stripId: nextSel, pointIndex: null, sectionIndex: null });
    }

    case 'addProfile': {
      const id = (spec.profiles ?? []).some((p) => p.id === action.profile.id) || !action.profile.id ? nextProfileId(spec) : action.profile.id;
      return commit(state, { ...spec, profiles: [...(spec.profiles ?? []), { ...action.profile, id }] }, action);
    }

    case 'updateProfile':
      if (!spec.profiles?.some((p) => p.id === action.profileId)) return state;
      return commit(state, { ...spec, profiles: spec.profiles.map((p) => (p.id === action.profileId ? { ...p, ...action.patch } : p)) }, action);

    case 'removeProfile': {
      if (!spec.profiles?.some((p) => p.id === action.profileId)) return state;
      // an empty list stays: the default presets don't come back
      return commit(state, { ...spec, profiles: spec.profiles.filter((p) => p.id !== action.profileId) }, action);
    }

    case 'updateStrip':
      if (!spec.strips.some((s) => s.id === action.stripId)) return state;
      return commit(
        state,
        mapStrip(spec, action.stripId, (s) => {
          const next = withoutUndefined({ ...s, ...action.patch });
          return action.patch.ledCount !== undefined ? { ...next, path: fitMatchedPoints(next.path, next.ledCount) } : next;
        }),
        action,
      );

    case 'reverseStrip': {
      const strip = spec.strips.find((s) => s.id === action.stripId);
      if (!strip) return state;
      // the profiles' sections of the strip are turned around with it, or they would land on the wrong parts
      const sections = strip.path.length - 1;
      const profiles = spec.profiles?.map((p) => {
        const own = p.sections[strip.id];
        return own && own.length === sections ? { ...p, sections: { ...p.sections, [strip.id]: reverseSections(own) } } : p;
      });
      const reversed = mapStrip(spec, strip.id, (s) => withoutUndefined({ ...s, ...reverseStrip(s) }));
      return commit(state, { ...reversed, ...(profiles ? { profiles } : {}) }, action);
    }

    case 'moveStrip': {
      const from = spec.strips.findIndex((s) => s.id === action.stripId);
      if (from < 0 || from === action.to) return state;
      return commit(state, { ...spec, strips: moveItem(spec.strips, from, action.to) }, action);
    }

    case 'updateRoom': {
      const room = fitRoom({ ...spec.room, ...action.patch });
      const screenMoved = JSON.stringify(room.screen) !== JSON.stringify(spec.room.screen);
      const strips = spec.strips.map((s) => {
        const carried = screenMoved ? carryWithScreen(s.path, spec.room.screen, room.screen) : s.path;
        const path = clampPath(room, carried);
        return path === s.path ? s : { ...s, path };
      });
      return commit(state, { ...spec, room, strips }, action);
    }

    case 'addController': {
      const taken = spec.controllers.map((c) => c.id);
      const id = taken.includes(action.controller.id) || !action.controller.id ? nextId('controller', taken) : action.controller.id;
      return commit(state, { ...spec, controllers: [...spec.controllers, { ...action.controller, id }] }, action);
    }

    case 'updateController':
      if (!spec.controllers.some((c) => c.id === action.controllerId)) return state;
      return commit(
        state,
        { ...spec, controllers: spec.controllers.map((c) => (c.id === action.controllerId ? { ...c, ...action.patch } : c)) },
        action,
      );

    case 'mirror': {
      const list = typeof action.actions === 'function' ? action.actions(spec) : action.actions;
      const next = list.reduce(editorReducer, state).spec;
      const touches = touchesOf(spec, next);
      const done = commit(state, next, action);
      if (!touches.length) return done;
      const own = state.mirrors.find((m) => m.id === action.id);
      const mirrors = own
        ? state.mirrors.map((m) => (m === own ? { ...m, touches: mergeTouches(m.touches, touches) } : m))
        : [...state.mirrors, { id: action.id, touches }];
      return { ...done, mirrors };
    }

    case 'mirrorDone': {
      const own = state.mirrors.find((m) => m.id === action.id);
      if (!own) return state;
      const rest = { ...state, mirrors: state.mirrors.filter((m) => m !== own) };
      if (action.saved) return rest;
      return commit(rest, revertTouches(spec, state.saved, own.touches), {});
    }

    case 'removeController': {
      if (!spec.controllers.some((c) => c.id === action.controllerId)) return state;
      const controllers = spec.controllers.filter((c) => c.id !== action.controllerId);
      return commit(state, withoutStrips({ ...spec, controllers }, spec.strips.filter((s) => s.controllerId !== action.controllerId)), action);
    }
  }
}
