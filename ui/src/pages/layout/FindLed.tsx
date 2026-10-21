import { useEffect, useRef, useState } from 'react';
import { Crosshair, LightbulbOff } from 'lucide-react';
import type { LayoutSpec } from '../../layout';
import { Button, Card, Stepper } from '../../components/ui';
import { Select } from '../../components/form';
import { useConnection } from '../../app/connection';
import { locateLed } from './model';

export const BLINK_DEBOUNCE_MS = 350;

/**
 * "Find LED": pick a controller and an LED number on it, and that LED blinks red → green → blue, so users can match
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
  // Follows the selected strip's controller until the user picks one here.
  const [picked, setPicked] = useState<string | null>(null);
  const controller =
    spec.controllers.find((c) => c.id === picked) ?? spec.controllers.find((c) => c.id === controllerId) ?? spec.controllers[0];
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

  // The LED stays lit until another one is picked or the finder is turned off (or left).
  const activeRef = useRef(false);
  activeRef.current = active;
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      if (activeRef.current) api.stopBlink().catch(() => undefined);
    },
    [api],
  );

  // Another controller (picked here, or the selected strip's): the LED lit on the previous one goes off.
  const controllerKey = controller?.id;
  const litOn = useRef(controllerKey);
  useEffect(() => {
    if (litOn.current === controllerKey) return;
    litOn.current = controllerKey;
    if (timer.current) clearTimeout(timer.current);
    if (activeRef.current) api.stopBlink().catch(() => undefined);
    setActive(false);
    setIndex(0);
  }, [controllerKey, api]);

  const blink = (i: number, immediate = false) => {
    if (timer.current) clearTimeout(timer.current);
    const id = controller?.id;
    const run = () => {
      setError(null);
      api.blinkLed(i, id, true).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    };
    if (immediate) run();
    else timer.current = setTimeout(run, BLINK_DEBOUNCE_MS);
  };

  const stop = () => {
    if (timer.current) clearTimeout(timer.current);
    setActive(false);
    api.stopBlink(controller?.id).catch(() => undefined);
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
        {spec.controllers.length > 1 && (
          <Select
            label="Controller"
            hideLabel
            className="min-w-[10rem]"
            value={controller.id}
            options={spec.controllers.map((c) => ({ value: c.id, label: c.name || c.id }))}
            onChange={setPicked}
          />
        )}
        <Stepper label="LED number" value={Math.min(index, total - 1)} min={0} max={total - 1} onChange={change} />
        <input
          type="range"
          aria-label="LED number slider"
          className="min-w-[10rem] flex-1"
          min={0}
          max={total - 1}
          value={Math.min(index, total - 1)}
          onChange={(e) => change(Number(e.target.value))}
        />
        <Button size="sm" icon={<LightbulbOff size={15} />} disabled={status !== 'open' || !active} onClick={stop}>
          Turn off
        </Button>
      </div>
      <p className="mt-2 text-[13px] text-ink-muted" aria-live="polite">
        {error ? (
          <span className="text-bad">Couldn’t blink: {error}</span>
        ) : active ? (
          loc ? (
            <>
              LED <b className="text-ink">#{index}</b>{spec.controllers.length > 1 ? ` of ${controller.name}` : ''} stays lit, cycling red → green → blue. In your layout it’s LED {loc.local + 1}
              {' '}of “{loc.strip.name}”{loc.local >= loc.strip.ledCount ? ', past the end of the strip' : ''}, ringed in the room.
            </>
          ) : (
            <>LED #{index} stays lit, cycling red → green → blue. It isn’t part of any strip in your layout.</>
          )
        ) : (
          <>Pick an LED number and it lights up on your strip until you pick another or turn it off — great for finding the corners.</>
        )}
      </p>
    </Card>
  );
}
