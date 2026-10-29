import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowLeft, ArrowRight, Check, CheckCircle2, Cpu, Loader2, Plus, Power } from 'lucide-react';
import { Button, cx } from '../../components/ui';
import { Advanced } from '../../components/form';
import { Logo } from '../../components/Logo';
import { useConnection } from '../../app/connection';
import { useConfig } from '../../app/config';
import { useEditor } from '../../app/editor';
import { useExpertTools } from '../../app/expert';
import { hrefFor } from '../../app/router';
import type { Controller } from '../../layout';
import type { BackendConfig, ServerInfo, SetupState } from '../../api/types';
import { AddDeviceDialog } from '../devices/AddDeviceDialog';
import { isTestDevice } from '../devices/drivers';
import { OtherAppNotice } from './OtherAppNotice';
import { deviceLine, importedItems, welcomeSummary } from './summary';

/** The setup's steps: welcome, the lights' device, turn them on. */
export const STEPS = ['Welcome', 'Your lights', 'Turn on'] as const;
export type Step = 0 | 1 | 2;

/** Where "Turn on lights" leads: add a device, place the strips, or Home when everything is set up. */
export type NextPage = 'devices' | 'layout' | 'home';

/** Devices: add one when there is none (or only the test device); Layout: until a layout is saved; else Home. */
export function nextPage(controllers: readonly Controller[], layoutSaved: boolean, unsaved: boolean): NextPage {
  if (!controllers.some((c) => !isTestDevice(c))) return 'devices';
  return !layoutSaved || unsaved ? 'layout' : 'home';
}

export interface WelcomeViewProps {
  setup: SetupState;
  config: BackendConfig | null;
  serverInfo: ServerInfo | null;
  step: Step;
  onStep(step: Step): void;
  /** The layout's devices (the test device of a new install included); null while loading. */
  devices: readonly Controller[] | null;
  onAddDevice(): void;
  next: NextPage;
  /** Expert tools are on: show the technical details. */
  expert: boolean;
  starting: boolean;
  error: string | null;
  autostart: boolean;
  onAutostart(v: boolean): void;
  onStart(): void;
  onLookAround(): void;
  /** HyperHDR quit while the setup was open. */
  otherAppQuit: boolean;
}

function StepDots({ step }: { step: Step }) {
  return (
    <ol className="flex items-center gap-2 text-xs text-ink-faint" aria-label={`Step ${step + 1} of ${STEPS.length}`}>
      {STEPS.map((label, i) => (
        <li key={label} className="flex items-center gap-2" aria-current={i === step ? 'step' : undefined}>
          <span
            className={cx(
              'flex h-5 w-5 items-center justify-center rounded-full text-[11px] font-semibold',
              i < step ? 'bg-accent/20 text-accent-strong' : i === step ? 'bg-accent text-accent-ink' : 'bg-surface-3 text-ink-muted',
            )}
            aria-hidden
          >
            {i < step ? <Check size={12} /> : i + 1}
          </span>
          <span className={cx('hidden sm:inline', i === step && 'font-medium text-ink')}>{label}</span>
          {i < STEPS.length - 1 && <span className="h-px w-5 bg-line-strong" aria-hidden />}
        </li>
      ))}
    </ol>
  );
}

function Heading({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div>
      <h1 id="welcome-title" tabIndex={-1} style={{ outline: 'none' }} className="text-2xl font-semibold">
        {title}
      </h1>
      {children && <p className="mt-1.5 text-sm text-ink-muted">{children}</p>}
    </div>
  );
}

function WelcomeStep(p: WelcomeViewProps) {
  const { setup } = p;
  const leds = (p.devices ?? []).filter((c) => !isTestDevice(c)).reduce((n, c) => n + c.outputs.reduce((m, o) => m + o.length, 0), 0);
  return (
    <>
      <Heading title="Welcome to Backbloom">Backbloom makes your LED strips glow along with your screen, your music or an effect. Check your lights, then turn them on.</Heading>
      {setup.otherApp && <OtherAppNotice app={setup.otherApp} />}
      {!setup.otherApp && p.otherAppQuit && (
        <p role="status" className="flex items-center gap-2 rounded-xl border border-ok/30 bg-ok/10 px-4 py-3 text-[13px] text-ink">
          <CheckCircle2 size={16} className="shrink-0 text-ok" aria-hidden /> HyperHDR has quit. Backbloom can take over your lights now.
        </p>
      )}
      {setup.importedFrom && (
        <section aria-labelledby="welcome-imported">
          <h2 id="welcome-imported" className="mb-2 text-sm font-semibold">
            Copied from HyperHDR
          </h2>
          {p.devices ? (
            <ul className="space-y-1.5 text-sm">
              {importedItems(p.devices, leds).map((item) => (
                <li key={item} className="flex items-start gap-2">
                  <Check size={16} className="mt-0.5 shrink-0 text-accent-strong" aria-hidden />
                  {item}
                </li>
              ))}
            </ul>
          ) : (
            <Loading />
          )}
        </section>
      )}
      {!setup.importedFrom && (
        <section aria-labelledby="welcome-ready">
          <h2 id="welcome-ready" className="mb-2 text-sm font-semibold">
            Before you start
          </h2>
          <ul className="space-y-1.5 text-sm">
            {['Your LED strips are plugged into their device, and it’s switched on.', 'A Wi-Fi device such as WLED is on the same network as this PC.'].map((item) => (
              <li key={item} className="flex items-start gap-2">
                <Check size={16} className="mt-0.5 shrink-0 text-accent-strong" aria-hidden />
                {item}
              </li>
            ))}
          </ul>
        </section>
      )}
      {p.expert && <TechnicalDetails {...p} />}
    </>
  );
}

