import { useEffect, useRef, useState } from 'react';
import { AppWindow, Gamepad2, Loader2, Plus, Trash2, X } from 'lucide-react';
import type { LayoutSpec } from '../../layout';
import { cx, IconButton } from '../../components/ui';
import type { EditorAction } from '../../editor/state';
import { nextProfileId } from '../../editor/state';

/** A program name as profiles store it: the executable's file name, lower case, with ".exe" added when missing. */
export function programName(text: string): string {
  const name = text.trim().split(/[\\/]/).pop()?.toLowerCase() ?? '';
  return !name || name.includes('.') ? name : `${name}.exe`;
}

/**
 * The profiles of the layout as tabs ("Everyday" = the layout itself), and the settings of the one being edited: its
 * name and the programs that turn it on.
 */
export function ProfileBar({
  spec,
  editing,
  active,
  dispatch,
  onEdit,
  extra,
  loadPrograms,
}: {
  spec: LayoutSpec;
  /** The profile being edited, or null for Everyday. */
  editing: string | null;
  /** The profile the lights use now (null: Everyday), when known. */
  active?: string | null;
  dispatch: (a: EditorAction) => void;
  onEdit(profileId: string | null): void;
  /** More controls for the profile being edited (e.g. trying it now). */
  extra?: React.ReactNode;
  /** Programs with a window open now, to pick one from (executable and window title). */
  loadPrograms?: () => Promise<Array<{ exe: string; title: string }>>;
}) {
  const profiles = spec.profiles ?? [];
  const profile = profiles.find((p) => p.id === editing);
  const [app, setApp] = useState('');

  const add = () => {
    const id = nextProfileId(spec);
    dispatch({ type: 'addProfile', profile: { id, name: `Profile ${profiles.length + 1}`, apps: [], sections: {} } });
    onEdit(id);
  };
  const addApp = (text: string) => {
    const name = programName(text);
    if (!profile || !name || profile.apps.includes(name)) return;
    dispatch({ type: 'updateProfile', profileId: profile.id, patch: { apps: [...profile.apps, name] } });
    setApp('');
  };

  const tab = (id: string | null, label: string) => (
    <button
      key={id ?? 'everyday'}
      type="button"
      role="tab"
      aria-selected={editing === id}
      onClick={() => onEdit(id)}
      className={cx(
        'inline-flex h-7 items-center gap-1.5 whitespace-nowrap rounded-md px-2.5 text-xs font-medium',
        editing === id ? 'bg-surface-3 text-ink shadow-sm' : 'text-ink-muted hover:text-ink',
      )}
    >
      {label}
      {active !== undefined && active === id && <span className="h-1.5 w-1.5 rounded-full bg-ok" title="In use now" />}
    </button>
  );

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Gamepad2 size={15} className="text-ink-faint" aria-hidden />
        <div role="tablist" aria-label="Profiles" className="inline-flex flex-wrap rounded-lg border border-line bg-bg/60 p-0.5">
          {tab(null, 'Everyday')}
          {profiles.map((p) => tab(p.id, p.name || 'Untitled'))}
        </div>
        <button type="button" onClick={add} className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium text-ink-muted hover:bg-surface-3 hover:text-ink">
          <Plus size={13} /> Profile
        </button>
      </div>
      {!profile && profiles.length === 0 && (
        <p className="text-xs text-ink-faint">
          Profiles give strips other jobs while a game or app runs, e.g. the ceiling plays music during one game.
        </p>
      )}
      {profile && (
        <div className="flex flex-wrap items-center gap-2 rounded-xl border border-accent/40 bg-accent/5 p-2">
          <input
            aria-label="Profile name"
            value={profile.name}
            onChange={(e) => dispatch({ type: 'updateProfile', profileId: profile.id, patch: { name: e.target.value }, coalesce: `profile-name-${profile.id}` })}
            className="h-7 w-36 rounded-md border border-line bg-bg px-2 text-xs text-ink"
          />
          <span className="text-xs text-ink-muted">on while</span>
          {profile.apps.map((a) => (
            <span key={a} className="inline-flex h-6 items-center gap-1 rounded-full bg-surface-3 pl-2.5 pr-1 text-xs text-ink">
              {a}
              <button
                type="button"
                aria-label={`Remove ${a}`}
                onClick={() => dispatch({ type: 'updateProfile', profileId: profile.id, patch: { apps: profile.apps.filter((x) => x !== a) } })}
                className="rounded-full p-0.5 text-ink-faint hover:text-ink"
              >
                <X size={12} />
              </button>
            </span>
          ))}
          <form
            className="inline-flex items-center gap-1"
            onSubmit={(e) => {
              e.preventDefault();
              addApp(app);
            }}
          >
            <input
              aria-label="Add a program"
              placeholder={profile.apps.length ? 'or…' : 'witcher3.exe'}
              value={app}
              onChange={(e) => setApp(e.target.value)}
              className="h-7 w-32 rounded-md border border-line bg-bg px-2 text-xs text-ink placeholder:text-ink-faint"
            />
            <button type="submit" disabled={!programName(app)} className="h-7 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-surface-3 disabled:opacity-40">
              Add
            </button>
          </form>
          <span className="text-xs text-ink-muted">runs</span>
          {loadPrograms && <ProgramPicker load={loadPrograms} taken={profile.apps} onPick={addApp} />}
          <div className="ml-auto flex items-center gap-1">
            {extra}
            <IconButton
              label={`Delete ${profile.name}`}
              className="h-7 w-7 hover:text-bad"
              onClick={() => {
                dispatch({ type: 'removeProfile', profileId: profile.id });
                onEdit(null);
              }}
            >
              <Trash2 size={14} />
            </IconButton>
          </div>
        </div>
      )}
    </div>
  );
}

