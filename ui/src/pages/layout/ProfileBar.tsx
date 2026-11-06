import { useEffect, useRef, useState } from 'react';
import { AppWindow, Loader2, Plus, Power, Trash2, Undo2, X } from 'lucide-react';
import { EVERYDAY, freeName } from '../../control/presets';
import { programName, type LayoutSpec } from '../../layout';
import { Menu } from '../../components/Menu';
import { Button, buttonClass, CHOICE_TRACK, choiceOption, cx, IconButton, useChoiceThumb, INPUT_SMALL } from '../../components/ui';
import type { EditorAction } from '../../editor/state';
import { nextProfileId } from '../../editor/state';


export const PROFILES_HINT =
  'A preset is a saved setup: what each strip shows while it’s in use, for example music on the ceiling while you play. Pick it on Home or Presets, or have a program use it while it runs. The Presets page also sets its Look.';

/** Adds a preset named "Preset n" (a name no other preset has) and returns its id. */
function addProfile(spec: LayoutSpec, dispatch: (a: EditorAction) => void): string {
  const id = nextProfileId(spec);
  dispatch({ type: 'addProfile', profile: { id, name: freeName(spec, `Preset ${(spec.profiles?.length ?? 0) + 1}`), apps: [], sections: {} } });
  return id;
}

/**
 * The presets as compact tabs for the toolbar ("Everyday" is the layout itself) with a button to add one. A dot marks
 * the preset that is on now, when known.
 */
export function ProfileTabs({
  spec,
  editing,
  active,
  dispatch,
  onEdit,
}: {
  spec: LayoutSpec;
  /** The preset being edited, or null for Everyday. */
  editing: string | null;
  /** The preset that is on now (null: Everyday), when known. */
  active?: string | null;
  dispatch: (a: EditorAction) => void;
  onEdit(profileId: string | null): void;
}) {
  const profiles = spec.profiles ?? [];
  const { ref, thumb } = useChoiceThumb(`${editing}|${profiles.map((p) => `${p.id}:${p.name}`).join(',')}`);
  const tab = (id: string | null, label: string) => (
    <button
      key={id ?? 'everyday'}
      type="button"
      role="tab"
      aria-selected={editing === id}
      onClick={() => onEdit(id)}
      className={cx(choiceOption(editing === id), 'h-7 max-w-[10rem] whitespace-nowrap px-3 text-xs')}
    >
      <span className="truncate">{label}</span>
      {active !== undefined && active === id && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-ok" title="In use now" />}
    </button>
  );
  return (
    <div className="flex min-w-0 items-center gap-1">
      <div ref={ref} role="tablist" aria-label="Presets" className={cx(CHOICE_TRACK, 'min-w-0')}>
        {thumb}
        {tab(null, 'Everyday')}
        {profiles.map((p) => tab(p.id, p.name || 'Untitled'))}
      </div>
      <IconButton label="Add a preset" className="h-7 w-7" onClick={() => onEdit(addProfile(spec, dispatch))}>
        <Plus size={15} />
      </IconButton>
    </div>
  );
}

/** Where presets start while there are none: what they are for, and a button to add one. */
export function ProfileIntro({ spec, dispatch, onEdit }: { spec: LayoutSpec; dispatch: (a: EditorAction) => void; onEdit(profileId: string): void }) {
  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold">Presets</h3>
      <p className="text-xs text-ink-faint">{PROFILES_HINT}</p>
      <Button size="sm" icon={<Plus size={14} />} onClick={() => onEdit(addProfile(spec, dispatch))}>
        Add a preset
      </Button>
    </div>
  );
}

/**
 * The preset being edited, as a sentence: "[name] is used while [program ×] [Add program…] [Pick from running apps]
 * is running", with Use / Use Everyday (on the lights now) and Delete.
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
  /** The preset is on now. */
  previewing: boolean;
  /** Why it can't be turned on now (unsaved changes), or undefined. */
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
    <section aria-label={`Preset ${profile.name}`} className="space-y-3">
      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-2 text-[13px] text-ink-muted">
        <input
          aria-label="Preset name"
          value={profile.name}
          onChange={(e) => dispatch({ type: 'updateProfile', profileId: profile.id, patch: { name: e.target.value }, coalesce: `profile-name-${profile.id}` })}
          className={cx('h-8 w-36 rounded-lg', INPUT_SMALL, 'px-2.5 text-[13px] font-medium text-ink')}
        />
        <span>is used while</span>
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
            className={cx('h-8 w-32 rounded-lg', INPUT_SMALL, 'px-2.5 text-xs text-ink placeholder:text-ink-faint')}
          />
          {programName(app) && (
            <button type="submit" className="h-8 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-white/[0.06]">
              Add
            </button>
          )}
        </form>
        {loadPrograms && <ProgramPicker load={loadPrograms} taken={profile.apps} onPick={addApp} />}
        <span>is running</span>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {previewing ? (
          <>
            <span className="text-[13px] text-ink-muted">In use now.</span>
            <Button size="sm" icon={<Undo2 size={14} />} title={`Switch back to ${EVERYDAY}, the strips without a preset`} onClick={() => onPreview(false)}>
              Use {EVERYDAY}
            </Button>
          </>
        ) : (
          <Button size="sm" icon={<Power size={14} />} disabled={!!previewBlocked} title={previewBlocked ?? 'Switch to this preset now'} onClick={() => onPreview(true)}>
            Use
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
          Delete preset
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
  /** Shown as the page's main button (the Presets page) instead of a link-like one. */
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
    return () => {
      live = false;
    };
  }, [open, load]);
  const shown = programs?.filter((p) => !taken.includes(p.exe.toLowerCase()));
  return (
    <div ref={ref}>
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className={
          primary
            ? buttonClass('primary')
            : 'inline-flex h-8 items-center gap-1 rounded-md px-2 text-xs font-medium text-accent-strong hover:bg-white/[0.06]'
        }
      >
        <AppWindow size={primary ? 16 : 13} /> {label}
      </button>
      {open && (
        <Menu anchor={ref} align={primary ? 'left' : 'right'} maxHeight={288} onClose={() => setOpen(false)}>
          {error ? (
            <p className="p-2 text-xs text-bad">Couldn’t list the apps: {error}</p>
          ) : !shown ? (
            <p className="flex items-center gap-2 p-2 text-xs text-ink-muted">
              <Loader2 size={13} className="animate-spin" /> Looking…
            </p>
          ) : shown.length === 0 ? (
            <p className="p-2 text-xs text-ink-muted">No other apps with a window. Start the program first, then pick it here.</p>
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
                className="block w-full rounded-lg px-2 py-1.5 text-left hover:bg-white/[0.06]"
              >
                <span className="block truncate text-xs font-medium text-ink">{p.title || p.exe}</span>
                <span className="block truncate text-[11px] text-ink-faint">{p.exe}</span>
              </button>
            ))
          )}
        </Menu>
      )}
    </div>
  );
}
