import { useEffect, useRef, useState } from 'react';
import { AppWindow, Eye, EyeOff, Gamepad2, Loader2, Plus, Trash2, X } from 'lucide-react';
import type { LayoutSpec } from '../../layout';
import { Button, CHOICE_ACTIVE, cx, IconButton } from '../../components/ui';
import type { EditorAction } from '../../editor/state';
import { nextProfileId } from '../../editor/state';

/** A program name as profiles store it: the executable's file name, lower case, with ".exe" added when missing. */
export function programName(text: string): string {
  const name = text.trim().split(/[\\/]/).pop()?.toLowerCase() ?? '';
  return !name || name.includes('.') ? name : `${name}.exe`;
}

export const PROFILES_HINT =
  'Profiles change what strips show while a game or app runs, for example music on the ceiling during one game. A game added on Games has one too, and also turns on Game mode.';

/** Adds a profile named "Profile n" and returns its id. */
function addProfile(spec: LayoutSpec, dispatch: (a: EditorAction) => void): string {
  const id = nextProfileId(spec);
  dispatch({ type: 'addProfile', profile: { id, name: `Profile ${(spec.profiles?.length ?? 0) + 1}`, apps: [], sections: {} } });
  return id;
}

/**
 * The profiles as compact tabs for the toolbar ("Everyday" is the layout itself) with a button to add one. A dot marks
 * the profile the lights use now, when known.
 */
export function ProfileTabs({
  spec,
  editing,
  active,
  dispatch,
  onEdit,
}: {
  spec: LayoutSpec;
  /** The profile being edited, or null for Everyday. */
  editing: string | null;
  /** The profile the lights use now (null: Everyday), when known. */
  active?: string | null;
  dispatch: (a: EditorAction) => void;
  onEdit(profileId: string | null): void;
}) {
  const profiles = spec.profiles ?? [];
  const tab = (id: string | null, label: string, game = false) => (
    <button
      key={id ?? 'everyday'}
      type="button"
      role="tab"
      aria-selected={editing === id}
      onClick={() => onEdit(id)}
      className={cx(
        'inline-flex h-7 max-w-[10rem] items-center gap-1.5 whitespace-nowrap rounded-md px-2.5 text-xs font-medium',
        editing === id ? CHOICE_ACTIVE : 'text-ink-muted hover:text-ink',
      )}
    >
      {game && <Gamepad2 size={12} className="shrink-0" aria-label="Game" />}
      <span className="truncate">{label}</span>
      {active !== undefined && active === id && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-ok" title="In use now" />}
    </button>
  );
  return (
    <div className="flex min-w-0 items-center gap-1">
      <div role="tablist" aria-label="Profiles" className="inline-flex min-w-0 flex-wrap rounded-lg border border-line-input bg-bg/60 p-0.5">
        {tab(null, 'Everyday')}
        {profiles.map((p) => tab(p.id, p.name || 'Untitled', p.game === true))}
      </div>
      <IconButton label="Add a profile" className="h-7 w-7" onClick={() => onEdit(addProfile(spec, dispatch))}>
        <Plus size={15} />
      </IconButton>
    </div>
  );
}

/** Where profiles start while there are none: what they are for, and a button to add one. */
export function ProfileIntro({ spec, dispatch, onEdit }: { spec: LayoutSpec; dispatch: (a: EditorAction) => void; onEdit(profileId: string): void }) {
  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold">Profiles</h3>
      <p className="text-xs text-ink-faint">{PROFILES_HINT}</p>
      <Button size="sm" icon={<Plus size={14} />} onClick={() => onEdit(addProfile(spec, dispatch))}>
        Add a profile
      </Button>
    </div>
  );
}

/**
 * The profile being edited, as a sentence: "[name] turns on when [program ×] [Add program…] [Pick from running apps]
 * is running", whether it's a game's (added on the Games page), with Preview (show it on the lights now) and Delete.
 */