/** "Pick a running program": a list of the programs with a window open now. */
function ProgramPicker({
  load,
  taken,
  onPick,
}: {
  load: () => Promise<Array<{ exe: string; title: string }>>;
  taken: readonly string[];
  onPick(exe: string): void;
}) {
  const [open, setOpen] = useState(false);
  const [programs, setPrograms] = useState<Array<{ exe: string; title: string }> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    let live = true;
    setPrograms(null);
    setError(null);
    load()
      .then((p) => live && setPrograms(p))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    const onDown = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false);
    document.addEventListener('mousedown', onDown);
    return () => {
      live = false;
      document.removeEventListener('mousedown', onDown);
    };
  }, [open, load]);
  const shown = programs?.filter((p) => !taken.includes(p.exe.toLowerCase()));
  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-surface-3"
      >
        <AppWindow size={13} /> Pick a running program
      </button>
      {open && (
        <div role="menu" className="scroll-thin absolute left-0 top-8 z-30 max-h-72 w-80 overflow-y-auto rounded-xl border border-line-strong bg-surface p-1.5 shadow-2xl">
          {error ? (
            <p className="p-2 text-xs text-bad">Couldn’t list the programs: {error}</p>
          ) : !shown ? (
            <p className="flex items-center gap-2 p-2 text-xs text-ink-muted">
              <Loader2 size={13} className="animate-spin" /> Looking…
            </p>
          ) : shown.length === 0 ? (
            <p className="p-2 text-xs text-ink-muted">No other programs with a window. Start the game first, then pick it here.</p>
          ) : (
            shown.map((p) => (
              <button
                key={p.exe}
                type="button"
                role="menuitem"
                onClick={() => {
                  onPick(p.exe);
                  setOpen(false);
                }}
                className="block w-full rounded-lg px-2 py-1.5 text-left hover:bg-surface-3"
              >
                <span className="block truncate text-xs font-medium text-ink">{p.title || p.exe}</span>
                <span className="block truncate text-[11px] text-ink-faint">{p.exe}</span>
              </button>
            ))
          )}
        </div>
      )}
    </div>
  );
}
