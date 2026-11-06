import { useCallback, useEffect, useRef, useState } from 'react';
import { Bookmark, Loader2, Plus, Save, SlidersHorizontal, Trash2, X } from 'lucide-react';
import { Badge, Button, Card, cx, IconButton, StatusDot, INPUT_SMALL } from '../components/ui';
import { Callout, Select } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { SWATCHES } from '../components/ColorPicker';
import { useConnection } from '../app/connection';
import { useConfig } from '../app/config';
import { useEditor } from '../app/editor';
import { useAction } from '../app/useAction';
import { OwnEdits } from '../app/ownEdits';
import { useToast } from '../app/toast';
import { hrefFor, hrefForProfile } from '../app/router';
import { editorReducer, type EditorAction } from '../editor/state';
import { ProgramPicker } from './layout/ProfileBar';
import { effectLabel, splitEffects } from '../control/modes';
import type { MainPick } from '../control/show';
import { usePick } from '../control/usePick';
import { EVERYDAY, gamingPreset, newPreset, presetStatus, statusText, switchToEveryday, whatPlays, type PresetStatus, type WhatPlays } from '../control/presets';
import { presetInUse, setupLookValues } from '../control/setup';
import { applyLook, detectLook, detectProfileLook, LOOKS, profileLook, type LookId } from '../settings/intensity';
import { programName, type LayoutSpec, type Profile, type Rgb } from '../layout';

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

/** "What plays" as the select's value: edges, average, color, customized, or `effect:<name>`. */
function playValue(play: WhatPlays): string {
  return play.kind === 'effect' ? `effect:${play.effect}` : play.kind;
}

/** What a setup plays, as a list; a choice is written into every strip of the setup (Off strips stay off). */
function WhatPlaysField({ play, hasStrips, effects, editHref, disabled, onPick }: { play: WhatPlays; hasStrips: boolean; effects: readonly string[]; editHref: string; disabled: boolean; onPick(p: MainPick): void }) {
  const { music, regular } = splitEffects(effects.map((name) => ({ name })));
  const options = [
    { value: 'edges', label: 'Screen edges' },
    ...(hasStrips ? [{ value: 'average', label: 'Screen average' }] : []),
    { value: 'color', label: 'A color' },
    ...regular.map((n) => ({ value: `effect:${n}`, label: effectLabel(n), group: 'Effects' })),
    ...music.map((n) => ({ value: `effect:${n}`, label: effectLabel(n), group: 'Music' })),
    ...(play.kind === 'customized' ? [{ value: 'customized', label: 'Customized in Layout', disabled: true }] : []),
  ];
  const choose = (v: string) => {
    if (v === 'edges' || v === 'average') onPick({ kind: v });
    else if (v === 'color') onPick({ kind: 'color', color: [...SWATCHES[0].rgb] as Rgb });
    else if (v.startsWith('effect:')) onPick({ kind: 'effect', effect: v.slice('effect:'.length) });
  };
  return (
    <div className="space-y-2">
      <Select
        label="What plays"
        value={playValue(play)}
        options={options}
        disabled={disabled || !hasStrips}
        hint={!hasStrips ? 'Set up your strips in Layout first.' : play.kind === 'customized' ? 'Its strips differ, set in Layout. Pick one to play it on every strip.' : 'On every strip; Off strips stay off.'}
        onChange={choose}
      />
      <a href={editHref} className={cx('inline-flex items-center gap-1.5 text-xs font-medium', LINK)} title="Choose what each strip shows">
        <SlidersHorizontal size={13} /> Edit strips in Layout
      </a>
      {play.kind === 'color' && (
        <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label="Color">
          {SWATCHES.map((s) => {
            const on = s.rgb.every((v, i) => v === play.color[i]);
            return (
              <button
                key={s.name}
                type="button"
                role="radio"
                aria-checked={on}
                title={s.name}
                aria-label={s.name}
                disabled={disabled}
                onClick={() => onPick({ kind: 'color', color: [...s.rgb] as Rgb })}
                className={cx('h-6 w-6 rounded-full border-2', on ? 'border-white' : 'border-transparent')}
                style={{ background: `rgb(${s.rgb.join(',')})` }}
              />
            );
          })}
        </div>
      )}
    </div>
  );
}

/** A preset's Look: the same as Everyday's (none of its own), one of the five, or values of its own (Colors). */
function LookField({ preset, disabled, onChange }: { preset: Profile; disabled: boolean; onChange(look: Profile['look']): void }) {
  const current = detectProfileLook(preset.look);
  const options = [
    { value: 'same', label: `Same as ${EVERYDAY}` },
    ...LOOKS.map((l) => ({ value: l.id, label: l.label })),
    ...(current === 'custom' ? [{ value: 'custom', label: 'Custom', disabled: true }] : []),
  ];
  const shown = current === null ? 'same' : current;
  return (
    <Select
      label="Look"
      value={shown}
      options={options}
      disabled={disabled}
      hint={current === null ? `${EVERYDAY}’s Look, whatever it is.` : current === 'custom' ? 'Fine-tuned on Colors while it was in use.' : (LOOKS.find((l) => l.id === current)?.description ?? '')}
      onChange={(v) => v !== 'custom' && onChange(v === 'same' ? undefined : profileLook(v as LookId))}
    />
  );
}

