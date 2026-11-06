import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowRight, Bookmark, CircleHelp, Cpu, Gauge, MemoryStick, MonitorSmartphone, Play, Sliders, Sun, TriangleAlert } from 'lucide-react';
import { Badge, Button, Card, cx, rangeFill } from '../components/ui';
import { Expert, Switch } from '../components/form';
import { PageHeader } from '../components/PageHeader';
import { useConnection } from '../app/connection';
import { useConfig } from '../app/config';
import { useEditor } from '../app/editor';
import { useAction } from '../app/useAction';
import { useThrottled } from '../app/useThrottled';
import { hrefFor } from '../app/router';
import { stripLedCount } from '../layout';
import { usePerformance } from '../live/hooks';
import type { ControllerPerformance } from '../live/performance';
import { deviceProblems } from '../control/health';
import { TroubleshooterDialog } from '../control/Troubleshooter';
import { useExpertTools } from '../app/expert';
import { PresetPicker } from '../control/PresetPicker';
import { PlayingPreview } from '../control/PlayingPreview';
import { SetupSwitch } from '../control/SetupSwitch';
import { useLookPick, useSetupInUse } from '../control/usePick';
import { gamingPreset, playLabel, presetStatus, statusText } from '../control/presets';
import { lookLabel, setupLook } from '../control/setup';
import { LOOKS } from '../settings/intensity';
import { brightnessForScale, scaleForBrightness } from '../settings/brightness';
import { NoLights, hasRealDevice } from '../setup/NoLights';
import { AddDeviceDialog } from './devices/AddDeviceDialog';

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

