import type { Controller, LayoutSpec, Segment, Strip } from '../layout';
import { newEdgeSegment } from './segments';

/**
 * Layout editor state. Every spec change goes through `commit`, which pushes the previous spec onto the
 * undo stack. Rapid edits sharing a `coalesce` key (slider drags, typing) within COALESCE_MS collapse into a
 * single undo step. Selection and view settings are not part of history.
 */
export interface Selection {
  stripId: string | null;
  segmentIndex: number | null;
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
}

export const HISTORY_LIMIT = 100;
export const COALESCE_MS = 1000;

export const EMPTY_SPEC: LayoutSpec = { version: 1, controllers: [], strips: [] };

export function initialEditorState(spec: LayoutSpec = EMPTY_SPEC, saved: LayoutSpec | null = null): EditorState {
  return {
    spec,
    saved,
    past: [],
    future: [],
    selection: { stripId: spec.strips[0]?.id ?? null, segmentIndex: null },
    lastKey: null,
    lastAt: 0,
  };
}

interface EditMeta {
  /** Edits with the same key within COALESCE_MS merge into one undo step. */
  coalesce?: string;
  /** Timestamp (ms); defaults to Date.now(). Injected by tests. */
  now?: number;
}

export type EditorAction =
  | { type: 'load'; spec: LayoutSpec }
  | { type: 'markSaved'; spec: LayoutSpec }
  | { type: 'undo' }
  | { type: 'redo' }
  | { type: 'selectStrip'; stripId: string | null }
  | { type: 'selectSegment'; index: number | null }
  | ({ type: 'addStrip'; controllerId?: string; output?: number; name?: string } & EditMeta)
  | ({ type: 'removeStrip'; stripId: string } & EditMeta)
  | ({ type: 'updateStrip'; stripId: string; patch: Partial<Pick<Strip, 'name' | 'controllerId' | 'output'>> } & EditMeta)
  | ({ type: 'moveStrip'; stripId: string; to: number } & EditMeta)
  | ({ type: 'setSegments'; stripId: string; segments: Segment[] } & EditMeta)
  | ({ type: 'addSegment'; stripId: string; segment?: Segment; index?: number } & EditMeta)
  | ({ type: 'removeSegment'; stripId: string; index: number } & EditMeta)
  | ({ type: 'moveSegment'; stripId: string; from: number; to: number } & EditMeta)
  | ({ type: 'updateSegment'; stripId: string; index: number; segment: Segment } & EditMeta)
  | ({ type: 'addController'; controller: Controller } & EditMeta)
  | ({ type: 'updateController'; controllerId: string; patch: Partial<Omit<Controller, 'id'>> } & EditMeta)
  | ({ type: 'removeController'; controllerId: string } & EditMeta);

// ---- selectors ----

export function isDirty(state: EditorState): boolean {
  if (!state.saved) return state.past.length > 0;
  return JSON.stringify(state.spec) !== JSON.stringify(state.saved);
}

export const canUndo = (s: EditorState) => s.past.length > 0;
export const canRedo = (s: EditorState) => s.future.length > 0;

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

function mapStrip(spec: LayoutSpec, stripId: string, fn: (s: Strip) => Strip): LayoutSpec {
  return { ...spec, strips: spec.strips.map((s) => (s.id === stripId ? fn(s) : s)) };
}

