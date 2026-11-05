import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type Dispatch, type ReactNode } from 'react';
import { validateLayout, type LayoutIssue, type LayoutSpec } from '../layout';
import { loadLayout, saveBlockers, saveLayout, type LayoutSource, type SaveBlocker, type SaveResult } from '../api/layoutService';
import { editorReducer, initialEditorState, isDirty, type EditorAction, type EditorState } from '../editor/state';
import { useConnection } from './connection';
import { useToast } from './toast';
import { createSaveQueue, type LayoutChange } from './layoutSaver';

export interface LoadInfo {
  status: 'idle' | 'loading' | 'ready' | 'error';
  source?: LayoutSource;
  note?: string;
  error?: string;
}

export interface EditorContextValue {
  state: EditorState;
  dispatch: Dispatch<EditorAction>;
  dirty: boolean;
  load: LoadInfo;
  /** Discard local edits and read the layout from the backend again. */
  reload(): void;
  issues: LayoutIssue[];
  /** Set when the validator itself failed (e.g. module not available yet). */
  validatorError: string | null;
  blockers: SaveBlocker[];
  saving: boolean;
  /**
   * Saves the layout; `done` replaces the usual “Layout saved” message (`quiet`: no message, for Customize, which
   * saves every change at once). `spec` saves that layout instead of the current one: the result of an edit dispatched
   * just now, which the state doesn't have yet. Resolves to whether it was saved.
   */
  save(done?: string, spec?: LayoutSpec, options?: { quiet?: boolean }): Promise<boolean>;
  /**
   * Saves a change without a message (picks, Looks, presets, the first-run setup), in the one queue every layout save
   * goes through, so saves never overlap or land out of order. A change given as a function is made to the layout saved
   * last, not to the edited one (which may hold edits that aren't saved, e.g. a strip being drawn); put the same change
   * into the edited layout too as a mirror (dispatch `mirror`, then `mirrorDone` when this resolves: it isn't counted
   * as an unsaved edit meanwhile, and goes back when the save fails). A failure is reported as a toast
   * (`quietIfBlocked`: not when the layout just can't be saved as it is). Resolves to whether it was saved.
   */
  quietSave(change: LayoutChange, options?: { quietIfBlocked?: boolean }): Promise<boolean>;
  /** Resolves once no save is waiting. */
  saveIdle(): Promise<void>;
}

const Ctx = createContext<EditorContextValue | null>(null);

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

export function EditorProvider({ children }: { children: ReactNode }) {
  const conn = useConnection();
  const toast = useToast();
  const [state, dispatch] = useReducer(editorReducer, undefined, () => initialEditorState());
  const [load, setLoad] = useState<LoadInfo>({ status: 'idle' });
  const [saving, setSaving] = useState(false);

  const dirty = isDirty(state);
  const stateRef = useRef(state);
  stateRef.current = state;
  /** The layout saved last (or loaded), kept at once (the state has it after the next render). */
  const savedRef = useRef<LayoutSpec | null>(null);
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;
  const loadedSession = useRef(-1);
  const loadSeq = useRef(0);

  const ready = conn.auth === 'admin' && conn.serverInfo !== null && conn.status === 'open';

  const doLoad = useCallback(() => {
    const seq = ++loadSeq.current;
    setLoad((l) => ({ ...l, status: 'loading' }));
    loadLayout(conn.api, { admin: conn.auth === 'admin', serverInfo: conn.serverInfo })
      .then((res) => {
        if (seq !== loadSeq.current) return;
        savedRef.current = res.spec;
        dispatch({ type: 'load', spec: res.spec });
        setLoad({ status: 'ready', source: res.source, note: res.note });
      })
      .catch((e) => {
        if (seq !== loadSeq.current) return;
        setLoad({ status: 'error', error: errorText(e) });
      });
  }, [conn.api, conn.auth, conn.serverInfo]);

  // Load once per backend session, unless the user has unsaved edits (never clobber them).
  useEffect(() => {
    if (!ready || loadedSession.current === conn.session) return;
    loadedSession.current = conn.session;
    if (dirtyRef.current) return;
    doLoad();
  }, [ready, conn.session, doLoad]);

  const reload = useCallback(() => {
    if (ready) doLoad();
  }, [ready, doLoad]);

  const { issues, validatorError } = useMemo(() => {
    try {
      return { issues: validateLayout(state.spec), validatorError: null };
    } catch (e) {
      return { issues: [] as LayoutIssue[], validatorError: errorText(e) };
    }
  }, [state.spec]);

  const blockers = useMemo(
    () => saveBlockers(state.spec, issues.filter((i) => i.severity === 'error').length),
    [state.spec, issues],
  );

  const markSaved = useCallback((spec: LayoutSpec) => {
    savedRef.current = spec;
    dispatch({ type: 'markSaved', spec });
    setLoad((l) => ({ ...l, source: 'saved', note: undefined }));
  }, []);

  const deps = useRef({ conn, toast, markSaved });
  deps.current = { conn, toast, markSaved };
  const [queue] = useState(() =>
    createSaveQueue<SaveResult>({
      saved: () => savedRef.current ?? stateRef.current.spec,
      blocked: (spec) => {
        if (deps.current.conn.auth !== 'admin') return 'Sign in on this PC to change your lights.';
        return saveBlockers(spec, validateLayout(spec).filter((i) => i.severity === 'error').length)[0]?.message ?? null;
      },
      save: async (spec) => {
        const { conn: c, markSaved: mark } = deps.current;
        const res = await saveLayout(c.api, spec, (c.serverInfo?.effects ?? []).map((e) => e.name), savedRef.current);
        mark(res.spec);
        return res;
      },
      report: (title, message) => deps.current.toast('bad', title, message),
    }),
  );
  const quietSave = useCallback((change: LayoutChange, options?: { quietIfBlocked?: boolean }) => queue.save(change, options).then((r) => r !== null), [queue]);
  const saveIdle = useCallback(() => queue.idle(), [queue]);

  const save = useCallback(async (done?: string, next?: LayoutSpec, options?: { quiet?: boolean }) => {
    if (saving) return false;
    if (conn.auth !== 'admin') return false;
    if (blockers.length) return false;
    setSaving(true);
    try {
      const res = await queue.save(next ?? state.spec);
      if (!res) return false;
      if (options?.quiet && res.layoutPersisted) {
        /* Customize: the change shows on the lights */
      } else if (done) toast('ok', done);
      else if (res.layoutPersisted)
        toast('ok', `Layout saved – ${res.ledCount} LEDs${res.controllers > 1 ? ` on ${res.controllers} devices` : ''}.`);
      else
        toast(
          'info',
          'LED positions saved',
          'Your lights use the new layout, but this version of Backbloom can’t store the layout itself yet, so editing it later starts from the LED positions.',
        );
      return true;
    } finally {
      setSaving(false);
    }
  }, [saving, conn.auth, blockers.length, state.spec, toast, queue]);

  // Warn before leaving with unsaved changes (mirrored changes being saved don't count: isDirty).
  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirty]);

  const value: EditorContextValue = {
    state,
    dispatch,
    dirty,
    load,
    reload,
    issues,
    validatorError,
    blockers,
    saving,
    save,
    quietSave,
    saveIdle,
  };
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useEditor(): EditorContextValue {
  const v = useContext(Ctx);
  if (!v) throw new Error('useEditor outside EditorProvider');
  return v;
}
