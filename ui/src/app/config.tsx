import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type Dispatch, type ReactNode } from 'react';
import type { BackendConfig, JsonSchema, SettingsSchema } from '../api/types';
import { RpcError } from '../api/types';
import { withDefaults, validate, type Issue } from '../schema/schema';
import { dirtySections, draftsReducer, isPageDirty, setIn, type DraftsAction, type DraftsState } from '../settings/drafts';
import { useConnection } from './connection';
import { useToast } from './toast';

export interface ConfigContextValue {
  /** Settings of the current instance; null until loaded or without admin access. */
  config: BackendConfig | null;
  schema: SettingsSchema | null;
  status: 'idle' | 'loading' | 'ready' | 'error';
  error: string | null;
  reload(): void;
  drafts: DraftsState;
  dispatchDraft: Dispatch<DraftsAction>;
  /** Page currently saving, if any. */
  saving: string | null;
  /** Saves the page's edited sections. Resolves true when they were stored. */
  save(page: string, labels?: Record<string, string>): Promise<boolean>;
}

const Ctx = createContext<ConfigContextValue | null>(null);

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

/** The per-field errors of a rejected `setconfig` (`errors: [{ path, message }]`). */
function rejectedFields(e: unknown): Issue[] {
  const errors = e instanceof RpcError ? e.reply?.errors : undefined;
  return Array.isArray(errors)
    ? errors.filter((x): x is Issue => !!x && typeof x === 'object' && typeof x.path === 'string' && typeof x.message === 'string')
    : [];
}

/** Turns backend save errors into something a user can act on. */
export function saveErrorText(e: unknown, labels: Record<string, string> = {}): string {
  if (e instanceof RpcError && e.isAuthError) return 'Your session isn’t signed in anymore. Sign in and save again.';
  const fields = rejectedFields(e);
  if (fields.length) return fields.slice(0, 3).map((f) => `${labels[f.path] ?? f.path}: ${f.message}`).join(' ');
  return errorText(e);
}

