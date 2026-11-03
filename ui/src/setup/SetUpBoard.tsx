import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ArrowLeft } from 'lucide-react';
import { Button, Dialog } from '../components/ui';
import { Callout } from '../components/form';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { useExpertTools } from '../app/expert';
import type { FindResult, FoundBoard, ScreenInfo, WledState } from '../api/types';
import { addedAddresses } from '../pages/devices/drivers';
import { DEFAULT_PLAN_FORM, PlanStep, loadPlanForm, savePlanForm, sizedScreens, withScreen, type PlanForm } from './PlanStep';
import { FindStep, needsWifi } from './FindStep';
import { BoardWifiStep } from './BoardWifiStep';
import { PowerStep } from './PowerStep';
import { FindStripsStep } from './FindStripsStep';
import { StripTypeStep } from './StripTypeStep';
import { ChecksStep } from './ChecksStep';
import { PlaceCards } from './StripPlaces';
import { Busy, StepDots, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';
import { normalizeInput, planStrips, type StripPlan } from './plan';
import {
  boardName,
  boardOutputs,
  initialBoard,
  placedPath,
  plannedScreen,
  setUpAlready,
  stripName,
  unplacedStrips,
  waitForBoard,
  withBoardOrder,
  withStrip,
  wledController,
  outputsAsRead,
  type BoardSettings,
} from './wled';
import { placedStrips, useAddToLayout, type StripToAdd } from './useAddToLayout';

export const BOARD_STEPS = ['Find', 'Wi-Fi', 'Power', 'Strips', 'Check'] as const;
type StepId = 0 | 1 | 2 | 3 | 4;

/**
 * A step's frame props with the wizard's Back (to the step before) unless the step has its own: a step passing
 * `back: undefined` (its first part) keeps the wizard's.
 */
export function withDefaultBack<P extends { back?: () => void }>(p: P, fallback: (() => void) | undefined): P & { back?: () => void } {
  return { ...p, back: p.back ?? fallback };
}

/**
 * The dialog every step shows in: the steps on top, the step's own buttons at the bottom (Back on the left). A click
 * beside it doesn't close it; X and Esc ask first (`onClose`).
 */
export function WizardFrame({
  step,
  onClose,
  back,
  backDisabled,
  actions,
  children,
}: {
  step: StepId;
  onClose(): void;
  back?: () => void;
  /** A write or a wait runs: going back now would leave it answering into another step. */
  backDisabled?: boolean;
  actions: ReactNode;
  children: ReactNode;
}) {
  return (
    <Dialog
      open
      wide
      closeOnBackdrop={false}
      title="Set up a WLED board"
      onClose={onClose}
      footer={
        <>
          {back && (
            <Button variant="ghost" className="mr-auto" icon={<ArrowLeft size={16} />} disabled={backDisabled} onClick={back}>
              Back
            </Button>
          )}
          {actions}
        </>
      }
    >
      <div className="space-y-5">
        <StepDots steps={BOARD_STEPS} current={step} />
        {children}
      </div>
    </Dialog>
  );
}

type View =
  | { kind: 'find'; notice?: string }
  | { kind: 'wifi'; board: FoundBoard; ssid: string | null; savedPassword: boolean }
  | { kind: 'reading'; host: string }
  | { kind: 'confirm'; what: string }
  /** Add it as it is: where each strip is, then into the layout; nothing is written to the board. */
  | { kind: 'places' }
  | { kind: 'power' }
  | { kind: 'strips' }
  | { kind: 'type'; strip: number }
  | { kind: 'plan'; strip: number }
  | { kind: 'check'; strip: number };

/** Where a wizard was when it closed, for this session (memory only: no passwords, nothing on disk). */
export interface Progress {
  view: View;
  host: string | null;
  read: WledState | null;
  board: BoardSettings | null;
}

let saved: Progress | null = null;

/**
 * Where to go on after reopening: a step that was running something starts again (Find your strips looks again, a
 * strip's checks start over), a board on the Wi-Fi already skips the Wi-Fi step.
 */
export function resumeView(p: Progress): View {
  const v = p.view;
  if (v.kind === 'wifi' || v.kind === 'reading') return p.host && p.board ? { kind: 'power' } : { kind: 'find' };
  if (!p.host || !p.board) return { kind: 'find' };
  if (v.kind === 'check' || v.kind === 'plan') return { kind: 'type', strip: v.strip };
  return v;
}

/** A board's setup was left in the middle this session (Add device offers to continue it). */
export function boardSetupInProgress(): boolean {
  return saved !== null && saved.view.kind !== 'find';
}

/** Keeps a wizard's progress for the session (on every change while it is open). */
export function rememberBoardSetup(p: Progress): void {
  saved = p;
}

/** Forgets a wizard's progress (tests, Start over). */
export function forgetBoardSetup(): void {
  saved = null;
}

/**
 * The progress a wizard opens with: the one left this session when it is to go on with it (Continue setting up your
 * board), else none, and the one left is forgotten (LED strips on a WLED board starts afresh).
 */
export function startingProgress(resume: boolean): Progress | null {
  if (!resume) saved = null;
  return saved;
}

/**
 * "Set up a WLED board" (docs/guided-setup.md): Find (one Search over USB, the home Wi-Fi and new boards' hotspots),
 * Wi-Fi for a new board, the power supply, Find your strips (the board lights its possible connections, the user
 * points out each strip and where it is), then per strip its type and checks; the device and its strips go into the
 * layout, each placed where it runs, and `onAdded` goes on (Your lights: another board, room lights, or on). The
 * progress is kept for the session: closing asks first, reopening goes on where it was.
 */
export function SetUpBoard({ onClose, onAdded, onBack, resume = false }: { onClose(): void; onAdded(name: string): void; onBack?: () => void; resume?: boolean }) {
  const { api } = useConnection();
  const { state } = useEditor();
  const expert = useExpertTools();
  const add = useAddToLayout();
  const life = useStepLife();
  const [start] = useState(() => startingProgress(resume));
  const resumed = useRef(start !== null && start.view.kind !== 'find');
  const [view, setView] = useState<View>(() => (start ? resumeView(start) : { kind: 'find' }));
  const [host, setHost] = useState<string | null>(start?.host ?? null);
  const [read, setRead] = useState<WledState | null>(start?.read ?? null);
  const [board, setBoard] = useState<BoardSettings | null>(start?.board ?? null);
  const [flashed, setFlashed] = useState(false);
  const [asking, setAsking] = useState(false);
  /** The board a test pattern was lit on, to put it back when the wizard closes. */
  const lit = useRef<string | null>(null);
  /** The board is in the layout: its progress isn't kept any more. */
  const finished = useRef(false);

  // the progress for the session, kept on every change
  useEffect(() => {
    if (!finished.current) rememberBoardSetup({ view, host, read, board });
  }, [view, host, read, board]);

  useEffect(
    () => () => {
      if (lit.current) api.wledTest(lit.current, { output: 0, pattern: 'off' }).catch(() => undefined);
    },
    [api],
  );

  /** X or Esc: while setting up a board, asks first (the progress stays for the session). */
  const close = () => {
    if (view.kind === 'find' && !host) onClose();
    else setAsking(true);
  };

  /** Reads the board at `host` (waiting while it comes onto the network after joining), then its power supply. */
  const open = async (h: string, joined: boolean) => {
    // an address typed in under Can’t find it? may be a board in the setup already (the cards mark those)
    if (addedAddresses(state.spec).has(h.toLowerCase())) {
      setView({ kind: 'find', notice: 'That board is in your setup already: it is on the Devices page.' });
      return;
    }
    setView({ kind: 'reading', host: h });
    try {
      const r = joined ? await waitForBoard(() => api.wledRead(h), { intervalMs: 1500, timeoutMs: 60000, signal: life.signal }) : await api.wledRead(h);
      if (!life.alive()) return;
      setHost(h);
      setRead(r);
      setBoard(initialBoard(r));
      // just joined: its lights flash, so the user sees it's the one
      if (joined) {
        setFlashed(true);
        api.wledIdentify(h).catch(() => undefined);
      }
      // set up already (also one that just joined: it may only have lost its Wi-Fi): asked first
      const what = setUpAlready(r);
      setView(what ? { kind: 'confirm', what } : { kind: 'power' });
    } catch (e) {
      if (!life.alive()) return;
      setView({
        kind: 'find',
        notice: joined ? 'The board joined your Wi-Fi but doesn’t answer yet. Wait a moment, then search again.' : `Couldn’t reach that board: ${errorMessage(e)}`,
      });
    }
  };

  const reset = () => {
    if (lit.current) api.wledTest(lit.current, { output: 0, pattern: 'off' }).catch(() => undefined);
    lit.current = null;
    resumed.current = false;
    setHost(null);
    setRead(null);
    setBoard(null);
    setFlashed(false);
    setView({ kind: 'find' });
  };

  /**
   * The device and its strips into the layout, each strip placed where it runs: on the outputs as written, or (`asIs`,
   * Add it as it is) as the board reports them.
   */
  const finish = (b: BoardSettings, asIs?: WledState) => {
    if (!host) return;
    const outputs = asIs ? outputsAsRead(asIs) : boardOutputs(b);
    const controller = wledController(host, boardName(b), outputs);
    const planned = b.strips.find((s) => s.places.includes('screen') && s.plan && s.plan.totalLeds > 0)?.plan ?? null;
    // the screen takes a plan's size only while nothing else is placed around it
    const resize = !!planned && placedStrips(state.spec, expert).length === 0;
    const screen = planned && resize ? plannedScreen(planned, state.spec.room) : state.spec.room.screen;
    const room = { ...state.spec.room, screen };
    const strips: StripToAdd[] = b.strips.map((s, i) => ({ output: outputs[i].index, name: stripName(b, i), path: placedPath(s, room, outputs[i].length, screen) }));
    add([{ controller, strips }], resize ? screen : undefined);
    if (lit.current) api.wledTest(lit.current, { output: 0, pattern: 'off' }).catch(() => undefined);
    lit.current = null;
    // done: nothing to go on with next time
    finished.current = true;
    saved = null;
    onAdded(controller.name);
  };

  const frame =
    (step: StepId, back?: () => void): StepFrame =>
    (p) => (
      <WizardFrame step={step} onClose={close} {...withDefaultBack(p, back)}>
        {resumed.current && (
          <p className="text-xs text-ink-faint">
            Going on where you left off.{' '}
            <button
              type="button"
              className="text-accent-strong hover:underline"
              onClick={() => {
                reset();
                saved = null;
              }}
            >
              Start over
            </button>
          </p>
        )}
        {p.children}
      </WizardFrame>
    );

  const askDialog = (
    <Dialog
      open={asking}
      title="Stop setting up this board?"
      onClose={() => setAsking(false)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Stop for now
          </Button>
          <Button variant="primary" onClick={() => setAsking(false)}>
            Keep setting up
          </Button>
        </>
      }
    >
      <p className="text-sm text-ink-muted">You can continue later from Add device.</p>
    </Dialog>
  );

  const body = ((): ReactNode => {
    switch (view.kind) {
      case 'find':
        return (
          <FindStep
            frame={(p) =>
              frame(0, onBack)({
                ...p,
                children: (
                  <>
                    {view.notice && <Callout tone="warn">{view.notice}</Callout>}
                    {p.children}
                  </>
                ),
              })
            }
            onPick={(b: FoundBoard, wifi: FindResult['wifi']) => {
              if (needsWifi(b)) setView({ kind: 'wifi', board: b, ssid: wifi.currentSsid, savedPassword: !!wifi.savedPassword });
              else if (b.host) void open(b.host, false);
            }}
            onAddress={(h) => void open(h, false)}
          />
        );
      case 'wifi':
        return (
          <BoardWifiStep
            frame={frame(1, () => setView({ kind: 'find' }))}
            board={view.board}
            currentSsid={view.ssid}
            savedPassword={view.savedPassword}
            onJoined={(h) => void open(h, true)}
          />
        );
      case 'reading':
        return frame(1)({ backDisabled: true, actions: null, children: <Busy>Connecting to the board…</Busy> });
      case 'confirm':
        return frame(2, reset)({
          actions: (
            <>
              <Button variant="secondary" onClick={() => setView({ kind: 'power' })}>
                Set it up again
              </Button>
              <Button variant="primary" onClick={() => setView({ kind: 'places' })}>
                Add it as it is
              </Button>
            </>
          ),
          children: (
            <>
              <StepTitle title="This board is set up already">{view.what}.</StepTitle>
              <p className="text-sm text-ink-muted">
                Add it as it is to use its strips as they are, or set it up again: that replaces its LED settings (if another app drives these lights, it
                stops working with them).
              </p>
            </>
          ),
        });
      case 'places':
        return board
          ? frame(2, () => setView(read && setUpAlready(read) ? { kind: 'confirm', what: setUpAlready(read)! } : { kind: 'find' }))({
              actions: (
                <Button variant="primary" disabled={unplacedStrips(board).length > 0 || !read} onClick={() => read && finish(board, read)}>
                  Add to my setup
                </Button>
              ),
              children: (
                <>
                  <StepTitle title="Where are its strips?">So Backbloom draws each one where it is. Nothing on the board changes.</StepTitle>
                  <div className="divide-y divide-line border-y border-line">
                    {board.strips.map((st, i) => (
                      <section key={i} aria-label={`Strip ${i + 1}`} className="space-y-3 py-4">
                        <h4 className="text-sm font-semibold">
                          Strip {i + 1} <span className="font-normal text-ink-faint">· {st.ledCount ?? 0} LEDs</span>
                        </h4>
                        <PlaceCards label="Where is it?" places={st.places} onChange={(places) => setBoard(withStrip(board, i, { places }))} />
                      </section>
                    ))}
                  </div>
                </>
              ),
            })
          : null;
      case 'power':
        return host && board ? (
          <PowerStep
            frame={frame(2, reset)}
            host={host}
            supply={board.supply}
            flashed={flashed}
            onChange={(supply) => setBoard({ ...board, supply })}
            onNext={() => setView({ kind: 'strips' })}
          />
        ) : null;
      case 'strips':
        return host && board ? (
          <FindStripsStep
            frame={frame(3, () => setView({ kind: 'power' }))}
            host={host}
            board={board}
            onLit={() => {
              lit.current = host;
            }}
            onDone={(b) => {
              setBoard(b);
              setView({ kind: 'type', strip: 0 });
            }}
          />
        ) : null;
      case 'type':
        return host && board && board.strips[view.strip] ? (
          <StripTypeStep
            key={view.strip}
            frame={frame(4, () => setView(view.strip > 0 ? { kind: 'type', strip: view.strip - 1 } : { kind: 'strips' }))}
            host={host}
            board={board}
            strip={view.strip}
            onChange={setBoard}
            onPlan={(i) => setView({ kind: 'plan', strip: i })}
            onLit={() => {
              lit.current = host;
            }}
            onNext={(r) => {
              setRead(r);
              setBoard((b) => (b ? withBoardOrder(b, r) : b));
              lit.current = host;
              setView({ kind: 'check', strip: view.strip });
            }}
          />
        ) : null;
      case 'plan':
        return board ? (
          <PlanView
            frame={frame(4, () => setView({ kind: 'type', strip: view.strip }))}
            preset={board.strips[view.strip].preset ?? 'ws2812b'}
            onUse={(plan) => {
              setBoard(withStrip(board, view.strip, { plan, ledCount: plan.totalLeds, preset: plan.preset.id }));
              setView({ kind: 'type', strip: view.strip });
            }}
          />
        ) : null;
      case 'check':
        return host && board ? (
          <ChecksStep
            key={view.strip}
            frame={frame(4, () => setView({ kind: 'type', strip: view.strip }))}
            host={host}
            board={board}
            strip={view.strip}
            onChange={setBoard}
            onLit={() => {
              lit.current = host;
            }}
            onFinish={(b) => {
              setBoard(b);
              if (view.strip + 1 < b.strips.length) setView({ kind: 'type', strip: view.strip + 1 });
              else finish(b);
            }}
          />
        ) : null;
    }
  })();

  return (
    <>
      {body}
      {askDialog}
    </>
  );
}

/** "Plan the cuts and power" for a strip behind the monitor or TV: the plan, then Use this plan. */
function PlanView({ frame, preset, onUse }: { frame: StepFrame; preset: string; onUse(plan: StripPlan): void }) {
  const { api } = useConnection();
  const [form, setFormState] = useState<PlanForm>(() => {
    const f = loadPlanForm() ?? DEFAULT_PLAN_FORM;
    return { ...f, input: normalizeInput({ ...f.input, preset }) };
  });
  const [screens, setScreens] = useState<ScreenInfo[]>([]);
  const plan = useMemo(() => planStrips(form.input), [form.input]);
  const remembered = useRef(loadPlanForm() !== null);
  useEffect(() => {
    let live = true;
    api
      .screens()
      .then((s) => {
        if (!live) return;
        setScreens(s);
        const sized = sizedScreens(s);
        if (!remembered.current && sized.length) setFormState((f) => withScreen({ ...f, source: 'detected', screen: Math.max(0, sized.findIndex((x) => x.primary)) }, s));
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [api]);
  return frame({
    actions: (
      <Button variant="primary" disabled={!plan} onClick={() => plan && onUse(plan)}>
        Use this plan
      </Button>
    ),
    children: (
      <>
        <StepTitle title="Plan the cuts and power">Where to cut, how to turn the corners and what power supply to get. Print it and take it to the screen.</StepTitle>
        <PlanStep
          form={form}
          plan={plan}
          screens={screens}
          onChange={(f) => {
            setFormState(f);
            savePlanForm(f);
          }}
        />
      </>
    ),
  });
}
