import { useEffect, useRef, useState } from 'react';
import { ArrowRight, CheckCircle2 } from 'lucide-react';
import { Button, cx } from '../components/ui';
import { Callout, Select } from '../components/form';
import { useConnection } from '../app/connection';
import type { WledTest } from '../api/types';
import { Busy, ErrorText, StepTitle, errorMessage, type StepFrame } from './parts';
import { STRIP_PRESETS, acceptsVolts, jointNear, jointText, presetById } from './plan';
import {
  DEFAULT_LEDS,
  boardChange,
  colorOrderFromAnswers,
  createSerial,
  stripName,
  stripProblem,
  supplyVolts,
  waitForBoard,
  withBoardOrder,
  withStrip,
  type BoardSettings,
  type BoardStrip,
  type SeenColor,
} from './wled';

/** The checks of a strip, numbered from 1 (whether its first LED lights, Find your strips saw already). */
export const CHECKS = ['Last LED', 'Colors', 'Power'] as const;
type Check = 1 | 2 | 3;

const COLOR_ANSWERS: Array<{ value: SeenColor; label: string; swatch?: string }> = [
  { value: 'red', label: 'Red', swatch: '#ef4444' },
  { value: 'green', label: 'Green', swatch: '#22c55e' },
  { value: 'blue', label: 'Blue', swatch: '#3b82f6' },
  { value: 'mixed', label: 'Different colors along the strip' },
  { value: 'none', label: 'Nothing lights up' },
];

/**
 * The strip types the color check offers when the colors differ along the strip: those wired the same (a data wire
 * only, or data and clock like the current one), so only the white LED (RGB or RGBW) and the color order change, and
 * that the supply's volts fit.
 */
export function colorCheckTypes(current: string, volts: number | null): typeof STRIP_PRESETS {
  const clock = !!presetById(current)?.clock;
  return STRIP_PRESETS.filter((p) => !!p.clock === clock && (p.id === current || volts === null || acceptsVolts(p, volts)));
}

/** The length to write so that LED `count` can be lit: a little past it, so More doesn't write every time. */
export function lengthToLight(count: number): number {
  return Math.min(4000, count + 50);
}

/** How long the power check keeps the strip at full brightness when nobody answers. */
export const POWER_MS = 20000;

/**
 * The test pattern each check lights on the board. The power check is full white at the board's limit only while
 * `full` (after the user asked for it, until they answer or POWER_MS passed); otherwise white at the setup's low
 * brightness.
 */
export function checkPattern(check: Check, count: number, phase: 'red' | 'green', output = 0, full = false): WledTest {
  switch (check) {
    case 1:
      return { output, pattern: 'range', count };
    case 2:
      return { output, pattern: 'color', color: phase === 'red' ? [255, 0, 0] : [0, 255, 0] };
    case 3:
      return full ? { output, pattern: 'white' } : { output, pattern: 'color', color: [255, 255, 255] };
  }
}

/**
 * The guided checks of one strip of a board (`strip`: its output), each lit directly on the board (`wled-test`): the
 * first LED, the last LED (Fewer / More), the colors (red, then green: the answers give the color order) and the power
 * at the far end (full brightness only after saying so, and dim again at the answer or after POWER_MS). Each answer
 * that changes a setting writes the board's settings again; a check lights only after the write before it returned
 * (the board has applied it by then).
 */
