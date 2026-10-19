import { useEffect, useRef, useState } from 'react';
import { Crosshair, Lightbulb } from 'lucide-react';
import type { LayoutSpec } from '../../layout';
import { Button, Card, Stepper } from '../../components/ui';
import { useConnection } from '../../app/connection';
import { describeSegment } from '../../editor/segments';
import { locateLed } from './model';

export const BLINK_DEBOUNCE_MS = 350;

/**
 * "Find LED": pick an LED number on the controller and it blinks red → green → blue, so users can match
 * physical positions ("LED 17 is the bottom-right corner"). Blink commands are debounced while dragging.
 */
export function FindLed({
  spec,
  controllerId,
  onLocate,
}: {
  spec: LayoutSpec;
  controllerId: string | null;
  onLocate: (loc: { stripId: string; local: number } | null) => void;
}) {
  const { api, status } = useConnection();
  const controller = spec.controllers.find((c) => c.id === controllerId) ?? spec.controllers[0];
  const total = controller ? controller.outputs.reduce((m, o) => Math.max(m, o.start + o.length), 0) : 0;
  const [index, setIndex] = useState(0);
  const [active, setActive] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const loc = controller && active ? locateLed(spec, controller.id, index) : null;
  const locKey = loc ? `${loc.strip.id}:${loc.local}` : '';
  useEffect(() => {
    onLocate(loc ? { stripId: loc.strip.id, local: loc.local } : null);
  }, [locKey]);

  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current);
  }, []);

  const blink = (i: number, immediate = false) => {
    if (timer.current) clearTimeout(timer.current);
    const run = () => {
      setError(null);
      api.blinkLed(i).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    };
    if (immediate) run();
    else timer.current = setTimeout(run, BLINK_DEBOUNCE_MS);
  };

  const change = (i: number) => {
    const clamped = Math.max(0, Math.min(total - 1, i));
    setIndex(clamped);
    setActive(true);
    blink(clamped);
  };

  if (!controller || total === 0) return null;
  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
        <div className="flex items-center gap-2">
          <Crosshair size={17} className="text-accent-strong" />
          <h2 className="text-sm font-semibold">Find an LED</h2>
        </div>
        <Stepper label="LED number" value={index} min={0} max={total - 1} onChange={change} />
        <input
          type="range"
          aria-label="LED number slider"
          className="min-w-[10rem] flex-1"
          min={0}
          max={total - 1}
          value={index}
          onChange={(e) => change(Number(e.target.value))}
        />
        <Button size="sm" icon={<Lightbulb size={15} />} disabled={status !== 'open'} onClick={() => { setActive(true); blink(index, true); }}>
          Blink again
        </Button>
      </div>
      <p className="mt-2 text-[13px] text-ink-muted" aria-live="polite">
        {error ? (
          <span className="text-bad">Couldn’t blink: {error}</span>
        ) : active ? (
          loc ? (
            <>
              LED <b className="text-ink">#{index}</b> blinks red → green → blue. In your layout it’s LED {loc.local + 1} of “{loc.strip.name}”
              {loc.segmentIndex !== null ? ` (part ${loc.segmentIndex + 1}: ${describeSegment(loc.strip.segments[loc.segmentIndex])})` : ' — not covered by any part yet'}.
            </>
          ) : (
            <>LED #{index} blinks red → green → blue. It isn’t part of any strip in your layout.</>
          )
        ) : (
          <>Pick an LED number and it blinks on your strip — great for finding the corners.</>
        )}
      </p>
    </Card>
  );
}
