import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Activity, ArrowRight, AudioLines, Cpu, Gauge, Loader2, MemoryStick, MonitorSmartphone, Palette, Play, ScreenShare, Sparkles, Square, Sun } from 'lucide-react';
import { Badge, Button, Card, cx, StatusDot } from '../components/ui';
import { Callout, Select, Switch } from '../components/form';
import { PageHeader } from '../components/PageHeader';
import { ColorPicker } from '../components/ColorPicker';
import { LedPreview } from '../components/LedPreview';
import { useConnection } from '../app/connection';
import { useConfig } from '../app/config';
import { useEditor } from '../app/editor';
import { useAction } from '../app/useAction';
import { useThrottled } from '../app/useThrottled';
import { hrefFor } from '../app/router';
import { stripLedCount } from '../layout';
import { useLedFrame, usePerformance } from '../live/hooks';
import type { ControllerPerformance } from '../live/performance';
import { currentSource, describeSource, effectLabel, splitEffects, type Mode } from '../control/modes';
import { applyIntensity, describeIntensity, detectIntensity, INTENSITY_PRESETS, type IntensityPreset } from '../settings/intensity';
import type { Rgb } from '../color/color';

const MODES: Array<{ id: Mode; label: string; hint: string; icon: ReactNode }> = [
  { id: 'screen', label: 'Screen sync', hint: 'Mirror what’s on your screen', icon: <ScreenShare size={22} /> },
  { id: 'music', label: 'Music', hint: 'Pulse to the sound', icon: <AudioLines size={22} /> },
  { id: 'effect', label: 'Effect', hint: 'Animated scenes', icon: <Sparkles size={22} /> },
  { id: 'color', label: 'Static color', hint: 'One color everywhere', icon: <Palette size={22} /> },
];

function PowerCard() {
  const { serverInfo, api } = useConnection();
  const { run, busy } = useAction();
  const comp = (name: string) => serverInfo?.components?.find((c) => c.name === name)?.enabled;
  const on = comp('ALL') !== false;
  const { source } = currentSource(serverInfo?.priorities);
  const manual = serverInfo?.priorities_autoselect === false;
  return (
    <Card className="flex flex-wrap items-center gap-5 p-5">
      <Switch size="lg" label="Lights" checked={on} disabled={busy === 'power'} onChange={(v) => void run('power', 'Couldn’t switch the lights', () => api.setComponent('ALL', v))} />
      <div className="min-w-0 flex-1">
        <p className="text-lg font-semibold">{on ? 'Lights are on' : 'Lights are off'}</p>
        <p className="text-[13px] text-ink-muted">
          {on ? (
            <>
              Showing: <span className="text-ink">{describeSource(source)}</span>
              {source && <span className="text-ink-faint"> · priority {source.priority}</span>}
            </>
          ) : (
            'Overglow is paused. Turn it on to light things up again.'
          )}
        </p>
      </div>
      {manual && (
        <Button size="sm" variant="secondary" onClick={() => void run('auto', 'Couldn’t switch sources', () => api.selectSource('auto'))}>
          Back to automatic source
        </Button>
      )}
    </Card>
  );
}