/** Everyday's Look: the saved one (Home's five, or Custom from Colors). */
function EverydayLookField({ disabled }: { disabled: boolean }) {
  const { config } = useConfig();
  const { api } = useConnection();
  const { run, busy } = useAction();
  const current = detectLook(config);
  const options = [...LOOKS.map((l) => ({ value: l.id, label: l.label })), ...(current === 'custom' ? [{ value: 'custom', label: 'Custom', disabled: true }] : [])];
  return (
    <Select
      label="Look"
      value={current ?? 'natural'}
      options={options}
      disabled={disabled || !config || busy === 'look'}
      hint={current === 'custom' ? 'Fine-tuned on Colors.' : (LOOKS.find((l) => l.id === current)?.description ?? '')}
      onChange={(v) => {
        if (config && v !== 'custom') void run('look', 'Couldn’t change the Look', () => api.setConfig(applyLook(config, v as LookId)));
      }}
    />
  );
}

function TurnsOnWith({ preset, gaming, disabled, loadPrograms, onChange }: { preset: Profile; gaming: boolean; disabled: boolean; loadPrograms: () => Promise<Array<{ exe: string; title: string }>>; onChange(apps: string[]): void }) {
  const [typing, setTyping] = useState(false);
  const [typed, setTyped] = useState('');
  const add = (text: string) => {
    const name = programName(text);
    if (name && !preset.apps.includes(name)) onChange([...preset.apps, name]);
  };
  return (
    <div className="space-y-2">
      <p className="text-sm font-medium text-ink">Used while running</p>
      {preset.apps.length ? (
        <div className="flex flex-wrap gap-1.5">
          {preset.apps.map((a) => (
            <span key={a} className="inline-flex h-7 items-center gap-1 rounded-full bg-surface-3 pl-2.5 pr-1 text-xs text-ink">
              {a}
              <button type="button" aria-label={`Remove ${a}`} disabled={disabled} onClick={() => onChange(preset.apps.filter((x) => x !== a))} className="rounded-full p-0.5 text-ink-faint hover:text-ink">
                <X size={12} />
              </button>
            </span>
          ))}
        </div>
      ) : (
        <p className="text-xs text-ink-faint">{gaming ? 'Add your game: Gaming is used by itself while it runs.' : 'No program: used when you pick it.'}</p>
      )}
      {!disabled && (
        <div className="flex flex-wrap items-center gap-1">
          <ProgramPicker load={loadPrograms} taken={preset.apps} onPick={(exe) => add(exe)} label="Add a running program" />
          {typing ? (
            <form
              className="flex items-center gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                add(typed);
                setTyped('');
                setTyping(false);
              }}
            >
              <input
                aria-label="Program name"
                placeholder="game.exe"
                value={typed}
                autoFocus
                onChange={(e) => setTyped(e.target.value)}
                className={cx('h-8 w-32 rounded-lg', INPUT_SMALL, 'px-2.5 text-xs text-ink placeholder:text-ink-faint')}
              />
              <Button size="xs" type="submit" disabled={!programName(typed)}>
                Add
              </Button>
            </form>
          ) : (
            <button type="button" className={`h-8 px-2 text-xs ${LINK}`} onClick={() => setTyping(true)}>
              Type its name
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/** The name, edited in place and stored when the field is left (or Enter). */
function NameField({ name, disabled, onChange }: { name: string; disabled: boolean; onChange(name: string): void }) {
  const [text, setText] = useState(name);
  useEffect(() => setText(name), [name]);
  const commit = () => {
    const t = text.trim();
    if (t && t !== name) onChange(t);
    else setText(name);
  };
  return (
    <input
      aria-label="Preset name"
      value={text}
      disabled={disabled}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') e.currentTarget.blur();
        if (e.key === 'Escape') {
          setText(name);
          e.currentTarget.blur();
        }
      }}
      className="h-9 min-w-[6rem] max-w-[16rem] rounded-lg border [field-sizing:content] border-transparent bg-transparent px-2 font-display text-base font-semibold text-ink hover:border-line-input focus:border-accent"
    />
  );
}

function InUseBadge() {
  return (
    <Badge tone="accent" className="gap-1.5 text-xs font-medium tracking-normal">
      <StatusDot tone="ok" live /> In use
    </Badge>
  );
}

/** Everyday, the setup without a preset: the layout's own strips and the saved Look; in use while no preset is. */
function EverydayCard({
  spec,
  inUse,
  dismissed,
  effects,
  disabled,
  switching,
  onPick,
  onUse,
}: {
  spec: LayoutSpec;
  inUse: boolean;
  dismissed: Profile | undefined;
  effects: readonly string[];
  disabled: boolean;
  switching: boolean;
  onPick(p: MainPick): void;
  onUse(): void;
}) {
  return (
    <Card className={cx('p-5', inUse && 'edge-brand')}>
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="flex h-9 items-center px-2 font-display text-base font-semibold">{EVERYDAY}</h2>
        {inUse && <InUseBadge />}
        <span className="text-[13px] text-ink-muted">
          Your strips as Layout has them, in use when no preset is.
          {inUse && dismissed && ` ${dismissed.name}: ${statusText(dismissed, 'dismissed')?.replace(/^N/, 'n')}.`}
        </span>
        {!inUse && (
          <Button size="sm" className="ml-auto" disabled={switching} title={`Switch to ${EVERYDAY} now`} onClick={onUse}>
            Use
          </Button>
        )}
      </div>
      <div className="mt-4 grid gap-5 md:grid-cols-3">
        <WhatPlaysField play={whatPlays(spec)} hasStrips={spec.strips.length > 0} effects={effects} editHref={hrefFor('layout')} disabled={disabled} onPick={onPick} />
        <EverydayLookField disabled={disabled} />
      </div>
    </Card>
  );
}

function PresetCard({
  spec,
  preset,
  status,
  effects,
  disabled,
  switching,
  loadPrograms,
  update,
  onPick,
  onRemove,
  onUse,
}: {
  spec: LayoutSpec;
  preset: Profile;
  status: PresetStatus;
  effects: readonly string[];
  disabled: boolean;
  switching: boolean;
  loadPrograms: () => Promise<Array<{ exe: string; title: string }>>;
  update(patch: Partial<Omit<Profile, 'id'>>): void;
  onPick(p: MainPick): void;
  onRemove(): void;
  onUse(): void;
}) {
  const inUse = status === 'program' || status === 'hand';
  const note = statusText(preset, status);
  return (
    <Card className={cx('p-5', inUse && 'edge-brand')}>
      <div className="flex flex-wrap items-center gap-2">
        <NameField name={preset.name} disabled={disabled} onChange={(name) => update({ name })} />
        {inUse && <InUseBadge />}
        {note && <span className="text-[13px] text-ink-muted">{note}</span>}
        <div className="ml-auto flex items-center gap-1">
          {!inUse && (
            <Button size="sm" disabled={switching || disabled} title="Switch to this preset now" onClick={onUse}>
              Use
            </Button>
          )}
          <IconButton label={`Remove ${preset.name}`} disabled={disabled} onClick={onRemove}>
            <Trash2 size={15} />
          </IconButton>
        </div>
      </div>
      <div className="mt-4 grid gap-5 md:grid-cols-3">
        <WhatPlaysField
          play={whatPlays(spec, preset)}
          hasStrips={spec.strips.length > 0}
          effects={effects}
          editHref={hrefForProfile(preset.id)}
          disabled={disabled}
          onPick={onPick}
        />
        <LookField preset={preset} disabled={disabled} onChange={(look) => update({ look })} />
        <TurnsOnWith preset={preset} gaming={preset.id === gamingPreset(spec)?.id} disabled={disabled} loadPrograms={loadPrograms} onChange={(apps) => update({ apps })} />
      </div>
    </Card>
  );
}

/**
 * Presets (the layout's profiles): saved setups of what plays, a Look, and programs that turn them on. Each change is
 * saved with the layout at once (shortly after the last one; leaving the page saves what is still waiting), so it
 * needs the layout without other unsaved edits. What plays is picked as on Effects (usePick).
 */
export function PresetsPage() {
  const { state, dispatch, load, dirty, blockers, saving, save, quietSave, reload } = useEditor();
  const { api, serverInfo, auth } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const toast = useToast();
  const pick = usePick();
  const loadPrograms = useCallback(() => api.runningPrograms(), [api]);
  const spec = state.spec;
  const presets = spec.profiles ?? [];
  const effects = (serverInfo?.effects ?? []).map((e) => e.name);
  // edits made here are saved by the page (OwnEdits); unsaved edits from Layout block it
  const saveRef = useRef(quietSave);
  saveRef.current = quietSave;
  const [mine] = useState(() => new OwnEdits((spec) => saveRef.current(spec)));
  const blocked = dirty && !mine.own ? 'unsaved' : blockers.length ? 'layout' : null;

  useEffect(() => {
    if (mine.own && dirty && !saving && blockers.length === 0 && mine.failed !== state.spec) mine.schedule(state.spec);
    else mine.cancel();
  }, [mine, dirty, saving, blockers.length, state.spec]);
  // leaving the page: what is still waiting is saved now
  useEffect(() => () => mine.flush(), [mine]);
  const saved = (edit: number) => mine.saved(edit);

  const apply = (action: EditorAction | EditorAction[], message?: string) => {
    const edit = mine.edit();
    const actions = Array.isArray(action) ? action : [action];
    if (message) {
      // added or removed: saved right away, with a toast
      const next = actions.reduce((st, a) => editorReducer(st, a), state).spec;
      for (const a of actions) dispatch(a);
      void quietSave(next).then((ok) => {
        if (!ok) return;
        saved(edit);
        toast('ok', message);
      });
    } else for (const a of actions) dispatch(a);
  };
  const add = () => {
    const inUse = presetInUse(spec, serverInfo?.profile);
    const preset = newPreset(spec, { profileId: inUse?.id ?? null, look: setupLookValues(config, inUse) });
    apply({ type: 'addProfile', profile: preset }, `${preset.name} added`);
  };
  /**
   * What plays, for Everyday (null) or a preset: as on Effects (screen sync and sound input turned on as needed). It is
   * saved by itself (a mirror, not an edit of the page's own: it doesn't count as unsaved while it is saved).
   */
  const pickFor = (preset: string | null) => (p: MainPick) => void pick(p, { preset });
  const active = serverInfo?.profile ?? null;
  const dismissed = serverInfo?.dismissedProfile ?? null;

  return (
    <PageFrame
      title="Presets"
      subtitle="Saved setups: what plays and how it looks. Pick one here or on Home, or let a program use it while it runs."
      wide
      actions={
        auth === 'admin' &&
        load.status === 'ready' &&
        !blocked && (
          <Button variant={presets.length ? 'secondary' : 'primary'} icon={<Plus size={15} />} disabled={saving} onClick={add}>
            New preset
          </Button>
        )
      }
    >
      {auth !== 'admin' ? (
        <Callout>Sign in on this PC to see and change the presets.</Callout>
      ) : load.status !== 'ready' ? (
        <p className="flex items-center gap-2 text-[13px] text-ink-muted">
          <Loader2 size={15} className="animate-spin" /> Loading your presets…
        </p>
      ) : (
        <>
          {blocked === 'unsaved' && (
            <Callout tone="warn">
              <div className="flex flex-wrap items-center gap-3">
                <span className="flex-1">
                  The layout has changes that aren’t saved, and presets are saved with it. Save them, or discard them on{' '}
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
              first: presets are saved with it.
            </Callout>
          )}
          <div className="space-y-4">
              <EverydayCard
                spec={spec}
                inUse={!active}
                dismissed={dismissed ? presets.find((p) => p.id === dismissed.id) : undefined}
                effects={effects}
                disabled={!!blocked}
                switching={busy === 'preset'}
                onPick={pickFor(null)}
                onUse={() => void run('preset', `Couldn’t switch to ${EVERYDAY}`, () => switchToEveryday(api))}
              />
              {presets.map((p) => (
                <PresetCard
                  key={p.id}
                  spec={spec}
                  preset={p}
                  status={presetStatus(p.id, active, dismissed)}
                  effects={effects}
                  disabled={!!blocked}
                  switching={busy === 'preset'}
                  loadPrograms={loadPrograms}
                  update={(patch) => apply({ type: 'updateProfile', profileId: p.id, patch, coalesce: patch.name !== undefined ? `preset-name-${p.id}` : undefined })}
                  onPick={pickFor(p.id)}
                  onRemove={() => apply({ type: 'removeProfile', profileId: p.id }, `${p.name || 'The preset'} removed`)}
                  onUse={() => void run('preset', `Couldn’t switch to ${p.name}`, () => api.setProfile(p.id))}
                />
              ))}
            {presets.length === 0 && (
            <Card className="p-6">
              <div className="flex items-start gap-4">
                <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-accent/15 text-accent-strong">
                  <Bookmark size={22} />
                </div>
                <div className="min-w-0 space-y-1.5">
                  <h2 className="text-base font-semibold">No presets</h2>
                  <p className="text-[13px] text-ink-muted">
                    A preset remembers what plays on your strips and how it looks, and can be used by itself while a program runs. For example{' '}
                    <span className="text-ink">Gaming: screen edges, Intense, used while your game runs</span>. Without a preset, your lights use {EVERYDAY}: your strips as Layout has them.
                  </p>
                  <p className="text-[13px] text-ink-muted">New preset starts from the setup in use: what it plays and its Look.</p>
                </div>
              </div>
            </Card>
            )}
          </div>
          {saving && (
            <p className="flex items-center gap-2 text-xs text-ink-faint">
              <Loader2 size={13} className="animate-spin" /> Saving…
            </p>
          )}
        </>
      )}
    </PageFrame>
  );
}
