import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowLeft, ArrowRight, Check, CheckCircle2, Loader2, Power } from 'lucide-react';
import { Button, CARD, cx } from '../../components/ui';
import { Advanced } from '../../components/form';
import { Logo } from '../../components/Logo';
import { useConnection } from '../../app/connection';
import { useConfig } from '../../app/config';
import { useEditor } from '../../app/editor';
import { useExpertTools } from '../../app/expert';
import { useToast } from '../../app/toast';
import { hrefFor } from '../../app/router';
import { defaultRoom, type Controller, type LayoutSpec, type PathPoint } from '../../layout';
import type { BackendConfig, ServerInfo, SetupState } from '../../api/types';
import { usePreviewFrame } from '../../live/hooks';
import { AddDeviceDialog } from '../devices/AddDeviceDialog';
import { SetupChoices, type AddStart } from '../../setup/SetupChoices';
import { YourLights } from '../../setup/YourLights';
import { pathWithRole, roleRows, type RoleRow, type SimpleRole } from '../../setup/roles';
import { TraceStrip, type TraceFrame } from '../../setup/TraceStrip';
import { applyLook, detectLook, type LookId } from '../../settings/intensity';
import { scaleForBrightness } from '../../settings/brightness';
import { isTestDevice } from '../devices/drivers';
import { addGameToGaming } from '../../control/presets';
import { OtherAppNotice } from './OtherAppNotice';
import { FeelStepBody, RolesStepBody, type FeelProps } from './ReadySteps';
import { addedThisSession, fittedScreen, monitorText, shownSteps, sizedMonitor, stepAfter, STEPS, type Step } from './ready';
import { importedItems, welcomeSummary } from './summary';

export { STEPS, type Step } from './ready';

/**
 * The `scaleOutput` a new install's lights start at when they are turned on: Home's Brightness at 60% (about a third
 * of the full light with the default contrast, as the 0.2 it used before).
 */
export const FIRST_BRIGHTNESS = scaleForBrightness(0.6);

/** The brightness to set when the lights are turned on: a new install still at full gets {@link FIRST_BRIGHTNESS}. */
export function firstBrightness(importedFrom: string | null | undefined, current: number | undefined): number | null {
  return !importedFrom && (current ?? 1) >= 0.999 ? FIRST_BRIGHTNESS : null;
}

/** Where "Turn on lights" leads: add a device, place the strips, or Home when everything is set up. */
export type NextPage = 'devices' | 'layout' | 'home';

/**
 * Devices: add one when there is none (or only the test device); Layout when the layout can't be saved as it is;
 * else Home (Turn on saves the layout first).
 */
export function nextPage(controllers: readonly Controller[], canSave: boolean): NextPage {
  if (!controllers.some((c) => !isTestDevice(c))) return 'devices';
  return canSave ? 'home' : 'layout';
}

/** The trace of a strip, shown in What each light does instead of its rows (see TraceStrip). */
export type TraceView = Parameters<TraceFrame>[0];

export interface WelcomeViewProps {
  setup: SetupState;
  config: BackendConfig | null;
  serverInfo: ServerInfo | null;
  step: Step;
  onStep(step: Step): void;
  /** The layout's devices (the test device of a new install included); null while loading. */
  devices: readonly Controller[] | null;
  /** The layout, for Your lights and What each light does (default: just the devices). */
  spec?: LayoutSpec | null;
  /** Opens Add device: its choice, or one of its flows. */
  onAddDevice(start?: AddStart): void;
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
  /** What each light does: the monitor in words, null while unknown. */
  monitor?: string | null;
  /** The live LED colors for the room picture. */
  frame?: readonly number[] | null;
  /** The light picked out in the room picture. */
  selected?: string | null;
  onSelect?(stripId: string): void;
  onRole?(row: RoleRow, role: SimpleRole): void;
  onTrace?(row: RoleRow): void;
  /** A strip being traced: shown instead of What each light does, with its own buttons. */
  trace?: TraceView | null;
  /** The Look and a game preset; null while the settings load. */
  feel?: FeelProps | null;
  /** "Skip — use the defaults" of What each light does and Look. */
  onSkip?(step: Step): void;
}