export function ChecksStep({
  frame,
  host,
  board,
  strip: index,
  onChange,
  onLit,
  onFinish,
}: {
  frame: StepFrame;
  host: string;
  board: BoardSettings;
  strip: number;
  onChange(b: BoardSettings): void;
  onLit(): void;
  onFinish(b: BoardSettings): void;
}) {
  const { api } = useConnection();
  const cur = board.strips[index];
  /** The strip as the checks see it: its LED count is what the board has (the default while unknown). */
  const s = { ...cur, ledCount: cur.ledCount ?? DEFAULT_LEDS, preset: cur.preset ?? 'ws2812b' };
  const plan = cur.plan;
  const [check, setCheck] = useState<Check>(1);
  const [count, setCount] = useState(s.ledCount);
  const [phase, setPhase] = useState<'red' | 'green' | 'result'>('red');
  const [red, setRed] = useState<SeenColor | null>(null);
  const [answer, setAnswer] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preset, setPreset] = useState(s.preset);
  const [seq, setSeq] = useState(0);
  /** The power check lights the strip at full brightness. */
  const [full, setFull] = useState(false);
  /** It went back to dim by itself (nobody answered in time). */
  const [dimmed, setDimmed] = useState(false);
  /** The first output's length on the board now: Find the last LED writes a longer one to light LEDs past it. */
  const boardLength = useRef(s.ledCount);
  /** Every write and test to the board, one after the other (a longer length lands before the LED it lights). */
  const [serial] = useState(createSerial);
  /** The settings as confirmed (for putting the board's length back when the checks are left). */
  const latest = useRef(board);
  latest.current = board;
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // nothing answers into a closed or left step (a write or restart can take 30 s)
  const live = useRef(true);
  const abort = useRef(new AbortController());
  useEffect(() => {
    live.current = true;
    abort.current = new AbortController();
    return () => {
      live.current = false;
      abort.current.abort();
      // left (Back, closed) after More wrote a longer length: the board gets the confirmed one back (best effort)
      const confirmed = latest.current;
      const length = confirmed.strips[index].ledCount ?? DEFAULT_LEDS;
      if (boardLength.current !== length)
        void serial(() => api.wledWrite(host, { outputs: boardChange(confirmed).outputs })).then(
          () => {
            boardLength.current = length;
          },
          () => undefined,
        );
    };
  }, []);
  const strip = presetById(s.preset) ?? STRIP_PRESETS[0];

  /** Fewer or More was pressed: their counts are sent a moment after the last press. */
  const pressed = useRef(false);
  // light the check's pattern (counts are sent a moment after the last press)
  const key = `${check}:${check === 1 ? count : ''}:${check === 2 ? phase : ''}:${check === 3 ? full : ''}:${seq}`;
  useEffect(() => {
    // the answers are in: keep what is lit (after a fix, `seq` lights red again)
    if (check === 2 && phase === 'result' && answer !== 'fixed') return;
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(
      () => {
        void serial(async () => {
          if (!live.current) return;
          try {
            // the board lights only LEDs it has: past its length, write a longer one first (applies without a restart)
            if (check === 1 && count > boardLength.current) {
              const longer = lengthToLight(count);
              await api.wledWrite(host, { outputs: boardChange(withStrip(latest.current, index, { ledCount: longer })).outputs });
              boardLength.current = longer;
              if (!live.current) return;
            }
            await api.wledTest(host, checkPattern(check, count, phase === 'green' ? 'green' : 'red', index, full));
            if (!live.current) return;
            onLit();
            setError(null);
          } catch (e) {
            if (live.current) setError(errorMessage(e));
          }
        });
      },
      // the first light right away (the write before this step has returned), counts a moment after the last press
      check === 1 && pressed.current ? 250 : 0,
    );
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [key]);

  // full brightness for POWER_MS at most: then dim again, whether or not anyone answered
  useEffect(() => {
    if (!full) return;
    const t = setTimeout(() => {
      setFull(false);
      setDimmed(true);
    }, POWER_MS);
    return () => clearTimeout(t);
  }, [full]);

  /** Writes a change of this strip to the board, then lights the check again. */
  const rewrite = async (patch: Partial<BoardStrip>, done?: string) => {
    const next = withStrip(board, index, patch);
    setBusy('Saving to the board…');
    setError(null);
    try {
      const restarting = await serial(() => api.wledWrite(host, boardChange(next)));
      boardLength.current = next.strips[index].ledCount ?? DEFAULT_LEDS;
      if (restarting) setBusy('The board restarts…');
      // read back: a new strip type starts with its usual color order, which the color check works from
      const read = restarting ? await waitForBoard(() => api.wledRead(host), { signal: abort.current.signal }) : await api.wledRead(host);
      if (!live.current) return false;
      onChange(withBoardOrder(next, read));
      if (done) setNote(done);
      setSeq((n) => n + 1);
      return true;
    } catch (e) {
      if (live.current) setError(errorMessage(e));
      return false;
    } finally {
      if (live.current) setBusy(null);
    }
  };

  const go = (c: Check) => {
    setCheck(c);
    setAnswer(null);
    setNote(null);
    setError(null);
    setFull(false);
    setDimmed(false);
    if (c === 2) {
      setPhase('red');
      setRed(null);
    }
  };

  const onColor = async (seen: SeenColor) => {
    if (phase === 'red') {
      setRed(seen);
      if (seen === 'none' || seen === 'mixed') {
        setPhase('result');
        setAnswer(seen);
        return;
      }
      setPhase('green');
      return;
    }
    setPhase('result');
    const order = red ? colorOrderFromAnswers(s.colorOrder ?? 'GRB', red, seen) : null;
    if (!order) {
      setAnswer(seen === 'mixed' || seen === 'none' ? seen : 'same');
      return;
    }
    if (order === s.colorOrder) {
      setAnswer('right');
      return;
    }
    // written, the board lights red again (see the pattern effect): it should look red now
    setAnswer('fixed');
    if (!(await rewrite({ colorOrder: order }, `Fixed: the strip takes its colors in the order ${order}. The strip should look red now.`))) setAnswer('same');
  };

  // the wizard puts the board's own state back (`off`) when it adds the device
  const finish = () => onFinish(board);

  const status = (
    <>
      {busy && <Busy>{busy}</Busy>}
      {error && <ErrorText>{error}</ErrorText>}
      {note && (
        <p role="status" className="flex items-start gap-2 text-sm">
          <CheckCircle2 size={16} className="mt-0.5 shrink-0 text-ok" aria-hidden /> {note}
        </p>
      )}
    </>
  );
  const progress = (
    <p className="text-xs text-ink-faint">
      {board.strips.length > 1 ? `${stripName(board, index)} strip (${index + 1} of ${board.strips.length}) · ` : ''}Check {check} of {CHECKS.length}:{' '}
      {CHECKS[check - 1]}
    </p>
  );
  // check 1: the wizard's own Back (to Strip and power); never while a write runs
  const back = check > 1 ? () => go((check - 1) as Check) : undefined;
  const backDisabled = !!busy;

  if (check === 1) {
    const near = plan ? jointNear(plan, count) : null;
    const step = (d: number) => {
      pressed.current = true;
      setAnswer(null);
      setCount((n) => Math.max(1, Math.min(4000, n + d)));
    };
    const useCount = async () => {
      // the count found is what the board keeps (it may have a longer one from lighting past the end)
      const ok = (cur.ledCount === count && count === boardLength.current) || (await rewrite({ ledCount: count }, `Saved: ${count} LEDs.`));
      if (ok) go(2);
    };
    return frame({
      back,
      backDisabled,
      actions:
        answer === 'dark' ? (
          <>
            <Button variant="secondary" onClick={() => setAnswer(null)}>
              I fixed it: check again
            </Button>
            <Button variant="primary" disabled={!!busy} onClick={() => void useCount()}>
              Use {count} LEDs for now
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" onClick={() => setAnswer('dark')}>
              The strip goes on, but stays dark
            </Button>
            <Button variant="primary" disabled={!!busy} onClick={() => void useCount()}>
              That’s the last LED
            </Button>
          </>
        ),
      children: (
        <>
          {progress}
          <StepTitle title="How many LEDs it has">
            Now we find the strip’s last LED: LED {count} is bright, the ones before it dim. Press Fewer or More until the bright LED is the very last
            one at the end of the strip, with nothing lit after it.
          </StepTitle>
          <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Move the bright LED">
            <Button size="sm" onClick={() => step(-10)} disabled={count <= 1}>
              −10
            </Button>
            <Button size="sm" onClick={() => step(-1)} disabled={count <= 1}>
              Fewer
            </Button>
            <span className="min-w-[5.5rem] text-center text-sm font-semibold tabular-nums" aria-live="polite">
              LED {count}
            </span>
            <Button size="sm" onClick={() => step(1)}>
              More
            </Button>
            <Button size="sm" onClick={() => step(10)}>
              +10
            </Button>
          </div>
          <p className="text-xs text-ink-faint">
            Dim LEDs after the bright one? Press More. No bright LED at all? It’s past the end of the strip: press Fewer until it shows.
          </p>
          {answer === 'dark' && (
            <Callout>
              <p className="font-medium text-ink">A connection is broken after LED {count}.</p>
              <p className="mt-1">
                {near ? <>Check {jointText(near)}: </> : 'Check the joint or cut after it: '}
                the solder or connector, and that the arrows keep pointing the same way on both sides.
              </p>
            </Callout>
          )}
          {status}
        </>
      ),
    });
  }

  if (check === 2) {
    const result = phase === 'result';
    return frame({
      back,
      backDisabled,
      actions: result ? (
        answer === 'right' || answer === 'fixed' ? (
          <>
            {answer === 'fixed' && (
              <Button variant="secondary" onClick={() => go(2)}>
                Still wrong: check again
              </Button>
            )}
            <Button variant="primary" disabled={!!busy} onClick={() => go(3)}>
              Next <ArrowRight size={16} />
            </Button>
          </>
        ) : (
          <Button variant="primary" onClick={() => go(answer === 'none' ? 1 : 2)}>
            {answer === 'none' ? 'Back to the last LED' : 'Check again'}
          </Button>
        )
      ) : null,
      children: (
        <>
          {progress}
          {!result && (
            <>
              <StepTitle title="What color is the strip?">
                {phase === 'red' ? 'The board sends red to every LED.' : 'Now the board sends green.'} Pick what you see.
              </StepTitle>
              <div className="flex flex-wrap gap-2" role="group" aria-label="The color you see">
                {COLOR_ANSWERS.map((c) => (
                  <button
                    key={c.value}
                    type="button"
                    disabled={!!busy}
                    onClick={() => void onColor(c.value)}
                    className={cx(
                      'inline-flex h-10 items-center gap-2 rounded-lg border border-line-input px-3.5 text-sm font-medium hover:border-accent disabled:opacity-40',
                    )}
                  >
                    {c.swatch && <span className="h-3.5 w-3.5 rounded-full" style={{ background: c.swatch }} aria-hidden />}
                    {c.label}
                  </button>
                ))}
              </div>
            </>
          )}
          {result && answer === 'right' && <StepTitle title="The colors are right">Red looked red and green looked green.</StepTitle>}
          {result && answer === 'fixed' && <StepTitle title="Colors fixed">The strip takes its colors in another order than the board sent them. It sends them that way now.</StepTitle>}
          {result && answer === 'mixed' && (
            <Callout>
              <p className="font-medium text-ink">Different colors along the strip: it may have a white LED in each one (RGBW), or the other way round.</p>
              <div className="mt-2 flex flex-wrap items-end gap-3">
                <Select
                  label="Strip type"
                  value={preset}
                  options={colorCheckTypes(s.preset, supplyVolts(board.supply)).map((p) => ({ value: p.id, label: `${p.name} · ${p.volts} V${p.white ? ' (RGBW)' : ''}` }))}
                  onChange={setPreset}
                />
                <Button
                  size="sm"
                  disabled={!!busy || preset === s.preset}
                  onClick={() => {
                    // the same checks as the strip type step: the supply's volts, two strips on one pin
                    const problem = stripProblem(withStrip(board, index, { preset, colorOrder: null }), index);
                    if (problem) return setError(problem);
                    void rewrite({ preset, colorOrder: null }, `Saved: ${presetById(preset)?.name}.`).then((ok) => {
                      if (ok) go(2);
                    });
                  }}
                >
                  Save and check again
                </Button>
              </div>
            </Callout>
          )}
          {result && answer === 'none' && (
            <Callout>Nothing lit: check the power and the wires on the strip’s start, as Find your strips lists them, then check the last LED again.</Callout>
          )}
          {result && answer === 'same' && <Callout>Both looked the same. Look at the strip from a little further away, then check again.</Callout>}
          {status}
        </>
      ),
    });
  }

  const extraFeeds = plan?.feeds.slice(1) ?? [];
  // before the strip goes to full brightness: say so, and that a humming supply is normal
  if (!full && !dimmed && answer === null)
    return frame({
      back,
      backDisabled,
      actions: (
        <Button variant="primary" onClick={() => setFull(true)}>
          Light the whole strip
        </Button>
      ),
      children: (
        <>
          {progress}
          <StepTitle title="Last: the power check">
            This lights the whole strip white at full brightness, as far as the power supply allows, for up to 20 seconds. The supply may hum under the
            full load: that’s normal.
          </StepTitle>
          {status}
        </>
      ),
    });
  const answerPower = (a: 'dim' | 'even') => {
    // back to dim as soon as there is an answer
    setFull(false);
    if (a === 'even') finish();
    else setAnswer('dim');
  };
  const lead = full
    ? `The whole strip is white at full brightness. Compare LED ${s.ledCount} with the start.`
    : dimmed && answer === null
      ? 'Back to dim after 20 seconds. Light it again to compare once more.'
      : 'Back to dim.';
  return frame({
    back,
    backDisabled,
    actions:
      answer === 'dim' ? (
        <Button variant="primary" onClick={finish}>
          Finish
        </Button>
      ) : (
        <>
          {dimmed && !full && (
            <Button variant="ghost" onClick={() => setFull(true)}>
              Light it again
            </Button>
          )}
          <Button variant="secondary" onClick={() => answerPower('dim')}>
            No, it’s orange or dimmer
          </Button>
          <Button variant="primary" onClick={() => answerPower('even')}>
            Yes, it looks the same
          </Button>
        </>
      ),
    children: (
      <>
        {progress}
        <StepTitle title="Does the far end look as bright and white as the start?">{lead}</StepTitle>
        {answer === 'dim' && (
          <Callout>
            <p className="font-medium text-ink">The strip needs power at more than one place.</p>
            <p className="mt-1">
              Run a second pair of wires (+ and −) from the supply to{' '}
              {extraFeeds.length ? extraFeeds.map((f) => f.where).join(' and ') : `the far end, LED ${s.ledCount}`}
              {strip.volts === 5 ? '. On 5 V strips that’s needed about every 2.5 m.' : '.'} It works for now: Backbloom keeps the brightness within the
              supply’s limit.
            </p>
          </Callout>
        )}
        {status}
      </>
    ),
  });
}
