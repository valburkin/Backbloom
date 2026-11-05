import { useMemo, useState, type ReactNode } from 'react';
import { AudioLines, MonitorPlay, Palette, Play, Search, ScreenShare, SlidersHorizontal, Sparkles, SunMedium } from 'lucide-react';
import { Badge, ChoiceCard, cx, StatusDot } from '../components/ui';
import { Callout, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { ColorPicker } from '../components/ColorPicker';
import { EffectThumb } from '../components/EffectThumb';
import type { RpcApi } from '../api/rpcApi';
import { usePageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { hrefFor } from '../app/router';
import { effectLabel, splitEffects } from '../control/modes';
import { PlayingPreview } from '../control/PlayingPreview';
import { playLabel, type WhatPlays } from '../control/presets';
import { SetupSwitch } from '../control/SetupSwitch';
import { isPicked, type MainPick } from '../control/show';
import { useColorPick, usePick, useSetupInUse } from '../control/usePick';
import type { Rgb } from '../color/color';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'soundEffect.device': 'Listen to',
};

type GridProps = {
  names: string[];
  descriptions: ReadonlyMap<string, string>;
  api: RpcApi;
  layoutKey: string;
  running?: string;
  onPlay: (n: string) => void;
  busy: boolean;
  query: string;
};

function EffectGrid({ names, descriptions, api, layoutKey, running, onPlay, busy, query }: GridProps) {
  if (!names.length) return <p className="text-[13px] text-ink-faint">{query ? `No effects match “${query}”.` : 'No effects found.'}</p>;
  return (
    <ul className="grid grid-cols-2 gap-2.5 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
      {names.map((n) => {
        const active = n === running;
        const description = descriptions.get(n);
        return (
          <li key={n}>
            <button
              type="button"
              disabled={busy}
              aria-pressed={active}
              title={description || undefined}
              onClick={() => onPlay(n)}
              className={cx(
                'group flex h-full w-full flex-col gap-1.5 rounded-xl border p-1.5 text-left transition-colors',
                active ? 'border-accent bg-accent/10' : 'border-line bg-surface-2 hover:border-line-strong',
              )}
            >
              <div className="relative">
                <EffectThumb api={api} name={n} layoutKey={layoutKey} />
                <span
                  className={cx(
                    'absolute right-1 top-1 flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[11px] font-medium backdrop-blur',
                    active ? 'bg-black/60 text-white' : 'bg-black/50 text-white/90 opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100',
                  )}
                >
                  {active ? <StatusDot tone="ok" live /> : <Play size={11} />}
                  {active ? 'Playing' : 'Play'}
                </span>
              </div>
              <div className="min-w-0 px-1 pb-0.5">
                <div className="truncate text-[13px] font-medium">{effectLabel(n)}</div>
                {description && <p className="mt-0.5 line-clamp-2 text-xs leading-snug text-ink-faint">{description}</p>}
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

const SCREEN_PICKS: ReadonlyArray<{ pick: MainPick & { kind: 'edges' | 'average' }; description: string; icon: ReactNode }> = [
  { pick: { kind: 'edges' }, description: 'Each strip follows the part of the screen next to it', icon: <ScreenShare size={18} /> },
  { pick: { kind: 'average' }, description: 'The whole screen’s average color, for soft light', icon: <SunMedium size={18} /> },
];

/** The Screen section: Screen edges or Screen average, the one the setup plays highlighted. */
function ScreenSection({ plays, onPick, busy }: { plays: WhatPlays; onPick: (p: MainPick) => void; busy: boolean }) {
  return (
    <Section title="Screen" icon={<MonitorPlay size={18} />} description="Your lights follow what’s on your screen.">
      <div role="radiogroup" aria-label="Screen" className="grid gap-2 sm:grid-cols-2">
        {SCREEN_PICKS.map(({ pick, description, icon }) => (
          <ChoiceCard key={pick.kind} active={isPicked(plays, pick)} title={playLabel(pick)} description={description} icon={icon} disabled={busy} onClick={() => onPick(pick)} />
        ))}
      </div>
    </Section>
  );
}

/** One color on every strip: dragging shows it live, letting go saves it to the setup. */
function ColorSection({ plays, busy }: { plays: WhatPlays; busy: boolean }) {
  const { live, commit } = useColorPick();
  const [color, setColor] = useState<Rgb>([255, 100, 0]);
  const [dragging, setDragging] = useState(false);
  const picked = plays.kind === 'color';
  const shown = picked && !dragging ? plays.color : color;
  return (
    <Section
      title="Color"
      icon={<Palette size={18} />}
      description="One color on every strip. Drag to try it on your lights; letting go saves it."
      className={cx(picked && 'ring-1 ring-inset ring-accent/60')}
      actions={picked ? <Badge tone="accent">Playing</Badge> : undefined}
    >
      <div className={cx(busy && 'pointer-events-none opacity-60')}>
        <ColorPicker
          compact
          wide
          value={shown}
          onChange={(rgb) => {
            setDragging(true);
            setColor(rgb);
            live(rgb);
          }}
          onCommit={(rgb) => {
            setColor(rgb);
            void commit(rgb).finally(() => setDragging(false));
          }}
        />
      </div>
    </Section>
  );
}

export function EffectsPage() {
  const { serverInfo, api, auth } = useConnection();
  const { state, load } = useEditor();
  const ps = usePageSettings('effects', LABELS, true);
  const pick = usePick();
  const { name, plays } = useSetupInUse();
  const [query, setQuery] = useState('');
  const [picking, setPicking] = useState(false);
  const { music, regular } = splitEffects(serverInfo?.effects);
  const descriptions = useMemo(() => new Map((serverInfo?.effects ?? []).map((e) => [e.name, e.description ?? ''])), [serverInfo?.effects]);
  const layoutKey = useMemo(() => JSON.stringify(serverInfo?.leds ?? []), [serverInfo?.leds]);
  const running = plays.kind === 'effect' ? plays.effect : undefined;
  const q = query.trim().toLowerCase();
  const matches = (n: string) => effectLabel(n).toLowerCase().includes(q);
  const filtered = q ? regular.filter(matches) : regular;
  const filteredMusic = q ? music.filter(matches) : music;
  const choose = (p: MainPick) => {
    setPicking(true);
    void pick(p).finally(() => setPicking(false));
  };
  const play = (n: string) => choose({ kind: 'effect', effect: n });
  const sound = ps.value<Obj>('soundEffect');
  const devices =
    serverInfo?.sound?.sound_devices?.map((d) => ({
      value: d.id,
      label: d.name,
      group: d.kind === 'microphone' ? 'Microphones (react to the room)' : 'What this PC plays',
    })) ??
    (serverInfo?.sound?.sound_available ?? []).map((d) => ({ value: d, label: d }));
  const isAdmin = auth === 'admin';
  const ready = isAdmin && load.status === 'ready';
  const noStrips = ready && state.spec.strips.length === 0;
  const busy = picking || !ready || noStrips;
  const grid = { descriptions, api, layoutKey, running, busy, query: query.trim() };

  return (
    <PageFrame title="Effects" subtitle={`What plays in ${name}. Your pick is saved to it.`} settings={isAdmin ? ps : undefined} wide actions={ready ? <SetupSwitch size="sm" /> : undefined}>
      <div className="z-10 rounded-2xl bg-bg sm:sticky sm:top-0 sm:shadow-[0_12px_24px_-12px_rgba(0,0,0,0.9)]">
        <PlayingPreview inline maxHeight={150} ignoreOwn />
      </div>
      {!isAdmin && <Callout>Sign in on this PC to change what your lights play.</Callout>}
      {noStrips && (
        <Callout tone="warn">
          Set up your strips in{' '}
          <a className={LINK} href={hrefFor('layout')}>
            Layout
          </a>{' '}
          first: what plays is saved per strip.
        </Callout>
      )}
      {plays.kind === 'customized' && (
        <Callout icon={<SlidersHorizontal size={16} />}>
          Strips are customized in{' '}
          <a className={LINK} href={hrefFor('layout')}>
            Layout
          </a>
          ; a pick here replaces that.
        </Callout>
      )}
      <ScreenSection plays={plays} onPick={choose} busy={busy} />
      <ColorSection plays={plays} busy={busy} />

      <Section
        title="Effects"
        icon={<Sparkles size={18} />}
        description={`Pick one to play it across every strip of ${name}.`}
        actions={
          <label className="relative block">
            <span className="sr-only">Search effects</span>
            <Search size={15} className="pointer-events-none absolute left-2.5 top-2.5 text-ink-faint" />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search"
              className="h-9 w-48 rounded-lg border border-line-input bg-bg pl-8 pr-2 text-[13px] focus:border-accent"
            />
          </label>
        }
      >
        <EffectGrid names={filtered} {...grid} onPlay={play} />
      </Section>

      <Section title="Music" icon={<AudioLines size={18} />} description="Effects that react to what this PC plays, or to a microphone.">
        <EffectGrid names={filteredMusic} {...grid} onPlay={play} />
        {serverInfo?.sound?.active === false && sound.enable === true && !ps.dirty && (
          <Callout tone="warn">
            {serverInfo.sound.error ? `The sound device can’t be used: ${serverInfo.sound.error}.` : 'Sound input is on but not running. Check that the device is connected.'}
          </Callout>
        )}
        {isAdmin && ps.ready ? (
          <div className="grid gap-x-6 gap-y-5 border-t border-line pt-4 md:grid-cols-2">
            <Toggle label="Listen to sound" hint="Needed for music effects." checked={sound.enable === true} onChange={(v) => ps.setPath('soundEffect', ['enable'], v)} />
            <Toggle
              label="Smooth music effects"
              hint="Softer, less jumpy reactions to the beat."
              checked={sound.enable_smoothing === true}
              onChange={(v) => ps.setPath('soundEffect', ['enable_smoothing'], v)}
            />
            <Select
              className="md:col-span-2 md:max-w-md"
              label="Listen to"
              value={String(sound.device ?? '')}
              hint={devices.length ? 'The default output follows Windows when you switch between speakers and headphones.' : 'No sound devices were found.'}
              options={devices}
              onChange={(v) => ps.setPath('soundEffect', ['device'], v)}
            />
          </div>
        ) : (
          <p className="text-xs text-ink-faint">Sign in to choose what the music effects listen to.</p>
        )}
      </Section>
    </PageFrame>
  );
}
