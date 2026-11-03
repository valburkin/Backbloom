import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Activity, ArrowRight, Gamepad2, AudioLines, CircleHelp, Cpu, Gauge, Lamp, Loader2, MemoryStick, MonitorSmartphone, Palette, Play, ScreenShare, Sparkles, Square, Sun, TriangleAlert } from 'lucide-react';
import { Badge, Button, Card, ChoiceCard, cx, rangeFill, StatusDot } from '../components/ui';
import { Callout, Expert, Select, Switch } from '../components/form';
import { PageHeader } from '../components/PageHeader';
import { ColorPicker } from '../components/ColorPicker';
import { LedPreview } from '../components/LedPreview';
import { useAppGlow } from '../app/appGlow';
import { LiveLayoutLights } from '../components/LayoutLights';
import { useLayoutView } from '../live/layoutView';
import { useConnection } from '../app/connection';
import { useConfig } from '../app/config';
import { useEditor } from '../app/editor';
import { useAction } from '../app/useAction';
import { useThrottled } from '../app/useThrottled';
import { hrefFor } from '../app/router';
import { stripLedCount } from '../layout';
import { useLedFrame, usePerformance } from '../live/hooks';
import { glowColors } from '../live/ledPreview';
import type { ControllerPerformance } from '../live/performance';
import { currentSource, describeSource, effectLabel, splitEffects, STOP_HINT, type Mode } from '../control/modes';
import { deviceProblems } from '../control/health';
import { TroubleshooterDialog } from '../control/Troubleshooter';
import { useExpertTools } from '../app/expert';
import { useStartMusic } from '../control/music';
import { PresetPicker } from '../control/PresetPicker';
import { applyGameIntensity, applyMood, detectGameIntensity, detectMood, GAME_PRESETS, MOOD_PRESETS } from '../settings/intensity';
import { ModeSwitch } from '../control/ModeSwitch';
import { ModeBadge } from '../components/ModeBadge';
import { NoLights, hasRealDevice } from '../setup/NoLights';
import { AddDeviceDialog } from './devices/AddDeviceDialog';

import type { Rgb } from '../color/color';

const SHOWS: Array<{ id: Mode; label: string; hint: string; icon: ReactNode }> = [
  { id: 'screen', label: 'Screen sync', hint: 'Follow your screen', icon: <ScreenShare size={20} /> },
  { id: 'music', label: 'Music', hint: 'React to sound', icon: <AudioLines size={20} /> },
  { id: 'effect', label: 'Effect', hint: 'Animated scenes', icon: <Sparkles size={20} /> },
  { id: 'color', label: 'Color', hint: 'One color everywhere', icon: <Palette size={20} /> },
];

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

/**
 * Game mode's place of the Show card: the lights always follow the screen there, so it says which game runs and leads
 * to the games that turn Game mode on.
 */
function PlayingCard() {
  const { serverInfo, api } = useConnection();
  const { run, busy } = useAction();
  const { state } = useEditor();
  const mode = serverInfo?.mode;
  const playing = mode?.gameProfile;
  const count = (state.spec.profiles ?? []).filter((p) => p.game).length;
  const grabberOn = serverInfo?.components?.find((c) => c.name === 'SYSTEMGRABBER')?.enabled !== false;
  // Game mode shows only the screen: with screen sync paused, nothing lights up
  const follows = grabberOn ? 'Your lights follow the game.' : 'Screen sync is paused, so your lights stay dark.';
  const after = mode?.saved === 'game' ? 'You chose Game mode, so it stays on after the game quits.' : 'Ambient comes back when it quits.';
  return (
    <Card className="p-5">
      <div className="flex items-start gap-4">
        <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-accent/15 text-accent-strong">
          <Gamepad2 size={22} />
        </div>
        <div className="min-w-0 flex-1">
          <h2 className="text-base font-semibold">{playing ? `Playing ${playing}` : 'No game running'}</h2>
          <p className="mt-0.5 text-[13px] text-ink-muted">
            {playing
              ? `${follows} ${after}`
              : `${grabberOn ? 'Your lights follow your screen, quicker and richer than in Ambient.' : follows} Effects, music and colors wait until you switch back to Ambient.`}
          </p>
          {!grabberOn && (
            <Button
              size="sm"
              className="mt-3"
              icon={<ScreenShare size={14} />}
              disabled={busy === 'grabber'}
              onClick={() => void run('grabber', 'Couldn’t turn on screen sync', () => api.setComponent('SYSTEMGRABBER', true))}
            >
              Turn on screen sync
            </Button>
          )}
          <p className="mt-3 text-[13px] text-ink-muted">
            {count ? `Game mode turns on by itself for ${count} game${count === 1 ? '' : 's'}.` : 'Add your games and Game mode turns on by itself when you play.'}{' '}
            <a className={LINK} href={hrefFor('games')}>
              {count ? 'Your games' : 'Add games'}
            </a>
          </p>
        </div>
      </div>
    </Card>
  );
}

