import { useMemo, useState } from 'react';
import { AudioLines, Palette, Play, Search, Sparkles, Square } from 'lucide-react';
import { Button, Card, cx, Segmented, StatusDot } from '../components/ui';
import { Advanced, Callout, NumberField, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { ColorPicker } from '../components/ColorPicker';
import { LedPreview } from '../components/LedPreview';
import { EffectThumb } from '../components/EffectThumb';
import { LiveLayoutLights } from '../components/LayoutLights';
import { useLayoutView } from '../live/layoutView';
import type { RpcApi } from '../api/rpcApi';
import { usePageSettings, type PageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useAction } from '../app/useAction';
import { useThrottled } from '../app/useThrottled';
import { useLedFrame } from '../live/hooks';
import { currentSource, effectLabel, splitEffects, STOP_HINT } from '../control/modes';
import { useStartMusic } from '../control/music';
import { isRgb, type Rgb } from '../color/color';
import { differsFromDefault, withDefaults } from '../schema/schema';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'foregroundEffect.duration_ms': 'Startup effect duration',
  'foregroundEffect.effect': 'Startup effect',
  'backgroundEffect.effect': 'Idle effect',
  'soundEffect.device': 'Listen to',
};


/** The startup effect plays this long when its duration is 0 or -1 (the backend's default). */
const DEFAULT_STARTUP_MS = 3000;

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

/** The startup or idle effect: on/off, then an effect or a color (and for the startup effect, how long). */
function SourceEffectForm({
  title,
  description,
  toggle,
  value,
  effects,
  set,
  withDuration,
}: {
  title: string;
  description: string;
  toggle: string;
  value: Obj;
  effects: string[];
  set: (key: string, v: unknown) => void;
  withDuration?: boolean;
}) {
  const enabled = value.enable === true;
  const ms = Number(value.duration_ms ?? DEFAULT_STARTUP_MS);
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold">{title}</h3>
        <p className="text-xs text-ink-faint">{description}</p>
      </div>
      <Toggle label={toggle} checked={enabled} onChange={(v) => set('enable', v)} />
      {enabled && (
        <>
          <Segmented
            label={`${title}: effect or color`}
            value={String(value.type ?? 'effect')}
            options={[
              { value: 'effect', label: 'Effect' },
              { value: 'color', label: 'Color' },
            ]}
            onChange={(v) => set('type', v)}
          />
          {value.type === 'color' ? (
            <div className="max-w-sm">
              <ColorPicker label={`${title} color`} compact value={isRgb(value.color) ? value.color : [255, 138, 0]} onChange={(rgb) => set('color', [...rgb])} />
            </div>
          ) : (
            <Select
              className="max-w-xs"
              label="Effect"
              value={String(value.effect ?? '')}
              options={effects.map((n) => ({ value: n, label: effectLabel(n) }))}
              onChange={(v) => set('effect', v)}
            />
          )}
          {withDuration && (
            <NumberField
              label="Duration"
              unit="s"
              min={0.5}
              step={0.5}
              value={(ms > 0 ? ms : DEFAULT_STARTUP_MS) / 1000}
              onChange={(v) => set('duration_ms', Math.round(v * 1000))}
              hint="How long it plays when Backbloom starts."
            />
          )}
        </>
      )}
    </div>
  );
}

/** More options of the Effects section: the startup and idle effects. */
function StartupAndIdle({ ps, effects }: { ps: PageSettings; effects: string[] }) {
  const sections = ['foregroundEffect', 'backgroundEffect'] as const;
  const changedKeys = sections.flatMap((s) => {
    const schema = ps.schemaOf(s);
    const value = ps.value<Obj>(s);
    return Object.entries(schema?.properties ?? {})
      .filter(([k, f]) => differsFromDefault(f, value[k]))
      .map(([k]) => [s, k] as const);
  });
  return (
    <Advanced
      label="Startup and idle effects"
      changed={changedKeys.length}
      invalid={ps.issues.some((i) => sections.some((s) => i.path.startsWith(`${s}.`)))}
      onReset={() => sections.forEach((s) => ps.set(s, withDefaults(ps.schemaOf(s), undefined)))}
    >
      <div className="grid gap-6 md:grid-cols-2">
        <SourceEffectForm
          title="Startup effect"
          description="Plays briefly when Backbloom starts, so you know it’s running."
          toggle="Play a startup effect"
          value={ps.value<Obj>('foregroundEffect')}
          effects={effects}
          set={(k, v) => ps.setPath('foregroundEffect', [k], v)}
          withDuration
        />
        <SourceEffectForm
          title="Idle effect"
          description="Shown when nothing else is, for example while screen sync is off."
          toggle="Show something when idle"
          value={ps.value<Obj>('backgroundEffect')}
          effects={effects}
          set={(k, v) => ps.setPath('backgroundEffect', [k], v)}
        />
      </div>
    </Advanced>
  );
}

