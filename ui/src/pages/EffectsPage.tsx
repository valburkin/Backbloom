import { useState, type ReactNode } from 'react';
import { AudioLines, Moon, Palette, Play, Power, Search, Sparkles, Square } from 'lucide-react';
import { Button, Card, cx, Segmented, StatusDot } from '../components/ui';
import { Callout, NumberField, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { ColorPicker } from '../components/ColorPicker';
import { LedPreview } from '../components/LedPreview';
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

function EffectGrid({ names, running, onPlay, busy }: { names: string[]; running?: string; onPlay: (n: string) => void; busy: boolean }) {
  if (!names.length) return <p className="text-[13px] text-ink-faint">No effects found.</p>;
  return (
    <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
      {names.map((n) => {
        const active = n === running;
        return (
          <li key={n}>
            <button
              type="button"
              disabled={busy}
              aria-pressed={active}
              onClick={() => onPlay(n)}
              className={cx(
                'flex w-full items-center gap-3 rounded-xl border px-3 py-2.5 text-left text-[13px] transition-colors',
                active ? 'border-accent bg-accent/10' : 'border-line bg-surface-2 hover:border-line-strong',
              )}
            >
              <span className={cx('flex h-7 w-7 shrink-0 items-center justify-center rounded-full', active ? 'bg-accent text-accent-ink' : 'bg-surface-3 text-ink-muted')}>
                {active ? <StatusDot tone="ok" /> : <Play size={13} />}
              </span>
              <span className="min-w-0 flex-1 truncate">{effectLabel(n)}</span>
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
              hint="How long it plays when Overglow starts (0 or -1: 3 seconds)."
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
    serverInfo?.sound?.sound_devices?.map((d) => ({ value: d.id, label: d.name })) ??
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
            <LedPreview rects={serverInfo?.leds} frame={frame} />
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
        <EffectGrid names={filtered} running={running} onPlay={play} busy={busy === 'play'} />
      </Section>

      <Section title="Music" icon={<AudioLines size={18} />} description="Sound-reactive effects listen to a sound device on this PC.">
        {isAdmin && ps.ready ? (
          <>
            <Toggle label="Sound capture" hint="Needed for music effects. Uses the device below." checked={sound.enable === true} onChange={(v) => ps.setPath('soundEffect', ['enable'], v)} />
            <Select
              label="Sound device"
              value={String(sound.device ?? '')}
              hint={devices.length ? 'To react to what you hear, pick an output (“what you hear”) device; a microphone reacts to the room.' : 'No sound devices were found.'}
              options={devices}
              onChange={(v) => ps.setPath('soundEffect', ['device'], v)}
            />
            <Toggle label="Smooth music effects" checked={sound.enable_smoothing === true} onChange={(v) => ps.setPath('soundEffect', ['enable_smoothing'], v)} />
          </>
        ) : (
          <Callout tone="info">Sign in to set up sound capture.</Callout>
        )}
        {serverInfo?.sound?.active === false && sound.enable === true && !ps.dirty && (
          <Callout tone="warn">Sound capture is enabled but not running. Check that the device is connected.</Callout>
        )}
        <EffectGrid names={filteredMusic} running={running} onPlay={play} busy={busy === 'play'} />
      </Section>

      {isAdmin && ps.ready && (
        <div className="grid gap-4 lg:grid-cols-2">
          <SourceEffectForm
            title="Startup effect"
            icon={<Power size={18} />}
            description="Plays briefly when Overglow starts, so you know it’s running."
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