/** The main card: lights on or off, what they show, and the brightness. */
function LightsCard() {
  const { serverInfo, api, setup } = useConnection();
  const { run, busy } = useAction();
  const comp = (name: string) => serverInfo?.components?.find((c) => c.name === name)?.enabled;
  const on = comp('ALL') !== false;
  // switched on, but nothing reaches the LEDs until "Turn on lights" (or while HyperHDR runs)
  const waiting = on && setup?.outputHeld === true;
  const preview = serverInfo?.profile?.manual ? serverInfo.profile : null;
  const { source } = currentSource(serverInfo?.priorities);
  const manual = serverInfo?.priorities_autoselect === false;
  const rgb = source?.componentId === 'COLOR' ? source.value?.RGB : undefined;
  return (
    <Card className="p-5">
      <div className="flex flex-wrap items-center gap-5">
        <Switch size="lg" label="Lights" checked={on} disabled={busy === 'power'} onChange={(v) => void run('power', 'Couldn’t switch the lights', () => api.setComponent('ALL', v))} />
        <div className="min-w-0 flex-1">
          <p className="text-lg font-semibold">
            {waiting ? (setup?.otherApp ? `Waiting for ${setup.otherApp.name} to quit` : 'Waiting — turn on your lights to start') : on ? 'Lights are on' : 'Lights are off'}
          </p>
          <p className="text-[13px] text-ink-muted">
            {on ? (
              <>
                {waiting ? 'Will show: ' : 'Showing: '}
                <span className="text-ink">{describeSource(source)}</span>
                {rgb && (
                  <span
                    className="ml-1.5 inline-block h-3 w-3 rounded-full border border-line-strong align-[-1px]"
                    style={{ backgroundColor: `rgb(${rgb.join(',')})` }}
                    title={`RGB ${rgb.join(', ')}`}
                    aria-hidden
                  />
                )}
                {serverInfo?.profile && (
                  <>
                    {' · '}profile <span className="text-ink">{serverInfo.profile.name}</span>
                    {preview ? ' (preview)' : ''}
                  </>
                )}
              </>
            ) : (
              'Your lights are off. Turn them on to light things up again.'
            )}
          </p>
        </div>
        {preview && (
          <Button
            size="sm"
            variant="secondary"
            icon={<Square size={13} />}
            title="Back to the profile chosen by the running programs"
            disabled={busy === 'preview'}
            onClick={() => void run('preview', 'Couldn’t stop the preview', () => api.setProfile(null))}
          >
            Stop preview
          </Button>
        )}
        {manual && (
          <Button size="sm" variant="secondary" title="Let Backbloom choose what to show" onClick={() => void run('auto', 'Couldn’t switch back to automatic', () => api.selectSource('auto'))}>
            Back to automatic
          </Button>
        )}
      </div>
      <Brightness />
    </Card>
  );
}

/**
 * Brightness 0–100% (`scaleOutput`, the same value as everywhere). A value boosted above 100% (Colors, expert) is
 * shown as it is; it only changes when the slider is moved.
 */