export function EffectsPage() {
  const { serverInfo, api, auth } = useConnection();
  const ps = usePageSettings('effects', LABELS, true);
  const { run, busy } = useAction();
  const startMusic = useStartMusic();
  const frame = useLedFrame();
  const [query, setQuery] = useState('');
  const [color, setColor] = useState<Rgb>([255, 100, 0]);
  const sendColor = useThrottled((rgb: Rgb) => void api.setColor(rgb).catch(() => undefined), 120);
  const { music, regular } = splitEffects(serverInfo?.effects);
  const descriptions = useMemo(() => new Map((serverInfo?.effects ?? []).map((e) => [e.name, e.description ?? ''])), [serverInfo?.effects]);
  const view = useLayoutView(api, auth === 'admin', serverInfo?.leds);
  const layoutKey = useMemo(() => JSON.stringify(serverInfo?.leds ?? []), [serverInfo?.leds]);
  const { source } = currentSource(serverInfo?.priorities);
  const running = source?.componentId === 'EFFECT' ? source.owner : undefined;
  const q = query.trim().toLowerCase();
  const matches = (n: string) => effectLabel(n).toLowerCase().includes(q);
  const filtered = q ? regular.filter(matches) : regular;
  const filteredMusic = q ? music.filter(matches) : music;
  const play = (n: string) => void run('play', 'Couldn’t start the effect', () => api.runEffect(n));
  const playMusic = (n: string) => void run('play', 'Couldn’t start the music effect', () => startMusic(n));
  const stop = () => void run('stop', 'Couldn’t stop', () => api.clear());
  const all = [...regular, ...music];
  const sound = ps.value<Obj>('soundEffect');
  const devices =
    serverInfo?.sound?.sound_devices?.map((d) => ({
      value: d.id,
      label: d.name,
      group: d.kind === 'microphone' ? 'Microphones (react to the room)' : 'What this PC plays',
    })) ??
    (serverInfo?.sound?.sound_available ?? []).map((d) => ({ value: d, label: d }));
  const isAdmin = auth === 'admin';
  const grid = { descriptions, api, layoutKey, running, busy: busy === 'play', query: query.trim() };

  return (
    <PageFrame
      title="Effects & Music"
      subtitle="Animated scenes, a single color, or lights that dance to your music."
      settings={isAdmin ? ps : undefined}
      wide
      actions={
        <Button icon={<Square size={14} />} disabled={busy === 'stop'} onClick={stop} title={STOP_HINT}>
          Stop
        </Button>
      }
    >
      <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <Card className="overflow-hidden">
          <div className="flex items-center justify-between border-b border-line px-5 py-3 text-[13px]">
            <span className="font-medium">Preview</span>
            <span className="text-ink-muted">{running ? effectLabel(running) : source?.componentId === 'COLOR' ? 'Color' : 'No effect running'}</span>
          </div>
          <div className="p-4">
            {view ? <LiveLayoutLights view={view} frame={frame} compact /> : <LedPreview rects={serverInfo?.leds} frame={frame} />}
          </div>
        </Card>
        <Section title="Color" icon={<Palette size={18} />} description="Drag to change the color live.">
          <ColorPicker
            value={color}
            onChange={(rgb) => {
              setColor(rgb);
              sendColor(rgb);
            }}
          />
        </Section>
      </div>

      <Section
        title="Effects"
        icon={<Sparkles size={18} />}
        description="Click an effect to play it. It keeps running until you stop it or start something else."
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
        {isAdmin && ps.ready && <StartupAndIdle ps={ps} effects={all} />}
      </Section>

      <Section title="Music" icon={<AudioLines size={18} />} description="Effects that react to what this PC plays, or to a microphone.">
        <EffectGrid names={filteredMusic} {...grid} onPlay={playMusic} />
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
