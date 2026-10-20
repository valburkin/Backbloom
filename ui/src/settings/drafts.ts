// Unsaved settings edits, per page. Pages show `edits[section] ?? config[section]`, so sections nobody is
// editing keep following the live config (other tabs, the tray app, MQTT...), while an edited section stays as
// the user left it until they save or discard. `baseline` is the section as it was when editing started; it is
// what Discard returns to (and what a live preview has to be rolled back to).

export interface PageDraft {
  edits: Record<string, unknown>;
  baseline: Record<string, unknown>;
}

export type DraftsState = Record<string, PageDraft>;

export type DraftsAction =
  | { type: 'edit'; page: string; section: string; value: unknown; current: unknown }
  | { type: 'discard'; page: string }
  /** `values`: what was stored. A section edited again meanwhile stays edited, against the stored value. */
  | { type: 'saved'; page: string; sections: string[]; values?: Record<string, unknown> };

export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    return a.length === bb.length && a.every((v, i) => deepEqual(v, bb[i]));
  }
  const ao = a as Record<string, unknown>;
  const bo = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(ao), ...Object.keys(bo)]);
  for (const k of keys) if (!deepEqual(ao[k], bo[k])) return false;
  return true;
}

const EMPTY: PageDraft = { edits: {}, baseline: {} };

export function draftsReducer(state: DraftsState, action: DraftsAction): DraftsState {
  const page = state[action.page] ?? EMPTY;
  switch (action.type) {
    case 'edit': {
      const baseline = action.section in page.baseline ? page.baseline[action.section] : action.current;
      const edits = { ...page.edits };
      const base = { ...page.baseline };
      if (deepEqual(action.value, baseline)) {
        delete edits[action.section];
        delete base[action.section];
      } else {
        edits[action.section] = action.value;
        base[action.section] = baseline;
      }
      return { ...state, [action.page]: { edits, baseline: base } };
    }
    case 'discard': {
      if (!state[action.page]) return state;
      const next = { ...state };
      delete next[action.page];
      return next;
    }
    case 'saved': {
      const edits = { ...page.edits };
      const baseline = { ...page.baseline };
      for (const s of action.sections) {
        if (action.values && s in edits && !deepEqual(edits[s], action.values[s])) {
          baseline[s] = action.values[s];
        } else {
          delete edits[s];
          delete baseline[s];
        }
      }
      return { ...state, [action.page]: { edits, baseline } };
    }
  }
}

export function dirtySections(draft: PageDraft | undefined): string[] {
  return draft ? Object.keys(draft.edits) : [];
}

export function isPageDirty(draft: PageDraft | undefined): boolean {
  return dirtySections(draft).length > 0;
}

/** Immutable set of a nested value: setIn({a:{b:1}}, ['a','b'], 2). Numeric keys index arrays. */
export function setIn<T>(obj: T, path: ReadonlyArray<string | number>, value: unknown): T {
  if (!path.length) return value as T;
  const [head, ...rest] = path;
  if (Array.isArray(obj)) {
    const copy = obj.slice();
    copy[head as number] = setIn(copy[head as number], rest, value);
    return copy as T;
  }
  const src = (obj && typeof obj === 'object' ? obj : {}) as Record<string, unknown>;
  return { ...src, [head]: setIn(src[head], rest, value) } as T;
}


