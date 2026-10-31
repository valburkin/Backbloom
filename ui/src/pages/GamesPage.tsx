import { useCallback, useState } from 'react';
import { Gamepad2, Loader2, Plus, Save, SlidersHorizontal, Trash2 } from 'lucide-react';
import { Badge, Button, IconButton } from '../components/ui';
import { Callout, Section } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { useToast } from '../app/toast';
import { hrefFor, hrefForProfile } from '../app/router';
import { editorReducer, nextProfileId } from '../editor/state';
import { ProgramPicker, programName } from './layout/ProfileBar';
import type { EditorAction } from '../editor/state';
import type { LayoutSpec, Profile } from '../layout';

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

/** A game's name from its window title or, without one, its program: "witcher3.exe" → "witcher3". */
function gameName(exe: string, title: string): string {
  return title.trim() || exe.replace(/\.exe$/i, '');
}

/** What adding a program does: the edit, and what to tell the user. */
export interface GameChange {
  action: EditorAction | null;
  message: string;
}

/**
 * Adding a game: marks the profile that already lists the program (whatever its case) as a game, else adds a profile
 * for it (no strip changes, so the lights keep the layout's sections). Nothing to do when it already is a game.
 */
export function addGame(spec: LayoutSpec, exe: string, title: string): GameChange | null {
  const program = programName(exe);
  if (!program) return null;
  const existing = spec.profiles?.find((p) => p.apps.some((a) => a.toLowerCase() === program));
  if (existing?.game) return { action: null, message: `${existing.name || program} is already in your games` };
  if (existing) return { action: { type: 'updateProfile', profileId: existing.id, patch: { game: true } }, message: `${existing.name || program} added` };
  const name = gameName(program, title);
  return { action: { type: 'addProfile', profile: { id: nextProfileId(spec), name, apps: [program], game: true, sections: {} } }, message: `${name} added` };
}

/** Removing a game: its profile stays when it changes strips (then it's no game any more), else it goes. */
export function removeGame(profile: Profile): GameChange {
  const name = profile.name || 'The game';
  const changesStrips = Object.values(profile.sections).some((list) => list.some((s) => s !== null));
  return changesStrips
    ? { action: { type: 'updateProfile', profileId: profile.id, patch: { game: false } }, message: `${name} is no longer a game. What its strips show stays on Layout.` }
    : { action: { type: 'removeProfile', profileId: profile.id }, message: `${name} removed` };
}

/**
 * The games that switch to Game mode while they run (the layout's profiles marked as a game). In Game mode's sidebar,
 * and linked from Ambient's Home, so games can be added without switching the lights to Game mode. Each change is
 * saved at once with the layout, so it needs the layout without unsaved edits.
 */