/** The main card: lights on or off, and the brightness. */
function LightsCard() {
  const { serverInfo, api, setup } = useConnection();
  const { run, busy } = useAction();
  const comp = (name: string) => serverInfo?.components?.find((c) => c.name === name)?.enabled;
  const on = comp('ALL') !== false;
  // switched on, but nothing reaches the LEDs until "Turn on lights" (or while HyperHDR runs)
  const waiting = on && setup?.outputHeld === true;
  const manual = serverInfo?.priorities_autoselect === false;
  return (
    <Card className="p-5">
      <div className="flex flex-wrap items-center gap-5">
        <Switch size="lg" label="Lights" checked={on} disabled={busy === 'power'} onChange={(v) => void run('power', 'Couldn’t switch the lights', () => api.setComponent('ALL', v))} />
        <div className="min-w-0 flex-1">
          <p className="text-lg font-semibold">
            {waiting ? (setup?.otherApp ? `Waiting for ${setup.otherApp.name} to quit` : 'Waiting — turn on your lights to start') : on ? 'Lights are on' : 'Lights are off'}
          </p>
          {!on && <p className="text-[13px] text-ink-muted">Your lights are off. Turn them on to light things up again.</p>}
        </div>
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
 * Brightness 0–100%: `scaleOutput` along the curve in settings/brightness.ts (at 50% the lights give about a quarter
 * of their full output). A value boosted above 100% (Colors, expert) is shown as it is; it only changes when the slider
 * is moved.
 */
function Brightness() {
  const { serverInfo, api, auth } = useConnection();
  const scale = serverInfo?.adjustment?.[0]?.scaleOutput ?? 1;
  const gamma = serverInfo?.adjustment?.[0]?.gamma;
  const stored = brightnessForScale(scale, gamma);
  const [dragging, setDragging] = useState<number | null>(null);
  // the `adjustment` command applies the value and stores it; it needs a signed-in session
  const send = useThrottled((v: number) => {
    void api.adjust({ scaleOutput: scaleForBrightness(v, gamma) }).catch(() => undefined);
  }, 100);
  const releaseTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const canEdit = auth === 'admin';
  // Show the dragged value until the backend's update has caught up.
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
          {Math.round(Math.min(1, value) * 100)}%{value > 1.005 && <span className="text-ink-faint"> (boosted)</span>}
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
        aria-valuetext={`${Math.round(Math.min(1, value) * 100)}%${value > 1.005 ? ' (boosted)' : ''}`}
        style={rangeFill(Math.min(1, value), 0, 1)}
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

/**
 * The setup in use, Everyday or a preset, as one switch (always one in use), the line "Screen edges · Intense · In use
 * while witcher3.exe runs", and the Look of the setup in use (a preset's own, else the saved one): a pick here saves
 * to it, as the Colors page's fine-tuning does.
 */
function SetupCard() {
  const { serverInfo } = useConnection();
  const { config } = useConfig();
  const { state, load } = useEditor();
  const { preset, name, plays } = useSetupInUse();
  const pickLook = useLookPick();
  const [picking, setPicking] = useState(false);
  const presets = state.spec.profiles ?? [];
  const active = serverInfo?.profile ?? null;
  const dismissed = serverInfo?.dismissedProfile ?? null;
  const off = dismissed ? presets.find((p) => p.id === dismissed.id) : undefined;
  const look = setupLook(config, preset);
  const lower = (t: string | null) => (t ? t.charAt(0).toLowerCase() + t.slice(1) : null);
  const status = preset ? lower(statusText(preset, presetStatus(preset.id, active, dismissed))) : off ? `${off.name}: ${lower(statusText(off, 'dismissed'))}` : null;
  const addGame = preset && preset.id === gamingPreset(state.spec)?.id && preset.apps.length === 0;
  return (
    <div id="look">
      <Card className="p-5">
        <div className="mb-3 flex items-center justify-between gap-2">
          <h2 className="flex items-center gap-2 text-base font-semibold">
            <Bookmark size={16} className="text-ink-faint" /> Preset
          </h2>
          <a className={cx('text-[13px]', LINK)} href={hrefFor('presets')}>
            {presets.length ? 'All presets' : 'Make a preset'}
          </a>
        </div>
        {load.status === 'ready' && presets.length > 0 && <SetupSwitch />}
        <p className="mt-3 text-[13px] text-ink-muted">
          {load.status !== 'ready' && !active ? (
            'Loading your presets…'
          ) : (
            <>
              <span className="font-medium text-ink">{name}</span>: {[playLabel(plays), lookLabel(look)].filter(Boolean).join(' · ')}
              {status && <> · {status}</>}.
              {addGame && (
                <>
                  {' '}
                  <a className={LINK} href={hrefFor('presets')}>
                    Add your game
                  </a>
                </>
              )}
            </>
          )}
        </p>
        <div className="mt-4 border-t border-line pt-4">
          <h3 className="mb-2 flex items-center gap-2 text-sm font-semibold">
            <Sliders size={15} className="text-ink-faint" /> Look
            {look === 'custom' && <Badge>Custom</Badge>}
          </h3>
          <PresetPicker
            label="Look"
            presets={LOOKS}
            current={look}
            disabled={!config || picking || (!!active && !preset)}
            onPick={(id) => {
              setPicking(true);
              void pickLook(id).finally(() => setPicking(false));
            }}
            customHint={
              <>
                Fine-tuned on{' '}
                <a className={LINK} href={hrefFor('colors')}>
                  Colors
                </a>
                . Pick one to start over from it.
              </>
            }
          />
          <p className="mt-1 text-xs text-ink-faint">Changes save to {name}.</p>
        </div>
      </Card>
    </div>
  );
}

function Stat({ icon, label, value, sub }: { icon: ReactNode; label: string; value: ReactNode; sub?: ReactNode }) {
  return (
    <div className="rounded-2xl border border-line bg-white/[0.03] p-3.5">
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
  const { setup } = useConnection();
  const expert = useExpertTools();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button size="sm" variant="ghost" icon={<CircleHelp size={15} />} onClick={() => setOpen(true)}>
        Lights look wrong?
      </Button>
      <TroubleshooterDialog open={open} onClose={() => setOpen(false)} held={setup?.outputHeld === true} expert={expert} />
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
        subtitle={host ? `Running on ${host}` : undefined}
        actions={
          <>
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
          <PlayingPreview title="Playing now" change glow />
        </div>
        <div className="space-y-4">
          <SetupCard />
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
