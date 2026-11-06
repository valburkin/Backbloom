import { useEffect, useRef, useState } from 'react';
import { Check, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Lightbulb, X } from 'lucide-react';
import { pathStations, stripLedCount, type LayoutSpec, type PathPoint, type Strip } from '../../layout';
import { Button, IconButton } from '../../components/ui';
import { useConnection } from '../../app/connection';
import type { EditorAction } from '../../editor/state';
import { controllerIndex } from './model';

export const MATCH_BLINK_MS = 120;

function pointName(strip: Strip, i: number): string {
  if (i === 0) return 'the start';
  if (i === strip.path.length - 1) return 'the end';
  return `bend ${i}`;
}

/**
 * Matching a strip with the real lights: for each bend (the start and the end included), one LED lights up on the
 * strip; the user moves the light until it sits at that spot in the room and confirms. The bend then remembers that
 * LED (PathPoint.at), so LEDs land where they really are even when the drawing isn’t to scale.
 */
export function MatchLights({
  spec,
  strip,
  dispatch,
  onLocate,
  onPoint,
  onClose,
}: {
  spec: LayoutSpec;
  strip: Strip;
  dispatch: (a: EditorAction) => void;
  onLocate(led: number | null): void;
  onPoint(index: number | null): void;
  onClose(): void;
}) {
  const { api } = useConnection();
  const count = stripLedCount(strip);
  const [step, setStep] = useState(0);
  const stations = pathStations(spec.room, strip.path, count);
  const suggested = (i: number) => {
    const at = strip.path[i]?.at;
    const s = at ?? stations[i] ?? 0;
    return Math.min(count - 1, Math.max(0, Math.floor(at !== undefined ? s : s - 0.5)));
  };
  const [led, setLed] = useState(() => suggested(0));
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const done = step >= strip.path.length;
  const previousAt = strip.path.slice(0, step).reduce((m, p) => (p.at !== undefined ? Math.max(m, Math.floor(p.at)) : m), 0);

  const blink = (local: number) => {
    if (timer.current) clearTimeout(timer.current);
    const index = controllerIndex(spec, strip, local);
    if (index === null) {
      setError('This strip isn’t plugged into an output yet, so its LEDs can’t be lit.');
      return;
    }
    timer.current = setTimeout(() => {
      setError(null);
      api.blinkLed(index, strip.controllerId, true).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    }, MATCH_BLINK_MS);
  };

  useEffect(() => {
    if (done) {
      // every bend is matched: the last LED goes off (it would stay lit until the panel closes)
      if (timer.current) clearTimeout(timer.current);
      api.stopBlink(strip.controllerId).catch(() => undefined);
      onPoint(null);
      onLocate(null);
      return;
    }
    onPoint(step);
    onLocate(led);
    blink(led);
  }, [step, led, done]);

  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      api.stopBlink(strip.controllerId).catch(() => undefined);
      onPoint(null);
      onLocate(null);
    },
    [],
  );

  const move = (delta: number) => setLed((l) => Math.min(count - 1, Math.max(0, l + delta)));

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT')) return;
      if (e.key === 'ArrowRight') move(e.shiftKey ? 10 : 1);
      else if (e.key === 'ArrowLeft') move(e.shiftKey ? -10 : -1);
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const goTo = (i: number) => {
    setStep(i);
    if (i < strip.path.length) setLed(suggested(i));
  };

  const confirm = () => {
    const path: PathPoint[] = strip.path.map((p, i) => (i === step ? { ...p, at: led + 0.5 } : p));
    dispatch({ type: 'updateStrip', stripId: strip.id, patch: { path } });
    goTo(step + 1);
  };

  const clearAll = () => {
    dispatch({ type: 'updateStrip', stripId: strip.id, patch: { path: strip.path.map(({ at: _at, ...p }) => p) } });
    goTo(0);
  };

  return (
    <section aria-label={`Match ${strip.name} with the lights`} className="space-y-4 rounded-2xl border border-line bg-white/[0.03] p-4">
      <div className="flex items-start gap-2">
        <Lightbulb size={18} className="mt-0.5 shrink-0 text-ink-faint" />
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold">Match {strip.name} with your lights</h2>
          <p className="mt-0.5 text-xs text-ink-muted">
            {done ? 'All bends are matched.' : `Bend ${step + 1} of ${strip.path.length}`}
          </p>
        </div>
        <IconButton label="Close matching" onClick={onClose}>
          <X size={16} />
        </IconButton>
      </div>

      {done ? (
        <>
          <p className="text-[13px] text-ink-muted">Every LED now sits where it is in your room. Save the layout to use it.</p>
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="primary" icon={<Check size={14} />} onClick={onClose}>
              Done
            </Button>
            <Button size="sm" variant="ghost" onClick={() => goTo(0)}>
              Start over
            </Button>
          </div>
        </>
      ) : (
        <>
          <p className="text-[13px]">
            One LED of the strip lights up in your room. Move it until it sits at <span className="font-semibold text-ink">{pointName(strip, step)}</span>{' '}
            (the blinking handle in the room), then confirm.
          </p>
          <div className="flex items-center justify-between gap-1 rounded-xl border border-line bg-bg/60 p-1.5">
            <IconButton label="Back 10 LEDs" onClick={() => move(-10)} disabled={led <= 0}>
              <ChevronsLeft size={16} />
            </IconButton>
            <IconButton label="Back 1 LED" onClick={() => move(-1)} disabled={led <= 0}>
              <ChevronLeft size={16} />
            </IconButton>
            <span className="min-w-[6rem] text-center text-sm tabular-nums">
              LED <span className="font-semibold">{led + 1}</span> <span className="text-ink-faint">of {count}</span>
            </span>
            <IconButton label="Forward 1 LED" onClick={() => move(1)} disabled={led >= count - 1}>
              <ChevronRight size={16} />
            </IconButton>
            <IconButton label="Forward 10 LEDs" onClick={() => move(10)} disabled={led >= count - 1}>
              <ChevronsRight size={16} />
            </IconButton>
          </div>
          <p className="text-xs text-ink-faint">Arrow keys move it too (with Shift: 10 at a time).</p>
          {led < previousAt && <p className="text-xs text-warn">This LED comes before the one you matched earlier, so the strip would run backwards.</p>}
          {error && <p className="text-xs text-bad">{error}</p>}
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="primary" icon={<Check size={14} />} onClick={confirm}>
              It’s there
            </Button>
            <Button size="sm" variant="ghost" onClick={() => goTo(step + 1)}>
              Skip this bend
            </Button>
            {step > 0 && (
              <Button size="sm" variant="ghost" onClick={() => goTo(step - 1)}>
                Back
              </Button>
            )}
          </div>
        </>
      )}
      {strip.path.some((p) => p.at !== undefined) && (
        <button type="button" onClick={clearAll} className="text-xs text-ink-faint hover:text-ink hover:underline">
          Forget matched points
        </button>
      )}
    </section>
  );
}