/** Clamp a selection to what exists in the spec. */
function fixSelection(spec: LayoutSpec, sel: Selection): Selection {
  const strip = spec.strips.find((s) => s.id === sel.stripId) ?? null;
  if (!strip) return { stripId: spec.strips[0]?.id ?? null, segmentIndex: null };
  const segmentIndex = sel.segmentIndex !== null && sel.segmentIndex < strip.segments.length ? sel.segmentIndex : null;
  return { stripId: strip.id, segmentIndex };
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

// ---- reducer ----

export function editorReducer(state: EditorState, action: EditorAction): EditorState {
  const { spec } = state;
  switch (action.type) {
    case 'load':
      return { ...initialEditorState(action.spec, action.spec), selection: fixSelection(action.spec, state.selection) };

    case 'markSaved':
      return { ...state, saved: action.spec, lastKey: null };

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
      return { ...state, selection: fixSelection(spec, { stripId: action.stripId, segmentIndex: null }), lastKey: null };

    case 'selectSegment':
      return { ...state, selection: fixSelection(spec, { ...state.selection, segmentIndex: action.index }), lastKey: null };

    case 'addStrip': {
      const id = nextId('strip', spec.strips.map((s) => s.id));
      const target = action.controllerId !== undefined && action.output !== undefined
        ? { controllerId: action.controllerId, output: action.output }
        : freeOutput(spec, action.controllerId);
      const strip: Strip = { id, name: action.name ?? `Strip ${spec.strips.length + 1}`, ...target, segments: [newEdgeSegment()] };
      return commit(state, { ...spec, strips: [...spec.strips, strip] }, action, { stripId: id, segmentIndex: 0 });
    }

    case 'removeStrip': {
      const idx = spec.strips.findIndex((s) => s.id === action.stripId);
      if (idx < 0) return state;
      const strips = spec.strips.filter((s) => s.id !== action.stripId);
      const nextSel = strips[Math.min(idx, strips.length - 1)]?.id ?? null;
      return commit(state, { ...spec, strips }, action, { stripId: nextSel, segmentIndex: null });
    }

    case 'updateStrip':
      if (!spec.strips.some((s) => s.id === action.stripId)) return state;
      return commit(state, mapStrip(spec, action.stripId, (s) => ({ ...s, ...action.patch })), action);

    case 'moveStrip': {
      const from = spec.strips.findIndex((s) => s.id === action.stripId);
      if (from < 0 || from === action.to) return state;
      return commit(state, { ...spec, strips: moveItem(spec.strips, from, action.to) }, action);
    }

    case 'setSegments':
      if (!spec.strips.some((s) => s.id === action.stripId)) return state;
      return commit(state, mapStrip(spec, action.stripId, (s) => ({ ...s, segments: action.segments })), action, {
        stripId: action.stripId,
        segmentIndex: null,
      });

    case 'addSegment': {
      const strip = spec.strips.find((s) => s.id === action.stripId);
      if (!strip) return state;
      const index = Math.max(0, Math.min(strip.segments.length, action.index ?? strip.segments.length));
      const segments = strip.segments.slice();
      segments.splice(index, 0, action.segment ?? newEdgeSegment());
      return commit(state, mapStrip(spec, strip.id, (s) => ({ ...s, segments })), action, { stripId: strip.id, segmentIndex: index });
    }

    case 'removeSegment': {
      const strip = spec.strips.find((s) => s.id === action.stripId);
      if (!strip || action.index < 0 || action.index >= strip.segments.length) return state;
      const segments = strip.segments.filter((_, i) => i !== action.index);
      const sel = segments.length ? Math.min(action.index, segments.length - 1) : null;
      return commit(state, mapStrip(spec, strip.id, (s) => ({ ...s, segments })), action, { stripId: strip.id, segmentIndex: sel });
    }

    case 'moveSegment': {
      const strip = spec.strips.find((s) => s.id === action.stripId);
      if (!strip || action.from === action.to) return state;
      if (action.from < 0 || action.from >= strip.segments.length || action.to < 0 || action.to >= strip.segments.length) return state;
      const segments = moveItem(strip.segments, action.from, action.to);
      const sel = state.selection.segmentIndex === action.from ? action.to : state.selection.segmentIndex;
      return commit(state, mapStrip(spec, strip.id, (s) => ({ ...s, segments })), action, { stripId: strip.id, segmentIndex: sel });
    }

    case 'updateSegment': {
      const strip = spec.strips.find((s) => s.id === action.stripId);
      if (!strip || action.index < 0 || action.index >= strip.segments.length) return state;
      const segments = strip.segments.map((s, i) => (i === action.index ? action.segment : s));
      return commit(state, mapStrip(spec, strip.id, (s) => ({ ...s, segments })), action);
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

    case 'removeController': {
      if (!spec.controllers.some((c) => c.id === action.controllerId)) return state;
      return commit(
        state,
        {
          ...spec,
          controllers: spec.controllers.filter((c) => c.id !== action.controllerId),
          strips: spec.strips.filter((s) => s.controllerId !== action.controllerId),
        },
        action,
      );
    }
  }
}