function Brightness() {
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
  useEffect(
    () => () => {
      if (releaseTimer.current) clearTimeout(releaseTimer.current);
    },
    [],
  );
  const value = dragging ?? stored;
  return (
    <div className="mt-5 border-t border-line pt-4">
      <div className="mb-2 flex items-center justify-between">
        <label htmlFor="home-brightness" className="flex items-center gap-2 text-sm font-semibold">
          <Sun size={16} className="text-ink-faint" /> Brightness
        </label>
        <span className="text-sm tabular-nums text-ink-muted">
          {Math.round(value * 100)}%{value > 1.005 && <span className="text-ink-faint"> (boosted)</span>}
        </span>
      </div>
      <input
        id="home-brightness"
        type="range"
        min={0}
        max={1}
        step={0.01}
        value={Math.min(1, value)}
        disabled={!canEdit}
        aria-valuetext={`${Math.round(value * 100)}%`}
        style={rangeFill(value, 0, 1)}
        className="range-lg w-full"
        onChange={(e) => {
          const v = Number(e.target.value);
          setDragging(v);
          send(v);
          release();
        }}
      />
      <p className="mt-1.5 text-xs text-ink-faint">{canEdit ? 'Saved automatically.' : 'Sign in to change the brightness.'}</p>
    </div>
  );
}

/** Shown only while a device that is switched on gets no colors; links to Setup → Devices. */
function DeviceHealth() {
  const { serverInfo } = useConnection();
  const on = serverInfo?.components?.find((c) => c.name === 'ALL')?.enabled !== false;
  const problems = on ? deviceProblems(serverInfo?.controllers, serverInfo?.ledDevices) : [];
  if (!problems.length) return null;
  const bad = problems.some((p) => p.tone === 'bad');
  return (
    <a
      href={hrefFor('devices')}
      className={cx(
        'group flex items-start gap-3 rounded-2xl border px-4 py-3 text-[13px] transition-colors',
        bad ? 'border-bad/30 bg-bad/10 text-bad hover:border-bad/50' : 'border-warn/30 bg-warn/10 text-warn hover:border-warn/50',
      )}
    >
      <TriangleAlert size={16} className="mt-px shrink-0" aria-hidden />
      <span className="min-w-0 flex-1 space-y-0.5">
        {problems.slice(0, 3).map((p) => (
          <span key={p.id} className="block">
            {p.text}
          </span>
        ))}
        {problems.length > 3 && <span className="block">And {problems.length - 3} more.</span>}
      </span>
      <span className="flex shrink-0 items-center gap-1 font-medium">
        Devices <ArrowRight size={14} className="transition-transform group-hover:translate-x-0.5" />
      </span>
    </a>
  );
}

