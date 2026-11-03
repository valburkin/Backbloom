import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowRight } from 'lucide-react';
import { Button, Segmented } from '../components/ui';
import { Callout, NumberField } from '../components/form';
import { useConnection } from '../app/connection';
import type { ProbeCandidate, WledChange, WledTest } from '../api/types';
import { Busy, CheckList, ErrorText, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';
import { PlaceCards } from './StripPlaces';
import { boardChange, maxOutputs, placesWords, safePin, stripsFromFound, waitForBoard, type BoardSettings, type StripPlace } from './wled';

/** A strip found: the connection that lit it, and where it runs. */
export interface FoundStrip {
  candidate: ProbeCandidate;
  places: StripPlace[];
}

/** The connections still to look at: those not found yet. */
export function remainingCandidates(candidates: readonly ProbeCandidate[], found: readonly FoundStrip[]): ProbeCandidate[] {
  return candidates.filter((c) => !found.some((f) => f.candidate.output === c.output));
}

/**
 * The connection to light next, stepping by `by` (1 or −1) from index `from` of `candidates` and skipping those found
 * already (wrapping round); −1 when every one is found. The connections keep their numbers over the whole search.
 */
export function stepConnection(candidates: readonly ProbeCandidate[], found: readonly FoundStrip[], from: number, by: 1 | -1): number {
  const n = candidates.length;
  for (let k = 1; k <= n; k++) {
    const i = (((from + by * k) % n) + n) % n;
    if (!found.some((f) => f.candidate.output === candidates[i].output)) return i;
  }
  return -1;
}

/**
 * Keeps the strips found: writes them to the board first (waiting while it restarts; the write turns off what the
 * search lit past the strips' ends), then ends the search keeping that (`wled-probe-end keep`, which only forgets the
 * board's settings from before it). `ended` runs once the search is over. If the write fails the search is still on,
 * so leaving puts the board's settings back.
 */
export async function keepFoundStrips(
  hw: { wledWrite(host: string, change: WledChange): Promise<boolean>; wledRead(host: string): Promise<unknown>; wledProbeEnd(host: string, keep: boolean): Promise<unknown> },
  host: string,
  next: BoardSettings,
  signal: AbortSignal,
  ended: () => void,
): Promise<void> {
  const restarting = await hw.wledWrite(host, boardChange(next));
  // the found pins need a restart: the next step lights the strip, so wait for it
  if (restarting) await waitForBoard(() => hw.wledRead(host), { signal });
  await hw.wledProbeEnd(host, true);
  ended();
}

/**
 * How a connection is lit while the user looks for their strip: the whole output white at the setup's low brightness
 * (`white` is full brightness at the board's limit, for the power check only).
 */
export function identifyPattern(output: number): WledTest {
  return { output, pattern: 'color', color: [255, 255, 255] };
}

type Phase = 'probing' | 'count' | 'none' | 'identify' | 'place' | 'saving' | 'manual';

/**
 * Find your strips (instead of asking for pins): the board lights the first LEDs of every likely connection white
 * (`wled-probe-start`), the user says how many strips light up, then lights one connection at a time themselves (Light
 * the next connection, Previous) and says when one of theirs lights up, and where that strip is. The board then keeps only the found
 * connections, in that order (`wled-write`, `wled-probe-end keep`). Leaving ends the probe without keeping it.
 * Strips with a clock wire (CLK) can't be found this way: their pins are entered by hand.
 */
export function FindStripsStep({
  frame,
  host,
  board,
  onLit,
  onDone,
}: {
  frame: StepFrame;
  host: string;
  board: BoardSettings;
  /** A test is lit on the board (the wizard puts its state back when it closes). */
  onLit?(): void;
  onDone(b: BoardSettings): void;
}) {
  const { api } = useConnection();
  const life = useStepLife();
  const [phase, setPhase] = useState<Phase>('probing');
  const [round, setRound] = useState<1 | 2>(1);
  const [candidates, setCandidates] = useState<ProbeCandidate[]>([]);
  const [count, setCount] = useState(0);
  const [more, setMore] = useState(false);
  const [found, setFound] = useState<FoundStrip[]>([]);
  /** The connection lit now: its index among all the candidates (numbered the same over the whole search). */
  const [cycle, setCycle] = useState(0);
  const [places, setPlaces] = useState<StripPlace[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [manual, setManual] = useState<Array<{ pin: number; clockPin: number; places: StripPlace[] }>>([]);
  /** A probe runs on the board: it is ended (without keeping it) when the step is left. */
  const probing = useRef(false);

  const probe = async (r: 1 | 2) => {
    setPhase('probing');
    setError(null);
    setRound(r);
    // a probe may run from now on: leaving ends it (an end with nothing saved does nothing; the board takes them in
    // order)
    probing.current = true;
    try {
      const c = await api.wledProbeStart(host, r);
      if (!life.alive()) return;
      setCandidates(c);
      setFound([]);
      setCycle(0);
      setPhase(c.length ? 'count' : 'none');
    } catch (e) {
      if (!life.alive()) return;
      setError(errorMessage(e));
      setPhase('none');
    }
  };

  useEffect(() => {
    void probe(1);
    return () => {
      if (probing.current) api.wledProbeEnd(host, false).catch(() => undefined);
    };
  }, []);

  const remaining = remainingCandidates(candidates, found);
  const lit = candidates[cycle] && remaining.includes(candidates[cycle]) ? candidates[cycle] : null;

  // the connection the user picked, lit (the others black); never by itself one after the other
  useEffect(() => {
    if ((phase !== 'identify' && phase !== 'place') || !lit) return;
    onLit?.();
    api.wledTest(host, identifyPattern(lit.output)).catch((e: unknown) => life.alive() && setError(errorMessage(e)));
  }, [phase, lit?.output]);

  const finish = async (all: FoundStrip[]) => {
    setPhase('saving');
    setError(null);
    try {
      const next = stripsFromFound(board, all);
      await keepFoundStrips(api, host, next, life.signal, () => (probing.current = false));
      if (life.alive()) onDone(next);
    } catch (e) {
      if (!life.alive()) return;
      setError(errorMessage(e));
      // the last strip is pointed out again (it wasn't saved)
      setFound(all.slice(0, -1));
      setPlaces(all[all.length - 1].places);
      setPhase('place');
    }
  };

  const confirmPlace = () => {
    if (!lit || !places.length) return;
    const all = [...found, { candidate: lit, places }];
    setFound(all);
    setPlaces([]);
    if (all.length >= count) void finish(all);
    else {
      // the next strip's search goes on at the next connection not found yet
      setCycle(stepConnection(candidates, all, cycle, 1));
      setPhase('identify');
    }
  };

  const saveManual = async () => {
    setPhase('saving');
    setError(null);
    try {
      if (probing.current) {
        await api.wledProbeEnd(host, false);
        probing.current = false;
      }
      const next = {
        ...board,
        strips: manual.map((m) => ({ places: m.places, preset: 'apa102', ledCount: null, pin: m.pin, clockPin: m.clockPin, colorOrder: null, plan: null })),
      };
      const restarting = await api.wledWrite(host, boardChange(next));
      if (restarting) await waitForBoard(() => api.wledRead(host), { signal: life.signal });
      if (life.alive()) onDone(next);
    } catch (e) {
      if (!life.alive()) return;
      setError(errorMessage(e));
      setPhase('manual');
    }
  };

  const status = error && <ErrorText>{error}</ErrorText>;
  const busy = phase === 'probing' || phase === 'saving';

  if (phase === 'probing' || phase === 'saving')
    return frame({
      backDisabled: true,
      actions: null,
      children: (
        <>
          <StepTitle title="Find your strips" />
          <Busy>{phase === 'probing' ? 'Looking for your strips… (about 15 seconds: the board restarts)' : 'Saving your strips to the board…'}</Busy>
        </>
      ),
    });

  if (phase === 'count') {
    const max = candidates.length;
    return frame({
      backDisabled: busy,
      actions: null,
      children: (
        <>
          <StepTitle title="How many strips light up?">The board lights the first LEDs of each strip on it white, dimly. Count the strips where some LEDs light up.</StepTitle>
          <div className="flex flex-wrap items-end gap-3">
            <Segmented<string>
              label="Strips lit"
              value={more ? 'more' : ''}
              options={[
                { value: '0', label: 'None' },
                ...['1', '2', '3'].filter((n) => Number(n) <= max).map((n) => ({ value: n, label: n })),
                ...(max > 3 ? [{ value: 'more', label: 'More' }] : []),
              ]}
              onChange={(v) => {
                if (v === 'more') return setMore(true);
                const n = Number(v);
                setCount(n);
                setPhase(n === 0 ? 'none' : 'identify');
              }}
            />
            {more && (
              <>
                <NumberField label="Strips lit" integer min={1} max={max} value={count || undefined} onChange={(n) => n >= 1 && n <= max && setCount(n)} />
                <Button variant="primary" disabled={!(count >= 1)} onClick={() => setPhase('identify')}>
                  Next <ArrowRight size={16} />
                </Button>
              </>
            )}
          </div>
          {status}
        </>
      ),
    });
  }

  if (phase === 'none')
    return frame({
      actions: (
        <>
          <Button variant="secondary" onClick={() => void probe(round)}>
            Look again
          </Button>
          {round === 1 && (
            <Button variant="primary" onClick={() => void probe(2)}>
              Try other connections
            </Button>
          )}
        </>
      ),
      children: (
        <>
          <StepTitle title="No strip lights up">Check these, then look again:</StepTitle>
          <CheckList
            items={[
              'The power supply is plugged in and switched on.',
              'The strip’s wires are on its start: the end where the arrows on it point away from the board.',
              'The connectors are pushed in all the way, and screw terminals are tight.',
            ]}
          />
          {round === 2 && <p className="text-sm text-ink-muted">No strip lit up on the other connections either.</p>}
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setManual([{ pin: board.strips[0]?.pin ?? 16, clockPin: safePin(board.chip, [board.strips[0]?.pin ?? 16]), places: [] }]);
              setPhase('manual');
            }}
          >
            My strip has 4 wires, one marked CLK
          </Button>
          {status}
        </>
      ),
    });

  if (phase === 'manual') {
    const max = maxOutputs(board.chip);
    const setN = (n: number) => {
      const next = manual.slice(0, n);
      while (next.length < n) {
        const used = next.flatMap((m) => [m.pin, m.clockPin]);
        const pin = safePin(board.chip, used);
        next.push({ pin, clockPin: safePin(board.chip, [...used, pin]), places: [] });
      }
      setManual(next);
    };
    const ready = manual.length > 0 && manual.every((m) => m.places.length > 0);
    return frame({
      back: () => setPhase('none'),
      actions: (
        <Button variant="primary" disabled={!ready} onClick={() => void saveManual()}>
          Save to the board
        </Button>
      ),
      children: (
        <>
          <StepTitle title="Strips with a clock wire">
            They can’t be found by lighting them: enter the pins their wires are on (the board’s maker lists them, often printed next to the connector).
          </StepTitle>
          <div className="space-y-1.5">
            <p className="text-sm font-medium">How many strips?</p>
            <Segmented<string>
              label="Strips"
              value={String(manual.length)}
              options={['1', '2', '3', '4'].filter((n) => Number(n) <= max).map((n) => ({ value: n, label: n }))}
              onChange={(v) => setN(Number(v))}
            />
          </div>
          <div className="divide-y divide-line border-y border-line">
            {manual.map((m, i) => (
              <section key={i} aria-label={`Strip ${i + 1}`} className="space-y-3 py-4">
                {manual.length > 1 && <h4 className="text-sm font-semibold">Strip {i + 1}</h4>}
                <div className="flex flex-wrap gap-3">
                  <NumberField label="Data pin (DI)" integer min={0} max={48} value={m.pin} onChange={(pin) => pin >= 0 && setManual(manual.map((x, k) => (k === i ? { ...x, pin } : x)))} />
                  <NumberField
                    label="Clock pin (CLK, CI)"
                    integer
                    min={0}
                    max={48}
                    value={m.clockPin}
                    onChange={(clockPin) => clockPin >= 0 && setManual(manual.map((x, k) => (k === i ? { ...x, clockPin } : x)))}
                  />
                </div>
                <PlaceCards label="Where is it?" places={m.places} onChange={(p) => setManual(manual.map((x, k) => (k === i ? { ...x, places: p } : x)))} />
              </section>
            ))}
          </div>
          {status}
        </>
      ),
    });
  }

  if (phase === 'place' && lit)
    return frame({
      back: () => setPhase('identify'),
      actions: (
        <>
          <Button
            variant="secondary"
            onClick={() => {
              setPlaces([]);
              setPhase('identify');
            }}
          >
            That’s not it: keep looking
          </Button>
          <Button variant="primary" disabled={!places.length} onClick={confirmPlace}>
            {found.length + 1 >= count ? 'Done' : 'Next strip'}
          </Button>
        </>
      ),
      children: (
        <>
          <p className="text-xs text-ink-faint">
            Strip {found.length + 1} of {count}
          </p>
          <StepTitle title="Where is this strip?">The strip on connection {cycle + 1}, lit now.</StepTitle>
          <PlaceCards label="Where does it run?" places={places} onChange={setPlaces} />
          {status}
        </>
      ),
    });

  // identify: the user lights one connection after the other
  const n = remaining.length;
  const step = (by: 1 | -1) => {
    const next = stepConnection(candidates, found, cycle, by);
    if (next >= 0) setCycle(next);
  };
  return frame({
    actions: (
      <>
        <Button variant="ghost" icon={<ArrowLeft size={15} />} disabled={n < 2} onClick={() => step(-1)}>
          Previous
        </Button>
        <Button variant="secondary" disabled={n < 2} onClick={() => step(1)}>
          Light the next connection <ArrowRight size={15} />
        </Button>
        <Button variant="primary" disabled={!lit} onClick={() => setPhase('place')}>
          This one lit up
        </Button>
      </>
    ),
    children: (
      <>
        <p className="text-xs text-ink-faint">
          Looking for strip {found.length + 1} of {count}
        </p>
        <StepTitle title="Which connection is your strip on?">
          {round === 1 ? `Your board has ${candidates.length} connections a strip is usually wired to` : `Your board has ${candidates.length} more connections a strip can be wired to`}
          {board.chip ? ` (on ${board.chip} boards)` : ''}. Backbloom lights them one at a time, white and dimly: tell it when one of your strips lights up.
          Take your time to look, also up at the ceiling.
        </StepTitle>
        <p className="flex items-center gap-2.5 text-sm font-medium" role="status" aria-live="polite">
          <span className="inline-flex h-3 w-3 rounded-full bg-accent" aria-hidden />
          {lit ? `Connection ${cycle + 1} of ${candidates.length} is lit` : 'No connection left'}
        </p>
        {found.length > 0 && (
          <ul className="space-y-0.5 text-xs text-ink-faint" aria-label="Found already">
            {found.map((f) => (
              <li key={f.candidate.output}>
                Connection {candidates.indexOf(f.candidate) + 1}: your strip {placesWords(f.places)}
              </li>
            ))}
          </ul>
        )}
        {n === 0 && <Callout>Every connection is taken: there are fewer strips than counted.</Callout>}
        <Button size="sm" variant="ghost" onClick={() => void probe(round)}>
          Wrong number of strips? Count again
        </Button>
        {status}
      </>
    ),
  });
}