/** The settings in detail, with expert tools on: LEDs, devices, capture, startup effects, the imported database. */
function TechnicalDetails(p: WelcomeViewProps) {
  const rows = welcomeSummary(p.config, p.serverInfo);
  return (
    <Advanced label="Technical details">
      <dl className="divide-y divide-line text-[13px]">
        {rows.map((r) => (
          <div key={r.label} className="grid gap-1 py-2 sm:grid-cols-[150px_1fr]">
            <dt className="text-ink-muted">{r.label}</dt>
            <dd className="min-w-0 break-words">
              {r.value}
              {r.details && (
                <ul className="mt-1 space-y-0.5 text-ink-muted">
                  {r.details.map((d) => (
                    <li key={d}>{d}</li>
                  ))}
                </ul>
              )}
            </dd>
          </div>
        ))}
        {p.setup.importedFrom && (
          <div className="grid gap-1 py-2 sm:grid-cols-[150px_1fr]">
            <dt className="text-ink-muted">Imported from</dt>
            <dd className="min-w-0 break-all font-mono text-xs text-ink-muted">{p.setup.importedFrom}</dd>
          </div>
        )}
      </dl>
    </Advanced>
  );
}

function Loading() {
  return (
    <p className="flex items-center gap-2 text-sm text-ink-muted">
      <Loader2 size={15} className="animate-spin" aria-hidden /> Looking at your setup…
    </p>
  );
}

function LightsStep(p: WelcomeViewProps) {
  const real = p.devices?.filter((c) => !isTestDevice(c)) ?? [];
  return (
    <>
      <Heading title="Your lights">The device your LED strips are plugged into, such as a WLED controller.</Heading>
      {!p.devices ? (
        <Loading />
      ) : real.length === 0 ? (
        <div className="flex flex-col items-center gap-3 rounded-xl border border-dashed border-line-strong px-6 py-8 text-center">
          <Cpu size={28} className="text-ink-faint" aria-hidden />
          <div>
            <p className="text-sm font-medium">No device yet</p>
            <p className="mt-1 max-w-sm text-[13px] text-ink-muted">Add the device your strips are plugged into. Backbloom can search your network for WLED.</p>
          </div>
          <Button variant="primary" icon={<Plus size={16} />} onClick={p.onAddDevice}>
            Add device
          </Button>
        </div>
      ) : (
        <div className="space-y-3">
          <ul className="divide-y divide-line border-y border-line">
            {real.map((c) => {
              const leds = c.outputs.reduce((n, o) => n + o.length, 0);
              return (
                <li key={c.id} className="flex items-center gap-3 py-3">
                  <Cpu size={20} className="shrink-0 text-ink-faint" aria-hidden />
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium">{deviceLine(c)}</p>
                    <p className="text-xs text-ink-faint">
                      {leds} LED{leds === 1 ? '' : 's'}
                      {c.outputs.length > 1 ? ` on ${c.outputs.length} outputs` : ''}
                    </p>
                  </div>
                </li>
              );
            })}
          </ul>
          <Button size="sm" icon={<Plus size={15} />} onClick={p.onAddDevice}>
            Add another device
          </Button>
        </div>
      )}
    </>
  );
}

const NEXT_TEXT: Record<NextPage, string> = {
  devices: 'Then add the device your strips are plugged into on the Devices page.',
  layout: 'Then show Backbloom where your strips are, so their colors match your screen.',
  home: 'Then pick what they show on Home: your screen, music, an effect or a color.',
};