function ShowCard() {
  const { serverInfo, api } = useConnection();
  const { run, busy } = useAction();
  const startMusic = useStartMusic();
  const { mode: liveMode, source } = currentSource(serverInfo?.priorities);
  // The controls shown: what runs, or what was picked last.
  const [mode, setMode] = useState<Mode>(liveMode ?? 'screen');
  useEffect(() => {
    if (liveMode) setMode(liveMode);
  }, [liveMode]);
  // Highlighted is only what runs; a pick counts while it starts and until the backend reports it (or 2 s pass).
  const [pending, setPending] = useState<Mode | null>(null);
  useEffect(() => {
    if (pending && liveMode === pending) setPending(null);
  }, [liveMode, pending]);
  useEffect(() => {
    if (!pending) return;
    const t = setTimeout(() => setPending(null), 2000);
    return () => clearTimeout(t);
  }, [pending]);
  const highlighted = pending ?? liveMode;
  const { music, regular } = splitEffects(serverInfo?.effects);
  const running = source?.componentId === 'EFFECT' ? source.owner : undefined;
  const [effect, setEffect] = useState<string>(() => (running && !music.includes(running) ? running : regular.includes('Rainbow swirl') ? 'Rainbow swirl' : (regular[0] ?? '')));
  const [song, setSong] = useState<string>(() => (running && music.includes(running) ? running : (music.find((m) => m.includes('MULTI COLOR)')) ?? music[0] ?? '')));
  const [color, setColor] = useState<Rgb>(() => (source?.value?.RGB as Rgb | undefined) ?? [255, 170, 90]);
  const sendColor = useThrottled((rgb: Rgb) => void api.setColor(rgb).catch(() => undefined), 120);
  const grabberOn = serverInfo?.components?.find((c) => c.name === 'SYSTEMGRABBER')?.enabled === true;

  // Picks a show; when it can't start, the card goes back to what actually runs.
  const start = async (m: Mode) => {
    const previous = mode;
    setMode(m);
    setPending(m);
    let ok = true;
    if (m === 'screen')
      ok = await run('mode', 'Couldn’t switch to screen sync', async () => {
        await api.clear();
        if (!grabberOn) await api.setComponent('SYSTEMGRABBER', true);
      });
    else if (m === 'effect' && effect) ok = await run('mode', 'Couldn’t start the effect', () => api.runEffect(effect));
    else if (m === 'music' && song) ok = await run('mode', 'Couldn’t start the music effect', () => startMusic(song));
    else if (m === 'color') ok = await run('mode', 'Couldn’t set the color', () => api.setColor(color));
    if (!ok) {
      setPending(null);
      setMode(liveMode ?? previous);
    }
  };

  return (
    <Card className="p-5">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-base font-semibold">Show</h2>
        {busy === 'mode' && <Loader2 size={16} className="animate-spin text-ink-faint" />}
      </div>
      <div role="radiogroup" aria-label="Show" className="grid grid-cols-2 gap-2 xl:grid-cols-4">
        {SHOWS.map((s) => (
          <ChoiceCard key={s.id} active={s.id === highlighted}title={s.label} description={s.hint} icon={s.icon} onClick={() => void start(s.id)} />
        ))}
      </div>

      <div className="mt-5">
        {mode === 'screen' && (
          <p className="text-[13px] text-ink-muted">
            {grabberOn ? 'Your lights follow your screen.' : 'Screen sync is paused.'}{' '}
            <a className={LINK} href={hrefFor('capture')}>
              Screen settings
            </a>
          </p>
        )}
        {mode === 'effect' && (
          <div className="flex flex-wrap items-end gap-3">
            <Select
              className="min-w-[16rem] flex-1"
              label="Effect"
              value={effect}
              options={regular.map((n) => ({ value: n, label: effectLabel(n) }))}
              onChange={(v) => {
                setEffect(v);
                void run('mode', 'Couldn’t start the effect', () => api.runEffect(v));
              }}
            />
            <StopButton />
          </div>
        )}
        {mode === 'music' && (
          <div className="flex flex-wrap items-end gap-3">
            <Select
              className="min-w-[16rem] flex-1"
              label="Music effect"
              value={song}
              options={music.map((n) => ({ value: n, label: effectLabel(n) }))}
              onChange={(v) => {
                setSong(v);
                void run('mode', 'Couldn’t start the music effect', () => startMusic(v));
              }}
            />
            <StopButton />
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
    <Button icon={<Square size={14} />} title={STOP_HINT} disabled={busy === 'stop'} onClick={() => void run('stop', 'Couldn’t stop', () => api.clear())}>
      Stop
    </Button>
  );
}

/**
 * The mode's presets: Ambient's Mood (the saved smoothing and color) or Game's Intensity (the `game` section). The
 * Colors page fine-tunes the same values.
 */
function PresetCard() {
  const { api, serverInfo } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const game = serverInfo?.mode?.mode === 'game';
  const custom = (what: string) => (
    <>
      You’ve fine-tuned {what} on{' '}
      <a className={LINK} href={hrefFor('colors')}>
        Colors
      </a>
      . Pick one to start over from it.
    </>
  );
  const header = (title: string, icon: ReactNode, isCustom: boolean) => (
    <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
      <h2 className="flex items-center gap-2 text-base font-semibold">
        {icon} {title}
      </h2>
      {isCustom && <Badge>Custom</Badge>}
    </div>
  );
  if (game) {
    const current = detectGameIntensity(config);
    return (
      <div id="intensity">
        <Card className="p-5">
          {header('Intensity', <Gauge size={16} className="text-ink-faint" />, current === 'custom')}
          <PresetPicker
            label="Intensity"
            presets={GAME_PRESETS}
            current={current}
            disabled={!config || busy === 'preset'}
            onPick={(id) =>
              config &&
              void run('preset', 'Couldn’t change the intensity', () => api.setConfig(applyGameIntensity(config, id)), `Intensity: ${GAME_PRESETS.find((p) => p.id === id)?.label}`)
            }
            customHint={custom('Game mode’s colors')}
          />
        </Card>
      </div>
    );
  }
  const current = detectMood(config);
  return (
    <div id="mood">
      <Card className="p-5">
        {header('Mood', <Lamp size={16} className="text-ink-faint" />, current === 'custom')}
        <PresetPicker
          label="Mood"
          presets={MOOD_PRESETS}
          current={current}
          disabled={!config || busy === 'preset'}
          onPick={(id) =>
            config && void run('preset', 'Couldn’t change the mood', () => api.setConfig(applyMood(config, id)), `Mood: ${MOOD_PRESETS.find((p) => p.id === id)?.label}`)
          }
          customHint={custom('the colors')}
        />
      </Card>
    </div>
  );
}

function PreviewCard() {
  const { serverInfo, api, auth } = useConnection();
  const view = useLayoutView(api, auth === 'admin', serverInfo?.leds);
  const frame = useLedFrame();
  useAppGlow(useMemo(() => glowColors(frame, serverInfo?.leds), [frame, serverInfo?.leds]));
  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-line px-5 py-3">
        <h2 className="flex items-center gap-2 text-base font-semibold">
          <Activity size={16} className="text-ink-faint" /> Live preview
        </h2>
        <span className="flex items-center gap-2 text-xs text-ink-faint">
          <StatusDot tone={frame ? 'ok' : 'neutral'} live />
          {frame ? 'Live' : 'Waiting for colors…'}
        </span>
      </div>
      <div className="bg-[radial-gradient(ellipse_at_center,#141826_0%,#0b0d13_70%)] p-4">
        {view ? <LiveLayoutLights view={view} frame={frame} compact /> : <LedPreview rects={serverInfo?.leds} frame={frame} />}
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

/** Diagnostics: processing and LED update rates, CPU and memory (expert tools only). */
function PerformanceCard() {
  const perf = usePerformance();
  const dash = <span className="text-ink-faint">—</span>;
  return (
    <Card className="p-5">
      <h2 className="mb-3 flex items-center gap-2 text-base font-semibold">
        <Gauge size={16} className="text-ink-faint" /> Performance
      </h2>
      <div className="grid grid-cols-2 gap-2">
        <Stat icon={<Play size={13} />} label="Processing" value={perf.processingFps !== null ? `${perf.processingFps} fps` : dash} sub="frames analyzed per second" />
        <Stat
          icon={<MonitorSmartphone size={13} />}
          label="LED updates"
          value={perf.ledFps !== null ? `${perf.ledFps} /s` : dash}
          sub={perf.controllers.length > 1 ? 'first device, all listed below' : perf.ledDropped ? <span className="text-warn">{perf.ledDropped} dropped</span> : 'sent to the device'}
        />
        <Stat icon={<Cpu size={13} />} label="CPU" value={perf.cpuPercent !== null ? `${perf.cpuPercent}%` : dash} sub="whole PC" />
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

/** LED updates per device, when more than one is fed. */
function ControllerRates({ controllers }: { controllers: ControllerPerformance[] }) {
  return (
    <div className="mt-3 overflow-hidden rounded-xl border border-line">
      <table className="w-full text-[13px]">
        <caption className="sr-only">LED updates per device</caption>
        <thead className="bg-surface-2 text-left text-xs text-ink-faint">
          <tr>
            <th className="px-3 py-2 font-medium">Device</th>
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

/** "Lights look wrong?": a small link to the troubleshooter. */
function TroubleshooterLink() {
  const { setup, serverInfo } = useConnection();
  const expert = useExpertTools();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button size="sm" variant="ghost" icon={<CircleHelp size={15} />} onClick={() => setOpen(true)}>
        Lights look wrong?
      </Button>
      <TroubleshooterDialog open={open} onClose={() => setOpen(false)} held={setup?.outputHeld === true} expert={expert} game={serverInfo?.mode?.mode === 'game'} />
    </>
  );
}

export function HomePage() {
  const { serverInfo, sysInfo, backend } = useConnection();
  const { state, load } = useEditor();
  const expert = useExpertTools();
  const [adding, setAdding] = useState(false);
  const ledTotal = state.spec.strips.reduce((n, s) => n + stripLedCount(s), 0);
  const host = sysInfo?.system?.hostName ?? serverInfo?.hostname;
  // a new install has only the test device (colors go nowhere): one way on instead of controls that do nothing
  const noLights = !expert && (load.status === 'ready' || load.status === 'error') && !hasRealDevice(state.spec.controllers);

  return (
    <div className="scroll-thin h-full overflow-y-auto">
      <PageHeader
        title="Home"
        badge={<ModeBadge />}
        subtitle={host ? `Running on ${host}` : undefined}
        actions={
          <>
            <ModeSwitch className="sm:hidden" />
            <TroubleshooterLink />
            {backend.mode === 'mock' && <Badge tone="accent">Demo data</Badge>}
          </>
        }
      />
      {noLights && (
        <div className="mx-auto max-w-3xl p-4 sm:p-6">
          <NoLights onAdd={() => setAdding(true)} />
        </div>
      )}
      <div className={cx('mx-auto grid max-w-6xl gap-4 p-4 sm:p-6 lg:grid-cols-[minmax(0,1.45fr)_minmax(0,1fr)]', noLights && 'hidden')}>
        <div className="space-y-4">
          <LightsCard />
          <DeviceHealth />
          {serverInfo?.mode?.mode === 'game' ? (
            <PlayingCard />
          ) : (
            <>
              {serverInfo?.mode?.dismissedGame && (
                <Callout icon={<Gamepad2 size={16} />}>
                  Game mode is off for “{serverInfo.mode.dismissedGame}” until it quits. Choose Game to turn it back on.
                </Callout>
              )}
              <ShowCard />
            </>
          )}
          <PreviewCard />
        </div>
        <div className="space-y-4">
          <PresetCard />
          <a href={hrefFor('layout')} className="group block">
            <Card className="flex items-center gap-4 p-5 transition-colors group-hover:border-line-strong">
              <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-surface-3 text-ink-muted">
                <MonitorSmartphone size={22} />
              </div>
              <div className="min-w-0 flex-1">
                <p className="font-semibold">Layout</p>
                <p className="text-[13px] text-ink-muted">
                  {state.spec.strips.length
                    ? `${state.spec.strips.length} strip${state.spec.strips.length === 1 ? '' : 's'} · ${ledTotal} LEDs in your room`
                    : 'Show Backbloom where your strips are.'}
                </p>
              </div>
              <ArrowRight size={18} className="text-ink-faint transition-transform group-hover:translate-x-0.5 group-hover:text-ink" />
            </Card>
          </a>
          <Expert>
            <PerformanceCard />
            <p className="px-1 text-xs text-ink-faint">
              {sysInfo?.backbloom?.version ? `Version ${sysInfo.backbloom.version} · ` : ''}
              {backend.url}
            </p>
          </Expert>
        </div>
      </div>
      {/* outside No lights yet: adding the first device hides that, and the dialog goes on with Your lights */}
      <AddDeviceDialog open={adding} onClose={() => setAdding(false)} />
    </div>
  );
}