export function ConfigProvider({ children }: { children: ReactNode }) {
  const conn = useConnection();
  const toast = useToast();
  const [config, setConfig] = useState<BackendConfig | null>(null);
  const [schema, setSchema] = useState<SettingsSchema | null>(null);
  const [status, setStatus] = useState<ConfigContextValue['status']>('idle');
  const [error, setError] = useState<string | null>(null);
  const [drafts, dispatchDraft] = useReducer(draftsReducer, {});
  const [saving, setSaving] = useState<string | null>(null);
  const seq = useRef(0);
  const draftsRef = useRef(drafts);
  draftsRef.current = drafts;
  const configRef = useRef(config);
  configRef.current = config;

  const ready = conn.status === 'open' && conn.auth === 'admin';

  const load = useCallback(() => {
    const n = ++seq.current;
    setStatus('loading');
    setError(null);
    const admin = conn.auth === 'admin';
    Promise.all([conn.api.getSchema().catch(() => null), admin ? conn.api.getConfig() : Promise.resolve(null)])
      .then(([sc, cfg]) => {
        if (n !== seq.current) return;
        setSchema(sc);
        setConfig(cfg);
        setStatus('ready');
      })
      .catch((e) => {
        if (n !== seq.current) return;
        setError(errorText(e));
        setStatus('error');
      });
  }, [conn.api, conn.auth]);

  useEffect(() => {
    if (ready) load();
    else seq.current++;
  }, [ready, conn.session, load]);

  // Keep the config fresh: the backend pushes changed sections (admin sessions only).
  useEffect(
    () =>
      conn.backend.onEvent('settings-update', (e) => {
        const d = e.data;
        if (d && typeof d === 'object' && !Array.isArray(d)) setConfig((c) => (c ? { ...c, ...(d as BackendConfig) } : c));
      }),
    [conn.backend],
  );

  // Warn before closing the tab with unsaved settings.
  const anyDirty = Object.values(drafts).some(isPageDirty);
  useEffect(() => {
    if (!anyDirty) return;
    const on = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener('beforeunload', on);
    return () => window.removeEventListener('beforeunload', on);
  }, [anyDirty]);

  const save = useCallback(
    async (page: string, labels: Record<string, string> = {}): Promise<boolean> => {
      if (conn.auth !== 'admin') return false;
      const draft = draftsRef.current[page];
      const sections = dirtySections(draft);
      if (!draft || !sections.length) return true;
      const partial: BackendConfig = {};
      for (const s of sections) partial[s] = draft.edits[s];
      const problems = sections.flatMap((s) => (schema?.properties[s] ? validate(schema.properties[s], partial[s], s) : []));
      if (problems.length) {
        toast('bad', 'Some values need attention', problems.map((p) => `${labels[p.path] ?? p.path}: ${p.message}`).slice(0, 3).join(' '));
        return false;
      }
      setSaving(page);
      try {
        // The backend stores the sections as sent or rejects the call; its settings-update events (sent before the
        // reply) bring the stored values into `config`.
        await conn.api.setConfig(partial);
        dispatchDraft({ type: 'saved', page, sections });
        toast('ok', 'Settings saved');
        return true;
      } catch (e) {
        toast('bad', 'Couldn’t save', saveErrorText(e, labels));
        return false;
      } finally {
        setSaving(null);
      }
    },
    [conn, schema, toast],
  );

  const value: ConfigContextValue = { config, schema, status, error, reload: load, drafts, dispatchDraft, saving, save };
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useConfig(): ConfigContextValue {
  const v = useContext(Ctx);
  if (!v) throw new Error('useConfig outside ConfigProvider');
  return v;
}

export interface PageSettings {
  /** Config and schema are loaded and the user may edit (admin). */
  ready: boolean;
  canEdit: boolean;
  schemaOf(section: string): JsonSchema | undefined;
  /** Section value as shown: unsaved edit, else live config, with schema defaults filled in ({} until loaded). */
  value<T = Record<string, unknown>>(section: string): T;
  set(section: string, value: unknown): void;
  setPath(section: string, path: ReadonlyArray<string | number>, value: unknown): void;
  dirty: boolean;
  dirtySections: string[];
  /** Section values as they were before editing (what Discard returns to). */
  baseline: Record<string, unknown>;
  issues: Issue[];
  saving: boolean;
  save(): Promise<boolean>;
  discard(): void;
}

/** Local editing of some config sections for one page; see settings/drafts.ts. */
export function usePageSettings(page: string, labels?: Record<string, string>): PageSettings {
  const { config, schema, drafts, dispatchDraft, saving, save } = useConfig();
  const { auth } = useConnection();
  const draft = drafts[page];
  const schemaOf = useCallback((s: string) => schema?.properties[s], [schema]);

  const current = useCallback((section: string) => withDefaults(schema?.properties[section], config?.[section]), [config, schema]);
  const value = useCallback(
    <T,>(section: string): T => (draft && section in draft.edits ? draft.edits[section] : (current(section) ?? {})) as T,
    [draft, current],
  );
  const set = useCallback(
    (section: string, v: unknown) => dispatchDraft({ type: 'edit', page, section, value: v, current: current(section) }),
    [dispatchDraft, page, current],
  );
  const setPath = useCallback(
    (section: string, path: ReadonlyArray<string | number>, v: unknown) => set(section, setIn(value(section), path, v)),
    [set, value],
  );
  const sections = dirtySections(draft);
  const issues = useMemo(
    () => dirtySections(draft).flatMap((s) => (schema?.properties[s] && draft ? validate(schema.properties[s], draft.edits[s], s) : [])),
    [draft, schema],
  );
  return {
    ready: !!config && !!schema,
    canEdit: auth === 'admin' && !!config,
    schemaOf,
    value,
    set,
    setPath,
    dirty: sections.length > 0,
    dirtySections: sections,
    baseline: draft?.baseline ?? {},
    issues,
    saving: saving === page,
    save: () => save(page, labels),
    discard: () => dispatchDraft({ type: 'discard', page }),
  };
}
