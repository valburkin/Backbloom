import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type Dispatch, type ReactNode } from 'react';
import { validateLayout, type LayoutIssue } from '../layout';
import { loadLayout, saveBlockers, saveLayout, type LayoutSource, type SaveBlocker } from '../api/layoutService';
import { editorReducer, initialEditorState, isDirty, type EditorAction, type EditorState } from '../editor/state';
import type { AspectId } from '../editor/geometry';
import { useConnection } from './connection';
import { useToast } from './toast';

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
  save(): Promise<void>;
  aspect: AspectId;
  setAspect(a: AspectId): void;
}

const Ctx = createContext<EditorContextValue | null>(null);
const ASPECT_KEY = 'overglow.aspect';

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

export function EditorProvider({ children }: { children: ReactNode }) {
  const conn = useConnection();
  const toast = useToast();
  const [state, dispatch] = useReducer(editorReducer, undefined, () => initialEditorState());
  const [load, setLoad] = useState<LoadInfo>({ status: 'idle' });
  const [saving, setSaving] = useState(false);
  const [aspect, setAspectState] = useState<AspectId>(() => {
    try {
      return (localStorage.getItem(ASPECT_KEY) as AspectId) || '16:9';
    } catch {
      return '16:9';
    }
  });
  const setAspect = useCallback((a: AspectId) => {
    setAspectState(a);
    try {
      localStorage.setItem(ASPECT_KEY, a);
    } catch {
      /* ignore */
    }
  }, []);

  const dirty = isDirty(state);
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

  const save = useCallback(async () => {
    if (saving) return;
    if (conn.auth !== 'admin') return;
    if (blockers.length) return;
    setSaving(true);
    const spec = state.spec;
    try {
      const res = await saveLayout(conn.api, spec);
      dispatch({ type: 'markSaved', spec });
      setLoad((l) => ({ ...l, source: 'saved', note: undefined }));
      if (res.layoutPersisted)
        toast('ok', 'Layout saved', `${res.ledCount} LEDs${res.controllers > 1 ? ` on ${res.controllers} controllers` : ''} are now mapped to your screen.`);
      else
        toast(
          'info',
          'LED positions saved',
          'Your lights use the new layout, but this version of the service can’t store the layout itself yet, so editing it later starts from the LED positions.',
        );
    } catch (e) {
      toast('bad', 'Couldn’t save the layout', errorText(e));
    } finally {
      setSaving(false);
    }
  }, [saving, conn, blockers.length, state.spec, toast]);

  // Warn before leaving with unsaved changes.
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
    aspect,
    setAspect,
  };
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useEditor(): EditorContextValue {
  const v = useContext(Ctx);
  if (!v) throw new Error('useEditor outside EditorProvider');
  return v;
}