function StartStep(p: WelcomeViewProps) {
  const { setup } = p;
  return (
    <>
      <Heading title="Turn on your lights">Your lights stay as they are until you turn them on here. {NEXT_TEXT[p.next]}</Heading>
      {setup.otherApp && (
        <p className="text-[13px] text-warn">{setup.otherApp.name} still runs: Backbloom takes over as soon as you quit it.</p>
      )}
      {setup.autostart.available && (
        <label className="flex cursor-pointer items-start gap-3 text-sm">
          <input
            type="checkbox"
            className="mt-0.5 h-4 w-4 accent-[var(--color-accent)]"
            checked={p.autostart}
            onChange={(e) => p.onAutostart(e.target.checked)}
          />
          <span>
            <span className="font-medium text-ink">Start with Windows</span>
            <span className="block text-xs text-ink-faint">Off unless you check it. You can change it later in Settings or from the tray icon.</span>
            {setup.importedFrom && (
              <span className="mt-1 block text-xs text-ink-faint">
                If HyperHDR starts with Windows, switch it off in Windows Settings → Apps → Startup, so only one app drives your lights.
              </span>
            )}
          </span>
        </label>
      )}
      {p.error && (
        <p role="alert" className="text-[13px] text-bad">
          {p.error}
        </p>
      )}
    </>
  );
}

/** The first-run setup as a pure view (see {@link WelcomeScreen}). */
export function WelcomeView(p: WelcomeViewProps) {
  const { step } = p;
  const noDevice = step === 1 && !!p.devices && !p.devices.some((c) => !isTestDevice(c));
  // focus moves to the title of each new step (not the first), so screen readers announce it
  const firstStep = useRef(step);
  const moved = useRef(false);
  if (step !== firstStep.current) moved.current = true;
  useEffect(() => {
    if (moved.current) document.getElementById('welcome-title')?.focus({ preventScroll: true });
  }, [step]);
  return (
    <div className="flex h-full justify-center overflow-y-auto p-4 sm:p-8">
      <main aria-labelledby="welcome-title" className="my-auto flex w-full max-w-xl flex-col gap-5 rounded-2xl border border-line bg-surface p-6 shadow-2xl sm:p-8">
        <header className="flex flex-wrap items-center justify-between gap-3">
          <span className="flex items-center gap-2.5">
            <Logo size={28} />
            <span className="text-[15px] font-semibold tracking-tight">Backbloom</span>
          </span>
          <StepDots step={step} />
        </header>

        <div className="min-h-[12rem] space-y-5">
          {step === 0 && <WelcomeStep {...p} />}
          {step === 1 && <LightsStep {...p} />}
          {step === 2 && <StartStep {...p} />}
        </div>

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-5">
          <Button variant="ghost" onClick={p.onLookAround}>
            Look around first
          </Button>
          <div className="flex flex-wrap gap-2">
            {step > 0 && (
              <Button variant="ghost" icon={<ArrowLeft size={16} />} onClick={() => p.onStep((step - 1) as Step)}>
                Back
              </Button>
            )}
            {step < 2 ? (
              <Button variant={noDevice ? 'secondary' : 'primary'} onClick={() => p.onStep((step + 1) as Step)}>
                {noDevice ? 'Skip for now' : 'Next'} <ArrowRight size={16} />
              </Button>
            ) : (
              <Button
                variant="primary"
                icon={p.starting ? <Loader2 size={16} className="animate-spin" /> : <Power size={16} />}
                disabled={p.starting}
                onClick={p.onStart}
              >
                Turn on lights
              </Button>
            )}
          </div>
        </footer>
      </main>
    </div>
  );
}

/**
 * Shown instead of the app until the lights were turned on once (a new install or a HyperHDR import): welcome, the
 * device, turn on. Then the app opens where setup goes on (see {@link nextPage}).
 */
export function WelcomeScreen({ onLookAround }: { onLookAround(): void }) {
  const { setup, serverInfo, api, applySetup } = useConnection();
  const { config } = useConfig();
  const { state, load, dirty } = useEditor();
  const expert = useExpertTools();
  const [step, setStep] = useState<Step>(0);
  const [adding, setAdding] = useState(false);
  const [autostart, setAutostart] = useState(false);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sawOtherApp, setSawOtherApp] = useState(false);
  if (!setup) return null;
  if (setup.otherApp && !sawOtherApp) setSawOtherApp(true);

  const devices = load.status === 'ready' || load.status === 'error' ? state.spec.controllers : null;
  const next = devices ? nextPage(devices, load.source === 'saved', dirty) : 'home';

  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      const done = await api.startLighting(setup.autostart.available ? autostart : undefined);
      window.location.hash = hrefFor(next);
      applySetup(done);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  };

  return (
    <>
      <WelcomeView
        setup={setup}
        config={config}
        serverInfo={serverInfo}
        step={step}
        onStep={setStep}
        devices={devices}
        onAddDevice={() => setAdding(true)}
        next={next}
        expert={expert}
        starting={starting}
        error={error}
        autostart={autostart}
        onAutostart={setAutostart}
        onStart={() => void start()}
        onLookAround={onLookAround}
        otherAppQuit={sawOtherApp && !setup.otherApp}
      />
      <AddDeviceDialog open={adding} onClose={() => setAdding(false)} onAdded={() => setAdding(false)} />
    </>
  );
}
