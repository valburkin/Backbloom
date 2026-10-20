import { useMemo, useState, type ReactNode } from 'react';
import { AudioLines, Moon, Palette, Play, Power, Search, Sparkles, Square } from 'lucide-react';
import { Button, Card, cx, Segmented, StatusDot } from '../components/ui';
import { Callout, NumberField, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { ColorPicker } from '../components/ColorPicker';
import { LedPreview } from '../components/LedPreview';
import { EffectThumb } from '../components/EffectThumb';
import { LiveLayoutLights } from '../components/LayoutLights';
import { useLayoutView, type LayoutView } from '../live/layoutView';
import type { RpcApi } from '../api/rpcApi';
import { usePageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useAction } from '../app/useAction';
import { useThrottled } from '../app/useThrottled';
import { useLedFrame } from '../live/hooks';
import { currentSource, effectLabel, splitEffects } from '../control/modes';
import { isRgb, type Rgb } from '../color/color';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'foregroundEffect.duration_ms': 'Startup effect duration',
  'foregroundEffect.effect': 'Startup effect',
  'backgroundEffect.effect': 'Background effect',
  'soundEffect.device': 'Sound device',
};

type GridProps = {
  names: string[];
  descriptions: ReadonlyMap<string, string>;
  api: RpcApi;
  view: LayoutView | null;
  layoutKey: string;
  running?: string;
  onPlay: (n: string) => void;
  busy: boolean;
  /** The effects can't run now (music effects without sound capture): shown dimmed, not clickable. */
  unavailable?: boolean;
};