export function GamesPage() {
  const { state, dispatch, load, dirty, blockers, saving, save, reload } = useEditor();
  const { api, serverInfo, auth } = useConnection();
  const toast = useToast();
  const loadPrograms = useCallback(() => api.runningPrograms(), [api]);
  const [typing, setTyping] = useState(false);
  const [typed, setTyped] = useState('');
  const spec = state.spec;
  const games = (spec.profiles ?? []).filter((p) => p.game);
  const mode = serverInfo?.mode;
  const playing = mode?.gameProfile ? serverInfo?.profile?.id : undefined;
  const blocked = dirty ? 'unsaved' : blockers.length ? 'layout' : null;

  // the edit and its save together: the page may go away before an effect would run (a game starting or quitting)
  const apply = (change: GameChange | null) => {
    if (!change) return;
    if (!change.action) {
      toast('info', change.message);
      return;
    }
    const next = editorReducer(state, change.action).spec;
    dispatch(change.action);
    void save(change.message, next);
  };
  const add = (exe: string, title: string) => apply(addGame(spec, exe, title));

  return (
    <PageFrame
      title="Games"
      subtitle={`Game mode turns on by itself while one of these runs${mode?.saved === 'game' ? '.' : ', and Ambient comes back when it quits.'}`}
    >
      {auth !== 'admin' ? (
        <Callout>Sign in on this PC to see and change the games.</Callout>
      ) : load.status !== 'ready' ? (
        <p className="flex items-center gap-2 text-[13px] text-ink-muted">
          <Loader2 size={15} className="animate-spin" /> Loading your games…
        </p>
      ) : (
        <>
          {blocked === 'unsaved' && (
            <Callout tone="warn">
              <div className="flex flex-wrap items-center gap-3">
                <span className="flex-1">
                  The layout has changes that aren’t saved, and games are saved with it. Save them, or discard them on{' '}
                  <a className={LINK} href={hrefFor('layout')}>
                    Layout
                  </a>
                  .
                </span>
                <Button size="sm" icon={<Save size={14} />} disabled={saving || blockers.length > 0} onClick={() => void save()}>
                  Save
                </Button>
                <Button size="sm" variant="ghost" disabled={saving} onClick={reload}>
                  Discard
                </Button>
              </div>
            </Callout>
          )}
          {blocked === 'layout' && (
            <Callout tone="warn">
              Set up your lights on{' '}
              <a className={LINK} href={hrefFor('layout')}>
                Layout
              </a>{' '}
              first: games are saved with it.
            </Callout>
          )}

          <Section title="Your games" icon={<Gamepad2 size={18} />} actions={saving && <Loader2 size={16} className="animate-spin text-ink-faint" />}>
            {games.length === 0 ? (
              <p className="text-[13px] text-ink-muted">No games yet. Start your game, come back here and pick it with Add a running game.</p>
            ) : (
              <ul className="divide-y divide-line">
                {games.map((g) => (
                  <li key={g.id} className="flex flex-wrap items-center gap-3 py-3 first:pt-0 last:pb-0">
                    <div className="min-w-0 flex-1">
                      <p className="flex items-center gap-2 text-sm font-medium text-ink">
                        <span className="truncate">{g.name || 'Untitled'}</span>
                        {playing === g.id && <Badge tone="ok">Playing now</Badge>}
                      </p>
                      <p className="truncate text-xs text-ink-faint">{g.apps.join(', ') || 'No program yet'}</p>
                    </div>
                    <a
                      href={hrefForProfile(g.id)}
                      className="inline-flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-xs font-medium text-ink-muted hover:bg-surface-3 hover:text-ink"
                      title="What each strip shows while this game runs"
                    >
                      <SlidersHorizontal size={14} /> Edit strips
                    </a>
                    <IconButton label={`Remove ${g.name}`} disabled={!!blocked || saving} onClick={() => apply(removeGame(g))}>
                      <Trash2 size={15} />
                    </IconButton>
                  </li>
                ))}
              </ul>
            )}
            <div className="space-y-3 border-t border-line pt-4">
              {!blocked && !saving && (
                <ProgramPicker load={loadPrograms} taken={games.flatMap((g) => g.apps)} onPick={add} label="Add a running game" primary />
              )}
              {typing ? (
                <form
                  className="flex flex-wrap items-center gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    add(typed, '');
                    setTyped('');
                  }}
                >
                  <input
                    aria-label="Program name"
                    placeholder="Program name, for example game.exe"
                    value={typed}
                    autoFocus
                    disabled={!!blocked || saving}
                    onChange={(e) => setTyped(e.target.value)}
                    className="h-9 w-72 rounded-md border border-line-input bg-bg px-3 text-sm text-ink placeholder:text-ink-faint disabled:opacity-40"
                  />
                  <Button size="sm" type="submit" icon={<Plus size={14} />} disabled={!programName(typed) || !!blocked || saving}>
                    Add
                  </Button>
                </form>
              ) : (
                <button type="button" className={`block text-[13px] ${LINK}`} onClick={() => setTyping(true)}>
                  Or type its program name
                </button>
              )}
            </div>
          </Section>
        </>
      )}
    </PageFrame>
  );
}