export function ProfileSettings({
  spec,
  profileId,
  dispatch,
  onEdit,
  loadPrograms,
  previewing,
  previewBlocked,
  previewError,
  onPreview,
}: {
  spec: LayoutSpec;
  profileId: string;
  dispatch: (a: EditorAction) => void;
  onEdit(profileId: string | null): void;
  /** Programs with a window open now, to pick one from (executable and window title). */
  loadPrograms?: () => Promise<Array<{ exe: string; title: string }>>;
  /** The lights show this profile because of Preview. */
  previewing: boolean;
  /** Why Preview can't run now (unsaved changes), or undefined. */
  previewBlocked?: string;
  previewError?: string | null;
  onPreview(on: boolean): void;
}) {
  const profile = spec.profiles?.find((p) => p.id === profileId);
  const [app, setApp] = useState('');
  if (!profile) return null;

  const addApp = (text: string) => {
    const name = programName(text);
    if (!name || profile.apps.includes(name)) return;
    dispatch({ type: 'updateProfile', profileId: profile.id, patch: { apps: [...profile.apps, name] } });
    setApp('');
  };

  return (
    <section aria-label={`Profile ${profile.name}`} className="space-y-3">
      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-2 text-[13px] text-ink-muted">
        <input
          aria-label="Profile name"
          value={profile.name}
          onChange={(e) => dispatch({ type: 'updateProfile', profileId: profile.id, patch: { name: e.target.value }, coalesce: `profile-name-${profile.id}` })}
          className="h-8 w-36 rounded-md border border-line-input bg-bg px-2 text-[13px] font-medium text-ink"
        />
        <span>turns on when</span>
        {profile.apps.map((a) => (
          <span key={a} className="inline-flex h-7 items-center gap-1 rounded-full bg-surface-3 pl-2.5 pr-1 text-xs text-ink">
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
            aria-label="Add program"
            placeholder="Add program…"
            value={app}
            onChange={(e) => setApp(e.target.value)}
            className="h-8 w-32 rounded-md border border-line-input bg-bg px-2 text-xs text-ink placeholder:text-ink-faint"
          />
          {programName(app) && (
            <button type="submit" className="h-8 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-surface-3">
              Add
            </button>
          )}
        </form>
        {loadPrograms && <ProgramPicker load={loadPrograms} taken={profile.apps} onPick={addApp} />}
        <span>is running</span>
      </div>
      {profile.game && (
        <p className="flex items-center gap-1.5 text-xs text-ink-faint">
          <Gamepad2 size={13} aria-hidden /> One of your games: Game mode is on while it runs (Games). Deleting the profile removes the game too.
        </p>
      )}
      <div className="flex flex-wrap items-center gap-2">
        {previewing ? (
          <Button size="sm" icon={<EyeOff size={14} />} onClick={() => onPreview(false)}>
            Stop preview
          </Button>
        ) : (
          <Button size="sm" icon={<Eye size={14} />} disabled={!!previewBlocked} title={previewBlocked ?? 'Show this profile on your lights now'} onClick={() => onPreview(true)}>
            Preview
          </Button>
        )}
        <Button
          size="sm"
          variant="ghost"
          icon={<Trash2 size={14} />}
          className="ml-auto hover:text-bad"
          onClick={() => {
            dispatch({ type: 'removeProfile', profileId: profile.id });
            onEdit(null);
          }}
        >
          Delete profile
        </Button>
      </div>
      {previewError && <p className="text-xs text-bad">{previewError}</p>}
    </section>
  );
}

/** "Pick from running apps": a list of the programs with a window open now. */
export function ProgramPicker({
  load,
  taken,
  onPick,
  label = 'Pick from running apps',
  primary = false,
}: {
  load: () => Promise<Array<{ exe: string; title: string }>>;
  taken: readonly string[];
  onPick(exe: string, title: string): void;
  label?: string;
  /** Shown as the page's main button (the Games page) instead of a link-like one. */
  primary?: boolean;
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
        className={
          primary
            ? 'inline-flex h-10 items-center gap-2 rounded-lg bg-accent px-4 text-sm font-medium text-accent-ink hover:bg-accent-strong'
            : 'inline-flex h-8 items-center gap-1 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-surface-3'
        }
      >
        <AppWindow size={primary ? 16 : 13} /> {label}
      </button>
      {open && (
        <div role="menu" className={cx('scroll-thin absolute z-30 max-h-72 w-72 overflow-y-auto rounded-xl border border-line-strong bg-surface p-1.5 shadow-2xl', primary ? 'left-0 top-11' : 'right-0 top-9')}>
          {error ? (
            <p className="p-2 text-xs text-bad">Couldn’t list the apps: {error}</p>
          ) : !shown ? (
            <p className="flex items-center gap-2 p-2 text-xs text-ink-muted">
              <Loader2 size={13} className="animate-spin" /> Looking…
            </p>
          ) : shown.length === 0 ? (
            <p className="p-2 text-xs text-ink-muted">No other apps with a window. Start the game first, then pick it here.</p>
          ) : (
            shown.map((p) => (
              <button
                key={p.exe}
                type="button"
                role="menuitem"
                onClick={() => {
                  onPick(p.exe, p.title);
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