function EffectGrid({ names, descriptions, api, view, layoutKey, running, onPlay, busy, unavailable = false }: GridProps) {
  if (!names.length) return <p className="text-[13px] text-ink-faint">No effects found.</p>;
  return (
    <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
      {names.map((n) => {
        const active = n === running;
        const description = descriptions.get(n);
        return (
          <li key={n}>
            <button
              type="button"
              disabled={busy || unavailable}
              aria-pressed={active}
              onClick={() => onPlay(n)}
              className={cx(
                'group flex h-full w-full flex-col gap-2 rounded-xl border p-2 text-left transition-colors',
                active ? 'border-accent bg-accent/10' : 'border-line bg-surface-2',
                unavailable ? 'cursor-not-allowed opacity-50' : !active && 'hover:border-line-strong',
              )}
            >
              <div className="relative">
                <EffectThumb api={api} name={n} view={view} layoutKey={layoutKey} />
                <span
                  className={cx(
                    'absolute left-1.5 top-1.5 flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium backdrop-blur',
                    active ? 'bg-black/60 text-white' : 'bg-black/50 text-white/90 opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100',
                  )}
                >
                  {active ? <StatusDot tone="ok" /> : <Play size={11} />}
                  {active ? 'Playing' : 'Play'}
                </span>
              </div>
              <div className="min-w-0 px-1 pb-1">
                <div className="truncate text-[13px] font-medium">{effectLabel(n)}</div>
                {description && <p className="mt-0.5 line-clamp-2 text-[12px] leading-snug text-ink-muted">{description}</p>}
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function SourceEffectForm({
  title,
  icon,
  description,
  value,
  effects,
  set,
  withDuration,
}: {
  title: string;
  icon: ReactNode;
  description: string;
  value: Obj;
  effects: string[];
  set: (key: string, v: unknown) => void;
  withDuration?: boolean;
}) {
  const enabled = value.enable === true;
  return (
    <Section title={title} icon={icon} description={description}>
      <Toggle label="Enabled" checked={enabled} onChange={(v) => set('enable', v)} />
      {enabled && (
        <>
          <Segmented
            label={`${title} type`}
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
            <Select label="Effect" value={String(value.effect ?? '')} options={effects.map((n) => ({ value: n, label: effectLabel(n) }))} onChange={(v) => set('effect', v)} />
          )}
          {withDuration && (
            <NumberField
              integer
              label="Duration"
              unit="ms"
              min={-1}
              step={500}
              value={Number(value.duration_ms ?? 3000)}
              onChange={(v) => set('duration_ms', v)}
              hint="How long it plays when Backbloom starts (0 or -1: 3 seconds)."
            />
          )}
        </>
      )}
    </Section>
  );
}

export function EffectsPage() {
  const { serverInfo, api, auth } = useConnection();
  const ps = usePageSettings('effects', LABELS, true);
  const { run, busy } = useAction();
  const frame = useLedFrame();
  const [query, setQuery] = useState('');
  const [color, setColor] = useState<Rgb>([255, 100, 0]);
  const sendColor = useThrottled((rgb: Rgb) => void api.setColor(rgb).catch(() => undefined), 120);
  const { music, regular } = splitEffects(serverInfo?.effects);
  const descriptions = useMemo(() => new Map((serverInfo?.effects ?? []).map((e) => [e.name, e.description ?? ''])), [serverInfo?.effects]);
  const view = useLayoutView(api, auth === 'admin', serverInfo?.leds);
  const layoutKey = useMemo(() => JSON.stringify(serverInfo?.leds ?? []), [serverInfo?.leds]);
  const thumbs = { api, view, layoutKey };
  // older backends don't say; they run music effects without capture
  const musicAvailable = serverInfo?.sound ? serverInfo.sound.available !== false : true;
  const { source } = currentSource(serverInfo?.priorities);
  const running = source?.componentId === 'EFFECT' ? source.owner : undefined;
  const q = query.trim().toLowerCase();
  const filtered = q ? regular.filter((n) => n.toLowerCase().includes(q)) : regular;
  const filteredMusic = q ? music.filter((n) => n.toLowerCase().includes(q)) : music;
  const play = (n: string) => void run('play', 'Couldn’t start the effect', () => api.runEffect(n));
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

  return (
    <PageFrame
      title="Effects & Music"
      subtitle="Animated scenes, a single color, or lights that dance to your music."
      settings={isAdmin ? ps : undefined}
      wide
      actions={
        <Button icon={<Square size={14} />} disabled={busy === 'stop'} onClick={stop}>
          Stop effect / color
        </Button>
      }
    >
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <Card className="overflow-hidden">
          <div className="flex items-center justify-between border-b border-line px-5 py-3 text-[13px]">
            <span className="font-medium">Preview</span>
            <span className="text-ink-muted">{running ? effectLabel(running) : source?.componentId === 'COLOR' ? 'Static color' : 'No effect running'}</span>
          </div>
          <div className="p-4">
            {view ? <LiveLayoutLights view={view} frame={frame} /> : <LedPreview rects={serverInfo?.leds} frame={frame} />}
          </div>
        </Card>
        <Section title="Static color" icon={<Palette size={18} />} description="Drag to change the color live.">
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
              className="h-9 w-48 rounded-lg border border-line bg-bg pl-8 pr-2 text-[13px] focus:border-accent"
            />
          </label>
        }
      >
        <EffectGrid names={filtered} descriptions={descriptions} {...thumbs} running={running} onPlay={play} busy={busy === 'play'} />
      </Section>

      <Section title="Music" icon={<AudioLines size={18} />} description="Sound-reactive effects listen to a sound device on this PC.">
        {isAdmin && ps.ready ? (
          <>
            <Toggle label="Sound capture" hint="Needed for music effects. Uses the device below." checked={sound.enable === true} onChange={(v) => ps.setPath('soundEffect', ['enable'], v)} />
            <Select
              label="Sound device"
              value={String(sound.device ?? '')}
              hint={
                devices.length
                  ? 'The default output follows Windows when you switch between speakers and headphones.'
                  : 'No sound devices were found.'
              }
              options={devices}
              onChange={(v) => ps.setPath('soundEffect', ['device'], v)}
            />
            <Toggle label="Smooth music effects" checked={sound.enable_smoothing === true} onChange={(v) => ps.setPath('soundEffect', ['enable_smoothing'], v)} />
          </>
        ) : (
          <Callout tone="info">Sign in to set up sound capture.</Callout>
        )}
        {!musicAvailable && (
          <Callout tone="info">
            {isAdmin ? 'Turn on sound capture above to use the music effects.' : 'Sound capture is off on this PC, so the music effects can’t run.'}
          </Callout>
        )}
        {serverInfo?.sound?.active === false && sound.enable === true && !ps.dirty && (
          <Callout tone="warn">
            {serverInfo.sound.error
              ? `The sound device can’t be used: ${serverInfo.sound.error}.`
              : 'Sound capture is enabled but not running. Check that the device is connected.'}
          </Callout>
        )}
        <EffectGrid names={filteredMusic} descriptions={descriptions} {...thumbs} running={running} onPlay={play} busy={busy === 'play'} unavailable={!musicAvailable} />
      </Section>

      {isAdmin && ps.ready && (
        <div className="grid gap-4 lg:grid-cols-2">
          <SourceEffectForm
            title="Startup effect"
            icon={<Power size={18} />}
            description="Plays briefly when Backbloom starts, so you know it’s running."
            value={ps.value<Obj>('foregroundEffect')}
            effects={all}
            set={(k, v) => ps.setPath('foregroundEffect', [k], v)}
            withDuration
          />
          <SourceEffectForm
            title="Background effect"
            icon={<Moon size={18} />}
            description="Shown whenever nothing else is, for example while screen capture is off."
            value={ps.value<Obj>('backgroundEffect')}
            effects={all}
            set={(k, v) => ps.setPath('backgroundEffect', [k], v)}
          />
        </div>
      )}
    </PageFrame>
  );
}