function ModeSwitch() {
  const { serverInfo, api, auth } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const { mode: liveMode, source } = currentSource(serverInfo?.priorities);
  const [mode, setMode] = useState<Mode>(liveMode ?? 'screen');
  useEffect(() => {
    if (liveMode) setMode(liveMode);
  }, [liveMode]);
  const { music, regular } = splitEffects(serverInfo?.effects);
  const running = source?.componentId === 'EFFECT' ? source.owner : undefined;
  const [effect, setEffect] = useState<string>(() => running ?? (regular.includes('Rainbow swirl') ? 'Rainbow swirl' : (regular[0] ?? '')));
  const [song, setSong] = useState<string>(() => (running && music.includes(running) ? running : (music.find((m) => m.includes('MULTI COLOR)')) ?? music[0] ?? '')));
  const [color, setColor] = useState<Rgb>(() => (source?.value?.RGB as Rgb | undefined) ?? [255, 170, 90]);
  const sendColor = useThrottled((rgb: Rgb) => void api.setColor(rgb).catch(() => undefined), 120);
  const grabberOn = serverInfo?.components?.find((c) => c.name === 'SYSTEMGRABBER')?.enabled === true;
  const sound = config?.soundEffect as { enable?: boolean; device?: string } | undefined;

  const start = (m: Mode) => {
    setMode(m);
    if (m === 'screen')
      void run('mode', 'Couldn’t switch to screen sync', async () => {
        await api.clear();
        if (!grabberOn) await api.setComponent('SYSTEMGRABBER', true);
      });
    else if (m === 'effect' && effect) void run('mode', 'Couldn’t start the effect', () => api.runEffect(effect));
    else if (m === 'music' && song) void run('mode', 'Couldn’t start music mode', () => api.runEffect(song));
    else if (m === 'color') void run('mode', 'Couldn’t set the color', () => api.setColor(color));
  };

  return (
    <Card className="p-5">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-[15px] font-semibold">Mode</h2>
        {busy === 'mode' && <Loader2 size={16} className="animate-spin text-ink-faint" />}
      </div>
      <div role="radiogroup" aria-label="Mode" className="grid grid-cols-2 gap-2 md:grid-cols-4">
        {MODES.map((m) => {
          const active = m.id === mode;
          return (
            <button
              key={m.id}
              type="button"
              role="radio"
              aria-checked={active}
              onClick={() => start(m.id)}
              className={cx(
                'flex flex-col items-start gap-2 rounded-xl border p-3.5 text-left transition-colors',
                active ? 'border-accent bg-accent/10' : 'border-line bg-surface-2 hover:border-line-strong',
              )}
            >
              <span className={active ? 'text-accent-strong' : 'text-ink-muted'}>{m.icon}</span>
              <span>
                <span className="block text-sm font-semibold">{m.label}</span>
                <span className="block text-xs text-ink-muted">{m.hint}</span>
              </span>
            </button>
          );
        })}
      </div>

      <div className="mt-5">
        {mode === 'screen' && (
          <p className="text-[13px] text-ink-muted">
            {grabberOn ? 'Your lights follow the picture on your screen.' : 'Screen capture is off.'}{' '}
            <a className="text-accent-strong underline-offset-2 hover:underline" href={hrefFor('capture')}>
              Screen capture settings
            </a>
          </p>
        )}
        {mode === 'effect' && (
          <div className="flex flex-wrap items-end gap-3">
            <Select
              className="min-w-[16rem] flex-1"
              label="Effect"
              value={effect}
              options={regular.map((n) => ({ value: n, label: n }))}
              onChange={(v) => {
                setEffect(v);
                void run('mode', 'Couldn’t start the effect', () => api.runEffect(v));
              }}
            />
            <StopButton />
          </div>
        )}
        {mode === 'music' && (
          <div className="space-y-3">
            {auth === 'admin' && sound && !sound.enable && <SoundOffNotice />}
            <div className="flex flex-wrap items-end gap-3">
              <Select
                className="min-w-[16rem] flex-1"
                label="Music effect"
                value={song}
                options={music.map((n) => ({ value: n, label: effectLabel(n) }))}
                onChange={(v) => {
                  setSong(v);
                  void run('mode', 'Couldn’t start music mode', () => api.runEffect(v));
                }}
              />
              <StopButton />
            </div>
          </div>
        )}
        {mode === 'color' && (
          <div className="grid gap-4 sm:grid-cols-[minmax(0,22rem)_1fr]">
            <ColorPicker
              value={color}
              onChange={(rgb) => {
                setColor(rgb);
                sendColor(rgb);
              }}
            />
            <div className="flex items-start">
              <StopButton />
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

function StopButton() {
  const { api } = useConnection();
  const { run, busy } = useAction();
  return (
    <Button icon={<Square size={14} />} disabled={busy === 'stop'} onClick={() => void run('stop', 'Couldn’t stop', () => api.clear())}>
      Stop
    </Button>
  );
}

function SoundOffNotice() {
  const { api } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  return (
    <Callout tone="warn" icon={<AudioLines size={16} />}>
      Sound capture is off, so music effects have nothing to react to.{' '}
      <button
        type="button"
        className="font-medium underline underline-offset-2"
        disabled={busy === 'sound'}
        onClick={() =>
          void run('sound', 'Couldn’t turn on sound capture', () => api.setConfig({ soundEffect: { ...(config?.soundEffect as object), enable: true } }), 'Sound capture is on')
        }
      >
        Turn it on
      </button>{' '}
      or pick a device under{' '}
      <a className="font-medium underline underline-offset-2" href={hrefFor('effects')}>
        Effects &amp; Music
      </a>
      .
    </Callout>
  );
}

function BrightnessCard() {
  const { serverInfo, api, auth } = useConnection();
  const stored = serverInfo?.adjustment?.[0]?.scaleOutput ?? 1;
  const [dragging, setDragging] = useState<number | null>(null);
  // The `adjustment` command applies the value and stores it (it needs a signed-in session).
  const send = useThrottled((v: number) => void api.adjust({ scaleOutput: v }).catch(() => undefined), 100);
  const releaseTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const canEdit = auth === 'admin';
  // Show the dragged value until the backend's adjustment-update has caught up.
  const release = () => {
    if (releaseTimer.current) clearTimeout(releaseTimer.current);
    releaseTimer.current = setTimeout(() => setDragging(null), 600);
  };
  useEffect(() => () => {
    if (releaseTimer.current) clearTimeout(releaseTimer.current);
  }, []);
  const value = dragging ?? Math.min(1, stored);
  return (
    <Card className="p-5">
      <div className="mb-3 flex items-center justify-between">
        <label htmlFor="home-brightness" className="flex items-center gap-2 text-[15px] font-semibold">
          <Sun size={17} className="text-accent-strong" /> Brightness
        </label>
        <span className="text-sm tabular-nums text-ink-muted">{Math.round(value * 100)}%</span>
      </div>
      <input
        id="home-brightness"
        type="range"
        min={0}
        max={1}
        step={0.01}
        value={value}
        className="w-full"
        disabled={!canEdit}
        onChange={(e) => {
          const v = Number(e.target.value);
          setDragging(v);
          send(v);
          release();
        }}
      />
      <p className="mt-1 text-xs text-ink-faint">{canEdit ? 'Applies immediately and is remembered.' : 'Sign in to change the brightness.'}</p>
    </Card>
  );
}

function IntensityCard() {
  const { api } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const current = detectIntensity(config);
  const [preview, setPreview] = useState<IntensityPreset['id'] | null>(null);
  const shown = INTENSITY_PRESETS.find((p) => p.id === (preview ?? (current !== 'custom' ? current : null)));
  const canEdit = !!config;
  return (
    <Card className="p-5">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="flex items-center gap-2 text-[15px] font-semibold">
          <Gauge size={17} className="text-accent-strong" /> Intensity
        </h2>
        {current === 'custom' && <Badge>Custom</Badge>}
      </div>
      <div role="radiogroup" aria-label="Intensity" className="grid grid-cols-2 gap-1.5 sm:grid-cols-4">
        {INTENSITY_PRESETS.map((p) => (
          <button
            key={p.id}
            type="button"
            role="radio"
            aria-checked={current === p.id}
            disabled={!canEdit || busy === 'intensity'}
            onMouseEnter={() => setPreview(p.id)}
            onMouseLeave={() => setPreview(null)}
            onFocus={() => setPreview(p.id)}
            onBlur={() => setPreview(null)}
            onClick={() =>
              config && void run('intensity', 'Couldn’t change the intensity', () => api.setConfig(applyIntensity(config, p.id)), `Intensity: ${p.label}`)
            }
            className={cx(
              'h-10 rounded-lg border text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50',
              current === p.id ? 'border-accent bg-accent/15 text-ink' : 'border-line bg-surface-2 text-ink-muted hover:text-ink',
            )}
          >
            {p.label}
          </button>
        ))}
      </div>
      <div className="mt-3 min-h-[4.5rem] text-[13px]">
        {shown ? (
          <>
            <p className="text-ink-muted">{shown.description}</p>
            <dl className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-xs">
              {describeIntensity(shown).map(([k, v]) => (
                <div key={k} className="flex gap-1.5">
                  <dt className="text-ink-faint">{k}</dt>
                  <dd className="tabular-nums text-ink">{v}</dd>
                </div>
              ))}
            </dl>
          </>
        ) : (
          <p className="text-ink-muted">
            Your smoothing and color settings don’t match a preset. Pick one to replace them, or fine-tune on the{' '}
            <a className="text-accent-strong underline-offset-2 hover:underline" href={hrefFor('colors')}>
              Colors
            </a>{' '}
            page.
          </p>
        )}
      </div>
    </Card>
  );
}

function PreviewCard() {
  const { serverInfo } = useConnection();
  const frame = useLedFrame();
  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-line px-5 py-3">
        <h2 className="flex items-center gap-2 text-[15px] font-semibold">
          <Activity size={17} className="text-accent-strong" /> Live preview
        </h2>
        <span className="flex items-center gap-2 text-xs text-ink-faint">
          <StatusDot tone={frame ? 'ok' : 'neutral'} />
          {frame ? 'Live' : 'Waiting for colors…'}
        </span>
      </div>
      <div className="bg-[radial-gradient(ellipse_at_center,#141826_0%,#0b0d13_70%)] p-4">
        <LedPreview rects={serverInfo?.leds} frame={frame} />
      </div>
    </Card>
  );
}

function Stat({ icon, label, value, sub }: { icon: ReactNode; label: string; value: ReactNode; sub?: ReactNode }) {
  return (
    <div className="rounded-xl border border-line bg-surface-2 p-3.5">
      <p className="flex items-center gap-1.5 text-xs text-ink-muted">
        {icon}
        {label}
      </p>
      <p className="mt-1.5 text-lg font-semibold tabular-nums">{value}</p>
      {sub && <p className="text-[11px] text-ink-faint">{sub}</p>}
    </div>
  );
}

function PerformanceCard() {
  const perf = usePerformance();
  const dash = <span className="text-ink-faint">—</span>;
  return (
    <Card className="p-5">
      <h2 className="mb-3 flex items-center gap-2 text-[15px] font-semibold">
        <Gauge size={17} className="text-accent-strong" /> Performance
      </h2>
      <div className="grid grid-cols-2 gap-2">
        <Stat icon={<Play size={13} />} label="Processing" value={perf.processingFps !== null ? `${perf.processingFps} fps` : dash} sub="frames analyzed per second" />
        <Stat
          icon={<MonitorSmartphone size={13} />}
          label="LED updates"
          value={perf.ledFps !== null ? `${perf.ledFps} /s` : dash}
          sub={perf.controllers.length > 1 ? 'first controller, all listed below' : perf.ledDropped ? <span className="text-warn">{perf.ledDropped} dropped</span> : 'sent to the controller'}
        />
        <Stat icon={<Cpu size={13} />} label="CPU" value={perf.cpuPercent !== null ? `${perf.cpuPercent}%` : dash} sub="whole computer" />
        <Stat
          icon={<MemoryStick size={13} />}
          label="Memory"
          value={perf.ram ? `${perf.ram.percent}%` : dash}
          sub={perf.ram ? `${(perf.ram.usedMb / 1024).toFixed(1)} of ${(perf.ram.totalMb / 1024).toFixed(0)} GB` : undefined}
        />
      </div>
      {perf.controllers.length > 1 && <ControllerRates controllers={perf.controllers} />}
    </Card>
  );
}

/** LED updates per controller, when more than one is fed. */
function ControllerRates({ controllers }: { controllers: ControllerPerformance[] }) {
  return (
    <div className="mt-3 overflow-hidden rounded-xl border border-line">
      <table className="w-full text-[13px]">
        <caption className="sr-only">LED updates per controller</caption>
        <thead className="bg-surface-2 text-left text-xs text-ink-faint">
          <tr>
            <th className="px-3 py-2 font-medium">Controller</th>
            <th className="px-3 py-2 text-right font-medium">Updates</th>
            <th className="px-3 py-2 text-right font-medium">Delay</th>
          </tr>
        </thead>
        <tbody>
          {controllers.map((c) => (
            <tr key={c.id} className="border-t border-line">
              <td className="max-w-0 truncate px-3 py-2" title={c.name}>
                {c.name}
                {c.dropped > 0 && <span className="ml-2 text-xs text-warn">{c.dropped} dropped</span>}
              </td>
              <td className="px-3 py-2 text-right tabular-nums">{c.fps !== null ? `${c.fps} /s` : '—'}</td>
              <td className="px-3 py-2 text-right tabular-nums text-ink-muted">{c.latencyMs === null ? '—' : c.latencyMs < 0.1 ? '< 0.1 ms' : `${c.latencyMs} ms`}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function HomePage() {
  const { serverInfo, sysInfo, backend } = useConnection();
  const { state } = useEditor();
  const ledTotal = state.spec.strips.reduce((n, s) => n + stripLedCount(s), 0);
  const host = sysInfo?.system?.hostName ?? serverInfo?.hostname;

  return (
    <div className="scroll-thin h-full overflow-y-auto">
      <PageHeader
        title="Home"
        subtitle={host ? `Running on ${host}` : undefined}
        actions={backend.mode === 'mock' ? <Badge tone="accent">Demo data</Badge> : null}
      />
      <div className="mx-auto grid max-w-6xl gap-4 p-4 sm:p-6 lg:grid-cols-[minmax(0,1.45fr)_minmax(0,1fr)]">
        <div className="space-y-4">
          <PowerCard />
          <ModeSwitch />
          <PreviewCard />
        </div>
        <div className="space-y-4">
          <BrightnessCard />
          <IntensityCard />
          <PerformanceCard />
          <a href={hrefFor('layout')} className="group block">
            <Card className="flex items-center gap-4 p-5 transition-colors group-hover:border-line-strong">
              <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-gradient-to-br from-cyan-400/20 via-violet-500/20 to-pink-500/20 text-accent-strong">
                <MonitorSmartphone size={22} />
              </div>
              <div className="min-w-0 flex-1">
                <p className="font-semibold">LED layout</p>
                <p className="text-[13px] text-ink-muted">
                  {state.spec.strips.length
                    ? `${state.spec.strips.length} strip${state.spec.strips.length === 1 ? '' : 's'} · ${ledTotal} LEDs around your screen`
                    : 'Tell us where your LED strips are.'}
                </p>
              </div>
              <ArrowRight size={18} className="text-ink-faint transition-transform group-hover:translate-x-0.5 group-hover:text-ink" />
            </Card>
          </a>
          <p className="px-1 text-xs text-ink-faint">
            {sysInfo?.overglow?.version ? `Service version ${sysInfo.overglow.version} · ` : ''}
            {backend.url}
          </p>
        </div>
      </div>
    </div>
  );
}