function StepDots({ step, shown }: { step: Step; shown: readonly Step[] }) {
  const at = shown.indexOf(step);
  return (
    <ol className="flex items-center gap-2 text-xs text-ink-faint" aria-label={`Step ${at + 1} of ${shown.length}`}>
      {shown.map((s, i) => (
        <li key={s} className="flex items-center gap-2" aria-current={i === at ? 'step' : undefined}>
          <span
            className={cx(
              'flex h-5 w-5 items-center justify-center rounded-full text-[11px] font-semibold',
              i < at ? 'bg-accent/20 text-accent-strong' : i === at ? 'bg-accent text-accent-ink' : 'bg-surface-3 text-ink-muted',
            )}
            aria-hidden
          >
            {i < at ? <Check size={12} /> : i + 1}
          </span>
          {i === at && <span className="hidden font-medium text-ink sm:inline">{STEPS[s]}</span>}
          {i < shown.length - 1 && <span className="h-px w-4 bg-line-strong" aria-hidden />}
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
        <p role="status" className="flex items-center gap-2 rounded-2xl border border-ok/30 bg-ok/10 px-4 py-3 text-[13px] text-ink">
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

/** The layout to show: the editor's, else one with just the devices. */
function specOf(p: WelcomeViewProps): LayoutSpec | null {
  if (p.spec) return p.spec;
  return p.devices ? { version: 1, room: defaultRoom(), controllers: [...p.devices], strips: [] } : null;
}

/** Your lights: what is added so far, and the ways to add more (the hub each board and room lights come back to). */
function LightsStep(p: WelcomeViewProps) {
  const spec = specOf(p);
  const real = spec?.controllers.filter((c) => !isTestDevice(c)) ?? [];
  return (
    <>
      <Heading title="Your lights">
        {real.length ? 'Everything you added so far. More strips on another board, or room lights? Add them now, or go on.' : 'Your LED strips and room lights, and how Backbloom reaches them.'}
      </Heading>
      {!spec ? (
        <Loading />
      ) : real.length === 0 ? (
        <section aria-labelledby="welcome-add" className="space-y-3">
          <h2 id="welcome-add" className="text-sm font-semibold">
            What do you have?
          </h2>
          <SetupChoices onPick={(kind) => p.onAddDevice(kind)} />
        </section>
      ) : (
        <YourLights spec={spec} withAddress={p.expert} onAdd={(kind) => p.onAddDevice(kind)} />
      )}
    </>
  );
}

function RolesStep(p: WelcomeViewProps) {
  const spec = specOf(p);
  if (p.trace)
    return (
      <>
        <Heading title={p.trace.title}>{p.trace.intro}</Heading>
        {p.trace.children}
      </>
    );
  const rows = spec ? roleRows(spec) : [];
  return (
    <>
      <Heading title="What each light does">
        Every light starts with the screen’s average color, soft and right whichever way it runs. Pick Screen edge to make a strip follow the part of the
        screen next to it: you show Backbloom where it runs. Each choice shows in the picture at once; more choices on Layout.
      </Heading>
      {spec ? (
        <RolesStepBody
          spec={spec}
          rows={rows}
          frame={p.frame ?? null}
          monitor={p.monitor ?? null}
          selected={p.selected ?? rows[0]?.stripId ?? null}
          onSelect={(id) => p.onSelect?.(id)}
          onRole={(r, role) => p.onRole?.(r, role)}
          onTrace={(r) => p.onTrace?.(r)}
        />
      ) : (
        <Loading />
      )}
    </>
  );
}

function FeelStep(p: WelcomeViewProps) {
  return (
    <>
      <Heading title="Look">How your lights look: calm and soft, or quick and vivid. You can change it on Home any time.</Heading>
      {p.feel ? <FeelStepBody {...p.feel} /> : <Loading />}
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

const SKIP = 'Skip — use the defaults';

/** The footer's buttons for a step: what goes on (one primary), and a way past it. */
function StepActions(p: WelcomeViewProps & { hasLights: boolean }) {
  const { step, hasLights } = p;
  if (step === 2 && p.trace) return <>{p.trace.actions}</>;
  const next = () => p.onStep(stepAfter(step, hasLights));
  const primary = (label: string, onClick = next) => (
    <Button variant="primary" onClick={onClick}>
      {label} <ArrowRight size={16} />
    </Button>
  );
  switch (step) {
    case 1:
      return !p.devices || hasLights ? (
        primary('Next')
      ) : (
        <Button variant="secondary" onClick={next}>
          Skip for now <ArrowRight size={16} />
        </Button>
      );
    case 2:
    case 3:
      return (
        <>
          <Button variant="ghost" onClick={() => p.onSkip?.(step)}>
            {SKIP}
          </Button>
          {primary('Next')}
        </>
      );
    case 4:
      return (
        <Button variant="primary" icon={p.starting ? <Loader2 size={16} className="animate-spin" /> : <Power size={16} />} disabled={p.starting} onClick={p.onStart}>
          Turn on lights
        </Button>
      );
    default:
      return primary('Next');
  }
}

/** The first-run setup as a pure view (see {@link WelcomeScreen}). */
export function WelcomeView(p: WelcomeViewProps) {
  const { step } = p;
  const hasLights = !!p.devices && p.devices.some((c) => !isTestDevice(c));
  const shown = shownSteps(hasLights);
  // focus moves to the title of each new step (not the first), so screen readers announce it
  const firstStep = useRef(step);
  const moved = useRef(false);
  if (step !== firstStep.current) moved.current = true;
  useEffect(() => {
    if (moved.current) document.getElementById('welcome-title')?.focus({ preventScroll: true });
  }, [step]);
  return (
    <div className="app-glow flex h-full justify-center overflow-y-auto p-4 sm:p-8">
      <main aria-labelledby="welcome-title" className={cx(CARD, 'my-auto flex w-full max-w-2xl flex-col gap-5 p-6 sm:p-8')}>
        <header className="flex flex-wrap items-center justify-between gap-3">
          <span className="flex items-center gap-2.5">
            <Logo size={28} />
            <span className="text-[15px] font-semibold tracking-tight">Backbloom</span>
          </span>
          <StepDots step={step} shown={shown} />
        </header>

        <div className="min-h-[12rem] space-y-5">
          {step === 0 && <WelcomeStep {...p} />}
          {step === 1 && <LightsStep {...p} />}
          {step === 2 && <RolesStep {...p} />}
          {step === 3 && <FeelStep {...p} />}
          {step === 4 && <StartStep {...p} />}
        </div>

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-5">
          <Button variant="ghost" onClick={p.onLookAround}>
            Look around first
          </Button>
          <div className="flex flex-wrap gap-2">
            {step > 0 && !p.trace && (
              <Button variant="ghost" icon={<ArrowLeft size={16} />} onClick={() => p.onStep(stepAfter(step, hasLights, -1))}>
                Back
              </Button>
            )}
            <StepActions {...p} hasLights={hasLights} />
          </div>
        </footer>
      </main>
    </div>
  );
}

/**
 * Saves layouts in the editor's one queue (choices made quickly in a row save once more at the end, never out of
 * order), without a message unless it fails; a layout that can't be saved as it is waits for Turn on.
 */
function useLayoutSaver() {
  const { quietSave, saveIdle } = useEditor();
  return { save: (spec: LayoutSpec) => quietSave(spec, { quietIfBlocked: true }), idle: saveIdle };
}

/**
 * Shown instead of the app until the lights were turned on once (a new install or a HyperHDR import): welcome, the
 * lights (Your lights, where each board and room lights come back to), What each light does (each choice saved at
 * once, so the room picture shows it), Look, turn on. Turn on saves the layout first (unless it can't be
 * saved as it is); then the app opens where setup goes on (see {@link nextPage}).
 */
export function WelcomeScreen({ onLookAround }: { onLookAround(): void }) {
  const { setup, serverInfo, api, applySetup } = useConnection();
  const { config } = useConfig();
  const { state, dispatch, load, dirty, blockers, save } = useEditor();
  const expert = useExpertTools();
  const toast = useToast();
  const [step, setStep] = useState<Step>(0);
  const [adding, setAdding] = useState<AddStart | null>(null);
  const [autostart, setAutostart] = useState(false);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sawOtherApp, setSawOtherApp] = useState(false);
  const [monitor, setMonitor] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [tracing, setTracing] = useState<RoleRow | null>(null);
  const [feelBusy, setFeelBusy] = useState(false);
  const saver = useLayoutSaver();
  // live colors for What each light does: the layout as saved, so each choice shows
  const { frame } = usePreviewFrame(step === 2 && !tracing);
  const specRef = useRef(state.spec);
  specRef.current = state.spec;
  const screensAsked = useRef(false);
  /** The strips' paths as added (every light the screen's average), what Skip on What each light does goes back to. */
  const defaults = useRef<Map<string, PathPoint[]> | null>(null);
  /** The devices the layout had when it loaded: the setup saves by itself only once it added others. */
  const loaded = useRef<ReadonlySet<string> | null>(null);
  const loadPrograms = useCallback(() => api.runningPrograms(), [api]);
  if (!setup) return null;
  if (setup.otherApp && !sawOtherApp) setSawOtherApp(true);

  const devices = load.status === 'ready' || load.status === 'error' ? state.spec.controllers : null;
  if (devices && !loaded.current) loaded.current = new Set(devices.map((c) => c.id));
  const addedHere = (spec: LayoutSpec) => addedThisSession(loaded.current, spec.controllers);
  const hasLights = !!devices?.some((c) => !isTestDevice(c));
  const next = devices ? nextPage(devices, blockers.length === 0) : 'home';

  const setConfig = async (partial: BackendConfig, what: string) => {
    setFeelBusy(true);
    try {
      await api.setConfig(partial);
    } catch (e) {
      toast('bad', `Couldn’t change the ${what}`, e instanceof Error ? e.message : String(e));
    } finally {
      setFeelBusy(false);
    }
  };

  /** The layout with one strip's path changed, put into the editor and saved at once (the room picture shows it). */
  const setPath = (stripId: string, path: PathPoint[]) => {
    dispatch({ type: 'updateStrip', stripId, patch: { path } });
    void saver.save({ ...state.spec, strips: state.spec.strips.map((x) => (x.id === stripId ? { ...x, path } : x)) });
  };

  const goTo = (s: Step) => {
    setTracing(null);
    if (s === 2 && !screensAsked.current) {
      screensAsked.current = true;
      api
        .screens()
        .then((screens) => {
          const m = sizedMonitor(screens);
          setMonitor(m ? monitorText(m) : null);
          const fitted = m && fittedScreen(specRef.current.room, m);
          if (fitted) {
            dispatch({ type: 'updateRoom', patch: { screen: fitted } });
            if (addedHere(specRef.current)) void saver.save({ ...specRef.current, room: { ...specRef.current.room, screen: fitted } });
          }
        })
        .catch(() => undefined);
    }
    if (s === 2) {
      if (!defaults.current) defaults.current = new Map(state.spec.strips.map((x) => [x.id, x.path]));
      // the room picture's live colors come from the saved layout: save what Your lights added this time (an imported
      // layout is saved only once the user changes something here, or at Turn on)
      if (addedHere(state.spec) && (dirty || load.source !== 'saved')) void saver.save(state.spec);
    }
    setStep(s);
  };

  const skip = (s: Step) => {
    if (s === 2) {
      let spec = state.spec;
      for (const strip of state.spec.strips) {
        const path = defaults.current?.get(strip.id);
        if (!path || path === strip.path) continue;
        dispatch({ type: 'updateStrip', stripId: strip.id, patch: { path } });
        spec = { ...spec, strips: spec.strips.map((x) => (x.id === strip.id ? { ...x, path } : x)) };
      }
      if (spec !== state.spec) void saver.save(spec);
    }
    if (s === 3 && config) {
      // a new install's Look: Natural (an imported install keeps its colors)
      if (!setup.importedFrom && detectLook(config) !== 'natural') void setConfig(applyLook(config, 'natural'), 'Look');
    }
    goTo(stepAfter(s, hasLights));
  };

  const role = (row: RoleRow, r: SimpleRole) => {
    const strip = state.spec.strips.find((x) => x.id === row.stripId);
    if (!strip) return;
    setSelected(strip.id);
    // Screen edge needs the way the strip runs: a strip on a WLED board is traced first (Cancel keeps what it shows)
    if (r === 'screen' && row.trace) return setTracing(row);
    const effects = (serverInfo?.effects ?? []).map((e) => e.name);
    setPath(strip.id, pathWithRole(strip, row.segments, r, effects));
  };

  const feel: FeelProps | null = config
    ? {
        look: detectLook(config),
        disabled: feelBusy,
        onLook: (id: LookId) => void setConfig(applyLook(config, id), 'Look'),
        presets: state.spec.profiles ?? [],
        loadPrograms,
        onAddGame: (exe) => {
          // saved with the layout when the lights are turned on
          const change = addGameToGaming(state.spec, exe, (serverInfo?.effects ?? []).map((e) => e.name));
          if (change?.actions.length) for (const action of change.actions) dispatch(action);
          else if (change) toast('info', change.message);
        },
      }
    : null;

  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      await saver.idle();
      let target = next;
      // the lights use the saved layout: save what the setup added and chose (Layout stays open for more)
      if (hasLights && (dirty || load.source !== 'saved')) {
        const saved = blockers.length === 0 && (await save());
        if (!saved) target = 'layout';
      }
      // a new install starts at a comfortable brightness (full is far too bright in a room); Home's slider changes it
      const brightness = firstBrightness(setup.importedFrom, serverInfo?.adjustment?.[0]?.scaleOutput);
      if (brightness !== null) await api.adjust({ scaleOutput: brightness });
      const done = await api.startLighting(setup.autostart.available ? autostart : undefined);
      window.location.hash = hrefFor(target);
      applySetup(done);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  };

  const traced = tracing && state.spec.strips.find((x) => x.id === tracing.stripId);
  const view = (trace: TraceView | null) => (
    <WelcomeView
      setup={setup}
      config={config}
      serverInfo={serverInfo}
      step={step}
      onStep={goTo}
      devices={devices}
      spec={devices ? state.spec : null}
      onAddDevice={(s) => setAdding(s ?? 'choose')}
      next={next}
      expert={expert}
      starting={starting}
      error={error}
      autostart={autostart}
      onAutostart={setAutostart}
      onStart={() => void start()}
      onLookAround={onLookAround}
      otherAppQuit={sawOtherApp && !setup.otherApp}
      monitor={monitor}
      frame={frame}
      selected={selected}
      onSelect={setSelected}
      onRole={role}
      onTrace={(r) => setTracing(r)}
      trace={trace}
      feel={feel}
      onSkip={skip}
    />
  );

  return (
    <>
      {step === 2 && tracing?.trace && traced ? (
        <TraceStrip
          key={traced.id}
          frame={view}
          room={state.spec.room}
          name={traced.name}
          host={tracing.trace.host}
          output={tracing.trace.output}
          leds={tracing.trace.leds}
          onCancel={() => setTracing(null)}
          onDone={(path) => {
            setTracing(null);
            setPath(traced.id, path);
          }}
        />
      ) : (
        view(null)
      )}
      <AddDeviceDialog open={adding !== null} start={adding ?? 'choose'} onClose={() => setAdding(null)} onAdded={() => setAdding(null)} />
    </>
  );
}
